using System;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using SsisMcp.Core.Building;
using SsisMcp.Core.Lineage;
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
    /// Fase 12 — Metadata &amp; Lineage engine. Reproduces the Data Conversion reload bug and proves
    /// the generic engine detects + safely rebinds the stale reference, surviving a double reload.
    /// </summary>
    public sealed class LineageEngineTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "lin-" + Guid.NewGuid().ToString("N"));
        private readonly PackageService _svc = new PackageService();
        private const string Cs = "Data Source=.;Initial Catalog=tempdb;Provider=MSOLEDBSQL;Integrated Security=SSPI;";

        public LineageEngineTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static bool Sql(out SqlConnection? c)
        {
            c = null;
            try { var x = new SqlConnection("Data Source=.;Initial Catalog=tempdb;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=3"); x.Open(); c = x; return true; }
            catch { return false; }
        }

        private Dts.Package BuildRawConversionChain(string table)
        {
            var pkg = new Dts.Package { Name = "DF" };
            var cm = pkg.Connections.Add("OLEDB"); cm.Name = "Db"; cm.ConnectionString = Cs;
            var dft = (Dts.TaskHost)pkg.Executables.Add("Microsoft.Pipeline"); dft.Name = "DFT";
            var pipe = (Wrapper.MainPipe)dft.InnerObject;
            var b = new DataFlowBuilder(pipe, pkg);
            b.AddComponent(ComponentKinds.OleDbSource, "Src");
            b.ConfigureOleDbSource("Src", "Db", 2, "SELECT CAST('x' AS varchar(10)) AS Name");
            b.AddComponent(ComponentKinds.DataConversion, "Conv");
            b.Connect("Src", "Conv");
            b.ConfigureDataConversion("Conv", "Name", "NameW", Rt.DataType.DT_WSTR, 10);
            b.AddComponent(ComponentKinds.OleDbDestination, "Dst");
            b.Connect("Conv", "Dst");
            b.ConfigureOleDbDestination("Dst", "Db", table);
            new MappingEngine(b).SetMapping("Dst", "NameW", "NameW");
            return pkg;
        }

        [Fact]
        public void Reproduces_stale_lineage_then_engine_repairs_across_double_reload()
        {
            if (!Sql(out var conn)) return;
            using (conn)
            {
                Exec(conn!, "IF OBJECT_ID('tempdb.dbo.SsisMcpLin') IS NOT NULL DROP TABLE dbo.SsisMcpLin;" +
                            "CREATE TABLE dbo.SsisMcpLin(NameW nvarchar(10) NULL);");
                var path = Path.Combine(_dir, "raw.dtsx");

                // build (valid in memory) then save + reload → lineage goes stale (the known bug)
                _svc.Save(BuildRawConversionChain("[dbo].[SsisMcpLin]"), path);
                var reloaded = _svc.Load(path);
                var pipe = Pipe(reloaded);
                var engine = new MetadataLineageEngine();

                var before = engine.Validate(pipe);
                Assert.False(before.IsValid);                    // reproduces "Cannot find input column with lineage ID N"
                Assert.Contains(before.Stale, s => s.Component == "Conv");

                // repair by stable identity (unique input column ⇒ Exact) and save
                var report = engine.Repair(pipe, RepairMode.SafeRepair, 3);
                Assert.True(report.FinalValid);
                Assert.Contains(report.Actions, a => a.Applied && a.Confidence == RepairConfidence.Exact);
                _svc.Save(reloaded, path);

                // DOUBLE reload: lineage stays valid AND SSIS package validation passes
                var again = _svc.Load(path);
                Assert.True(engine.Validate(Pipe(again)).IsValid);
                Assert.Equal(Dts.DTSExecResult.Success, _svc.Validate(again));

                Exec(conn!, "IF OBJECT_ID('tempdb.dbo.SsisMcpLin') IS NOT NULL DROP TABLE dbo.SsisMcpLin;");
            }
        }

        [Fact]
        public void PackageEditor_dataflow_autorepairs_conversion_chain_end_to_end()
        {
            if (!Sql(out var conn)) return;
            using (conn)
            {
                Exec(conn!, "IF OBJECT_ID('tempdb.dbo.SsisMcpLin2') IS NOT NULL DROP TABLE dbo.SsisMcpLin2;" +
                            "CREATE TABLE dbo.SsisMcpLin2(NameW nvarchar(10) NULL);");
                var pkg = new Dts.Package { Name = "DF" };
                var cm = pkg.Connections.Add("OLEDB"); cm.Name = "Db"; cm.ConnectionString = Cs;
                var path = Path.Combine(_dir, "ed.dtsx");
                _svc.Save(pkg, path);
                Assert.True(new PackageEditor(_svc).Apply(path, b => b.AddTask(TaskKinds.DataFlow, "DFT")).Succeeded);

                var r = new PackageEditor(_svc).ApplyDataFlow(path, "DFT", b =>
                {
                    b.AddComponent(ComponentKinds.OleDbSource, "Src");
                    b.ConfigureOleDbSource("Src", "Db", 2, "SELECT CAST('x' AS varchar(10)) AS Name");
                    b.AddComponent(ComponentKinds.DataConversion, "Conv");
                    b.Connect("Src", "Conv");
                    b.ConfigureDataConversion("Conv", "Name", "NameW", Rt.DataType.DT_WSTR, 10);
                    b.AddComponent(ComponentKinds.OleDbDestination, "Dst");
                    b.Connect("Conv", "Dst");
                    b.ConfigureOleDbDestination("Dst", "Db", "[dbo].[SsisMcpLin2]");
                    new MappingEngine(b).SetMapping("Dst", "NameW", "NameW");
                });

                Assert.True(r.Succeeded, r.ErrorCode + ": " + r.Detail);           // committed AND validated on reload
                Assert.NotNull(r.LineageRepair);
                Assert.True(r.LineageRepair!.FinalValid);
                // a real rebind happened (the engine actually did work)
                Assert.Contains(r.LineageRepair!.Actions, a => a.Applied);

                // independent double reload from disk validates cleanly
                var final = _svc.Load(path);
                Assert.True(new MetadataLineageEngine().Validate(Pipe(final)).IsValid);
                Assert.Equal(Dts.DTSExecResult.Success, _svc.Validate(final));

                Exec(conn!, "IF OBJECT_ID('tempdb.dbo.SsisMcpLin2') IS NOT NULL DROP TABLE dbo.SsisMcpLin2;");
            }
        }

        [Fact]
        public void DiagnoseOnly_reports_but_does_not_mutate()
        {
            if (!Sql(out var conn)) return;
            using (conn)
            {
                Exec(conn!, "IF OBJECT_ID('tempdb.dbo.SsisMcpLin3') IS NOT NULL DROP TABLE dbo.SsisMcpLin3;" +
                            "CREATE TABLE dbo.SsisMcpLin3(NameW nvarchar(10) NULL);");
                var path = Path.Combine(_dir, "diag.dtsx");
                _svc.Save(BuildRawConversionChain("[dbo].[SsisMcpLin3]"), path);
                var reloaded = _svc.Load(path);
                var engine = new MetadataLineageEngine();

                var report = engine.Repair(Pipe(reloaded), RepairMode.DiagnoseOnly, 3);
                Assert.False(report.FinalValid);
                Assert.True(report.ManualInterventionRequired);
                Assert.DoesNotContain(report.Actions, a => a.Applied);   // nothing mutated
                Assert.False(engine.Validate(Pipe(reloaded)).IsValid);   // still stale

                Exec(conn!, "IF OBJECT_ID('tempdb.dbo.SsisMcpLin3') IS NOT NULL DROP TABLE dbo.SsisMcpLin3;");
            }
        }

        [Fact]
        public void Ambiguous_multiple_conversions_are_not_auto_repaired()
        {
            if (!Sql(out var conn)) return;
            using (conn)
            {
                var pkg = new Dts.Package { Name = "DF" };
                var cm = pkg.Connections.Add("OLEDB"); cm.Name = "Db"; cm.ConnectionString = Cs;
                var dft = (Dts.TaskHost)pkg.Executables.Add("Microsoft.Pipeline"); dft.Name = "DFT";
                var pipe = (Wrapper.MainPipe)dft.InnerObject;
                var b = new DataFlowBuilder(pipe, pkg);
                b.AddComponent(ComponentKinds.OleDbSource, "Src");
                b.ConfigureOleDbSource("Src", "Db", 2, "SELECT CAST('a' AS varchar(10)) AS A, CAST('b' AS varchar(10)) AS B");
                b.AddComponent(ComponentKinds.DataConversion, "Conv");
                b.Connect("Src", "Conv");
                b.ConfigureDataConversion("Conv", "A", "AW", Rt.DataType.DT_WSTR, 10);
                b.ConfigureDataConversion("Conv", "B", "BW", Rt.DataType.DT_WSTR, 10);

                var path = Path.Combine(_dir, "amb.dtsx");
                _svc.Save(pkg, path);
                var reloaded = _svc.Load(path);
                var engine = new MetadataLineageEngine();

                var report = engine.Repair(Pipe(reloaded), RepairMode.SafeRepair, 3);
                // Two input columns ⇒ cannot disambiguate ⇒ ambiguous, not applied, manual intervention.
                Assert.Contains(report.Actions, a => a.Confidence == RepairConfidence.Ambiguous && !a.Applied);
                Assert.True(report.ManualInterventionRequired);
            }
        }

        private static Wrapper.MainPipe Pipe(Dts.Package pkg)
        {
            foreach (Dts.Executable e in pkg.Executables)
                if (e is Dts.TaskHost th && th.InnerObject is Wrapper.MainPipe p) return p;
            throw new InvalidOperationException("no pipeline");
        }

        private static void Exec(SqlConnection c, string sql) { using (var cmd = new SqlCommand(sql, c)) cmd.ExecuteNonQuery(); }
    }
}
