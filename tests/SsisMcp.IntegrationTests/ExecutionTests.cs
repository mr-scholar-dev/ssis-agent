using System;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using SsisMcp.Core.Building;
using SsisMcp.Core.Execution;
using SsisMcp.Ssis;
using SsisMcp.Ssis.Building;
using SsisMcp.Ssis.Execution;
using Xunit;
using Dts = Microsoft.SqlServer.Dts.Runtime;
using Rt = Microsoft.SqlServer.Dts.Runtime.Wrapper;

namespace SsisMcp.IntegrationTests
{
    /// <summary>
    /// ExecutionVerified via a Microsoft-signed SSIS host (SsdtDebugExecutionHost). On this machine
    /// signed hosts are currently blocked (SQL dtexec license gate / SSDT dtexec COM 0x80040154), so
    /// execution reports a STRUCTURED EnvironmentBlocked — build/validate still PASS. When a licensed
    /// Integration Services feature is installed, the SAME tests execute and verify destination data
    /// (Derived Impuesto, Lookup Match=2/NoMatch=1). ExecutionVerified is NOT declared while blocked.
    /// </summary>
    public sealed class ExecutionTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "exec-" + Guid.NewGuid().ToString("N"));
        private readonly PackageService _svc = new PackageService();

        public ExecutionTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static bool SqlUp()
        {
            try { using (var c = new SqlConnection("Data Source=.;Initial Catalog=tempdb;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=3")) { c.Open(); } return true; }
            catch { return false; }
        }
        private static void Exec(string sql) { using (var c = new SqlConnection("Data Source=.;Initial Catalog=tempdb;Integrated Security=true;TrustServerCertificate=true")) { c.Open(); using (var cmd = new SqlCommand(sql, c)) cmd.ExecuteNonQuery(); } }

        private string BaseWithDft()
        {
            var pkg = new Dts.Package { Name = "Exec" };
            ConnectionFactory.AddSqlOleDb(pkg, "Db", ".", "tempdb");
            var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".dtsx");
            _svc.Save(pkg, path);
            Assert.True(new PackageEditor(_svc).Apply(path, b => b.AddTask(TaskKinds.DataFlow, "DFT")).Succeeded);
            return path;
        }

        [Fact]
        public void Derived_column_executes_or_reports_blocked_then_verifies_impuesto()
        {
            if (!SqlUp()) return;
            Exec("IF OBJECT_ID('tempdb.dbo.ExdSrc') IS NOT NULL DROP TABLE dbo.ExdSrc; IF OBJECT_ID('tempdb.dbo.ExdDst') IS NOT NULL DROP TABLE dbo.ExdDst;" +
                 "CREATE TABLE dbo.ExdSrc(Monto decimal(10,2)); INSERT dbo.ExdSrc VALUES(100.00);" +
                 "CREATE TABLE dbo.ExdDst(Monto decimal(10,2) NULL, Impuesto decimal(10,2) NULL);");
            var path = BaseWithDft();
            var r = new PackageEditor(_svc).ApplyDataFlow(path, "DFT", b =>
            {
                b.AddComponent(ComponentKinds.OleDbSource, "Src");
                b.ConfigureOleDbSource("Src", "Db", 2, "SELECT Monto FROM dbo.ExdSrc");
                b.AddComponent(ComponentKinds.DerivedColumn, "Der"); b.Connect("Src", "Der"); b.ExposeAllInputColumns("Der");
                b.ConfigureDerivedColumn("Der", "Impuesto", "(DT_NUMERIC,10,2)(Monto * 0.13)", Rt.DataType.DT_NUMERIC, 0, 10, 2, 0);
                b.AddComponent(ComponentKinds.OleDbDestination, "Dst"); b.Connect("Der", "Dst");
                b.ConfigureOleDbDestination("Dst", "Db", "[dbo].[ExdDst]"); new MappingEngine(b).AutoMap("Dst");
            });
            Assert.True(r.Succeeded, r.ErrorCode + ": " + r.Detail);        // build + validate PASS

            var exec = new SsdtDebugExecutionHost().Execute(path);
            AssertExecutedOrBlocked(exec);
            if (exec.Outcome == ExecutionOutcome.Success)
            {
                var v = DestinationDataVerifier.LocalSql();
                Assert.Equal(1, v.RowCount("dbo.ExdDst"));
                Assert.True(v.AssertScalar("SELECT Impuesto FROM dbo.ExdDst WHERE Monto = 100.00", 13.00m)); // 100 * 0.13
            }
            Exec("IF OBJECT_ID('tempdb.dbo.ExdSrc') IS NOT NULL DROP TABLE dbo.ExdSrc; IF OBJECT_ID('tempdb.dbo.ExdDst') IS NOT NULL DROP TABLE dbo.ExdDst;");
        }

        [Fact]
        public void Lookup_executes_or_reports_blocked_then_verifies_match_counts()
        {
            if (!SqlUp()) return;
            Exec(@"IF OBJECT_ID('tempdb.dbo.ExlIn') IS NOT NULL DROP TABLE dbo.ExlIn;
                   IF OBJECT_ID('tempdb.dbo.ExlRef') IS NOT NULL DROP TABLE dbo.ExlRef;
                   IF OBJECT_ID('tempdb.dbo.ExlMatch') IS NOT NULL DROP TABLE dbo.ExlMatch;
                   IF OBJECT_ID('tempdb.dbo.ExlNoMatch') IS NOT NULL DROP TABLE dbo.ExlNoMatch;
                   CREATE TABLE dbo.ExlIn(Codigo int, Nombre nvarchar(50));
                   CREATE TABLE dbo.ExlRef(Codigo int, Tipo nvarchar(20));
                   CREATE TABLE dbo.ExlMatch(Codigo int, Nombre nvarchar(50), TipoCliente nvarchar(20));
                   CREATE TABLE dbo.ExlNoMatch(Codigo int, Nombre nvarchar(50));
                   INSERT dbo.ExlIn VALUES(1,'a'),(2,'b'),(999,'z');
                   INSERT dbo.ExlRef VALUES(1,'Premium'),(2,'Basic');");
            var path = BaseWithDft();
            var r = new PackageEditor(_svc).ApplyDataFlow(path, "DFT", b =>
            {
                b.AddComponent(ComponentKinds.OleDbSource, "Src");
                b.ConfigureOleDbSource("Src", "Db", 2, "SELECT Codigo, Nombre FROM dbo.ExlIn");
                b.AddComponent(ComponentKinds.Lookup, "Lk"); b.Connect("Src", "Lk");
                b.ConfigureLookup("Lk", "Db", "SELECT Codigo, Tipo FROM dbo.ExlRef",
                    new[] { ("Codigo", "Codigo") },
                    new[] { ("Tipo", "TipoCliente", Rt.DataType.DT_WSTR, 20, 0, 0, 0) });
                b.AddComponent(ComponentKinds.OleDbDestination, "MatchDst");
                b.Connect("Lk", "MatchDst", fromOutput: DataFlowBuilder.LookupMatchOutput);
                b.ConfigureOleDbDestination("MatchDst", "Db", "[dbo].[ExlMatch]"); new MappingEngine(b).AutoMap("MatchDst");
                b.AddComponent(ComponentKinds.OleDbDestination, "NoMatchDst");
                b.Connect("Lk", "NoMatchDst", fromOutput: DataFlowBuilder.LookupNoMatchOutput);
                b.ConfigureOleDbDestination("NoMatchDst", "Db", "[dbo].[ExlNoMatch]"); new MappingEngine(b).AutoMap("NoMatchDst");
            });
            Assert.True(r.Succeeded, r.ErrorCode + ": " + r.Detail);

            var exec = new SsdtDebugExecutionHost().Execute(path);
            AssertExecutedOrBlocked(exec);
            if (exec.Outcome == ExecutionOutcome.Success)
            {
                var v = DestinationDataVerifier.LocalSql();
                Assert.Equal(2, v.RowCount("dbo.ExlMatch"));       // Codigo 1,2 matched
                Assert.Equal(1, v.RowCount("dbo.ExlNoMatch"));     // Codigo 999 no-match
                Assert.True(v.AssertScalar("SELECT TipoCliente FROM dbo.ExlMatch WHERE Codigo=1", "Premium"));
            }
            Exec(@"IF OBJECT_ID('tempdb.dbo.ExlIn') IS NOT NULL DROP TABLE dbo.ExlIn; IF OBJECT_ID('tempdb.dbo.ExlRef') IS NOT NULL DROP TABLE dbo.ExlRef;
                   IF OBJECT_ID('tempdb.dbo.ExlMatch') IS NOT NULL DROP TABLE dbo.ExlMatch; IF OBJECT_ID('tempdb.dbo.ExlNoMatch') IS NOT NULL DROP TABLE dbo.ExlNoMatch;");
        }

        private static void AssertExecutedOrBlocked(ExecutionResult exec)
        {
            // Honest: Success (licensed signed host present) OR EnvironmentBlocked with a KNOWN reason.
            if (exec.Outcome == ExecutionOutcome.EnvironmentBlocked)
                Assert.True(exec.Detail != null &&
                    (exec.Detail.Contains("license-gated") || exec.Detail.Contains("0x80040154") || exec.Detail.Contains("no signed dtexec")),
                    "unexpected blocked reason: " + exec.Detail);
            else
                Assert.Equal(ExecutionOutcome.Success, exec.Outcome); // not a silent Failure
        }
    }
}
