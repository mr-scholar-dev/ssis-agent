# Metadata & Lineage Engine (Fase 12)

Generic engine that resolves the `build → validate PASS → save → reload → invalid lineage` class of
SSIS bugs — **not** a Data-Conversion-specific patch.

## Root cause (exact)

A component may store a lineage reference **outside** `InputColumn.LineageID` — for Data Conversion,
each output column carries the custom property `SourceInputColumnLineageID` pointing at the input
column it converts. At build time these are the design-time lineage ids and everything validates.
On the first `save → reload`, **SSIS reassigns lineage ids** (observed: `Conv IN 17 → 43`), but the
stored `SourceInputColumnLineageID` keeps the old value `17`. The dangling reference makes
`Package.Validate()` fail with *"Cannot find input column with lineage ID 17 … Check
SourceInputColumnLineageID"* — the `VS_NEEDSNEWMETADATA` / stale-lineage family.

## Repair strategy

Treat `LineageID` as a **reassignable runtime id**, never re-write an old one. After reload, rebind
each stale reference by **stable logical identity** (component + port + column name + type + the
component's current input columns) to the *current* lineage id, then re-validate. Bounded loop
(`maxRepairPasses = 3`, never infinite). Handlers detect their components by **structural signature**
(the presence of the custom property), not `ComponentClassID`, because SSIS persists a CLSID after
reload rather than the creation moniker.

```
build → save → reload (ids reassigned) → engine.Repair (rebind) → save
      → reload (stable) → lineage.validate PASS → package.validate PASS
```

All of this runs inside the Safety transaction (`PackageEditor.ApplyDataFlow` performs the
build → save → reload → repair → save cycle before Safety validates + commits).

## Repairs supported

| Repair | Confidence | Applied in SafeRepair |
|---|---|---|
| Data Conversion `SourceInputColumnLineageID`, unique input column | `Exact` | ✅ |
| … with several input columns (can't disambiguate) | `Ambiguous` | ❌ (ManualInterventionRequired) |
| … with no input columns (upstream path removed) | `None` | ❌ |

Every action reports `repairType / component / oldReference / newReference / confidence / reason /
applied`. Modes: `DiagnoseOnly` (report, never mutate), `SafeRepair` (Exact/Compatible only),
`ForceRepair` (internal, applies best-guess — not exposed).

## Internal API (not exposed over MCP yet)

`ILineageReferenceHandler` (per-family), `MetadataLineageEngine.BuildGraph / Validate / Repair`.
Graph answers: which upstream column produces an input, which component consumes a lineage id, and
which references are orphaned.

## Verified (round-trip gate)

Data Conversion is promoted **Partial → Verified**: `OLE DB Source → Data Conversion → OLE DB
Destination` passes construct → validate → save → reload → inspect → lineage.validate →
package.validate → **double reload** → PASS. Regression is permanent (`LineageEngineTests`).

Negative coverage: stale reference detected; DiagnoseOnly mutates nothing; ambiguous (multiple
conversions) is not auto-repaired and reports ManualInterventionRequired.

## Not regressed

Safety, Control Flow, project/data-flow inspection, OLE DB Source/Destination, AutoMap, Derived
Column (`Impuesto = (DT_NUMERIC,10,2)(Monto*0.13)` — name-based, untouched by the engine) and
Conditional Split all remain green (65 tests total).

## Remaining risks

- Rebind disambiguation for **multiple** converted columns needs a logical-name hint (currently
  reported Ambiguous). Fine for the benchmark; revisit if a fixture needs N simultaneous conversions.
- **Lookup** (next) will be the engine's second benchmark; its join/reference/return columns carry
  their own lineage references and will need a dedicated `ILineageReferenceHandler`.
