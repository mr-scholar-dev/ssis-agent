# MCP tool surface

A coherent, small API — not hundreds of hyper-specific tools. Implemented incrementally.
The server (`SsisMcp.Server`) speaks JSON-RPC 2.0 over newline-delimited **stdio**. All tool
results are structured DTOs serialized as JSON (never free text).

> Why a hand-rolled server: the SSIS assemblies are .NET Framework/COM and must run in a **net48**
> process, while the official MCP C# SDK targets modern .NET. A minimal in-process net48 server
> keeps SSIS in-proc and avoids a cross-runtime bridge. Dispatch is unit-tested independently of stdio.

## Implemented — public build surface

### Read / inspect

| Tool | Arguments | Returns |
|---|---|---|
| `environment.detect` | — | `EnvironmentReport` (checks + `coreUsable`) |
| `project.inspect` | `projectPath` | `ProjectInfo` |
| `package.inspect` | `packagePath` | `PackageInfo` (control flow, precedence, connections, data flows) |
| `controlflow.inspect` | `packagePath` | `{ name, executables, precedenceConstraints }` |
| `dataflow.inspect` | `packagePath` | `{ name, dataFlows }` |
| `metadata.inspect` | `packagePath, dataFlowTask` | `LineageReport` (graph + validation + stale refs) |

### Create / mutate / run

| Tool | Arguments | Returns |
|---|---|---|
| `package.create` | `packagePath, name, targetServerVersion?` | `PackageInfo` of the new empty package |
| `controlflow.apply` | `packagePath, operations[], preview?` | `OperationResult` (re-inspected package) |
| `dataflow.apply` | `packagePath, dataFlowTask, operations[], preview?` | `OperationResult` |
| `layout.apply` | `packagePath, mode?` (`AddMissing`/`Relayout`) | `{ applied, mode, nodes[] }` |
| `package.validate` | `packagePath` | `{ result, valid }` |
| `package.execute` | `packagePath` | `ExecutionResult` (`Success`/`Failure`/`EnvironmentBlocked`) |
| `data.verify` | `connectionString, sql, expected?` | `{ value, expected, matched }` |

All mutations run **strictly through the Safety layer** (`preview → apply → validate → commit |
rollback → reload → re-inspect`). `preview: true` on the two `*.apply` tools runs the batch in memory
and validates it **without writing**. Enums serialize as names (e.g. `"Success"`), not integers.

### Why this granularity (composable, not tool-per-component)

Instead of dozens of hyper-specific tools (`controlflow.add_task`, `dataflow.add_component`,
`dataflow.configure_lookup`, …), the write surface is **two batch tools** — `controlflow.apply` and
`dataflow.apply` — that each take an **`operations[]` array** in a small op-DSL. One batch = one atomic
pass through the Safety layer. This is:

- **Composable** — a client sends N primitives in one call; they commit or roll back together, so a
  half-built Data Flow never lands on disk.
- **Small & stable** — new component kinds are new *op names*, not new tools; the tool list (and its
  JSON schemas) stays short and legible for Claude Code / Codex.
- **Faithful to Safety** — a batch maps 1:1 to a single `PackageEditor.Apply` / `ApplyDataFlow`, which
  is exactly where preview/validate/rollback/reload live. A tool-per-component design would either
  commit each mutation separately (N reload cycles, partial states) or need a hidden session — both
  worse.

**Control Flow ops:** `addConnection`, `addVariable`, `addTask`, `configureExecuteSql`,
`configureScriptTask`, `setTaskProperty`, `connect`, `disconnect`, `removeTask`, `rename`.
**Data Flow ops:** `addComponent`, `configure{OleDb,AdoNet,Excel,FlatFile}{Source,Destination}`,
`connect`, `exposeAllInputColumns`, `derivedColumn`, `dataConversion`, `conditionalSplitCase`,
`lookup`, `map`, `autoMap`.

`McpProtocolTests` proves an external client rebuilds a representative practice
(connection → DFT → Source → Derived → Destination → mapping → layout → validate → metadata → execute →
data.verify) using **only** these tools — no direct builder calls, nothing hardcoded to the practice.

### What inspection reports

- **Control Flow**: hierarchical executables (containers keep children), task type, description,
  and referenced connection managers.
- **Precedence constraints**: `from`, `to`, `value` (Success/Failure/Completion), `evalOperation`
  (Constraint/Expression/…), and `expression`.
- **Data Flow**: components with role (source/transformation/destination), referenced connection
  managers, inputs/outputs, columns (name, `lineageId`, SSIS data type, length/precision/scale/
  codepage), external metadata columns, and paths (start/end component + port).
- **project.inspect diagnostics**: detected SSIS runtime, compatible + recommended Visual Studio,
  TargetServerVersion (echoed), `targetServerVersionVerified` (conservative — never true without a
  proven real build), package format versions, and known incompatibilities.

### Known limitation (documented, not faked)

Task-level connection managers cannot be read from the Object Model for COM-backed tasks
(`DtsProperty` reflection throws `TargetException`). They are read from the .dtsx file instead —
read-only inspection of a single fact, never XML surgery for mutation. Data Flow component
connections come directly from the Pipeline API's `RuntimeConnectionCollection`.

## Not yet exposed (candidates for later phases)

```
package.undo / package.backup      (Safety keeps backups; no undo tool surfaced yet)
metadata.repair                    (lineage auto-repair runs inside apply; not a standalone tool)
connection.test                    (validate a CM connects, without executing the package)
mapping.inspect / mapping.repair   (mapping compare/repair beyond map/autoMap)
sql.compare / excel.inspect / access.inspect / requirements.analyze
```

These are intentionally out of scope for this phase; none blocks a client from building, laying out,
validating, executing and verifying a package end-to-end today.
