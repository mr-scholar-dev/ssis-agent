using System;
using System.Data.SqlClient;
using System.IO;
using SsisMcp.Core.Building;
using SsisMcp.Ssis;
using SsisMcp.Ssis.Building;
using Xunit;
using Dts = Microsoft.SqlServer.Dts.Runtime;
using Rt = Microsoft.SqlServer.Dts.Runtime.Wrapper;

namespace SsisMcp.IntegrationTests
{
    /// <summary>
    /// Regression for the multi-column Data Conversion lineage rebind. A Data Conversion that converts
    /// TWO+ input columns carries a SourceInputColumnLineageID per output column; after the
    /// save→reload cycle those go stale. The per-column "unique input" test cannot disambiguate, so the
    /// pipeline used to fail validation with VS_ISBROKEN ("Cannot find input column with lineage ID ...").
    /// The handler now rebinds POSITIONALLY (output[i] ⇄ input[i]) for builder-produced pipelines, which
    /// is exact. This pins that: ApplyDataFlow must build + validate + reload + repair successfully.
    /// Surfaced by the Fase 28 IntegracionPractica benchmark (every DFT uses multi-column conversion).
    /// </summary>
    public sealed class MultiColumnDataConversionTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcdc-" + Guid.NewGuid().ToString("N"));
        private readonly PackageService _svc = new PackageService();

        public MultiColumnDataConversionTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static bool SqlUp()
        {
            try { using (var c = new SqlConnection("Data Source=.;Initial Catalog=tempdb;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=3")) { c.Open(); } return true; }
            catch { return false; }
        }
        private static void Exec(string sql) { using (var c = new SqlConnection("Data Source=.;Initial Catalog=tempdb;Integrated Security=true;TrustServerCertificate=true")) { c.Open(); using (var cmd = new SqlCommand(sql, c)) cmd.ExecuteNonQuery(); } }

        [Fact]
        public void Data_conversion_with_multiple_columns_survives_reload_and_validates()
        {
            if (!SqlUp()) return;
            Exec("IF OBJECT_ID('tempdb.dbo.McSrc') IS NOT NULL DROP TABLE dbo.McSrc; IF OBJECT_ID('tempdb.dbo.McDst') IS NOT NULL DROP TABLE dbo.McDst;" +
                 "CREATE TABLE dbo.McSrc(a nvarchar(50), b nvarchar(50), c int);" +
                 "INSERT dbo.McSrc VALUES(N'x', N'y', 7);" +
                 "CREATE TABLE dbo.McDst(a varchar(50) NULL, b varchar(20) NULL, c int NULL);");

            var pkg = new Dts.Package { Name = "Mc" };
            ConnectionFactory.AddSqlOleDb(pkg, "Db", ".", "tempdb");
            var path = Path.Combine(_dir, "mc.dtsx");
            _svc.Save(pkg, path);
            Assert.True(new PackageEditor(_svc).Apply(path, b => b.AddTask(TaskKinds.DataFlow, "DFT")).Succeeded);

            var r = new PackageEditor(_svc).ApplyDataFlow(path, "DFT", b =>
            {
                b.AddComponent(ComponentKinds.OleDbSource, "Src");
                b.ConfigureOleDbSource("Src", "Db", 2, "SELECT a,b,c FROM dbo.McSrc");
                b.AddComponent(ComponentKinds.DataConversion, "Conv"); b.Connect("Src", "Conv");
                b.ConfigureDataConversion("Conv", "a", "ca", Rt.DataType.DT_STR, 50, 0, 0, 1252);
                b.ConfigureDataConversion("Conv", "b", "cb", Rt.DataType.DT_STR, 20, 0, 0, 1252);   // 2nd converted column
                b.AddComponent(ComponentKinds.OleDbDestination, "Dst"); b.Connect("Conv", "Dst");
                b.ConfigureOleDbDestination("Dst", "Db", "[dbo].[McDst]");
                var m = new MappingEngine(b);
                m.SetMapping("Dst", "ca", "a");
                m.SetMapping("Dst", "cb", "b");
                m.SetMapping("Dst", "c", "c");
            });

            // Before the positional-rebind fix this failed with ValidationFailed (VS_ISBROKEN).
            Assert.True(r.Succeeded, r.ErrorCode + ": " + r.Detail);
            Assert.NotNull(r.LineageRepair);
            Assert.Contains(r.LineageRepair!.Actions, a => a.Applied && a.Component == "Conv");

            Exec("IF OBJECT_ID('tempdb.dbo.McSrc') IS NOT NULL DROP TABLE dbo.McSrc; IF OBJECT_ID('tempdb.dbo.McDst') IS NOT NULL DROP TABLE dbo.McDst;");
        }
    }
}
