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
| Lookup (Match/No Match, join, returned cols) | ✅ | ✅ | ✅ (double reload) | **verified**\* |
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

## Lookup (Fase 12 second benchmark)

Real internal properties used (empirically probed on v17; class id is the CLSID
`{E180A859-E5BF-4E0C-A824-89797EF1315B}`, so handlers detect it structurally):

- Component custom properties: `SqlCommand`, `CacheType` (0=Full/1=Partial/2=None),
  `NoMatchBehavior` (1 = redirect unmatched rows to the No Match output), `ReferenceMetadataXml`.
- Three outputs exist after `ProvideComponentProperties`: **Lookup Match Output**,
  **Lookup No Match Output**, **Lookup Error Output**.
- **Join** columns: input column custom property `JoinToReferenceColumn` (by reference name).
- **Returned** columns: added to the match output via the design-time `InsertOutputColumnAt`
  (a plain `OutputColumnCollection.New()` yields an "invalid row disposition" corrupt column),
  then bound with the `CopyFromReferenceColumn` custom property + explicit data type.
- **Cache**: Full Cache (`CacheType=0`) verified. Partial/No cache not exercised (documented partial).
- **No-match**: redirect-to-No-Match-Output verified. FailComponent/IgnoreFailure documented, not run.

Because Lookup references are **name-based**, they survive lineage reassignment on reload — Lookup
does **not** exhibit the Data Conversion stale-lineage bug. `LookupLineageReferenceHandler`
recognizes it, validates returned-column bindings, and reports "nothing numeric to rebind".

`dataflow.inspect` reports both Match and No Match outputs and the paths off each, so an agent can
see the Match/No-Match topology without reading XML.

> \* **Verified for build → validate → save → reload → lineage → second reload.** EXECUTE + data
> verify are **blocked on this host by SSIS edition licensing** (see risk #4) — the round-trip test
> executes via `dtexec` when a licensed Integration Services edition is present and skips otherwise.

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
4. **🔴 Data Flow execution is license-gated on this host.** Both in-process `Package.Execute` and
   `dtexec.exe` fail with *"To run a SSIS package outside of SQL Server Data Tools you must install
   Standard Edition (64-bit) of Integration Services or higher."* This is an **environment**
   limitation, not architectural: build / validate / save / reload / lineage all work. It blocks the
   EXECUTE + data-verify steps here and will gate the future Execution engine (Fase 17) — that phase
   needs a licensed Integration Services edition (or a remote execution host). Tests that execute
   detect the gate and skip data-verify rather than fail.

## Definition of Done status

The full six-component chain (Source→DataConversion→DerivedColumn→ConditionalSplit→Lookup→
Destination) is **NOT** declared done: Data Conversion (reload) and Lookup are not green. The
verified subset (SQL→SQL with Derived Column, Conditional Split, mapping, lineage, round-trip,
Safety) is complete. Remaining components are scheduled behind the Fase 12 metadata/lineage work
and dedicated Lookup/Excel/Flat File fixtures.
