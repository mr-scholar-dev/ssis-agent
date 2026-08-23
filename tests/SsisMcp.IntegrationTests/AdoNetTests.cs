using System;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using SsisMcp.Core.Building;
using SsisMcp.Ssis;
using SsisMcp.Ssis.Building;
using Xunit;
using Dts = Microsoft.SqlServer.Dts.Runtime;
using Wrapper = Microsoft.SqlServer.Dts.Pipeline.Wrapper;
using Rt = Microsoft.SqlServer.Dts.Runtime.Wrapper;

namespace SsisMcp.IntegrationTests
{
    /// <summary>
    /// ADO.NET as an OFFICIAL capability: ADO.NET (SqlClient) connection manager + ADO NET
    /// Source/Destination managed components with REAL metadata + mappings. Metadata acquisition is
    /// unblocked by deploying the Microsoft.Data.SqlClient closure to the app dir
    /// (build/AdoNet.SqlClient.targets) + binding redirects (App.config). StructurallyVerified;
    /// standalone execution remains license-gated (EnvironmentBlocked), tracked separately.
    /// </summary>
    public sealed class AdoNetTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "ado-" + Guid.NewGuid().ToString("N"));
        private readonly PackageService _svc = new PackageService();

        public AdoNetTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static bool SqlUp()
        {
            try { using (var c = new SqlConnection("Data Source=.;Initial Catalog=tempdb;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=3")) { c.Open(); } return true; }
            catch { return false; }
        }
        private static void Exec(string sql)
        {
            using (var c = new SqlConnection("Data Source=.;Initial Catalog=tempdb;Integrated Security=true;TrustServerCertificate=true"))
            { c.Open(); using (var cmd = new SqlCommand(sql, c)) cmd.ExecuteNonQuery(); }
        }

        [Fact]
        public void AdoNet_connection_manager_roundtrips()
        {
            var pkg = new Dts.Package { Name = "DF" };
            ConnectionFactory.AddAdoNetSql(pkg, "OrigenAdoNet", ".", "tempdb");
            var path = Path.Combine(_dir, "adocm.dtsx");
            _svc.Save(pkg, path);
            var cm = _svc.InspectFile(path).Connections.Single(c => c.Name == "OrigenAdoNet");
            Assert.Contains("ADO.NET", cm.CreationName);
            Assert.Equal(Dts.DTSExecResult.Success, _svc.Validate(_svc.Load(path)));
        }

        [Fact]
        public void AdoNet_source_acquires_real_metadata_columns()
        {
            if (!SqlUp()) return;
            Exec("IF OBJECT_ID('tempdb.dbo.AdoM') IS NOT NULL DROP TABLE dbo.AdoM; CREATE TABLE dbo.AdoM(Codigo int, Nombre nvarchar(50)); INSERT dbo.AdoM VALUES(1,'a');");
            var path = BaseWithDft();
            var r = new PackageEditor(_svc).ApplyDataFlow(path, "DFT", b =>
            {
                b.AddComponent(ComponentKinds.AdoNetSource, "Src");
                b.ConfigureAdoNetSource("Src", "Ado", 1, "SELECT Codigo, Nombre FROM dbo.AdoM");
            });
            // We don't require the whole package to validate (a lone source may warn), but metadata
            // acquisition must have produced the real columns and survived reload.
            var info = _svc.InspectFile(path);
            var cols = info.DataFlows.Single().Components.Single(c => c.Name == "Src").Outputs.SelectMany(o => o.Columns).ToList();
            Assert.Contains(cols, c => c.Name == "Codigo" && c.DataType == "DT_I4");
            Assert.Contains(cols, c => c.Name == "Nombre" && c.DataType == "DT_WSTR");
            Exec("IF OBJECT_ID('tempdb.dbo.AdoM') IS NOT NULL DROP TABLE dbo.AdoM;");
        }

        [Fact]
        public void AdoNet_source_derived_destination_roundtrips_with_mappings()
        {
            if (!SqlUp()) return;
            Exec("IF OBJECT_ID('tempdb.dbo.AdoSrc') IS NOT NULL DROP TABLE dbo.AdoSrc; IF OBJECT_ID('tempdb.dbo.AdoDst') IS NOT NULL DROP TABLE dbo.AdoDst;" +
                 "CREATE TABLE dbo.AdoSrc(Codigo int, Monto decimal(10,2)); INSERT dbo.AdoSrc VALUES(1,100.00);" +
                 "CREATE TABLE dbo.AdoDst(Codigo int NULL, Impuesto decimal(10,2) NULL);");
            var path = BaseWithDft();
            var r = new PackageEditor(_svc).ApplyDataFlow(path, "DFT", b =>
            {
                b.AddComponent(ComponentKinds.AdoNetSource, "Src");
                b.ConfigureAdoNetSource("Src", "Ado", 1, "SELECT Codigo, Monto FROM dbo.AdoSrc");
                b.AddComponent(ComponentKinds.DerivedColumn, "Der");
                b.Connect("Src", "Der"); b.ExposeAllInputColumns("Der");
                b.ConfigureDerivedColumn("Der", "Impuesto", "(DT_NUMERIC,10,2)(Monto * 0.13)", Rt.DataType.DT_NUMERIC, 0, 10, 2, 0);
                b.AddComponent(ComponentKinds.AdoNetDestination, "Dst");
                b.Connect("Der", "Dst");
                b.ConfigureAdoNetDestination("Dst", "Ado", "dbo.AdoDst");
                new MappingEngine(b).AutoMap("Dst"); // maps Codigo + Impuesto by name
            });

            Assert.True(r.Succeeded, r.ErrorCode + ": " + r.Detail);   // validate + commit on reload
            var df = r.Package!.DataFlows.Single();
            Assert.Equal("source", df.Components.Single(c => c.Name == "Src").Role);
            Assert.Equal("destination", df.Components.Single(c => c.Name == "Dst").Role);
            Assert.Contains(df.Paths, p => p.StartComponent == "Src" && p.EndComponent == "Der");
            Assert.Contains(df.Paths, p => p.StartComponent == "Der" && p.EndComponent == "Dst");
            // destination mapped input columns present after reload
            Assert.NotEmpty(df.Components.Single(c => c.Name == "Dst").Inputs.Single().Columns);
            Assert.Equal(Dts.DTSExecResult.Success, _svc.Validate(_svc.Load(path)));
            Exec("IF OBJECT_ID('tempdb.dbo.AdoSrc') IS NOT NULL DROP TABLE dbo.AdoSrc; IF OBJECT_ID('tempdb.dbo.AdoDst') IS NOT NULL DROP TABLE dbo.AdoDst;");
        }

        [Fact]
        public void AdoNet_source_lookup_destination_roundtrips()
        {
            if (!SqlUp()) return;
            Exec("IF OBJECT_ID('tempdb.dbo.AdoIn') IS NOT NULL DROP TABLE dbo.AdoIn; IF OBJECT_ID('tempdb.dbo.AdoRef') IS NOT NULL DROP TABLE dbo.AdoRef; IF OBJECT_ID('tempdb.dbo.AdoLkOut') IS NOT NULL DROP TABLE dbo.AdoLkOut;" +
                 "CREATE TABLE dbo.AdoIn(Codigo int, Nombre nvarchar(50)); INSERT dbo.AdoIn VALUES(1,'a');" +
                 "CREATE TABLE dbo.AdoRef(Codigo int, Tipo nvarchar(20)); INSERT dbo.AdoRef VALUES(1,'Premium');" +
                 "CREATE TABLE dbo.AdoLkOut(Codigo int NULL, Nombre nvarchar(50) NULL, TipoCliente nvarchar(20) NULL);");
            var path = BaseWithDft(addOleDb: true);
            var r = new PackageEditor(_svc).ApplyDataFlow(path, "DFT", b =>
            {
                b.AddComponent(ComponentKinds.AdoNetSource, "Src");
                b.ConfigureAdoNetSource("Src", "Ado", 1, "SELECT Codigo, Nombre FROM dbo.AdoIn");
                b.AddComponent(ComponentKinds.Lookup, "Lk");
                b.Connect("Src", "Lk");
                b.ConfigureLookup("Lk", "Db", "SELECT Codigo, Tipo FROM dbo.AdoRef",
                    new[] { ("Codigo", "Codigo") },
                    new[] { ("Tipo", "TipoCliente", Rt.DataType.DT_WSTR, 20, 0, 0, 0) });
                b.AddComponent(ComponentKinds.AdoNetDestination, "Dst");
                b.Connect("Lk", "Dst", fromOutput: DataFlowBuilder.LookupMatchOutput);
                b.ConfigureAdoNetDestination("Dst", "Ado", "dbo.AdoLkOut");
                new MappingEngine(b).AutoMap("Dst");
            });

            Assert.True(r.Succeeded, r.ErrorCode + ": " + r.Detail);
            var df = r.Package!.DataFlows.Single();
            Assert.Contains(df.Components, c => c.Name == "Lk");
            Assert.Contains(df.Paths, p => p.StartComponent == "Lk" && p.EndComponent == "Dst");
            Assert.Equal(Dts.DTSExecResult.Success, _svc.Validate(_svc.Load(path)));
            Exec("IF OBJECT_ID('tempdb.dbo.AdoIn') IS NOT NULL DROP TABLE dbo.AdoIn; IF OBJECT_ID('tempdb.dbo.AdoRef') IS NOT NULL DROP TABLE dbo.AdoRef; IF OBJECT_ID('tempdb.dbo.AdoLkOut') IS NOT NULL DROP TABLE dbo.AdoLkOut;");
        }

        private string BaseWithDft(bool addOleDb = false)
        {
            var pkg = new Dts.Package { Name = "DF" };
            ConnectionFactory.AddAdoNetSql(pkg, "Ado", ".", "tempdb");
            if (addOleDb) ConnectionFactory.AddSqlOleDb(pkg, "Db", ".", "tempdb");
            var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".dtsx");
            _svc.Save(pkg, path);
            Assert.True(new PackageEditor(_svc).Apply(path, cb => cb.AddTask(TaskKinds.DataFlow, "DFT")).Succeeded);
            return path;
        }
    }
}
