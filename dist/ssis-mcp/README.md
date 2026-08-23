# SSIS MCP server — redistributable

A self-contained build of the SSIS Agent MCP server. Copy this whole `ssis-mcp` folder to any
Windows machine and run `install.ps1` — no source tree and no .NET SDK required.

## Contents

```
ssis-mcp/
  bin/                       SsisMcp.Server.exe + all managed dependencies
  install.ps1                probe environment + register with Codex / Claude Code
  uninstall.ps1              unregister + remove generated config
  README.md                  this file
  config/                    (created by install.ps1) generated client configs for THIS machine
```

The `Microsoft.SqlServer.*` SSIS assemblies are **not** shipped — they are resolved from the GAC of
the target machine's Integration Services install (shipping them would be a licensing violation).

## Requirements

- Windows x64, **.NET Framework 4.8**.
- **SQL Server Integration Services** (licensed shared feature) — required only for `package.execute`
  and ADO.NET metadata/execution. Build / inspect / validate / layout / lineage work without it;
  `package.execute` returns a structured `EnvironmentBlocked` when IS is absent.
- The SSIS **VSTA** design-time (installed with IS) for the Script Task tool.

## Install on a new PC

```powershell
# 1) copy the ssis-mcp folder anywhere, e.g. C:\Tools\ssis-mcp
# 2) from that folder:
powershell -ExecutionPolicy Bypass -File install.ps1
# optional: -LogFile C:\logs\ssis-mcp.log   (also append diagnostics to a file)
```

`install.ps1` prints an **Environment Probe** (`coreUsable` + checks), writes `config\.mcp.json`
(Claude Code) and `config\codex-config.toml` (Codex) using this machine's absolute exe path, and
registers `ssis` with whichever CLIs are found.

## Register manually (if the CLI wasn't auto-detected)

**Claude Code** — copy `config\.mcp.json` to your project root, or:
```
claude mcp add ssis --scope user -- "<INSTALL_DIR>\bin\SsisMcp.Server.exe"
```

**Codex** — paste `config\codex-config.toml` into `%USERPROFILE%\.codex\config.toml`, or:
```
codex mcp add ssis -- "<INSTALL_DIR>\bin\SsisMcp.Server.exe"
```

Replace `<INSTALL_DIR>` with wherever you copied the folder.

## Verify

The server speaks MCP (JSON-RPC 2.0) over stdio; **stdout is JSON-RPC only**, logs go to stderr
(and to `SSIS_MCP_LOG` if set). A client should handshake and list **15 tools**
(`environment.detect, project.inspect, package.inspect, controlflow.inspect, dataflow.inspect,
metadata.inspect, package.create, controlflow.apply, dataflow.apply, layout.apply, package.validate,
package.execute, package.undo, connection.test, data.verify`).

## Uninstall

```powershell
powershell -ExecutionPolicy Bypass -File uninstall.ps1
# then delete the folder to remove the binaries
```

No secrets and no build-machine paths are stored in this package; all paths are resolved at install
time on the target machine.
