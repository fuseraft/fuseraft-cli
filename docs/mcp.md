# MCP Integration

fuseraft-cli supports the [Model Context Protocol (MCP)](https://modelcontextprotocol.io). You can connect any MCP server at session startup, and its tools are registered as a plugin that any agent can call.

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
| `Url` | string | — | **http only.** SSE endpoint URL (e.g. `"http://localhost:3000/sse"`). |

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

Connect to a running MCP server over HTTP (Server-Sent Events).

```yaml
McpServers:
  - Name: RemoteTools
    Transport: http
    Url: http://localhost:8080/sse
```

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
