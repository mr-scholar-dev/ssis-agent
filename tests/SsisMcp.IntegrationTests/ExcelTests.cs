using System;
using System.Data.OleDb;
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
    /// Excel Source/Destination via ACE (x64). StructurallyVerified only — execution is
    /// EnvironmentBlocked on this host. Covers .xlsx and .xls, Excel→DataConversion→OLE DB and
    /// OLE DB→Excel, with save / reload / double reload / lineage / inspector round-trip.
    /// </summary>
    public sealed class ExcelTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "xl-" + Guid.NewGuid().ToString("N"));
        private readonly PackageService _svc = new PackageService();
        private const string SqlCs = "Data Source=.;Initial Catalog=tempdb;Provider=MSOLEDBSQL;Integrated Security=SSPI;";

        public ExcelTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static bool SqlUp()
        {
            try { using (var c = new SqlConnection("Data Source=.;Initial Catalog=tempdb;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=3")) { c.Open(); } return true; }
            catch { return false; }
        }
        private static void Sql(string sql) { using (var c = new SqlConnection("Data Source=.;Initial Catalog=tempdb;Integrated Security=true;TrustServerCertificate=true")) { c.Open(); using (var cmd = new SqlCommand(sql, c)) cmd.ExecuteNonQuery(); } }

        private string CreateExcel(bool xlsx, string sheet, bool withRows)
        {
            var path = Path.Combine(_dir, "data." + (xlsx ? "xlsx" : "xls"));
            var ext = xlsx ? "Excel 12.0 Xml" : "Excel 8.0";
            var cs = $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={path};Extended Properties=\"{ext};HDR=YES\"";
            using (var conn = new OleDbConnection(cs))
            {
                conn.Open();
                using (var cmd = new OleDbCommand($"CREATE TABLE [{sheet}] ([Codigo] INT, [Nombre] VARCHAR(50))", conn)) cmd.ExecuteNonQuery();
                if (withRows)
                {
                    using (var cmd = new OleDbCommand($"INSERT INTO [{sheet}$] ([Codigo],[Nombre]) VALUES (1,'a')", conn)) cmd.ExecuteNonQuery();
                    using (var cmd = new OleDbCommand($"INSERT INTO [{sheet}$] ([Codigo],[Nombre]) VALUES (2,'b')", conn)) cmd.ExecuteNonQuery();
                }
            }
            return path;
        }

        private string BaseWithDft(Action<Dts.Package> addConnections)
        {
            var pkg = new Dts.Package { Name = "DF" };
            addConnections(pkg);
            var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".dtsx");
            _svc.Save(pkg, path);
            Assert.True(new PackageEditor(_svc).Apply(path, b => b.AddTask(TaskKinds.DataFlow, "DFT")).Succeeded);
            return path;
        }

        [Fact]
        public void Xlsx_source_dataconversion_to_oledb_roundtrips()
        {
            if (!SqlUp()) return;
            var xlsx = CreateExcel(xlsx: true, sheet: "Sheet1", withRows: true);
            Sql("IF OBJECT_ID('tempdb.dbo.XlDest') IS NOT NULL DROP TABLE dbo.XlDest; CREATE TABLE dbo.XlDest(CodigoNum int, Nombre nvarchar(255));");

            var path = BaseWithDft(pkg =>
            {
                ConnectionFactory.AddExcel(pkg, "Xl", xlsx, xlsx: true);
                ConnectionFactory.AddSqlOleDb(pkg, "Db", ".", "tempdb");
            });

            var r = new PackageEditor(_svc).ApplyDataFlow(path, "DFT", b =>
            {
                b.AddComponent(ComponentKinds.ExcelSource, "XlSrc");
                b.ConfigureExcelSource("XlSrc", "Xl", "Sheet1$");

                b.AddComponent(ComponentKinds.DataConversion, "Conv");
                b.Connect("XlSrc", "Conv");
                b.ConfigureDataConversion("Conv", "Codigo", "CodigoNum", Rt.DataType.DT_I4);

                b.AddComponent(ComponentKinds.OleDbDestination, "Dst");
                b.Connect("Conv", "Dst");
                b.ConfigureOleDbDestination("Dst", "Db", "[dbo].[XlDest]");
                new MappingEngine(b).AutoMap("Dst"); // maps CodigoNum + Nombre by name
            });

            Assert.True(r.Succeeded, r.ErrorCode + ": " + r.Detail);
            var df = r.Package!.DataFlows.Single();
            Assert.Equal("source", df.Components.Single(c => c.Name == "XlSrc").Role);
            Assert.Contains(df.Paths, p => p.StartComponent == "XlSrc" && p.EndComponent == "Conv");

            // double reload: lineage stays valid (Data Conversion rebound by the engine)
            var reload = _svc.Load(path);
            Assert.True(new MetadataLineageEngine().Validate(Pipe(reload)).IsValid);
            Assert.Equal(Dts.DTSExecResult.Success, _svc.Validate(_svc.Load(path)));
            Sql("DROP TABLE dbo.XlDest;");
        }

        [Fact]
        public void Oledb_source_to_xlsx_destination_roundtrips()
        {
            if (!SqlUp()) return;
            var xlsx = CreateExcel(xlsx: true, sheet: "Report", withRows: false); // sheet defines columns
            Sql("IF OBJECT_ID('tempdb.dbo.XlSrcTbl') IS NOT NULL DROP TABLE dbo.XlSrcTbl; CREATE TABLE dbo.XlSrcTbl(Codigo int, Nombre varchar(50));");

            var path = BaseWithDft(pkg =>
            {
                ConnectionFactory.AddSqlOleDb(pkg, "Db", ".", "tempdb");
                ConnectionFactory.AddExcel(pkg, "Xl", xlsx, xlsx: true);
            });

            var r = new PackageEditor(_svc).ApplyDataFlow(path, "DFT", b =>
            {
                b.AddComponent(ComponentKinds.OleDbSource, "Src");
                // Excel columns are text (DT_WSTR); cast so the report columns map cleanly.
                b.ConfigureOleDbSource("Src", "Db", 2, "SELECT CAST(Codigo AS nvarchar(50)) AS Codigo, CAST(Nombre AS nvarchar(50)) AS Nombre FROM dbo.XlSrcTbl");
                b.AddComponent(ComponentKinds.ExcelDestination, "XlDst");
                b.Connect("Src", "XlDst");
                b.ConfigureExcelDestination("XlDst", "Xl", "Report$");
                new MappingEngine(b).AutoMap("XlDst");
            });

            Assert.True(r.Succeeded, r.ErrorCode + ": " + r.Detail);
            var df = r.Package!.DataFlows.Single();
            Assert.Equal("destination", df.Components.Single(c => c.Name == "XlDst").Role);
            Assert.Equal(Dts.DTSExecResult.Success, _svc.Validate(_svc.Load(path)));
            Sql("DROP TABLE dbo.XlSrcTbl;");
        }

        [Fact]
        public void Xls_source_reads_and_validates()
        {
            var xls = CreateExcel(xlsx: false, sheet: "Sheet1", withRows: true);
            var pkg = new Dts.Package { Name = "DF" };
            ConnectionFactory.AddExcel(pkg, "Xl", xls, xlsx: false);
            var dft = (Dts.TaskHost)pkg.Executables.Add("Microsoft.Pipeline"); dft.Name = "DFT";
            var b = new DataFlowBuilder((Wrapper.MainPipe)dft.InnerObject, pkg);
            b.AddComponent(ComponentKinds.ExcelSource, "XlSrc");
            b.ConfigureExcelSource("XlSrc", "Xl", "Sheet1$");

            var path = Path.Combine(_dir, "xls.dtsx");
            _svc.Save(pkg, path);
            var info = _svc.InspectFile(path);
            var src = info.DataFlows.Single().Components.Single(c => c.Name == "XlSrc");
            Assert.Contains(src.Outputs.SelectMany(o => o.Columns), c => c.Name == "Codigo");
            Assert.Equal(Dts.DTSExecResult.Success, _svc.Validate(_svc.Load(path)));
        }

        private static Wrapper.MainPipe Pipe(Dts.Package pkg)
        {
            foreach (Dts.Executable e in pkg.Executables)
                if (e is Dts.TaskHost th && th.InnerObject is Wrapper.MainPipe p) return p;
            throw new InvalidOperationException("no pipeline");
        }
    }
}
