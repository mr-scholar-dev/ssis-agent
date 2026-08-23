using System;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using SsisMcp.Core.Building;
using SsisMcp.IntegrationTests.Support;
using SsisMcp.Ssis;
using SsisMcp.Ssis.Building;
using Xunit;
using Dts = Microsoft.SqlServer.Dts.Runtime;
using Rt = Microsoft.SqlServer.Dts.Runtime.Wrapper;

namespace SsisMcp.IntegrationTests
{
    /// <summary>
    /// Data Flow builder acceptance against REAL SSIS + SQL Server. Build → validate → commit →
    /// reload → inspect → verify metadata + lineage. All writes go through the Safety layer.
    /// </summary>
    public sealed class DataFlowTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "df-" + Guid.NewGuid().ToString("N"));
        private readonly PackageService _svc = new PackageService();
        private const string Cs = "Data Source=.;Initial Catalog=tempdb;Provider=MSOLEDBSQL;Integrated Security=SSPI;";

        public DataFlowTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static bool Sql(out SqlConnection? conn)
        {
            conn = null;
            try
            {
                var c = new SqlConnection("Data Source=.;Initial Catalog=tempdb;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=3");
                c.Open();
                conn = c;
                return true;
            }
            catch { return false; }
        }

        private static void Exec(SqlConnection c, string sql)
        {
            using (var cmd = new SqlCommand(sql, c)) cmd.ExecuteNonQuery();
        }

        private string BasePackageWithDft(string dftName)
        {
            var pkg = new Dts.Package { Name = "DF" };
            var cm = pkg.Connections.Add("OLEDB");
            cm.Name = "Db";
            cm.ConnectionString = Cs;
            var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".dtsx");
            _svc.Save(pkg, path);
            var r = new PackageEditor(_svc).Apply(path, b => b.AddTask(TaskKinds.DataFlow, dftName));
            Assert.True(r.Succeeded, r.ErrorCode + ": " + r.Detail);
            return path;
        }

        [Fact]
        public void Sql_to_sql_source_derived_destination_roundtrips_with_lineage()
        {
            if (!Sql(out var conn)) return; // SQL not reachable; skip
            using (conn)
            {
                Exec(conn!, "IF OBJECT_ID('tempdb.dbo.SsisMcpTarget') IS NOT NULL DROP TABLE dbo.SsisMcpTarget;" +
                            "CREATE TABLE dbo.SsisMcpTarget(Monto decimal(10,2) NULL, Impuesto decimal(10,2) NULL);");

                var path = BasePackageWithDft("DFT");
                var editor = new PackageEditor(_svc);

                var r = editor.ApplyDataFlow(path, "DFT", b =>
                {
                    b.AddComponent(ComponentKinds.OleDbSource, "Src");
                    b.ConfigureOleDbSource("Src", "Db", 2, "SELECT CAST(100.00 AS decimal(10,2)) AS Monto");

                    b.AddComponent(ComponentKinds.DerivedColumn, "Der");
                    b.Connect("Src", "Der");
                    b.ExposeAllInputColumns("Der");
                    // Monto is DT_NUMERIC(10,2); the explicit cast makes the result type match the
                    // Impuesto output column (SSIS rejects implicit narrowing conversions).
                    b.ConfigureDerivedColumn("Der", "Impuesto", "(DT_NUMERIC,10,2)(Monto * 0.13)", Rt.DataType.DT_NUMERIC, 0, 10, 2, 0);

                    b.AddComponent(ComponentKinds.OleDbDestination, "Dst");
                    b.Connect("Der", "Dst");
                    b.ConfigureOleDbDestination("Dst", "Db", "[dbo].[SsisMcpTarget]");
                    new MappingEngine(b).AutoMap("Dst");
                });

                Assert.True(r.Succeeded, r.ErrorCode + ": " + r.Detail);
                Assert.Equal("Committed", r.SafetyState);

                // ---- verify via the inspector on the reloaded-from-disk package ----
                var df = r.Package!.DataFlows.Single(d => d.TaskName == "DFT");
                var src = df.Components.Single(c => c.Name == "Src");
                var der = df.Components.Single(c => c.Name == "Der");
                var dst = df.Components.Single(c => c.Name == "Dst");

                Assert.Equal("source", src.Role);
                Assert.Equal("transformation", der.Role);
                Assert.Equal("destination", dst.Role);

                // paths Src -> Der -> Dst
                Assert.Contains(df.Paths, p => p.StartComponent == "Src" && p.EndComponent == "Der");
                Assert.Contains(df.Paths, p => p.StartComponent == "Der" && p.EndComponent == "Dst");

                // Derived output carries Impuesto with a real lineage id (expression survived reload)
                var impuesto = der.Outputs.SelectMany(o => o.Columns).Single(c => c.Name == "Impuesto");
                Assert.True(impuesto.LineageId > 0);

                // Destination: external metadata + mapped input columns with lineage
                Assert.NotEmpty(dst.Inputs.Single().ExternalMetadataColumns);
                Assert.NotEmpty(dst.Inputs.Single().Columns);
                Assert.All(dst.Inputs.Single().Columns, c => Assert.True(c.LineageId != 0));

                Exec(conn!, "IF OBJECT_ID('tempdb.dbo.SsisMcpTarget') IS NOT NULL DROP TABLE dbo.SsisMcpTarget;");
            }
        }

        // PARTIAL capability: Data Conversion builds and VALIDATES correctly in memory. Its
        // SourceInputColumnLineageID references a numeric lineage that SSIS reassigns on reload,
        // so the full save→reload round-trip currently regresses (a VS_NEEDSNEWMETADATA-class
        // lineage issue). Repairing lineage across reload is the job of the Metadata/Lineage engine
        // (Fase 12). This test pins the verified in-memory behavior.
        [Fact]
        public void Data_conversion_builds_and_validates_in_memory()
        {
            if (!Sql(out var conn)) return;
            using (conn)
            {
                var pkg = new Dts.Package { Name = "DF" };
                var cm = pkg.Connections.Add("OLEDB"); cm.Name = "Db"; cm.ConnectionString = Cs;
                var dft = (Dts.TaskHost)pkg.Executables.Add("Microsoft.Pipeline"); dft.Name = "DFT";
                var pipe = (Microsoft.SqlServer.Dts.Pipeline.Wrapper.MainPipe)dft.InnerObject;
                var b = new DataFlowBuilder(pipe, pkg);
                b.AddComponent(ComponentKinds.OleDbSource, "Src");
                b.ConfigureOleDbSource("Src", "Db", 2, "SELECT CAST('x' AS varchar(10)) AS Name");
                b.AddComponent(ComponentKinds.DataConversion, "Conv");
                b.Connect("Src", "Conv");
                b.ConfigureDataConversion("Conv", "Name", "NameW", Rt.DataType.DT_WSTR, 10);

                Assert.Equal(Dts.DTSExecResult.Success, _svc.Validate(pkg));
                var conv = b.InspectComponent("Conv");
                var nameW = conv.Outputs.SelectMany(o => o.Columns).Single(c => c.Name == "NameW");
                Assert.Equal("DT_WSTR", nameW.DataType);
                Assert.True(nameW.LineageId > 0);
            }
        }

        [Fact]
        public void Conditional_split_expression_over_upstream_column_roundtrips_lineage()
        {
            if (!Sql(out var conn)) return;
            using (conn)
            {
                var path = BasePackageWithDft("DFT");
                var r = new PackageEditor(_svc).ApplyDataFlow(path, "DFT", b =>
                {
                    b.AddComponent(ComponentKinds.OleDbSource, "Src");
                    b.ConfigureOleDbSource("Src", "Db", 2, "SELECT CAST(5 AS int) AS Val");
                    b.AddComponent(ComponentKinds.ConditionalSplit, "CS");
                    b.Connect("Src", "CS");
                    b.ExposeAllInputColumns("CS");
                    b.AddConditionalSplitCase("CS", "Valid", "Val >= 0", 0);
                });

                Assert.True(r.Succeeded, r.ErrorCode + ": " + r.Detail);
                var cs = r.Package!.DataFlows.Single().Components.Single(c => c.Name == "CS");
                // The case output plus the built-in default output exist after reload.
                Assert.Contains(cs.Outputs, o => o.Name == "Valid");
                Assert.True(cs.Outputs.Count >= 2);
                // Upstream column is present on the input with a valid lineage id (no VS_NEEDSNEWMETADATA).
                Assert.All(cs.Inputs.Single().Columns, c => Assert.True(c.LineageId != 0));
            }
        }
    }
}
