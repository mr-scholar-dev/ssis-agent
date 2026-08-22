# MCP tool surface

A coherent, small API — not hundreds of hyper-specific tools. Implemented incrementally.
The server (`SsisMcp.Server`) speaks JSON-RPC 2.0 over newline-delimited **stdio**. All tool
results are structured DTOs serialized as JSON (never free text).

> Why a hand-rolled server: the SSIS assemblies are .NET Framework/COM and must run in a **net48**
> process, while the official MCP C# SDK targets modern .NET. A minimal in-process net48 server
> keeps SSIS in-proc and avoids a cross-runtime bridge. Dispatch is unit-tested independently of stdio.

## Implemented — READ-ONLY (this phase)

| Tool | Arguments | Returns |
|---|---|---|
| `environment.detect` | — | `EnvironmentReport` (checks + `coreUsable`) |
| `project.inspect` | `projectPath` | `ProjectInfo` (target version, packages, connections, diagnostics) |
| `package.inspect` | `packagePath` | `PackageInfo` (control flow, precedence, connections, data flows) |
| `controlflow.inspect` | `packagePath` | `{ name, executables, precedenceConstraints }` |
| `dataflow.inspect` | `packagePath` | `{ name, dataFlows }` |

**No write/mutation tools are exposed yet** — by design. The Safety layer exists, but
`changes.preview` / `changes.apply` / `package.undo` will only be exposed after the Control Flow
builder can create/modify and then correctly re-inspect what it produced.

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

## Planned surface (later phases, gated by Safety + agent modes)

```
package.backup / package.validate / package.execute / package.undo
controlflow.add_task / controlflow.connect
dataflow.add_component / dataflow.configure_component / dataflow.connect
connection.create / connection.test
metadata.inspect / metadata.refresh / metadata.repair
mapping.inspect / mapping.auto_map / mapping.repair
sql.inspect / sql.compare
excel.inspect / access.inspect
requirements.analyze
changes.preview / changes.apply
execution.status / execution.errors / execution.verify
```
