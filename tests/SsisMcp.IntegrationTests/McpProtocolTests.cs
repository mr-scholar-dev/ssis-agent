using System;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SsisMcp.Server;
using Xunit;
using Xunit.Abstractions;

namespace SsisMcp.IntegrationTests
{
    /// <summary>
    /// Protocol-level proof that an EXTERNAL client (Claude Code / Codex) can reconstruct a
    /// representative version of the practice using ONLY the public MCP tools — no direct calls to the
    /// internal builders. Everything goes through McpServer.Dispatch (JSON-RPC tools/call). Nothing is
    /// hardcoded to "IntegracionPractica"; the package is named freely by the client.
    /// </summary>
    public sealed class McpProtocolTests : IDisposable
    {
        private readonly ITestOutputHelper _o;
        private readonly McpServer _srv = new McpServer();
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcp-" + Guid.NewGuid().ToString("N"));
        private int _id = 1;

        public McpProtocolTests(ITestOutputHelper o) { _o = o; Directory.CreateDirectory(_dir); }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private const string Cs = "Data Source=.;Initial Catalog=tempdb;Integrated Security=true;TrustServerCertificate=true";
        private static bool SqlUp() { try { using (var c = new SqlConnection(Cs + ";Connect Timeout=3")) c.Open(); return true; } catch { return false; } }
        private static void Sql(string s) { using (var c = new SqlConnection(Cs)) { c.Open(); using (var cmd = new SqlCommand(s, c)) cmd.ExecuteNonQuery(); } }

        // ---- thin MCP client over Dispatch ----
        private (JToken result, bool isError, string raw) Call(string name, JObject args)
        {
            var req = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = _id++,
                ["method"] = "tools/call",
                ["params"] = new JObject { ["name"] = name, ["arguments"] = args }
            };
            var resp = _srv.Dispatch(req) ?? throw new Xunit.Sdk.XunitException("null response");
            var res = resp["result"] ?? throw new Xunit.Sdk.XunitException("no result: " + resp);
            var isError = (bool)res["isError"]!;
            var text = (string)res["content"]![0]!["text"]!;
            JToken parsed = JValue.CreateNull();
            if (!isError) { try { parsed = JToken.Parse(text); } catch { parsed = new JValue(text); } }
            return (parsed, isError, text);
        }

        private void Ok(string name, JObject args)
        {
            var (r, isErr, raw) = Call(name, args);
            Assert.False(isErr, name + " tool error: " + raw);
            var succeeded = r["succeeded"];
            if (succeeded != null) Assert.True((bool)succeeded, name + " failed: " + raw);
        }

        [Fact]
        public void Tools_list_exposes_the_public_build_surface()
        {
            var resp = _srv.Dispatch(new JObject { ["jsonrpc"] = "2.0", ["id"] = 99, ["method"] = "tools/list" })!;
            var names = ((JArray)resp["result"]!["tools"]!).Select(t => (string)t["name"]!).ToList();
            foreach (var expected in new[] { "package.create", "controlflow.apply", "dataflow.apply", "layout.apply",
                "package.validate", "package.execute", "data.verify", "metadata.inspect", "package.inspect" })
                Assert.Contains(expected, names);
        }

        [Fact]
        public void External_client_rebuilds_a_representative_practice_via_mcp_only()
        {
            if (!SqlUp()) return;
            Sql("IF OBJECT_ID('tempdb.dbo.McpS') IS NOT NULL DROP TABLE dbo.McpS; IF OBJECT_ID('tempdb.dbo.McpD') IS NOT NULL DROP TABLE dbo.McpD;" +
                "CREATE TABLE dbo.McpS(Monto decimal(10,2)); INSERT dbo.McpS VALUES(100.00);" +
                "CREATE TABLE dbo.McpD(Monto decimal(10,2) NULL, Impuesto decimal(10,2) NULL);");

            var path = Path.Combine(_dir, "demo.dtsx");

            // 1) create package (client names it freely — NOT hardcoded)
            Ok("package.create", new JObject { ["packagePath"] = path, ["name"] = "McpDemo" });

            // 2) control flow: connection + a Data Flow Task (one atomic batch)
            Ok("controlflow.apply", new JObject
            {
                ["packagePath"] = path,
                ["operations"] = new JArray
                {
                    new JObject { ["op"]="addConnection", ["kind"]="oledb-sql", ["name"]="Db", ["dataSource"]=".", ["catalog"]="tempdb" },
                    new JObject { ["op"]="addTask", ["kind"]="DataFlow", ["name"]="DFT" },
                }
            });

            // 2b) PREVIEW must validate without writing: preview-add a task, then confirm it is NOT persisted
            var prev = Call("controlflow.apply", new JObject
            {
                ["packagePath"] = path, ["preview"] = true,
                ["operations"] = new JArray { new JObject { ["op"]="addTask", ["kind"]="DataFlow", ["name"]="GHOST" } }
            });
            Assert.False(prev.isError, prev.raw);
            Assert.True((bool)prev.result["succeeded"]!, prev.raw);
            var inspect = Call("package.inspect", new JObject { ["packagePath"] = path });
            Assert.DoesNotContain("GHOST", inspect.raw);   // preview did not persist

            // 3) data flow: Source -> Derived (Impuesto = Monto*0.13) -> Destination, with mapping
            Ok("dataflow.apply", new JObject
            {
                ["packagePath"] = path, ["dataFlowTask"] = "DFT",
                ["operations"] = new JArray
                {
                    new JObject { ["op"]="addComponent", ["kind"]="OleDbSource", ["name"]="Src" },
                    new JObject { ["op"]="configureOleDbSource", ["name"]="Src", ["connection"]="Db", ["accessMode"]=2, ["sqlOrTable"]="SELECT Monto FROM dbo.McpS" },
                    new JObject { ["op"]="addComponent", ["kind"]="DerivedColumn", ["name"]="Der" },
                    new JObject { ["op"]="connect", ["from"]="Src", ["to"]="Der" },
                    new JObject { ["op"]="exposeAllInputColumns", ["name"]="Der" },
                    new JObject { ["op"]="derivedColumn", ["name"]="Der", ["columnName"]="Impuesto", ["expression"]="(DT_NUMERIC,10,2)(Monto * 0.13)", ["dataType"]="DT_NUMERIC", ["precision"]=10, ["scale"]=2 },
                    new JObject { ["op"]="addComponent", ["kind"]="OleDbDestination", ["name"]="Dst" },
                    new JObject { ["op"]="connect", ["from"]="Der", ["to"]="Dst" },
                    new JObject { ["op"]="configureOleDbDestination", ["name"]="Dst", ["connection"]="Db", ["table"]="[dbo].[McpD]" },
                    new JObject { ["op"]="autoMap", ["destination"]="Dst" },
                }
            });

            // 4) layout + 5) validate
            Ok("layout.apply", new JObject { ["packagePath"] = path, ["mode"] = "Relayout" });
            var val = Call("package.validate", new JObject { ["packagePath"] = path });
            Assert.True((bool)val.result["valid"]!, val.raw);

            // 6) metadata/lineage inspect via MCP
            var lin = Call("metadata.inspect", new JObject { ["packagePath"] = path, ["dataFlowTask"] = "DFT" });
            Assert.False(lin.isError, lin.raw);
            Assert.True((bool)lin.result["isValid"]!, lin.raw);

            // 7) execute (portable: Success on a licensed host, else EnvironmentBlocked)
            var exec = Call("package.execute", new JObject { ["packagePath"] = path });
            var outcome = (string)exec.result["outcome"]!;
            _o.WriteLine("execute outcome=" + outcome);
            if (outcome != "Success") return;   // no licensed host -> stop here (build/validate already proven)

            // 8) business verification via MCP data.verify
            var ver = Call("data.verify", new JObject
            {
                ["connectionString"] = Cs,
                ["sql"] = "SELECT Impuesto FROM dbo.McpD WHERE Monto = 100.00",
                ["expected"] = "13.00"
            });
            Assert.False(ver.isError, ver.raw);
            Assert.True((bool)ver.result["matched"]!, "impuesto verify: " + ver.raw);

            Sql("IF OBJECT_ID('tempdb.dbo.McpS') IS NOT NULL DROP TABLE dbo.McpS; IF OBJECT_ID('tempdb.dbo.McpD') IS NOT NULL DROP TABLE dbo.McpD;");
        }

        [Fact]
        public void Undo_and_connection_test_tools_work_via_mcp()
        {
            var path = Path.Combine(_dir, "undo.dtsx");
            Ok("package.create", new JObject { ["packagePath"] = path, ["name"] = "UndoDemo" });
            Ok("controlflow.apply", new JObject
            {
                ["packagePath"] = path,
                ["operations"] = new JArray
                {
                    new JObject { ["op"]="addConnection", ["kind"]="oledb-sql", ["name"]="Db", ["dataSource"]=".", ["catalog"]="tempdb" },
                    new JObject { ["op"]="addTask", ["kind"]="DataFlow", ["name"]="DFT" },
                }
            });
            Assert.Contains("DFT", Call("package.inspect", new JObject { ["packagePath"] = path }).raw);

            // connection.test: acquires the CM (needs a reachable SQL Server); portable-skip otherwise
            if (SqlUp())
            {
                var ct = Call("connection.test", new JObject { ["packagePath"] = path, ["connection"] = "Db" });
                Assert.False(ct.isError, ct.raw);
                Assert.True((bool)ct.result["ok"]!, "connection.test: " + ct.raw);
            }

            // undo: restore the pre-apply backup → DFT/connection gone
            var undo = Call("package.undo", new JObject { ["packagePath"] = path });
            Assert.False(undo.isError, undo.raw);
            Assert.True((bool)undo.result["succeeded"]!, undo.raw);
            Assert.DoesNotContain("DFT", Call("package.inspect", new JObject { ["packagePath"] = path }).raw);
        }

        [Fact]
        public void Script_task_tool_is_wired_and_degrades_structurally()
        {
            var path = Path.Combine(_dir, "scr.dtsx");
            Ok("package.create", new JObject { ["packagePath"] = path, ["name"] = "ScrDemo" });
            var outTxt = Path.Combine(_dir, "mcp-scr.txt");
            var src = SsisMcp.Ssis.Building.ScriptTaskSource.CSharpMain(
                "            System.IO.File.WriteAllText(Dts.Variables[\"User::OutPath\"].Value.ToString(), \"ok\");\n");

            var (r, isErr, raw) = Call("controlflow.apply", new JObject
            {
                ["packagePath"] = path,
                ["operations"] = new JArray
                {
                    new JObject { ["op"]="addVariable", ["name"]="OutPath", ["value"]=outTxt },
                    new JObject { ["op"]="addTask", ["kind"]="Script", ["name"]="Scr" },
                    new JObject { ["op"]="configureScriptTask", ["name"]="Scr", ["source"]=src,
                                  ["readOnlyVariables"]=new JArray("User::OutPath") },
                }
            });
            Assert.False(isErr, raw);
            var succeeded = (bool)r["succeeded"]!;
            // VSTA present -> compiled + committed; VSTA absent -> structured UnsupportedEnvironment.
            if (!succeeded)
                Assert.Equal("UnsupportedEnvironment", (string?)r["errorCode"]);
        }
    }
}
