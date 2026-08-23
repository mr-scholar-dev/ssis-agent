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

namespace SsisMcp.IntegrationTests
{
    /// <summary>
    /// ADO.NET as an official capability: ADO.NET (SqlClient) connection manager + ADO NET
    /// Source/Destination managed components. Connection manager and component STRUCTURE are
    /// StructurallyVerified (round-trip). ADO NET metadata acquisition (ReinitializeMetaData) needs
    /// Microsoft.Data.SqlClient's binding closure from the SSDT host and is EnvironmentBlocked on a
    /// standalone net48 host — surfaced as a STRUCTURED error, not faked. Contrasted with OLE DB.
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

        [Fact]
        public void AdoNet_connection_manager_roundtrips()
        {
            var pkg = new Dts.Package { Name = "DF" };
            ConnectionFactory.AddAdoNetSql(pkg, "OrigenAdoNet", ".", "tempdb");
            var path = Path.Combine(_dir, "adocm.dtsx");
            _svc.Save(pkg, path);

            var info = _svc.InspectFile(path);
            var cm = info.Connections.Single(c => c.Name == "OrigenAdoNet");
            Assert.Contains("ADO.NET", cm.CreationName);                 // ADO.NET family, not OLEDB
            Assert.Equal(Dts.DTSExecResult.Success, _svc.Validate(_svc.Load(path)));
        }

        [Fact]
        public void AdoNet_source_and_destination_components_structure_roundtrips()
        {
            var pkg = new Dts.Package { Name = "DF" };
            ConnectionFactory.AddAdoNetSql(pkg, "OrigenAdoNet", ".", "tempdb");
            var dft = (Dts.TaskHost)pkg.Executables.Add("Microsoft.Pipeline"); dft.Name = "DFT";
            var b = new DataFlowBuilder((Wrapper.MainPipe)dft.InnerObject, pkg);

            // Components are created + ProvideComponentProperties succeed (structure), independent of
            // the SqlClient metadata blocker.
            b.AddComponent(ComponentKinds.AdoNetSource, "AdoSrc");
            b.AddComponent(ComponentKinds.AdoNetDestination, "AdoDst");

            var path = Path.Combine(_dir, "adocomp.dtsx");
            _svc.Save(pkg, path);

            var info = _svc.InspectFile(path);
            var df = info.DataFlows.Single();
            // Both managed adapters survive save/reload (SSIS normalizes the class id to a CLSID,
            // like Lookup) — the structure round-trips even though metadata acquisition is blocked.
            var src = df.Components.Single(c => c.Name == "AdoSrc");
            var dst = df.Components.Single(c => c.Name == "AdoDst");
            Assert.False(string.IsNullOrEmpty(src.ComponentClassId));
            Assert.False(string.IsNullOrEmpty(dst.ComponentClassId));
        }

        [Fact]
        public void AdoNet_source_metadata_is_environment_blocked_structured()
        {
            if (!SqlUp()) return;
            Exec("IF OBJECT_ID('tempdb.dbo.AdoT') IS NOT NULL DROP TABLE dbo.AdoT; CREATE TABLE dbo.AdoT(Codigo int, Nombre nvarchar(50));");
            var pkg = new Dts.Package { Name = "DF" };
            ConnectionFactory.AddAdoNetSql(pkg, "Ado", ".", "tempdb");
            var path = Path.Combine(_dir, "adometa.dtsx");
            _svc.Save(pkg, path);
            Assert.True(new PackageEditor(_svc).Apply(path, cb => cb.AddTask(TaskKinds.DataFlow, "DFT")).Succeeded);

            var r = new PackageEditor(_svc).ApplyDataFlow(path, "DFT", b =>
            {
                b.AddComponent(ComponentKinds.AdoNetSource, "AdoSrc");
                b.ConfigureAdoNetSource("AdoSrc", "Ado", 1, "SELECT Codigo, Nombre FROM dbo.AdoT"); // triggers ReinitializeMetaData
            });

            // Honest, structured outcome — not a crash, not faked.
            Assert.False(r.Succeeded);
            Assert.Equal(nameof(BuilderErrorCode.UnsupportedEnvironment), r.ErrorCode);
            Assert.Contains("Microsoft.Data.SqlClient", r.Detail);
            Exec("IF OBJECT_ID('tempdb.dbo.AdoT') IS NOT NULL DROP TABLE dbo.AdoT;");
        }

        [Fact]
        public void OleDb_metadata_path_still_verified_for_contrast()
        {
            if (!SqlUp()) return;
            // The OLE DB family DOES acquire metadata + map columns on this host: proves the inspector
            // and mapping engine handle both families, and isolates the blocker to ADO NET's SqlClient
            // dependency (not the builder/mapping code).
            var pkg = new Dts.Package { Name = "DF" };
            ConnectionFactory.AddSqlOleDb(pkg, "Db", ".", "tempdb");
            var dft = (Dts.TaskHost)pkg.Executables.Add("Microsoft.Pipeline"); dft.Name = "DFT";
            var b = new DataFlowBuilder((Wrapper.MainPipe)dft.InnerObject, pkg);
            b.AddComponent(ComponentKinds.OleDbSource, "Src");
            b.ConfigureOleDbSource("Src", "Db", 2, "SELECT CAST(1 AS int) AS Codigo, CAST('a' AS nvarchar(50)) AS Nombre");

            var path = Path.Combine(_dir, "oledbmeta.dtsx");
            _svc.Save(pkg, path);
            var src = _svc.InspectFile(path).DataFlows.Single().Components.Single(c => c.Name == "Src");
            Assert.Contains(src.Outputs.SelectMany(o => o.Columns), c => c.Name == "Codigo");
            Assert.Equal(Dts.DTSExecResult.Success, _svc.Validate(_svc.Load(path)));
        }

        private static void Exec(string sql)
        {
            using (var c = new SqlConnection("Data Source=.;Initial Catalog=tempdb;Integrated Security=true;TrustServerCertificate=true"))
            { c.Open(); using (var cmd = new SqlCommand(sql, c)) cmd.ExecuteNonQuery(); }
        }
    }
}
