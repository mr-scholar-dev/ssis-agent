# Installing & registering the SSIS MCP server

The server is a **net48 console app** that speaks MCP (JSON-RPC 2.0) over **stdio**. stdout carries
**only** JSON-RPC; all diagnostics go to **stderr** (and to a file when `SSIS_MCP_LOG` is set), so no
log line ever corrupts the protocol stream.

## Prerequisites (target PC)

- Windows x64, .NET Framework 4.8.
- **SQL Server Integration Services** (the licensed shared feature) — required only for
  `package.execute` and for ADO.NET metadata/execution. Building/inspecting/validating/layout work
  without it; `package.execute` returns a structured `EnvironmentBlocked` where it is absent.
- .NET SDK (to build), or copy a prebuilt `bin` folder.
- For the Script Task tool: the SSIS **VSTA** design-time (installed with Integration Services).

## One-command setup

From the repo root:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\setup-mcp.ps1
# options: -Configuration Release   -LogFile C:\logs\ssis-mcp.log
```

It builds `SsisMcp.Server`, resolves the absolute `SsisMcp.Server.exe` path (spaces handled), and
writes:

- **`.mcp.json`** at the repo root — Claude Code project config (no BOM, valid JSON).
- **`mcp/codex-config.toml`** — a snippet to paste into Codex's config.

It then prints the exact `claude mcp add` / `codex mcp add` commands for this machine.

## Claude Code

**Project scope (recommended):** the generated `.mcp.json` sits at the repo root — open Claude Code in
that folder and it discovers the `ssis` server automatically. Format:

```json
{
  "mcpServers": {
    "ssis": {
      "command": "C:\\path\\to\\SsisMcp.Server\\bin\\Debug\\net48\\SsisMcp.Server.exe",
      "args": [],
      "env": {}
    }
  }
}
```

**User scope (any folder):**

```
claude mcp add ssis --scope user -- "C:\path\to\SsisMcp.Server.exe"
```

## Codex

Add via CLI:

```
codex mcp add ssis -- "C:\path\to\SsisMcp.Server.exe"
```

…or paste the generated `mcp/codex-config.toml` into `%USERPROFILE%\.codex\config.toml`:

```toml
[mcp_servers.ssis]
command = "C:\\path\\to\\SsisMcp.Server.exe"
args = []
env = { }
```

## Verifying the connection

Any MCP client should complete the handshake and list **15 tools**. A minimal manual check:

```
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
{"jsonrpc":"2.0","id":2,"method":"tools/list"}
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"environment.detect","arguments":{}}}
```

`initialize` returns `serverInfo.name = "ssis-agent-mcp"`. `tools/list` returns
`environment.detect, project.inspect, package.inspect, controlflow.inspect, dataflow.inspect,
metadata.inspect, package.create, controlflow.apply, dataflow.apply, layout.apply, package.validate,
package.execute, package.undo, connection.test, data.verify`.

This exact flow (out-of-process, over real stdio, with spaces in paths) is covered by
`ExternalMcpClientTests`.

## Paths with spaces

Fully supported. The command path and all file-path arguments are passed as JSON strings (never split
by a shell). The regression test deliberately uses a working directory **and** a package file name
containing spaces.

## Logging / troubleshooting

- Diagnostics print to **stderr**; set `SSIS_MCP_LOG=C:\logs\ssis-mcp.log` (env) to also append to a
  file. Never write logs to stdout — it is reserved for JSON-RPC.
- If a client shows "no tools", confirm the `command` path exists and the process starts (run the exe
  manually; it should block reading stdin and print a startup line to stderr).
- `package.execute` → `EnvironmentBlocked`: install the licensed Integration Services feature.
