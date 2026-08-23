using System;
using System.Data.OleDb;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SsisMcp.Planner;
using SsisMcp.Server;
using Xunit;
using Xunit.Abstractions;

namespace SsisMcp.IntegrationTests
{
    /// <summary>In-process MCP client: the planner drives the REAL McpServer over tools/call only.</summary>
    internal sealed class InProcessMcpInvoker : IMcpToolInvoker
    {
        private readonly McpServer _s = new McpServer();
        private int _id;
        public JToken Invoke(string tool, JObject arguments)
        {
            var req = new JObject { ["jsonrpc"] = "2.0", ["id"] = ++_id, ["method"] = "tools/call", ["params"] = new JObject { ["name"] = tool, ["arguments"] = arguments } };
            var resp = _s.Dispatch(req) ?? throw new McpToolException(tool, "null response");
            var res = resp["result"] ?? throw new McpToolException(tool, "no result");
            var text = (string)res["content"]![0]!["text"]!;
            if ((bool)res["isError"]!) throw new McpToolException(tool, text);
            return JToken.Parse(text);
        }
    }

    /// <summary>
    /// Two DISTINCT domains (Retail, HR) — deliberately not the vet practice — prove the planner is a
    /// general engine, not overfit to Fase 28. Retail: mixed SQL + Excel sources, full autonomy with
    /// auto Data Conversions, real execute + row-count verify. HR: a genuine ambiguity (NOT-NULL target
    /// column with no name match) makes the planner ASK instead of invent, then an explicit hint
    /// resolves it and it completes.
    /// </summary>
    public sealed class PlannerTests : IDisposable
    {
        private readonly ITestOutputHelper _o;
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "planner test " + Guid.NewGuid().ToString("N")); // space in path
        public PlannerTests(ITestOutputHelper o) { _o = o; Directory.CreateDirectory(_dir); }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private const string Master = "Data Source=.;Initial Catalog=master;Integrated Security=true;TrustServerCertificate=true";
        private static bool SqlUp() { try { using (var c = new SqlConnection(Master + ";Connect Timeout=3")) c.Open(); return true; } catch { return false; } }
        private static void M(string sql) { using (var c = new SqlConnection(Master)) { c.Open(); using (var cmd = new SqlCommand(sql, c) { CommandTimeout = 60 }) cmd.ExecuteNonQuery(); } }
        private static void D(string db, string sql) { using (var c = new SqlConnection($"Data Source=.;Initial Catalog={db};Integrated Security=true;TrustServerCertificate=true")) { c.Open(); using (var cmd = new SqlCommand(sql, c) { CommandTimeout = 60 }) cmd.ExecuteNonQuery(); } }
        private static void DropDb(string db) => M($"IF DB_ID('{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END");

        private void MakeXlsx(string path, string sheet, string createCols, params string[] rows)
        {
            var cs = $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={path};Extended Properties=\"Excel 12.0 Xml;HDR=YES\";";
            using (var c = new OleDbConnection(cs))
            {
                c.Open();
                using (var cmd = new OleDbCommand($"CREATE TABLE [{sheet}] ({createCols})", c)) cmd.ExecuteNonQuery();
                foreach (var r in rows) using (var cmd = new OleDbCommand($"INSERT INTO [{sheet}$] {r}", c)) cmd.ExecuteNonQuery();
            }
        }

        [Fact]
        public void Scenario_Retail_mixed_sources_full_autonomy()
        {
            if (!SqlUp()) return;
            DropDb("PlanSrcRetail"); DropDb("PlanDstRetail");
            M("CREATE DATABASE [PlanSrcRetail]"); M("CREATE DATABASE [PlanDstRetail]");
            // source SQL DB (nvarchar name -> will need WSTR->STR conversion into varchar dest)
            D("PlanSrcRetail", "CREATE TABLE Customer(id int, name nvarchar(50)); INSERT Customer VALUES(1,N'Ann'),(2,N'Ben'),(3,N'Cy');");
            // destination (empty)
            D("PlanDstRetail", "CREATE TABLE Customer(id int, name varchar(50) NULL); CREATE TABLE Product(sku int NULL, price money NULL);");

            // discovery inputs: a source .sql (with inserts) + a dest .sql (no inserts) + a Products.xlsx
            File.WriteAllText(Path.Combine(_dir, "source.sql"),
                "CREATE TABLE Customer(id int, name nvarchar(50));\ninsert into Customer values(1,'Ann');\ninsert into Customer values(2,'Ben');\ninsert into Customer values(3,'Cy');\n");
            File.WriteAllText(Path.Combine(_dir, "dest.sql"),
                "CREATE DATABASE PlanDstRetail;\nCREATE TABLE Customer(id int, name varchar(50));\nCREATE TABLE Product(sku int, price money);\n");
            var xlsx = Path.Combine(_dir, "Products.xlsx");
            MakeXlsx(xlsx, "Product", "[sku] int, [price] double", "VALUES(10, 9.99)", "VALUES(20, 19.50)");

            var pkg = Path.Combine(_dir, "retail.dtsx");
            var req = new PlannerRequest
            {
                InputDir = _dir, PackagePath = pkg, PackageName = "Retail",
                Target = new ConnectionSpec { Name = "Dst", Kind = "oledb-sql", DataSource = ".", Catalog = "PlanDstRetail" },
                Sources =
                {
                    new ConnectionSpec { Name = "SrcSql", Kind = "oledb-sql", DataSource = ".", Catalog = "PlanSrcRetail" },
                    new ConnectionSpec { Name = "SrcXls", Kind = "excel", FilePath = xlsx, Xlsx = true, Header = true },
                },
                Execute = true,
            };

            var res = new AutonomousPlanner(new InProcessMcpInvoker()).Run(req);
            foreach (var p in res.Phases) _o.WriteLine($"{p.State,-9} {(p.Ok ? "ok " : "ERR")} {p.Detail}");
            _o.WriteLine("SUMMARY: " + res.Summary);

            Assert.Empty(res.Ambiguities);                        // fully autonomous
            Assert.Equal(PlannerState.Complete, res.FinalState);
            Assert.Contains(res.Phases, p => p.State == PlannerState.Preview);
            Assert.Contains(res.Phases, p => p.State == PlannerState.Apply);
            // conversions were inferred (nvarchar->varchar, excel double->int/money)
            Assert.Contains(res.InferredDecisions, d => d.Contains("DT_WSTR->DT_STR"));
            Assert.Contains(res.InferredDecisions, d => d.Contains("->DT_CY") || d.Contains("->DT_I4"));
            // real destination data verified against source row counts
            var cust = res.Verifications.Single(v => v.Target == "Customer");
            var prod = res.Verifications.Single(v => v.Target == "Product");
            Assert.True(cust.Matched, $"Customer {cust.Actual}!={cust.Expected}");
            Assert.Equal(3, cust.Actual);
            Assert.True(prod.Matched, $"Product {prod.Actual}!={prod.Expected}");
            Assert.Equal(2, prod.Actual);

            DropDb("PlanSrcRetail"); DropDb("PlanDstRetail");
        }

        [Fact]
        public void Planner_exposed_as_mcp_tool_returns_questions_without_writing()
        {
            // plan.run over MCP, Clarify path — needs only files (no SQL, no writes).
            File.WriteAllText(Path.Combine(_dir, "src.sql"),
                "CREATE TABLE Widget(id int, label varchar(50));\ninsert into Widget values(1,'a');\n");
            File.WriteAllText(Path.Combine(_dir, "dst.sql"),
                "CREATE TABLE Widget(id int, title varchar(50) not null);\n");   // title NOT NULL, no 'title' source
            var pkg = Path.Combine(_dir, "planrun.dtsx");
            var req = new JObject
            {
                ["inputDir"] = _dir,
                ["packagePath"] = pkg,
                ["packageName"] = "PlanRun",
                ["targetSchemaSql"] = Path.Combine(_dir, "dst.sql"),
                ["target"] = new JObject { ["name"] = "Dst", ["kind"] = "oledb-sql", ["dataSource"] = ".", ["catalog"] = "whatever" },
                ["sources"] = new JArray { new JObject { ["name"] = "Src", ["kind"] = "oledb-sql", ["dataSource"] = ".", ["catalog"] = "whatever" } },
                ["execute"] = false,
            };
            var srv = new McpServer();
            var resp = srv.Dispatch(new JObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JObject { ["name"] = "plan.run", ["arguments"] = req } })!;
            var res = resp["result"]!;
            Assert.False((bool)res["isError"]!, (string?)res["content"]![0]!["text"]);
            var result = JObject.Parse((string)res["content"]![0]!["text"]!);
            _o.WriteLine("plan.run finalState=" + result["finalState"]);
            Assert.Equal("Clarify", (string?)result["finalState"]);
            Assert.Contains(((JArray)result["ambiguities"]!), a => ((string)a["question"]!).Contains("title"));
            Assert.False(File.Exists(pkg), "Clarify must not write a package");
        }

        [Fact]
        public void Scenario_HR_ambiguity_asks_then_resolves_with_hint()
        {
            if (!SqlUp()) return;
            DropDb("PlanSrcHr"); DropDb("PlanDstHr");
            M("CREATE DATABASE [PlanSrcHr]"); M("CREATE DATABASE [PlanDstHr]");
            D("PlanSrcHr", "CREATE TABLE Employee(id int, name varchar(100), dept varchar(50)); INSERT Employee VALUES(1,'Ann','Eng'),(2,'Ben','Ops');");
            // dest requires fullName (NOT NULL) — source has 'name', not 'fullName' -> ambiguity
            D("PlanDstHr", "CREATE TABLE Employee(id int, fullName varchar(100) NOT NULL, dept varchar(50) NULL);");

            File.WriteAllText(Path.Combine(_dir, "hr_source.sql"),
                "CREATE TABLE Employee(id int, name varchar(100), dept varchar(50));\ninsert into Employee values(1,'Ann','Eng');\ninsert into Employee values(2,'Ben','Ops');\n");
            File.WriteAllText(Path.Combine(_dir, "hr_dest.sql"),
                "CREATE DATABASE PlanDstHr;\nCREATE TABLE Employee(id int, fullName varchar(100) not null, dept varchar(50));\n");

            var pkg = Path.Combine(_dir, "hr.dtsx");
            PlannerRequest Make() => new PlannerRequest
            {
                InputDir = _dir, PackagePath = pkg, PackageName = "HR",
                TargetSchemaSql = Path.Combine(_dir, "hr_dest.sql"),   // disambiguate which script is the target
                Target = new ConnectionSpec { Name = "Dst", Kind = "oledb-sql", DataSource = ".", Catalog = "PlanDstHr" },
                Sources = { new ConnectionSpec { Name = "Src", Kind = "oledb-sql", DataSource = ".", Catalog = "PlanSrcHr" } },
                Execute = true,
            };

            // 1) without a hint -> the planner ASKS (does not invent), stops at Clarify, writes nothing
            var r1 = new AutonomousPlanner(new InProcessMcpInvoker()).Run(Make());
            _o.WriteLine("PASS1 state=" + r1.FinalState + " ambiguities=" + string.Join(" | ", r1.Ambiguities.Select(a => a.Question)));
            Assert.Equal(PlannerState.Clarify, r1.FinalState);
            Assert.Contains(r1.Ambiguities, a => a.Question.Contains("fullName"));
            Assert.False(File.Exists(pkg), "no package should be written while clarifying");

            // 2) supply the explicit mapping the user answered -> resolves, builds, executes, verifies
            var req2 = Make();
            req2.Hints.Add(new MappingHint { TargetTable = "Employee", ColumnMap = { ["fullName"] = "name" } });
            var r2 = new AutonomousPlanner(new InProcessMcpInvoker()).Run(req2);
            foreach (var p in r2.Phases) _o.WriteLine($"{p.State,-9} {(p.Ok ? "ok " : "ERR")} {p.Detail}");
            Assert.Empty(r2.Ambiguities);
            Assert.Equal(PlannerState.Complete, r2.FinalState);
            Assert.Contains(r2.ExplicitDecisions, d => d.Contains("Employee.fullName"));
            var emp = r2.Verifications.Single(v => v.Target == "Employee");
            Assert.True(emp.Matched && emp.Actual == 2, $"Employee {emp.Actual}!={emp.Expected}");

            DropDb("PlanSrcHr"); DropDb("PlanDstHr");
        }
    }
}
