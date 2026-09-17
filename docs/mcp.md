# MCP Integration

fuseraft-cli supports the [Model Context Protocol (MCP)](https://modelcontextprotocol.io). You can connect any MCP server at session startup, and its tools are registered as a plugin that any agent can call.

> **REPL users:** everything below configures MCP servers for `fuseraft run` via a YAML/JSON config. If you're in `fuseraft repl`, use `/mcp add` instead for an interactive wizard that connects a server on the spot and persists it for future sessions — see [CLI Reference — Connecting an MCP server](cli-reference.md#fuseraft-repl).

> **Looking for the other direction?** Everything below is about fuseraft connecting *out* to MCP servers as a client. If you want fuseraft itself to be callable as an MCP server — so another agent can dispatch tasks into a running fuseraft process — see [CLI Reference — `fuseraft serve`](cli-reference.md#fuseraft-serve).

---

## How it works

1. MCP servers are declared in `McpServers` in the config.
2. At session startup, the orchestrator connects to each server.
3. The server's tool list is fetched and registered as a named plugin under the server's `Name`.
4. Agents reference the server name in their `Plugins` list exactly like a built-in plugin.
5. When an agent calls a tool from the MCP plugin, the call is routed to the connected server.
6. When the session ends the connection is closed and any stdio child process is terminated.

---

## McpServerConfig fields

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Name` | string | — | Plugin name exposed to agents. Must be unique. |
| `Transport` | string | `"stdio"` | `"stdio"` or `"http"`. |
| `Command` | string | — | **stdio only.** Executable to launch (e.g. `"npx"`, `"python"`, `"dotnet"`). |
| `Args` | array | `[]` | **stdio only.** Arguments passed to `Command`. |
| `Env` | object | `{}` | **stdio only.** Additional environment variables for the child process. |
| `WorkingDirectory` | string | — | **stdio only.** Working directory for the child process. |
| `Url` | string | — | **http only.** Endpoint URL (e.g. `"https://mcp.example.com/mcp"`). Supports `${ENV_VAR}` tokens. |
| `Headers` | object | `{}` | **http only.** Extra HTTP headers sent with every request — e.g. a static API key or bearer token. Values support `${ENV_VAR}` tokens. |
| `TransportMode` | string | `"auto"` | **http only.** `"auto"`, `"streamable-http"`, or `"sse"`. Pin this for a server that only implements one, or when auto-detection is unreliable behind a proxy. |
| `OAuth` | object | — | **http only.** Enables interactive OAuth 2.1 login. See [OAuth login](#oauth-login) below. |

---

## Connecting to a production / hosted server

Most hosted MCP servers require authentication. fuseraft-cli supports the two common cases:

### Static header (API key / bearer token)

```yaml
McpServers:
  - Name: RemoteTools
    Transport: http
    Url: https://mcp.example.com/mcp
    Headers:
      Authorization: "Bearer ${MY_SERVER_TOKEN}"
```

`${ENV_VAR}` tokens in `Url` and `Headers` values are expanded at connect time (same syntax as
`ApiProfiles`), so the secret itself never has to live in the config file — set
`MY_SERVER_TOKEN` in your shell or a `.env` your process loads before running fuseraft.
`fuseraft validate-config` warns if a referenced env var isn't set in the current shell.

### OAuth login

For a server that requires OAuth 2.1 (the MCP spec's native auth flow), set `OAuth` on the
server entry — most fields are optional, since fuseraft-cli requests dynamic client registration
automatically when the server supports it:

```yaml
McpServers:
  - Name: HostedMcp
    Transport: http
    Url: https://mcp.example.com/mcp
    OAuth: {}
```

The first time fuseraft connects, it opens your default browser to the server's consent page and
listens on `http://localhost:1179/callback` (configurable via `OAuth.CallbackPort`) for the
redirect. Once you approve, the resulting tokens are cached in your OS keychain (macOS Keychain,
GNOME Keyring, or Windows Credential Manager — never plaintext on disk) keyed by server name, so
later sessions reconnect silently until the token is revoked or expires with no refresh token.

If no OS keychain is available, the login still works — the token just isn't persisted, so you'll
be prompted again next process run.

`McpOAuthConfig` fields, all optional:

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `ClientId` | string | — | Pre-registered OAuth client ID. Omit to use dynamic client registration. |
| `ClientSecret` | string | — | Pre-registered client secret, paired with `ClientId`. Supports `${ENV_VAR}`. |
| `Scopes` | array | `[]` | OAuth scopes to request. Omit to accept the server's default scopes. |
| `CallbackPort` | int | `1179` | Loopback port for the local redirect callback. Change only if it collides with something else. |

In the REPL, `/mcp add` offers the same two options interactively — pick "Header" or "OAuth" when
prompted for authentication after entering the URL.

Once an OAuth server is saved, two more REPL commands manage its login state without needing to
remove and re-add it:

- `/mcp login <name>` — (re)connect it, e.g. after it failed to auto-connect at REPL startup.
  Reuses a still-valid cached token silently; it does not by itself force a new browser prompt.
- `/mcp logout <name>` — clears the cached token and disconnects it, without forgetting the saved
  config. Use this before `/mcp login <name>` to force a fresh browser authorization (for example
  after revoking access on the server side, or to switch which account you're authorized as).

---

## Stdio transport

The most common setup. fuseraft-cli spawns the MCP server as a child process and communicates over stdin/stdout.

```yaml
McpServers:
  - Name: Filesystem
    Transport: stdio
    Command: npx
    Args:
      - "-y"
      - "@modelcontextprotocol/server-filesystem"
      - /home/user/projects
```

```yaml
McpServers:
  - Name: Puppeteer
    Transport: stdio
    Command: npx
    Args:
      - "-y"
      - "@modelcontextprotocol/server-puppeteer"
```

```yaml
McpServers:
  - Name: MyPython
    Transport: stdio
    Command: python
    Args:
      - "-m"
      - my_mcp_server
    WorkingDirectory: /home/user/my-mcp-server
    Env:
      MY_CONFIG_PATH: /etc/myserver.yaml
```

---

## HTTP transport

Connect to a running MCP server over HTTP. By default fuseraft-cli probes the endpoint and picks
the right wire protocol automatically — modern Streamable HTTP or legacy SSE. Set `TransportMode`
to pin one explicitly if a server only implements one or auto-detection is unreliable behind a
proxy.

```yaml
McpServers:
  - Name: RemoteTools
    Transport: http
    Url: http://localhost:8080/mcp
```

For a server that requires an API key, bearer token, or OAuth login, see
[Connecting to a production / hosted server](#connecting-to-a-production--hosted-server) above.

---

## Referencing MCP tools from agents

Add the server's `Name` to an agent's `Plugins` list:

```yaml
Agents:
  - Name: Developer
    Plugins:
      - FileSystem
      - Shell
      - Puppeteer
    ...
```

The agent then sees all tools from the Puppeteer server alongside the built-in FileSystem and Shell tools.

---

## Building your own MCP server

Any MCP-compliant server works. For .NET, use the `ModelContextProtocol` NuGet package.

**Minimal .NET MCP server** (`Program.cs`):

```csharp
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);

// Keep stdio clean — logs would corrupt the MCP protocol stream.
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();   // discovers all [McpServerTool] methods

await builder.Build().RunAsync();
```

**Tool definition** (`MyTools.cs`):

```csharp
using ModelContextProtocol.Server;
using System.ComponentModel;

[McpServerToolType]
public sealed class MyTools
{
    [McpServerTool, Description("Echo a message back to the caller.")]
    public string Echo(string message) => message;

    [McpServerTool, Description("Return the current UTC time.")]
    public string GetUtcTime() => DateTime.UtcNow.ToString("O");
}
```

Build and reference it in the config:

```yaml
McpServers:
  - Name: MyServer
    Transport: stdio
    Command: dotnet
    Args:
      - path/to/my-server.dll
```

This section is about building a *separate* MCP server for fuseraft to connect to as a client. If instead you want fuseraft itself to expose tools as an MCP server — no separate process to write — `fuseraft serve` already does this: it hosts `dispatch_task`/`get_status`/`get_result` over streamable HTTP so any MCP client (including another fuseraft instance) can hand it work. See [CLI Reference — `fuseraft serve`](cli-reference.md#fuseraft-serve).
