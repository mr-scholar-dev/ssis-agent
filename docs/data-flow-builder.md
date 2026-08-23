# Data Flow Builder (Fase 6)

Every write goes through the Safety layer exactly like the Control Flow builder:
`inspect → hash → lock → temp → mutate → validate → commit/rollback → reload → inspect`.
Success is confirmed by reloading from disk and re-inspecting — `Package.Validate()==Success`
alone is never treated as sufficient. Write tools remain unexposed over MCP by design.

## Component lifecycle (empirically confirmed, v17)

```
New() → ComponentClassID → Instantiate() → ProvideComponentProperties()
→ wire RuntimeConnection (ConnectionManagerID + GetExtendedInterface)
→ AcquireConnections() → ReinitializeMetaData() → ReleaseConnections()
→ configure columns/custom-properties → attach paths → validate
```

Empirically discovered requirements (all handled by the builder):
- Component **Name** must be set *after* `ProvideComponentProperties()` (it resets Name).
- Derived Column / Data Conversion output columns need **ErrorRowDisposition** +
  **TruncationRowDisposition** or validation returns `VS_ISCORRUPT`.
- Derived Column expression lives in the **Expression/FriendlyExpression custom properties** of the
  output column (not `SetOutputColumnProperty`, which throws `0xC0204006`).
- Data Conversion output column also needs the **FastParse** custom property.
- Conditional Split case outputs need `ExclusionGroup`, `SynchronousInputID`, **0-based
  EvaluationOrder**, and row dispositions.
- Explicit narrowing in Derived Column needs an explicit cast, e.g. `(DT_NUMERIC,10,2)(Monto * 0.13)`.

## API

`AddComponent / RenameComponent / RemoveComponent` (dependency guard + force),
`Connect / Disconnect` (explicit output/input ports + error-output selection),
`ConfigureOleDbSource / ConfigureOleDbDestination`, `ConfigureDerivedColumn`,
`ConfigureDataConversion`, `AddConditionalSplitCase`, `ConfigureLookup`, `ExposeAllInputColumns`,
`InspectComponent`. Component class ids resolved via `ISsisPipelineComponentCatalog` (versioned).

Mapping engine (`MappingEngine`): `Compare`, `AutoMap`, `SetMapping`, `RemoveMapping` with
classification `Exact / Compatible / RequiresConversion / Incompatible / MissingSource /
MissingDestination`. Conversions are **reported, never inserted silently**.

## Capability matrix — empirically verified on this host (SSIS v17 + local SQL Server)

| Component | Build | Validate | Full round-trip (reload) | Status |
|---|---|---|---|---|
| OLE DB Source (SQL command) | ✅ | ✅ | ✅ | **verified** |
| OLE DB Destination (+ AutoMap) | ✅ | ✅ | ✅ | **verified** |
| Derived Column (`Impuesto=Monto*0.13`) | ✅ | ✅ | ✅ | **verified** |
| Conditional Split (case + default, expression over upstream) | ✅ | ✅ | ✅ | **verified** |
| Column mapping engine (compare/auto/set/remove) | ✅ | ✅ | ✅ | **verified** |
| Data Conversion | ✅ | ✅ | ✅ (via Metadata/Lineage engine) | **verified** (Fase 12) |
| Lookup | code present | not run | — | **unverified** |
| Excel Source/Destination | code present | not run | — | **unverified** |
| Flat File Source/Destination | code present | not run | — | **unverified** |
| Aggregate/Sort/UnionAll/Merge/MergeJoin/Multicast/RowCount | catalog only | — | — | **not implemented** |

### Verified acceptance chains
- **SQL → SQL**: OLE DB Source → Derived Column → OLE DB Destination — build, validate, commit,
  reload, inspect, verify paths + external metadata + lineage + mapping. (`Sql_to_sql_...`)
- **Conditional Split** lineage regression: source → conditional split with an expression over an
  upstream column, reload, inspect lineage, validate. (`Conditional_split_...`)

### Negative paths (all end in structured error + safe rollback/abort)
unknown component kind → `Unsupported`; connect to missing component → `TaskNotFound`; bad
connection manager → fail, file untouched; remove component with attached path → `HasDependents`;
concurrent lock → `Busy`, file untouched.

## Discovered risks

1. **Data Conversion reload-lineage (important).** Data Conversion builds and validates in memory,
   but its `SourceInputColumnLineageID` references a numeric lineage that SSIS **reassigns on
   reload**, so the save→reload round-trip regresses with a *"Cannot find input column with lineage
   ID N … Check SourceInputColumnLineageID"* error — the exact `VS_NEEDSNEWMETADATA`-class problem.
   Repairing/re-anchoring lineage across reload is precisely the **Metadata/Lineage engine (Fase 12)**.
   This is a discovered risk, reported not hidden; Data Conversion is marked **partial** until Fase 12.
2. **Lookup / Excel / Flat File** are implemented but **not yet verified** with real fixtures, so
   they are not declared supported (per the phase rules).
3. **Designer layout preservation** (Fase 6 §17) is not yet covered by a test — it needs a real
   VS-authored golden package (Fase 27). Flagged as an open item before any layout-affecting change.

## Definition of Done status

The full six-component chain (Source→DataConversion→DerivedColumn→ConditionalSplit→Lookup→
Destination) is **NOT** declared done: Data Conversion (reload) and Lookup are not green. The
verified subset (SQL→SQL with Derived Column, Conditional Split, mapping, lineage, round-trip,
Safety) is complete. Remaining components are scheduled behind the Fase 12 metadata/lineage work
and dedicated Lookup/Excel/Flat File fixtures.
