# SSIS-Agent-MCP

A local **Model Context Protocol (MCP)** server for Windows that lets an MCP-compatible agent
inspect, build, validate, execute, diagnose and repair **SQL Server Integration Services (SSIS)**
projects and packages — programmatically, through the **SSIS Object Model / Runtime / Pipeline
APIs**, not through fragile UI automation.

> Status: **Milestone 0 — Repository + Environment Validation.** Only the environment detector
> (Fase 0) is implemented so far. Everything else in the phase plan is not built yet.

## Why the API-first approach

The core manipulates packages via `Microsoft.SqlServer.Dts.Runtime` / `...Dts.Pipeline`. Visual
Studio is a *secondary* visual bridge, and UI automation is a last resort only. See
[docs/architecture.md](docs/architecture.md).

## Requirements (verified on the current dev machine)

| Requirement | Needed | This machine |
|---|---|---|
| Windows 10/11 | yes | Windows 11 (build 26200) |
| SSIS runtime (`Microsoft.SqlServer.ManagedDTS` in GAC) | **critical** | **v17.0.0.0 (SQL Server 2025 family)** |
| .NET Framework 4.8 targeting pack | yes (SSIS assemblies are net48/COM) | present |
| .NET 10 SDK (to drive `dotnet build/test`) | yes | 10.0.301 |
| Visual Studio 2022 / 2026 | for the VS bridge (later phase) | 2026 (v18.7.3 Community) |
| SSIS Projects extension | for in-VS editing | **not detected** |
| ACE OLE DB (Excel/Access) | for Excel/Access sources | 12.0 + 16.0 present |
| SQL Server OLE DB provider | for SQL sources | MSOLEDBSQL / 19 present |

> **Important compatibility note:** the requested initial target was **SSIS 2016 (assembly v13)**,
> but **only the v17 runtime is installed** here. See
> [docs/ssis-versioning.md](docs/ssis-versioning.md) for what this means and the decision needed.

## Build & run the environment probe (Milestone 0)

```powershell
dotnet build SSIS-Agent-MCP.sln -c Debug
dotnet run --project src/SsisMcp.EnvProbe -c Debug
dotnet test SSIS-Agent-MCP.sln -c Debug
```

The probe prints a report and returns a non-zero exit code if a **critical** dependency
(the SSIS runtime) is missing — it never continues silently.

## Layout

```
src/
  SsisMcp.Core/       DTOs, enums, version map, environment detector (net48)
  SsisMcp.EnvProbe/   Fase 0 console runner (net48, x64)
tests/
  SsisMcp.UnitTests/  xUnit tests (version map, GAC scanner, report)
docs/                 architecture, versioning, safety, tools, testing
```

Later phases add `SsisMcp.Ssis`, `SsisMcp.SqlServer`, `SsisMcp.Excel`, `SsisMcp.Access`,
`SsisMcp.Safety`, `SsisMcp.Planner`, `SsisMcp.Server`, `SsisMcp.VisualStudioBridge`.

## Security

No credentials in code, commits, fixtures or logs. Destructive SQL tools are disabled by default.
See [docs/safety-model.md](docs/safety-model.md).
