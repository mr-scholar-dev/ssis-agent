# SSIS-Agent-MCP

A local **Model Context Protocol (MCP)** server for Windows that lets an MCP-compatible agent
inspect, build, validate, execute, diagnose and repair **SQL Server Integration Services (SSIS)**
projects and packages — programmatically, through the **SSIS Object Model / Runtime / Pipeline
APIs**, not through fragile UI automation.

> Status: **Fase 0, 1, 4, 2/3 and 5 (Control Flow Builder) complete.** Environment detection, a real
> SSIS Object Model roundtrip, the transactional Safety layer, full inspection, a **read-only MCP
> server** (5 tools), a Safety-gated **Control Flow builder**, and a Safety-gated **Data Flow
> builder** (OLE DB source/dest, Derived Column, Conditional Split, mapping engine — round-tripped
> with lineage) all pass against the installed v17 runtime (**61 tests**). Write tools remain
> unexposed by design. Data Flow capability matrix: [docs/data-flow-builder.md](docs/data-flow-builder.md).
>
> **Fase 12 — Metadata & Lineage engine** resolves the generic `save→reload→stale-lineage`
> (`VS_NEEDSNEWMETADATA`) bug by rebinding references to current lineage ids by stable identity,
> bounded and confidence-rated, inside Safety. Data Conversion round-trip fixed (double reload).
> Data Flow now also covers **Lookup**, **Excel (.xlsx/.xls via ACE)**, **Access (ACE OLE DB)** and
> **Flat File** — all `StructurallyVerified`. **72 tests.** No component is `ExecutionVerified`:
> Data Flow execution is `EnvironmentBlocked` by SSIS edition licensing on this host
> (`execution.dataFlow.available=false`). See [docs/data-flow-builder.md](docs/data-flow-builder.md).
>
> Run the MCP server: `dotnet run --project src/SsisMcp.Server`. Tools & schemas:
> [docs/mcp-tools.md](docs/mcp-tools.md).
>
> Visual Studio **2022 and 2026** are officially supported from the design; adapter contracts are
> locked (`IVisualStudioAdapter`, `ISsisVersionAdapter` — separate responsibilities). See
> [docs/visual-studio-adapters.md](docs/visual-studio-adapters.md).

## Fase 1 proof of concept

```powershell
dotnet run --project src/SsisMcp.SsisPoc -c Debug        # roundtrips a package, validates, reports
dotnet test tests/SsisMcp.IntegrationTests -c Debug      # same, as regression tests (real SSIS)
```

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
  SsisMcp.Core/       DTOs, enums, version map, environment detector, contracts (net48)
  SsisMcp.Ssis/       SSIS Object Model + Pipeline API: services & inspectors (net48)
  SsisMcp.Safety/     transactional mutation gate: hash/backup/lock/audit (net48)
  SsisMcp.Server/     read-only MCP stdio server (net48)
  SsisMcp.EnvProbe/   Fase 0 console runner (net48, x64)
  SsisMcp.SsisPoc/    Fase 1 SSIS roundtrip proof of concept
tests/
  SsisMcp.UnitTests/         version map, GAC scanner, safety pipeline, MCP dispatch
  SsisMcp.IntegrationTests/  real SSIS: roundtrip, safety, inspection, server stdio, project
docs/                        architecture, versioning, safety, tools, testing, VS adapters
```

Later phases add `SsisMcp.SqlServer`, `SsisMcp.Excel`, `SsisMcp.Access`, `SsisMcp.Planner`,
and `SsisMcp.VisualStudioBridge` (VS 2022/2026 adapters).

## Security

No credentials in code, commits, fixtures or logs. Destructive SQL tools are disabled by default.
See [docs/safety-model.md](docs/safety-model.md).
