---
name: mcp-setup
description: Connect a fuseraft orchestration config to an MCP server and wire its tools to agents. Trigger when the user wants to add an MCP server, use external tools via MCP, or verify that an MCP connection is working.
---

# MCP Setup

Add an MCP server to an orchestration config, verify the connection, and wire the server's tools to the right agents.

## When to Use

Use this skill when:
- The user wants to use an off-the-shelf MCP server (npm package, Python package, etc.)
- The user wants to connect to a running HTTP MCP server
- An existing config uses `McpServers` but the connection is failing at startup
- The user wants to know which agents should receive MCP tools

Do **not** use this skill to build a custom MCP server from scratch — this skill covers wiring, not server authorship.

## Workflow

### Step 1: Gather Requirements

Ask these questions. Extract answers from the user's description if already given.

1. **Which config?** Path to the orchestration YAML/JSON being modified. Default: `.fuseraft/config/orchestration.yaml`.
2. **What server?** Name or npm/pip/binary of the MCP server. Common examples:
   - `@modelcontextprotocol/server-filesystem` (npm)
   - `@modelcontextprotocol/server-puppeteer` (npm)
   - A custom Python module (`python -m my_mcp_server`)
   - A running HTTP server (`http://localhost:8080/sse`)
3. **Transport?** `stdio` (server is spawned as a child process) or `http` (server is already running). If the user doesn't know: off-the-shelf npm/Python servers are almost always `stdio`; remote or shared servers are `http`.
4. **Which agents need the tools?** Usually the agent doing the work (Developer, Researcher). Multiple agents can share the same MCP server.
5. **Secrets or env vars needed?** Some servers need an API key (e.g. a search server). Ask the user to name the env var; tell them to set it in their shell before running, not in the config.

### Step 2: Verify the Server Command

For **stdio** servers, confirm the command is available before touching the config.

**npm-based server:**
```bash
npx --yes <package-name> --help 2>&1 | head -5
```
If this fails with "command not found", check that `node` and `npx` are installed:
```bash
node --version && npx --version
```

**Python-based server:**
```bash
python -m <module-name> --help 2>&1 | head -5
```
If this fails, the package may need to be installed first:
```bash
pip install <package-name>
```

**Binary/compiled server:**
```bash
which <binary-name>
```

For **http** servers, verify the endpoint is reachable:
```bash
curl -s --max-time 5 <url> | head -c 200
```
An SSE endpoint returns a stream — any non-error response confirms it is up.

If the command or endpoint is not available, stop and tell the user what to install or start before continuing.

### Step 3: Read the Config

Call `read_file` on the config. Note:
- Whether a `McpServers` block already exists (add to it, do not replace)
- The `Agents` section — identify which agents will receive the MCP plugin
- Any existing `Plugins` lists on those agents

### Step 4: Build the McpServers Entry

Choose the right template based on transport.

**stdio — npm package:**
```yaml
McpServers:
  - Name: <PluginName>
    Transport: stdio
    Command: npx
    Args:
      - "-y"
      - "<npm-package-name>"
      - <optional-arg-1>   # e.g. a directory path the server needs
```

**stdio — Python module:**
```yaml
McpServers:
  - Name: <PluginName>
    Transport: stdio
    Command: python
    Args:
      - "-m"
      - "<module-name>"
    WorkingDirectory: /path/to/server   # only if the module requires a specific cwd
    Env:
      MY_API_KEY: "${MY_API_KEY}"        # reference env var; never hardcode secrets
```

**http — running server:**
```yaml
McpServers:
  - Name: <PluginName>
    Transport: http
    Url: <sse-endpoint-url>
```

**Naming rules:**
- `Name` becomes the plugin identifier agents use in their `Plugins` list — pick a short PascalCase name (e.g. `Puppeteer`, `SearchAPI`, `MyServer`).
- `Name` must be unique across all entries in `McpServers`.
- Do not use a name that collides with built-in plugins: `FileSystem`, `Shell`, `Git`, `Http`, `Search`, `Scratchpad`, `Handoff`, `Changes`, `Git`.

**Secrets:** Never put API keys in the config. Reference them as env vars. Tell the user to export them in their shell before running:
```bash
export MY_API_KEY=sk-...
fuseraft run --config <path> "..."
```

### Step 5: Add the Plugin to Agents

For each agent that needs access to the MCP server's tools, add the `Name` from Step 4 to its `Plugins` list:

```yaml
- Name: Developer
  Plugins:
    - FileSystem
    - Shell
    - Puppeteer    # ← MCP server name added here
```

Agents that do not need the server's tools should not list it — keeping plugin lists lean reduces context and avoids confusion.

If agent instructions need to reference specific MCP tool names, the tool names are determined by the server. To discover them, run the dry-run in Step 6 first, then update instructions.

### Step 6: Apply and Validate

Patch the config using `patch_file` (preferred for surgical edits) or `write_file`:

1. Add the `McpServers` block (or new entry) at the top level under `Orchestration`.
2. Add the plugin name to the relevant agents' `Plugins` lists.

Then validate:
```bash
fuseraft validate <config-path>
```

Fix any reported errors before continuing.

### Step 7: Dry-Run Verification

Run a minimal one-turn session to confirm the MCP server connects and its tools are visible:

```bash
fuseraft run --config <config-path> --max-iterations 1 "List your available tools and stop."
```

Look for:
- The server name appearing in the startup output alongside built-in plugins — confirms the connection succeeded.
- The agent listing tool names from the MCP server in its response — confirms tools were registered.
- Any startup errors: `MCP connection failed`, `process exited`, `timeout` — see the troubleshooting table below.

If `--max-iterations` is not supported in the installed version, add `Termination: { MaxIterations: 1 }` temporarily to the config for this test, then restore it.

### Step 8: Troubleshoot If Needed

| Symptom | Likely cause | Fix |
|---------|-------------|-----|
| `MCP connection failed: process exited immediately` | Server binary crashed at startup | Run the command manually in a terminal to see its error output; check that required env vars are set |
| `MCP connection failed: timeout` | stdio server is printing to stderr before MCP protocol starts | Add `builder.Logging.SetMinimumLevel(LogLevel.Warning)` (for .NET servers) or suppress startup logs in your server |
| `MCP connection failed: command not found` | `Command` binary is not on `$PATH` | Use the full path to the binary, or install it first |
| Server connects but agent doesn't call any tools | Agent's `Plugins` list missing the server `Name` | Add the name to the agent's `Plugins` |
| `Name` collision warning at startup | Two `McpServers` entries share a name, or name matches a built-in plugin | Rename one entry; update agent `Plugins` lists to match |
| HTTP transport: connection refused | Server is not running at the specified URL | Start the server first; verify the SSE endpoint path (usually `/sse`, not `/`) |
| Tools visible but calls return auth errors | API key env var not set | `export <VAR>=<value>` before running fuseraft |

### Step 9: Confirm and Summarize

Tell the user:
1. Which config field was added and where
2. Which agents now have access to the server
3. The env vars they need to set before running (if any)
4. The full run command:
   ```bash
   fuseraft run --config <config-path> "Your task here"
   ```

## References

- MCP field reference: `docs/mcp.md`
- Plugin wiring: `docs/plugins.md`
- Config top-level fields: `docs/configuration.md`
