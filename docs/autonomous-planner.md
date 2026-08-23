# Autonomous Planner

A **provider-agnostic** engine (`SsisMcp.Planner`) that turns *"analyze these files and build this SSIS
practice"* into a real, validated, executed, verified `.dtsx` — **driving the public MCP tools only**.
It is not coupled to Claude or Codex, and it does not hardcode any specific practice.

## Hard rules (enforced in code)

- **Never invent** columns, mappings, business rules, or connections.
- **Separate `explicit` from `inferred`** provenance on every decision.
- A **low-confidence inference becomes a question**, not a guess.
- **No writes during Analyze or Plan** (read-only phases).
- Every change goes through **preview → apply** (the Safety layer); rollback/undo preserved.
- The planner acts **only** through `IMcpToolInvoker` — it never references the builders.

## States

```
Discover → Analyze → Plan → Clarify | Ready → Preview → Apply → Validate → Execute → Verify → Repair* → Complete
```

- **Discover** — `files.discover`: classify inputs (sql/excel/access/dtsx/doc).
- **Analyze** (read-only) — `sql.inspect` / `excel.inspect` / `access.inspect`: target schema (the
  insert-free `.sql`, or a specified one) + a pool of source objects. Ambiguous target ⇒ question.
- **Plan** (read-only) — per target table: pick a source by name (or hint); map columns by name;
  resolve SSIS types; insert Data Conversions where a **known safe** conversion exists; emit MCP ops.
  Anything without evidence (no source column for a NOT-NULL target, no safe type conversion, no source
  object) becomes an **Ambiguity**.
- **Clarify** — if any ambiguity remains, STOP and return questions. **Nothing is written.**
- **Ready** — plan complete, no unresolved ambiguity.
- **Preview → Apply** — `package.create`, then `controlflow.apply` and each `dataflow.apply` with
  `preview:true` first, then for real. One batch = one atomic Safety pass. Then `layout.apply`.
- **Validate** — `package.validate` + `metadata.inspect` (lineage) per Data Flow.
- **Execute** — `package.execute` (licensed host). `EnvironmentBlocked` ⇒ verify skipped, reported.
- **Verify** — `data.verify`: destination row counts vs. source row counts (business check, not exit code).
- **Repair*** — on a preview/apply/validate failure: diagnose → bounded retry (`MaxRepairAttempts`) →
  re-validate; unfixable items are recorded as **unresolved**, never silently dropped.
- **Complete** — final report.

## Plan model

`Plan` = `ControlFlowOps[]` (addConnection/addVariable/addTask/connect) + `Dfts[]` (each with its
`dataflow.apply` operations and a `ColumnPlan[]`) + `Ambiguities[]` + `Notes[]`. Every `ColumnPlan`
and decision carries `Provenance ∈ {Explicit, InferredHigh, InferredLow}`. The result lists
`ExplicitDecisions`, `InferredDecisions`, `Ambiguities`, `Verifications`, `Unresolved`.

## Ambiguity handling (ask, don't invent)

Raised — with a concrete question and options — when:
- the destination schema script cannot be inferred uniquely;
- no source object name matches a target table;
- a **NOT NULL** target column has no name-matched source column;
- a source→dest type pair has **no known safe conversion**.

The client (or user) answers via `hints` (explicit `columnMap` / `sourceName` / `derived`) or
`answers`; those decisions are then tagged **Explicit**. Business transforms (derived columns, splits,
lookups) are **never inferred** — only included when explicitly provided.

## Type inference (the evidence core)

`SsisTypes` resolves SQL/`.NET` source & destination types to SSIS `DT_*`, then decides: **equal →
direct map**, **known safe conversion → insert Data Conversion**, **otherwise → ask**. Known
conversions include unicode↔ansi, numeric widening/among numeric/currency, date/time families, and
string→scalar (a real SSIS Data Conversion capability, e.g. Excel text cells → numeric).

## Tools used (public MCP only)

`files.discover, sql.inspect, excel.inspect, access.inspect, package.create, controlflow.apply,
dataflow.apply, layout.apply, package.validate, metadata.inspect, package.execute, data.verify`
(+ `package.undo` / `connection.test` available for repair/diagnosis). Exposed end to end as the single
`plan.run` tool so Claude Code / Codex can invoke the whole flow in one call.

## Tests (not overfit to Fase 28)

Two distinct domains, real execution + row-count verification:
- **Retail** — mixed **SQL + Excel** sources, full autonomy: infers name mappings + Data Conversions
  (nvarchar→varchar, Excel numeric→int/money), builds 2 Data Flows, executes, verifies (Customer=3,
  Product=2). No ambiguities.
- **HR** — a NOT-NULL target column with no name match makes the planner **ask** (Clarify, nothing
  written); an explicit `columnMap` hint then resolves it and it completes + verifies (Employee=2).

Plus `plan.run` over MCP returns questions without writing (Widget), and `SsisTypeTests` pins the
type/conversion rules. None of these is the veterinary practice.

## Not yet / gaps

- Multi-source-into-one-table (append/union) and FK-graph ordering beyond declaration order.
- Inference is name+type based; semantic renames always route to a question (by design).
- The final **unseen** acceptance benchmark (a brand-new practice) is intentionally run *with the user*.
