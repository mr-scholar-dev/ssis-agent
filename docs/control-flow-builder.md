# Control Flow Builder (Fase 5)

The builder mutates packages **only** through the Safety layer. `PackageEditor.Apply` runs:

```
structural precheck (no write)  →  temp copy  →  builder mutation  →  SSIS validate
   →  commit | rollback  →  reload from disk  →  re-inspect  →  OperationResult
```

A committed operation is confirmed only after the package is reloaded and re-inspected with the
Fase 2/3 inspector — `Package.Validate() == Success` is never treated as sufficient on its own.

## Operations

`AddTask`, `ConfigureExecuteSql` / `SetTaskProperty`, `RenameTask`, `RemoveTask` (with dependency
guard + `force`), `Connect`, `Disconnect`. Tasks are addressable at any nesting depth; precedence
constraints are created in the scope (package or container) that owns both tasks.

Structured error codes (`BuilderErrorCode`): `NameCollision`, `TaskNotFound`, `InvalidPrecedence`,
`InvalidExpression`, `HasDependents`, `Unsupported`, `ValidationFailed`, `ExternalChange`, `Busy`,
`MutationError`. Task creation names are resolved through `ISsisComponentCatalog` (centralized, so
future runtimes/targets can vary monikers).

## Precedence

Values: `Success`, `Failure`, `Completion`. Evaluation ops: `Constraint`, `Expression`,
`ExpressionAndConstraint`, `ExpressionOrConstraint` — all present in the v17 runtime enum.

## Capability matrix — empirically verified on this host (SSIS v17)

| Kind | Create | Commit (validated) | Notes |
|---|---|---|---|
| Execute SQL Task | ✅ | ✅ (when configured) | Connection + SqlStatement verified; ResultSet/SourceType/BypassPrepare/TimeOut set best-effort |
| Data Flow Task | ✅ | ✅ | empty pipeline validates |
| Sequence Container | ✅ | ✅ | verified nesting of a configured child |
| For Loop | ✅ | ⚠️ needs config | creation verified; empty loop fails validation (needs EvalExpression) — expected |
| Foreach Loop | ✅ | ✅ empty | **partial**: enumerator NOT yet exercised — do not treat as full support |
| Script Task | ⚠️ | ❌ | **partial/unsupported on this host**: `Add("Microsoft.ScriptTask")` fails with `0x80070057` — requires VSTA/script design-time components not installed |

### Verified capabilities
- Create + configure + connect + rename + remove + round-trip inspect for **Execute SQL Task**,
  **Data Flow Task**, **Sequence Container** (incl. nesting).
- Precedence constraints with Success/Completion + Expression, re-inspected from disk.
- All negative paths end in safe rollback/abort: TaskNotFound, NameCollision, InvalidExpression,
  HasDependents, ValidationFailed (rollback), Busy (concurrent lock).

### Partial / not verified
- **Foreach Loop**: creates and commits empty, but no real enumerator has been configured/validated.
- **For Loop**: creates; full configuration (EvalExpression/Init/Assign) not yet exercised.
- **Script Task**: cannot be instantiated on this host (missing script design-time). Marked
  Unsupported by the builder rather than faked.

## Not yet exposed

Write tools are **not** exposed over MCP yet (by design). `changes.preview` / `changes.apply` /
`package.undo` will be exposed only after this internal API is stable and, next, the Data Flow
builder lands.
