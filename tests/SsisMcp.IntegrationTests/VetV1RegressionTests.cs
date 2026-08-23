using System;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SsisMcp.Server;
using Xunit;
using Xunit.Abstractions;

namespace SsisMcp.IntegrationTests
{
    /// <summary>
    /// FINAL end-to-end regression of the whole stack on the real vet practice files, driven ONLY
    /// through the `plan.run` MCP tool (which itself uses only public MCP tools). This is a regression,
    /// not an unseen benchmark. Guarded by the presence of the original files so it stays portable
    /// (skips elsewhere / in CI). Two facets:
    ///   (1) Full practice → the planner autonomously reaches Clarify with REAL ambiguities and invents
    ///       nothing (the F/O->idTipoCliente rule, edad->nacimiento, DELETE-vs-DROP are NOT guessed).
    ///   (2) A slice the planner can complete from a real source (Access Enfermedad, schema extracted
    ///       verbatim from the real destino.sql) exercises preview→apply→layout→validate→execute→verify
    ///       and undo — proving the execution half works from original files.
    /// </summary>
    public sealed class VetV1RegressionTests
    {
        private const string Base = @"C:\Users\serra\Downloads\Archivos de practica";
        private static readonly string Destino = Path.Combine(Base, "Base practica destino.sql");
        private static readonly string Accdb = Path.Combine(Base, "Carga de tablas emergentes.accdb");
        private const string Master = "Data Source=.;Initial Catalog=master;Integrated Security=true;TrustServerCertificate=true";

        private readonly ITestOutputHelper _o;
        public VetV1RegressionTests(ITestOutputHelper o) => _o = o;

        private static bool FilesPresent() => File.Exists(Destino) && File.Exists(Accdb);
        private static bool SqlUp() { try { using (var c = new SqlConnection(Master + ";Connect Timeout=3")) c.Open(); return true; } catch { return false; } }

        private static (bool isErr, JToken res) Call(McpServer s, string tool, JObject args)
        {
            var resp = s.Dispatch(new JObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JObject { ["name"] = tool, ["arguments"] = args } })!;
            var r = resp["result"]!;
            var isErr = (bool)r["isError"]!;
            var text = (string)r["content"]![0]!["text"]!;
            return (isErr, isErr ? new JValue(text) : JToken.Parse(text));
        }

        [Fact]
        public void Full_practice_reaches_Clarify_and_invents_nothing()
        {
            if (!FilesPresent()) return; // portable skip
            var req = new JObject
            {
                ["inputDir"] = Base,
                ["packagePath"] = Path.Combine(Path.GetTempPath(), "v1full-" + Guid.NewGuid().ToString("N") + ".dtsx"),
                ["packageName"] = "IntegracionPracticaV1",
                ["target"] = new JObject { ["name"] = "Vet", ["kind"] = "oledb-sql", ["dataSource"] = ".", ["catalog"] = "Vet" },
                ["sources"] = new JArray {
                    new JObject { ["name"]="Origen", ["kind"]="oledb-sql", ["dataSource"]=".", ["catalog"]="PracticaOrigen" },
                    new JObject { ["name"]="Excel", ["kind"]="excel", ["filePath"]=Path.Combine(Base,"Carga de tablas enfermedad y enfermedad mascota.xls"), ["xlsx"]=false, ["header"]=true },
                    new JObject { ["name"]="Access", ["kind"]="access", ["filePath"]=Accdb },
                },
                ["execute"] = false,
            };
            var (isErr, res) = Call(new McpServer(), "plan.run", req);
            Assert.False(isErr, res.ToString());
            Assert.Equal("Clarify", (string?)res["finalState"]);
            var qs = ((JArray)res["ambiguities"]!).Select(a => (string)a["question"]!).ToList();
            _o.WriteLine("ambiguities:\n  " + string.Join("\n  ", qs));
            // the F/O -> idTipoCliente business rule is NOT inventable -> must be asked
            Assert.Contains(qs, q => q.Contains("idTipoCliente"));
            // nothing was built
            Assert.False(File.Exists((string)req["packagePath"]!));
        }

        [Fact]
        public void Slice_from_real_access_source_executes_and_verifies_and_undo_works()
        {
            if (!FilesPresent() || !SqlUp()) return; // portable skip
            var destino = File.ReadAllText(Destino);
            var m = Regex.Match(destino, @"(Create\s+table\s+Enfermedad\s*\(.*?\))\s*;", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            Assert.True(m.Success, "could not extract Enfermedad DDL from the real destino.sql");
            var ddl = m.Groups[1].Value + ";";

            Master_Exec("IF DB_ID('VetV1Slice') IS NOT NULL BEGIN ALTER DATABASE VetV1Slice SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE VetV1Slice; END");
            Master_Exec("CREATE DATABASE VetV1Slice");
            Db_Exec("VetV1Slice", ddl);

            var dir = Path.Combine(Path.GetTempPath(), "v1 slice " + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "target.sql"), "CREATE DATABASE VetV1Slice;\n" + ddl + "\n");
            var pkg = Path.Combine(dir, "enf.dtsx");

            var srv = new McpServer();
            var req = new JObject
            {
                ["inputDir"] = dir, ["packagePath"] = pkg, ["packageName"] = "EnfV1",
                ["target"] = new JObject { ["name"] = "Vet", ["kind"] = "oledb-sql", ["dataSource"] = ".", ["catalog"] = "VetV1Slice" },
                ["sources"] = new JArray { new JObject { ["name"] = "Access", ["kind"] = "access", ["filePath"] = Accdb } },
                ["execute"] = true,
            };
            var (isErr, res) = Call(srv, "plan.run", req);
            Assert.False(isErr, res.ToString());
            foreach (var ph in (JArray)res["phases"]!) _o.WriteLine($"{ph["state"]} {(bool)ph["ok"]!} {ph["detail"]}");
            Assert.Equal("Complete", (string?)res["finalState"]);
            var verif = ((JArray)res["verifications"]!).Single();
            Assert.True((bool)verif["matched"]! && (long)verif["actual"]! == 6, "Enfermedad verify: " + verif);
            Assert.True(CountIn(pkg, "NodeLayout") >= 1, "layout not persisted");

            // undo reverts the last apply (Safety)
            var before = Call(srv, "dataflow.inspect", new JObject { ["packagePath"] = pkg }).res.ToString();
            var undo = Call(srv, "package.undo", new JObject { ["packagePath"] = pkg });
            Assert.False(undo.isErr, undo.res.ToString());
            Assert.True((bool)undo.res["succeeded"]!, undo.res.ToString());
            var after = Call(srv, "dataflow.inspect", new JObject { ["packagePath"] = pkg }).res.ToString();
            Assert.True(Regex.Matches(after, "\"role\"").Count < Regex.Matches(before, "\"role\"").Count, "undo did not revert components");

            Master_Exec("IF DB_ID('VetV1Slice') IS NOT NULL BEGIN ALTER DATABASE VetV1Slice SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE VetV1Slice; END");
            try { Directory.Delete(dir, true); } catch { }
        }

        private static int CountIn(string path, string token) => File.Exists(path) ? Regex.Matches(File.ReadAllText(path), Regex.Escape(token)).Count : 0;
        private static void Master_Exec(string sql) { using (var c = new SqlConnection(Master)) { c.Open(); using (var cmd = new SqlCommand(sql, c) { CommandTimeout = 60 }) cmd.ExecuteNonQuery(); } }
        private static void Db_Exec(string db, string sql) { using (var c = new SqlConnection($"Data Source=.;Initial Catalog={db};Integrated Security=true;TrustServerCertificate=true")) { c.Open(); using (var cmd = new SqlCommand(sql, c) { CommandTimeout = 60 }) cmd.ExecuteNonQuery(); } }
    }
}
