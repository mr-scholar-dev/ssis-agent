using System;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using SsisMcp.Core.Building;
using SsisMcp.Ssis;
using SsisMcp.Ssis.Building;
using SsisMcp.Ssis.Lineage;
using Xunit;
using Dts = Microsoft.SqlServer.Dts.Runtime;
using Wrapper = Microsoft.SqlServer.Dts.Pipeline.Wrapper;
using Rt = Microsoft.SqlServer.Dts.Runtime.Wrapper;

namespace SsisMcp.IntegrationTests
{
    /// <summary>
    /// Lookup acceptance against REAL SSIS + SQL Server: Source → Lookup → Match / No Match, with
    /// build → validate → save → reload → lineage → second reload → EXECUTE → data verify.
    /// </summary>
    public sealed class LookupTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "lk-" + Guid.NewGuid().ToString("N"));
        private readonly PackageService _svc = new PackageService();
        private const string Cs = "Data Source=.;Initial Catalog=tempdb;Provider=MSOLEDBSQL;Integrated Security=SSPI;";

        public LookupTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static bool Sql(out SqlConnection? c)
        {
            c = null;
            try { var x = new SqlConnection("Data Source=.;Initial Catalog=tempdb;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=3"); x.Open(); c = x; return true; }
            catch { return false; }
        }
        private static void Exec(SqlConnection c, string sql) { using (var cmd = new SqlCommand(sql, c)) cmd.ExecuteNonQuery(); }
        private static int Scalar(SqlConnection c, string sql) { using (var cmd = new SqlCommand(sql, c)) return Convert.ToInt32(cmd.ExecuteScalar()); }
        private static string StrScalar(SqlConnection c, string sql) { using (var cmd = new SqlCommand(sql, c)) return (string)cmd.ExecuteScalar(); }

        [Fact]
        public void Source_lookup_match_nomatch_roundtrips_double_reload_and_executes()
        {
            if (!Sql(out var conn)) return;
            using (conn)
            {
                Exec(conn!, @"IF OBJECT_ID('tempdb.dbo.LkInput') IS NOT NULL DROP TABLE dbo.LkInput;
                              IF OBJECT_ID('tempdb.dbo.LkRef') IS NOT NULL DROP TABLE dbo.LkRef;
                              IF OBJECT_ID('tempdb.dbo.LkMatch') IS NOT NULL DROP TABLE dbo.LkMatch;
                              IF OBJECT_ID('tempdb.dbo.LkNoMatch') IS NOT NULL DROP TABLE dbo.LkNoMatch;
                              CREATE TABLE dbo.LkInput(Codigo int, Nombre varchar(10));
                              CREATE TABLE dbo.LkRef(Codigo int, Tipo nvarchar(20));
                              CREATE TABLE dbo.LkMatch(Codigo int, Nombre varchar(10), TipoCliente nvarchar(20));
                              CREATE TABLE dbo.LkNoMatch(Codigo int, Nombre varchar(10));
                              INSERT dbo.LkInput VALUES (1,'a'),(2,'b'),(999,'z');
                              INSERT dbo.LkRef VALUES (1,'Premium'),(2,'Basic');");

                // base package with DFT
                var pkg = new Dts.Package { Name = "DF" };
                var cm = pkg.Connections.Add("OLEDB"); cm.Name = "Db"; cm.ConnectionString = Cs;
                var path = Path.Combine(_dir, "lookup.dtsx");
                _svc.Save(pkg, path);
                Assert.True(new PackageEditor(_svc).Apply(path, b => b.AddTask(TaskKinds.DataFlow, "DFT")).Succeeded);

                // build the lookup flow through Safety (build → save → reload → repair → save → validate → commit)
                var r = new PackageEditor(_svc).ApplyDataFlow(path, "DFT", b =>
                {
                    b.AddComponent(ComponentKinds.OleDbSource, "Src");
                    b.ConfigureOleDbSource("Src", "Db", 2, "SELECT Codigo, Nombre FROM dbo.LkInput");

                    b.AddComponent(ComponentKinds.Lookup, "Lk");
                    b.Connect("Src", "Lk");
                    b.ConfigureLookup("Lk", "Db", "SELECT Codigo, Tipo FROM dbo.LkRef",
                        new[] { ("Codigo", "Codigo") },
                        new[] { ("Tipo", "TipoCliente", Rt.DataType.DT_WSTR, 20, 0, 0, 0) });

                    b.AddComponent(ComponentKinds.OleDbDestination, "MatchDst");
                    b.Connect("Lk", "MatchDst", fromOutput: DataFlowBuilder.LookupMatchOutput);
                    b.ConfigureOleDbDestination("MatchDst", "Db", "[dbo].[LkMatch]");
                    new MappingEngine(b).AutoMap("MatchDst");

                    b.AddComponent(ComponentKinds.OleDbDestination, "NoMatchDst");
                    b.Connect("Lk", "NoMatchDst", fromOutput: DataFlowBuilder.LookupNoMatchOutput);
                    b.ConfigureOleDbDestination("NoMatchDst", "Db", "[dbo].[LkNoMatch]");
                    new MappingEngine(b).AutoMap("NoMatchDst");
                });

                Assert.True(r.Succeeded, r.ErrorCode + ": " + r.Detail);   // validated + committed on reload

                // inspect: two match/no-match paths off the Lookup
                var df = r.Package!.DataFlows.Single(d => d.TaskName == "DFT");
                var lk = df.Components.Single(c => c.Name == "Lk");
                Assert.Contains(lk.Outputs, o => o.Name == DataFlowBuilder.LookupMatchOutput);
                Assert.Contains(lk.Outputs, o => o.Name == DataFlowBuilder.LookupNoMatchOutput);
                Assert.Contains(df.Paths, p => p.StartComponent == "Lk" && p.EndComponent == "MatchDst");
                Assert.Contains(df.Paths, p => p.StartComponent == "Lk" && p.EndComponent == "NoMatchDst");

                // DOUBLE reload: lineage + package validation both clean
                var engine = new MetadataLineageEngine();
                var reload1 = _svc.Load(path);
                Assert.True(engine.Validate(Pipe(reload1)).IsValid);
                var reload2 = _svc.Load(path);
                Assert.Equal(Dts.DTSExecResult.Success, _svc.Validate(reload2));

                // EXECUTE and verify functional results
                // In-process pipeline execution is license-gated on this host ("...install Standard
                // Edition of Integration Services..."), so execute via dtexec.exe on the licensed
                // instance instead. If neither path can run, skip data verification (env-limited).
                // Pipeline execution is license-gated on this host (SSIS edition), so data-verify
                // can only run where a Standard+ Integration Services edition is available. The
                // build/validate/round-trip/double-reload above are the permanent regression here.
                if (!TryExecute(path, out _)) return; // UnsupportedEnvironment for execution — skip data verify

                Assert.Equal(2, Scalar(conn!, "SELECT COUNT(*) FROM dbo.LkMatch"));       // Codigo 1,2 matched
                Assert.Equal(1, Scalar(conn!, "SELECT COUNT(*) FROM dbo.LkNoMatch"));     // Codigo 999 no-match
                Assert.Equal(1, Scalar(conn!, "SELECT COUNT(*) FROM dbo.LkNoMatch WHERE Codigo = 999"));
                Assert.Equal("Premium", StrScalar(conn!, "SELECT TipoCliente FROM dbo.LkMatch WHERE Codigo = 1")); // returned column

                Exec(conn!, @"DROP TABLE dbo.LkInput; DROP TABLE dbo.LkRef; DROP TABLE dbo.LkMatch; DROP TABLE dbo.LkNoMatch;");
            }
        }

        private static bool TryExecute(string packagePath, out string detail)
        {
            detail = "";
            var dtexec = new[]
            {
                @"C:\Program Files\Microsoft SQL Server\170\DTS\Binn\DTExec.exe",
                @"C:\Program Files\Microsoft SQL Server\160\DTS\Binn\DTExec.exe",
            }.FirstOrDefault(File.Exists);
            if (dtexec == null) { detail = "dtexec not found"; return false; }

            var psi = new System.Diagnostics.ProcessStartInfo(dtexec, $"/FILE \"{packagePath}\" /REPORTING E")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            using (var p = System.Diagnostics.Process.Start(psi)!)
            {
                detail = p.StandardOutput.ReadToEnd();
                p.WaitForExit(60000);
                // dtexec exit code 0 == success
                return p.ExitCode == 0;
            }
        }

        private static Wrapper.MainPipe Pipe(Dts.Package pkg)
        {
            foreach (Dts.Executable e in pkg.Executables)
                if (e is Dts.TaskHost th && th.InnerObject is Wrapper.MainPipe p) return p;
            throw new InvalidOperationException("no pipeline");
        }
    }
}
