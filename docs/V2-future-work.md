# V2 / Future Work

Non-critical enhancements deferred out of V1. **None of these block V1** — each is either buildable
today through the granular MCP tools (the planner just won't infer it) or an environment/DX nicety.

## Autonomous Planner
- **Partial build**: build the unambiguous Data Flows and ask only about the rest, instead of stopping
  the whole plan at Clarify.
- **Multi-source → one table** (append / union all), and **FK-graph precedence ordering** beyond target
  declaration order.
- **IDENTITY-preserving loads** (OLE DB keep-identity) chosen automatically when the target column is
  IDENTITY and source ids must be preserved.
- **Headerless / irregular Excel** handling (e.g. the practice's "Tipo Cliente" sheet) and column
  aliasing heuristics.
- **Business-rule intake**: a structured requirements format so derived columns / conditional splits /
  lookups can be provided as explicit intent (still never inferred).
- Confidence scoring surfaced per decision (`InferredLow` routed to questions with ranked options).

## Tooling / MCP
- `package.backup` (explicit) and richer `mapping.inspect` / `mapping.repair`.
- `sql.compare`, `requirements.analyze`, deeper `excel.inspect` type sampling.
- Native-ish XML export helper (today: a precompiled Script Task via `ConfigureScriptTask`).
- `.dtproj` / `.sln` project wrapper generation (grounded on a real VS-authored golden).

## Client integration
- Scripted **Claude Code** agentic smoke test once the `claude` CLI is present (config already in place;
  identical stdio transport already proven by `ExternalMcpClientTests`).
- Self-contained publish for PCs without the .NET SDK (today: the `dist/ssis-mcp` folder + install.ps1).

## Practice-specific (vet) items intentionally left to the user
- `edad → nacimiento` derivation rule and the DELETE-vs-DROP meaning of "borrar las tablas" are **not**
  determinable from the original files; the planner correctly raises/omits rather than inventing them.
  Resolving them is a data/requirements decision, not an engine gap.
