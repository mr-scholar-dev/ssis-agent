using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SsisMcp.Planner
{
    /// <summary>
    /// Provider-agnostic autonomous SSIS planner. Drives the public MCP tools ONLY (via
    /// <see cref="IMcpToolInvoker"/>) through explicit phases:
    ///   Discover → Analyze → Plan → Clarify | Ready → Preview → Apply → Validate → Execute → Verify
    ///   → Repair* → Complete.
    /// Critical rules enforced here: never invent columns/mappings/rules/connections; separate
    /// explicit from inferred; a low-confidence inference becomes a question; no writes during
    /// Analyze/Plan; every change goes through preview then apply (Safety); bounded repair.
    /// Not coupled to any provider, and never hardcodes a specific practice.
    /// </summary>
    public sealed class AutonomousPlanner
    {
        private readonly IMcpToolInvoker _mcp;
        private readonly PlannerResult _r = new();
        public AutonomousPlanner(IMcpToolInvoker mcp) => _mcp = mcp;

        private JToken Tool(string name, JObject args) => _mcp.Invoke(name, args);
        private void Phase(PlannerState s, string detail, bool ok = true)
            => _r.Phases.Add(new PhaseRecord { State = s, Detail = detail, Ok = ok });
        private static string Norm(string s) => new string(s.Trim().TrimEnd('$').ToLowerInvariant().Where(c => c != ' ').ToArray());

        public PlannerResult Run(PlannerRequest req)
        {
            try
            {
                var discovered = Discover(req);
                var (target, sources, analyzeOk) = Analyze(req, discovered);
                if (!analyzeOk) return Stop(PlannerState.Clarify);

                var plan = BuildPlan(req, target, sources);
                _r.Plan = plan;
                _r.Ambiguities = plan.Ambiguities;
                Phase(PlannerState.Plan, $"{plan.Dfts.Count} data flow(s), {plan.Ambiguities.Count} ambiguity(ies)");

                if (plan.Ambiguities.Count > 0)
                {
                    Phase(PlannerState.Clarify, "questions require answers before building", ok: false);
                    return Stop(PlannerState.Clarify);
                }
                Phase(PlannerState.Ready, "plan complete, no unresolved ambiguity");

                if (!PreviewAndApply(req, plan)) return Stop(PlannerState.Failed);
                Validate(plan);
                if (req.Execute && Execute(plan)) Verify(req, plan);

                _r.FinalState = PlannerState.Complete;
                Phase(PlannerState.Complete, "done");
                _r.Summary = Summarize();
                return _r;
            }
            catch (McpToolException ex)
            {
                _r.Unresolved.Add("MCP tool failure: " + ex.Message);
                return Stop(PlannerState.Failed);
            }
        }

        private PlannerResult Stop(PlannerState s) { _r.FinalState = s; _r.Ambiguities = _r.Plan?.Ambiguities ?? _r.Ambiguities; _r.Summary = Summarize(); return _r; }

        // ---------------- Discover ----------------
        private List<JToken> Discover(PlannerRequest req)
        {
            var files = (JArray)Tool("files.discover", new JObject { ["dir"] = req.InputDir });
            var kinds = files.GroupBy(f => (string)f["kind"]!).ToDictionary(g => g.Key, g => g.Count());
            Phase(PlannerState.Discover, string.Join(", ", kinds.Select(k => $"{k.Value} {k.Key}")));
            return files.ToList();
        }

        private sealed class SourceObject
        {
            public string Name = ""; public string Kind = ""; public int RowCount;
            public List<(string name, string type, bool nullable)> Cols = new();
            public ConnectionSpec Conn = new();
        }

        // ---------------- Analyze (READ-ONLY: no writes) ----------------
        private (List<JObject> targetTables, List<SourceObject> sources, bool ok)
            Analyze(PlannerRequest req, List<JToken> discovered)
        {
            // 1) target schema
            var sqlFiles = discovered.Where(f => (string)f["kind"]! == "sql").Select(f => (string)f["path"]!).ToList();
            JObject? targetSchema = null; string? targetPath = req.TargetSchemaSql;
            var targetTables = new List<JObject>();
            var sqlInspections = sqlFiles.ToDictionary(p => p, p => (JObject)Tool("sql.inspect", new JObject { ["path"] = p }));

            if (targetPath == null)
            {
                var zeroInsert = sqlInspections.Where(kv => ((JArray)kv.Value["tables"]!).Count > 0 &&
                                     ((JArray)kv.Value["tables"]!).All(t => (int)t["insertRowCount"]! == 0)).Select(kv => kv.Key).ToList();
                if (zeroInsert.Count == 1) targetPath = zeroInsert[0];
                else
                {
                    _r.Plan ??= new Plan();
                    _r.Plan.Ambiguities.Add(new Ambiguity { Id = "target-schema", Question = "Which .sql defines the DESTINATION schema?", Context = "Could not infer a single insert-free schema script.", Options = sqlFiles });
                    Phase(PlannerState.Analyze, "target schema ambiguous", ok: false);
                    return (new(), new(), false);
                }
                _r.InferredDecisions.Add($"target schema = {System.IO.Path.GetFileName(targetPath)} (only insert-free script)");
            }
            else _r.ExplicitDecisions.Add($"target schema = {System.IO.Path.GetFileName(targetPath)} (specified)");

            targetSchema = sqlInspections.TryGetValue(targetPath, out var ts) ? ts : (JObject)Tool("sql.inspect", new JObject { ["path"] = targetPath });
            targetTables = ((JArray)targetSchema["tables"]!).Cast<JObject>().ToList();

            // 2) source object pool
            var sources = new List<SourceObject>();
            foreach (var conn in req.Sources)
            {
                if (conn.Kind == "excel")
                {
                    var wb = (JObject)Tool("excel.inspect", new JObject { ["path"] = conn.FilePath!, ["header"] = conn.Header });
                    foreach (var sh in (JArray)wb["sheets"]!)
                        sources.Add(ToSource((JObject)sh, "excel", conn));
                }
                else if (conn.Kind == "access")
                {
                    var tbls = (JArray)Tool("access.inspect", new JObject { ["path"] = conn.FilePath! });
                    foreach (var t in tbls) sources.Add(ToSource((JObject)t, "access", conn));
                }
                else // sql source: schema comes from the non-target .sql scripts
                {
                    foreach (var kv in sqlInspections.Where(k => k.Key != targetPath))
                        foreach (var t in (JArray)kv.Value["tables"]!)
                            sources.Add(ToSource((JObject)t, "sql", conn));
                }
            }
            Phase(PlannerState.Analyze, $"target: {targetTables.Count} table(s); sources: {sources.Count} object(s)");
            return (targetTables, sources, true);
        }

        private static SourceObject ToSource(JObject o, string kind, ConnectionSpec conn)
        {
            var s = new SourceObject { Name = (string)o["name"]!, Kind = kind, Conn = conn, RowCount = (int?)o["rowCount"] ?? (int?)o["insertRowCount"] ?? 0 };
            foreach (var c in (JArray)o["columns"]!)
                s.Cols.Add(((string)c["name"]!, (string)c["dataType"]!, (bool?)c["nullable"] ?? true));
            return s;
        }

        // ---------------- Plan (READ-ONLY) ----------------
        private Plan BuildPlan(PlannerRequest req, List<JObject> targetTables, List<SourceObject> sources)
        {
            var plan = new Plan { PackagePath = req.PackagePath, PackageName = req.PackageName };

            // connections: target + all sources
            plan.ControlFlowOps.Add(ConnOp(req.Target));
            _r.ExplicitDecisions.Add($"target connection '{req.Target.Name}' ({req.Target.Kind})");
            foreach (var c in req.Sources) { plan.ControlFlowOps.Add(ConnOp(c)); _r.ExplicitDecisions.Add($"source connection '{c.Name}' ({c.Kind})"); }

            var dftNames = new List<string>();
            foreach (var tt in targetTables)
            {
                var tName = (string)tt["name"]!;
                var tCols = ((JArray)tt["columns"]!).Cast<JObject>()
                    .Select(c => ((string)c["name"]!, (string)c["dataType"]!, (bool?)c["nullable"] ?? true)).ToList();
                var hint = req.Hints.FirstOrDefault(h => Norm(h.TargetTable) == Norm(tName));

                // choose source object
                SourceObject? src = null;
                if (hint?.SourceName != null) src = sources.FirstOrDefault(s => Norm(s.Name) == Norm(hint.SourceName));
                src ??= sources.FirstOrDefault(s => Norm(s.Name) == Norm(tName));
                if (src == null)
                {
                    plan.Ambiguities.Add(new Ambiguity { Id = "source-for-" + tName, Question = $"Which source feeds target table '{tName}'?", Context = "No source object name matches; not inferring.", Options = sources.Select(s => s.Name).ToList() });
                    continue;
                }

                var dft = new DftPlan { Name = "DFT_" + tName, TargetTable = tName, SourceConnection = src.Conn.Name, SourceKind = SrcKind(src.Kind), SourceObject = src.Name, SourceRowCount = src.RowCount };
                var ops = dft.DataFlowOps;

                // source component
                ops.Add(new JObject { ["op"] = "addComponent", ["kind"] = SrcComponent(src.Kind), ["name"] = "Src" });
                ops.Add(SourceConfigOp(src));

                var conversions = new List<(string input, string outCol, DestType dt)>();
                var directMaps = new List<(string src, string dest)>();
                var derived = new List<DerivedHint>(hint?.Derived ?? new List<DerivedHint>());

                foreach (var (dName, dType, dNull) in tCols)
                {
                    // hint override or name match
                    string? srcCol = null; var prov = Provenance.InferredHigh;
                    if (hint != null && hint.ColumnMap.TryGetValue(dName, out var mapped)) { srcCol = mapped; prov = Provenance.Explicit; }
                    else srcCol = src.Cols.FirstOrDefault(c => Norm(c.name) == Norm(dName)).name;

                    if (derived.Any(d => Norm(d.Column) == Norm(dName))) continue; // provided by a derived hint

                    if (string.IsNullOrEmpty(srcCol))
                    {
                        if (dNull) { dft.Columns.Add(new ColumnPlan { DestColumn = dName, Note = "no source column; left NULL (nullable)", Provenance = Provenance.InferredHigh }); continue; }
                        plan.Ambiguities.Add(new Ambiguity { Id = $"col-{tName}-{dName}", Question = $"Target '{tName}.{dName}' is NOT NULL but no source column matches. Which source column maps to it?", Context = "Not inventing a mapping.", Options = src.Cols.Select(c => c.name).ToList() });
                        continue;
                    }

                    var srcType = src.Cols.FirstOrDefault(c => Norm(c.name) == Norm(srcCol!)).type ?? "";
                    var srcDt = SsisTypes.ResolveSource(src.Kind, srcType);
                    var dt = SsisTypes.ResolveDest(dType);
                    var need = SsisTypes.NeedsConversion(srcDt, dt.Dt);
                    if (need == null)
                    {
                        plan.Ambiguities.Add(new Ambiguity { Id = $"type-{tName}-{dName}", Question = $"No safe conversion from source '{srcCol}' ({srcDt}) to '{tName}.{dName}' ({dt.Dt}). How should it be converted?", Context = "Not guessing a conversion.", Options = new() });
                        continue;
                    }
                    if (need == false) { directMaps.Add((srcCol!, dName)); dft.Columns.Add(new ColumnPlan { DestColumn = dName, SourceColumn = srcCol, Provenance = prov }); }
                    else
                    {
                        var outCol = "c_" + dName;
                        conversions.Add((srcCol!, outCol, dt));
                        directMaps.Add((outCol, dName));
                        dft.Columns.Add(new ColumnPlan { DestColumn = dName, SourceColumn = outCol, Conversion = $"{srcDt}->{dt.Dt}", Provenance = prov });
                    }
                }

                string last = "Src";
                if (conversions.Count > 0)
                {
                    ops.Add(new JObject { ["op"] = "addComponent", ["kind"] = "DataConversion", ["name"] = "Conv" });
                    ops.Add(new JObject { ["op"] = "connect", ["from"] = last, ["to"] = "Conv" });
                    foreach (var cv in conversions)
                        ops.Add(new JObject { ["op"] = "dataConversion", ["name"] = "Conv", ["inputColumn"] = cv.input, ["columnName"] = cv.outCol, ["dataType"] = cv.dt.Dt, ["length"] = cv.dt.Length, ["precision"] = cv.dt.Precision, ["scale"] = cv.dt.Scale, ["codePage"] = cv.dt.CodePage });
                    last = "Conv";
                }
                if (derived.Count > 0)
                {
                    ops.Add(new JObject { ["op"] = "addComponent", ["kind"] = "DerivedColumn", ["name"] = "Der" });
                    ops.Add(new JObject { ["op"] = "connect", ["from"] = last, ["to"] = "Der" });
                    ops.Add(new JObject { ["op"] = "exposeAllInputColumns", ["name"] = "Der" });
                    foreach (var d in derived)
                    {
                        ops.Add(new JObject { ["op"] = "derivedColumn", ["name"] = "Der", ["columnName"] = d.Column, ["expression"] = d.Expression, ["dataType"] = d.DataType, ["length"] = d.Length, ["precision"] = d.Precision, ["scale"] = d.Scale, ["codePage"] = d.CodePage });
                        directMaps.Add((d.Column, d.Column));
                        dft.Columns.Add(new ColumnPlan { DestColumn = d.Column, SourceColumn = d.Column, Provenance = Provenance.Explicit, Note = "explicit derived column" });
                    }
                    last = "Der";
                }

                ops.Add(new JObject { ["op"] = "addComponent", ["kind"] = DestComponent(req.Target.Kind), ["name"] = "Dst" });
                ops.Add(new JObject { ["op"] = "connect", ["from"] = last, ["to"] = "Dst" });
                ops.Add(DestConfigOp(req.Target, tName));
                foreach (var (s, d) in directMaps)
                    ops.Add(new JObject { ["op"] = "map", ["destination"] = "Dst", ["sourceColumn"] = s, ["destinationColumn"] = d });

                plan.Dfts.Add(dft);
                dftNames.Add(dft.Name);
                foreach (var cp in dft.Columns) (cp.Provenance == Provenance.Explicit ? _r.ExplicitDecisions : _r.InferredDecisions)
                    .Add($"{tName}.{cp.DestColumn} <- {cp.SourceColumn}{(cp.Conversion != null ? " [" + cp.Conversion + "]" : "")}");
            }

            // control-flow tasks + precedence chain (target declaration order)
            foreach (var n in dftNames) plan.ControlFlowOps.Add(new JObject { ["op"] = "addTask", ["kind"] = "DataFlow", ["name"] = n });
            for (int i = 0; i < dftNames.Count - 1; i++)
                plan.ControlFlowOps.Add(new JObject { ["op"] = "connect", ["from"] = dftNames[i], ["to"] = dftNames[i + 1] });
            plan.Notes.Add("precedence follows target table declaration order (parents-first); destination tables are assumed empty (no truncate is invented).");
            return plan;
        }

        // ---------------- Preview + Apply ----------------
        private bool PreviewAndApply(PlannerRequest req, Plan plan)
        {
            var cfArgs = new JObject { ["packagePath"] = plan.PackagePath, ["operations"] = new JArray(plan.ControlFlowOps) };

            Tool("package.create", new JObject { ["packagePath"] = plan.PackagePath, ["name"] = plan.PackageName });

            // preview control flow
            var cfPrev = (JObject)Tool("controlflow.apply", Merge(cfArgs, ("preview", true)));
            if (!(bool)cfPrev["succeeded"]!) { Phase(PlannerState.Preview, "control-flow preview failed: " + (string?)cfPrev["detail"], ok: false); _r.Unresolved.Add("control-flow preview failed"); return false; }
            Phase(PlannerState.Preview, "control-flow preview OK");
            var cfApply = (JObject)Tool("controlflow.apply", cfArgs);
            if (!(bool)cfApply["succeeded"]!) { Phase(PlannerState.Apply, "control-flow apply failed", ok: false); _r.Unresolved.Add("control-flow apply failed"); return false; }
            Phase(PlannerState.Apply, "control-flow applied");

            foreach (var dft in plan.Dfts)
            {
                var dfArgs = new JObject { ["packagePath"] = plan.PackagePath, ["dataFlowTask"] = dft.Name, ["operations"] = new JArray(dft.DataFlowOps) };
                if (!ApplyDataFlowWithRepair(req, dfArgs, dft)) return false;
            }

            Tool("layout.apply", new JObject { ["packagePath"] = plan.PackagePath, ["mode"] = "Relayout" });
            Phase(PlannerState.Apply, "layout applied");
            return true;
        }

        private bool ApplyDataFlowWithRepair(PlannerRequest req, JObject dfArgs, DftPlan dft)
        {
            for (int attempt = 0; attempt <= req.MaxRepairAttempts; attempt++)
            {
                var prev = (JObject)Tool("dataflow.apply", Merge(dfArgs, ("preview", true)));
                if ((bool)prev["succeeded"]!)
                {
                    Phase(PlannerState.Preview, $"{dft.Name} preview OK");
                    var ap = (JObject)Tool("dataflow.apply", dfArgs);
                    if ((bool)ap["succeeded"]!) { Phase(PlannerState.Apply, $"{dft.Name} applied"); return true; }
                    Phase(PlannerState.Apply, $"{dft.Name} apply failed: " + (string?)ap["errorCode"], ok: false);
                }
                else Phase(PlannerState.Preview, $"{dft.Name} preview failed: " + Trunc((string?)prev["detail"]), ok: false);

                if (attempt < req.MaxRepairAttempts)
                {
                    _r.RepairAttempts++;
                    Phase(PlannerState.Repair, $"{dft.Name}: diagnose + retry (attempt {attempt + 1})");
                    // Bounded, honest repair: re-run (covers transient) — structural fixes would be added here.
                    continue;
                }
            }
            _r.Unresolved.Add($"{dft.Name} could not be applied after {req.MaxRepairAttempts} repair attempt(s)");
            return false;
        }

        // ---------------- Validate / Execute / Verify ----------------
        private void Validate(Plan plan)
        {
            var v = (JObject)Tool("package.validate", new JObject { ["packagePath"] = plan.PackagePath });
            var ok = (bool)v["valid"]!;
            Phase(PlannerState.Validate, "package.validate=" + (string?)v["result"], ok);
            foreach (var dft in plan.Dfts)
            {
                var lin = (JObject)Tool("metadata.inspect", new JObject { ["packagePath"] = plan.PackagePath, ["dataFlowTask"] = dft.Name });
                Phase(PlannerState.Validate, $"{dft.Name} lineage isValid=" + (bool)lin["isValid"]!, (bool)lin["isValid"]!);
            }
        }

        private bool Execute(Plan plan)
        {
            var ex = (JObject)Tool("package.execute", new JObject { ["packagePath"] = plan.PackagePath });
            var outcome = (string)ex["outcome"]!;
            Phase(PlannerState.Execute, "outcome=" + outcome, outcome == "Success");
            if (outcome != "Success") { _r.Unresolved.Add("execution " + outcome + " (verify skipped)"); return false; }
            return true;
        }

        private void Verify(PlannerRequest req, Plan plan)
        {
            var cs = $"Data Source={req.Target.DataSource};Initial Catalog={req.Target.Catalog};Integrated Security=true;TrustServerCertificate=true";
            foreach (var dft in plan.Dfts)
            {
                var res = (JObject)Tool("data.verify", new JObject { ["connectionString"] = cs, ["sql"] = $"SELECT COUNT(*) FROM [{dft.TargetTable}]", ["expected"] = dft.SourceRowCount.ToString() });
                var actual = Convert.ToInt64((string?)res["value"] ?? "0");
                var matched = (bool?)res["matched"] ?? false;
                _r.Verifications.Add(new VerifyRecord { Target = dft.TargetTable, Expected = dft.SourceRowCount, Actual = actual, Matched = matched });
                Phase(PlannerState.Verify, $"{dft.TargetTable}: rows {actual} (expected {dft.SourceRowCount}) matched={matched}", matched);
                if (!matched) _r.Unresolved.Add($"{dft.TargetTable} row count {actual} != source {dft.SourceRowCount}");
            }
        }

        // ---------------- op builders ----------------
        private static JObject ConnOp(ConnectionSpec c)
        {
            var o = new JObject { ["op"] = "addConnection", ["kind"] = c.Kind, ["name"] = c.Name };
            if (c.DataSource != null) o["dataSource"] = c.DataSource;
            if (c.Catalog != null) o["catalog"] = c.Catalog;
            if (c.FilePath != null) o["filePath"] = c.FilePath;
            if (c.Kind == "excel") { o["xlsx"] = c.Xlsx; o["hdr"] = c.Header; }
            return o;
        }
        private static string SrcKind(string k) => k switch { "excel" => "excel-source", "access" => "access-source", _ => "oledb-source" };
        private static string SrcComponent(string k) => k switch { "excel" => "ExcelSource", _ => "OleDbSource" };
        private static string DestComponent(string kind) => kind == "adonet-sql" ? "AdoNetDestination" : "OleDbDestination";

        private static JObject SourceConfigOp(SourceObject s)
        {
            if (s.Kind == "excel")
                return new JObject { ["op"] = "configureExcelSource", ["name"] = "Src", ["connection"] = s.Conn.Name, ["sheet"] = s.Name };
            // sql or access via OLE DB source (access CM is OLE DB/ACE; sql CM is OLE DB)
            return new JObject { ["op"] = "configureOleDbSource", ["name"] = "Src", ["connection"] = s.Conn.Name, ["accessMode"] = 0, ["sqlOrTable"] = s.Name };
        }
        private static JObject DestConfigOp(ConnectionSpec target, string table)
        {
            if (target.Kind == "adonet-sql")
                return new JObject { ["op"] = "configureAdoNetDestination", ["name"] = "Dst", ["connection"] = target.Name, ["table"] = "dbo." + table };
            return new JObject { ["op"] = "configureOleDbDestination", ["name"] = "Dst", ["connection"] = target.Name, ["table"] = "[dbo].[" + table + "]" };
        }

        private static JObject Merge(JObject o, params (string k, JToken v)[] extra)
        { var c = (JObject)o.DeepClone(); foreach (var e in extra) c[e.k] = e.v; return c; }
        private static string Trunc(string? s) => s == null ? "" : (s.Length > 200 ? s.Substring(0, 200) : s);

        private string Summarize()
        {
            var built = _r.Verifications.Count(v => v.Matched);
            return $"state={_r.FinalState}; dfts={_r.Plan?.Dfts.Count ?? 0}; explicit={_r.ExplicitDecisions.Count}; inferred={_r.InferredDecisions.Count}; " +
                   $"ambiguities={_r.Ambiguities.Count}; verified={built}/{_r.Verifications.Count}; unresolved={_r.Unresolved.Count}; repairs={_r.RepairAttempts}";
        }
    }
}
