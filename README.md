# SSIS Agent MCP — v1.0.0

A local (Claude Code,
Codex, …) **inspect, build, validate, execute, verify, diagnose and repair SQL Server Integration
Services (SSIS) packages** — programmatically through the SSIS Object Model / Runtime / Pipeline APIs,
never through fragile UI automation. It also ships an **Autonomous Planner** that turns *"analyze these
files and build this SSIS practice"* into a real, executed, verified `.dtsx`, using only the public MCP
tools.

## What it does

- **Analyze** SQL scripts, Excel workbooks and Access databases (schemas, columns, row counts).
- **Build** packages: connection managers, variables, Control Flow tasks, precedence, Data Flow Tasks
  with Sources / Destinations / transformations (Derived Column, Data Conversion, Conditional Split,
  Lookup), column mappings, and **precompiled Script Tasks**.
- **Lay out** the Control Flow and every internal Data Flow (top→bottom) so packages open cleanly in
  Visual Studio.
- **Validate** (real `Package.Validate`), inspect **metadata & lineage**, **execute** with a licensed
  `dtexec`, and **verify destination data** (row counts, values) — not just exit codes.
- Everything mutating goes through a transactional **Safety layer**: `preview → apply → validate →
  commit | rollback → reload → re-inspect`, with backups and **undo**.
- **Autonomous Planner**: discover → analyze → plan → clarify → preview → apply → validate → execute →
  verify → repair → complete. It **never invents** columns/mappings/rules/connections; when the inputs
  are insufficient it **asks** instead of guessing.

## Requirements

- Windows x64, **.NET Framework 4.8**.
- **SQL Server Integration Services** (licensed shared feature) — required for `package.execute`, the
  Script Task design-time (VSTA), and ADO.NET metadata/execution. Building / inspecting / validating /
  layout / planning-to-Clarify work without it; execution then reports a structured `EnvironmentBlocked`.
- SQL Server + the **ACE OLE DB** provider (x64) for Excel/Access sources.
- To build from source: the .NET SDK. To just run it: the redistributable in `dist/ssis-mcp`.

## Installation

**Redistributable (no source tree / SDK):** copy `dist/ssis-mcp` to the target PC and run:

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1
```

It runs an Environment Probe through the shipped server, writes client configs with the target
machine's absolute path, and registers `ssis` with Codex/Claude Code if their CLIs are present. See
[docs/mcp-install.md](docs/mcp-install.md). Uninstall: `uninstall.ps1`.

**From source:** `powershell -ExecutionPolicy Bypass -File scripts\setup-mcp.ps1` (builds + writes
`.mcp.json` and a Codex snippet). Rebuild the redistributable with `scripts\package-dist.ps1`.

### Claude Code

Project scope: the generated `.mcp.json` at the repo root is auto-discovered. User scope:

```
claude mcp add ssis --scope user -- "C:\path\to\ssis-mcp\bin\SsisMcp.Server.exe"
```

### Codex

```
codex mcp add ssis -- "C:\path\to\ssis-mcp\bin\SsisMcp.Server.exe"
```

…or paste `config/codex-config.toml` into `%USERPROFILE%\.codex\config.toml`. Both clients launch the
**same** server binary. stdout carries JSON-RPC only; logs go to stderr (and to `SSIS_MCP_LOG` if set).

## Tools

Read: `environment.detect`, `project.inspect`, `package.inspect`, `controlflow.inspect`,
`dataflow.inspect`, `metadata.inspect`, `files.discover`, `sql.inspect`, `excel.inspect`,
`access.inspect`.
Build/run: `package.create`, `controlflow.apply`, `dataflow.apply`, `layout.apply`, `package.validate`,
`package.execute`, `package.undo`, `connection.test`, `data.verify`.
Orchestration: `plan.run` (the Autonomous Planner end to end).

The write surface is composable: `controlflow.apply` / `dataflow.apply` take an `operations[]` op-DSL,
each batch = one atomic Safety pass; `preview:true` is a validated dry-run. Full contract +
granularity rationale: [docs/mcp-tools.md](docs/mcp-tools.md).

## Autonomous Planner

Provider-agnostic engine (`SsisMcp.Planner`) that drives only the public MCP tools. Explicit states,
`Explicit` vs `InferredHigh/Low` provenance on every decision, ambiguity → question, bounded repair.
Details + design: [docs/autonomous-planner.md](docs/autonomous-planner.md).

### Example (from Claude Code / Codex)

> "Analyze the files in `C:\work\practice` and build the SSIS package into `out.dtsx`, target the
> local `Warehouse` DB."

The client calls `plan.run` with the input dir, output path, and the target/source connection
endpoints. The planner discovers the files, infers name/type mappings (adding Data Conversions where
safe), and either **builds + executes + verifies** the package or returns **questions** for anything it
cannot determine from the files (never inventing a mapping or business rule).

## Classroom / Exam Prompts

Reusable, domain-neutral prompts for using SSIS Agent MCP in class (analyze, build, fix, review,
exam mode, quick) — identical for **Claude Code** and **Codex** against the same `ssis` server. See
[prompts/](prompts/README.md).

## Limitations (V1)

- The planner infers by **name + type**. Semantic renames, business rules (derived columns, splits,
  lookups), multi-source-into-one-table (append/union), IDENTITY-preserving loads, headerless Excel
  sheets, and FK-graph ordering beyond declaration order are **not inferred** — they surface as
  questions or require explicit hints. (These build fine via the granular tools; the planner just
  won't guess them.) Tracked in [docs/V2-future-work.md](docs/V2-future-work.md).
- Native XML export is not an SSIS destination; use a Script Task (`ConfigureScriptTask`) — see the
  Fase 28 benchmark.
- Execution/ADO.NET/Script Task require the licensed Integration Services feature; otherwise those
  steps report `EnvironmentBlocked` (never faked).

## Troubleshooting

- **Client shows no tools** → verify the `command` path exists; run the exe manually (it blocks reading
  stdin and prints a startup line to **stderr**). Never let anything write to stdout but JSON-RPC.
- **`package.execute` → EnvironmentBlocked** → install the licensed Integration Services feature.
- **ADO.NET metadata errors** → the `Microsoft.Data.SqlClient` closure must sit next to the exe (the
  redistributable already includes it; `AdoNet.SqlClient.targets` handles source builds).
- **Excel/Access fails** → install the x64 **ACE OLE DB** provider.
- **Paths with spaces** → fully supported (paths travel as JSON strings, never through a shell).

## Architecture

```
SsisMcp.Core        DTOs, interfaces, Safety contracts (no SSIS dependency)
SsisMcp.Safety      transactional editor, backups, locks, audit, undo
SsisMcp.Ssis        SSIS Object Model / Pipeline builders, inspectors, execution, lineage engine
SsisMcp.Designer    Control Flow + Data Flow layout engines
SsisMcp.Server      MCP server (JSON-RPC over stdio); the ONLY public interface
SsisMcp.Planner     Autonomous Planner (drives the MCP tools via IMcpToolInvoker; no builder access)
SsisMcp.VisualStudioBridge   VS 2022/2026 detection + adapters
```

Design docs: [Safety](docs/mcp-tools.md) · [Control Flow](docs/control-flow-builder.md) · [Data Flow](docs/data-flow-builder.md) ·
[Metadata/Lineage](docs/metadata-lineage-engine.md) · [Execution](docs/execution.md) ·
[VS adapters](docs/visual-studio-adapters.md) · [Planner](docs/autonomous-planner.md) ·
[Fase 28 benchmark](docs/fase28-integracion-practica.md) · [Install](docs/mcp-install.md).

## Tests

**120 tests** (53 unit + 67 integration) green; solution builds with **0 warnings**. Coverage includes
the Safety pipeline, Control/Data Flow builders, metadata/lineage repair, Script Task precompile,
the MCP protocol over real stdio, the redistributable server, the planner on two distinct domains
(Retail, HR), and the final vet-practice regression (planner reaches Clarify on the full practice;
a real Access→Enfermedad slice executes + verifies + undoes). Execution/data-verify tests are portable
(skip cleanly where a licensed host or SQL Server is absent).

## License

**GNU AGPL-3.0** — Copyright (C) 2026 **Isaac Serrano** — see [LICENSE](LICENSE).

Strong copyleft: you may use, study, modify and distribute this software, but **any distributed or
network-served derivative must be released as open source under the AGPL-3.0 and keep this copyright
and attribution**. Running a modified version as a network service also obligates you to provide its
source. This protects the work from being taken closed-source or rebranded without credit.

    SSIS Agent MCP — Copyright (C) 2026 Isaac Serrano
    This program is free software: you can redistribute it and/or modify it under the terms of the
    GNU Affero General Public License as published by the Free Software Foundation, version 3.
    This program is distributed WITHOUT ANY WARRANTY. See the GNU AGPL-3.0 for details.

## Status

**V1.0.0 — closed.** All critical acceptance gates pass end to end, from original files → planner →
public MCP tools → `.dtsx` → layout → validate → execute → verify, including from the redistributable
server. Non-critical enhancements are in [docs/V2-future-work.md](docs/V2-future-work.md).
