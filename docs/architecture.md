# Architecture

## Principle: API-first, UI-last

Priority of mechanisms for manipulating SSIS:

1. **SSIS Object Model / Runtime / Pipeline APIs** (`Microsoft.SqlServer.Dts.Runtime`,
   `...Dts.Pipeline`, `...Pipeline.Wrapper`, `...Runtime.Wrapper`). This is the primary and
   default mechanism for everything: open, inspect, build, connect, map, validate, execute.
2. **Visual Studio bridge** (VSIX) — a *thin* pass-through for showing/reloading packages and
   building projects inside the IDE. It contains **no** SSIS logic.
3. **UI Automation** — last resort only, never a primary path.

## Runtime split

SSIS managed assemblies are **.NET Framework (net48) / COM interop**. They do not load on
.NET 10. Therefore every project that touches the SSIS Object Model targets **net48**. To keep
V1 simple the whole solution is net48. The `dotnet` CLI (from the .NET 10 SDK) still builds and
tests net48 because the v4.8 targeting pack is installed.

If a future phase needs a .NET 10 host (e.g. a modern MCP transport), the SSIS-touching code will
be isolated behind a process boundary or an out-of-proc net48 worker, not force-loaded in-proc.

## Modules (target layout)

| Project | Responsibility |
|---|---|
| `SsisMcp.Core` | DTOs, enums, interfaces, version map, environment detector |
| `SsisMcp.Ssis` | SSIS Object Model wrappers, version adapters, control/data flow builders |
| `SsisMcp.SqlServer` | SQL inspection & schema compare (writes disabled by default) |
| `SsisMcp.Excel` / `SsisMcp.Access` | ACE-based inspectors |
| `SsisMcp.Safety` | backup/hash/lock/transaction layer (mandatory before any mutation) |
| `SsisMcp.Planner` | requirement interpretation → ETL plan → MCP operations |
| `SsisMcp.Server` | MCP transport + tool surface |
| `SsisMcp.VisualStudioBridge` | thin VSIX bridge (2022 + 2026 via adapters) |

## Version adapters

Two independent adapter layers, because a VS version does **not** imply an SSIS target version:

- **VS adapter**: detects installed VS instances at runtime (via `vswhere`), never hard-codes a
  version. VS 2022 (v17) and VS 2026 (v18) are the initial targets.
- **SSIS adapter**: binds to the runtime major installed in the GAC and uses
  `TargetServerVersion` for downlevel package generation. See
  [ssis-versioning.md](ssis-versioning.md).

## Reliability model

Every mutation follows: `inspect → hash → backup → temp copy → modify → validate → commit | rollback`.
Designed to **fail safe**, never "modify original and hope". See [safety-model.md](safety-model.md).
