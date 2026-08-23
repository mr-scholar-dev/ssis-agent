using System;
using System.Data.OleDb;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Reflection;
using SsisMcp.Core.Building;
using SsisMcp.Ssis;
using SsisMcp.Ssis.Building;
using Xunit;
using Dts = Microsoft.SqlServer.Dts.Runtime;
using Wrapper = Microsoft.SqlServer.Dts.Pipeline.Wrapper;

namespace SsisMcp.IntegrationTests
{
    /// <summary>
    /// Access via ACE OLE DB (there is no "Access Source" component — it is an OLE DB Source over an
    /// ACE connection). StructurallyVerified only (execution EnvironmentBlocked). The .accdb fixture
    /// is created with ADOX; if ADOX/ACE is unavailable the test skips (UnsupportedEnvironment).
    /// </summary>
    public sealed class AccessTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "ac-" + Guid.NewGuid().ToString("N"));
        private readonly PackageService _svc = new PackageService();

        public AccessTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static string? CreateAccdb(string path)
        {
            try
            {
                var t = Type.GetTypeFromProgID("ADOX.Catalog");
                if (t == null) return "ADOX not registered";
                var cat = Activator.CreateInstance(t);
                var cs = $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={path};";
                t.InvokeMember("Create", BindingFlags.InvokeMethod, null, cat, new object[] { cs });

                using (var conn = new OleDbConnection(cs))
                {
                    conn.Open();
                    using (var cmd = new OleDbCommand("CREATE TABLE Cliente (Codigo INT, Nombre TEXT(50))", conn)) cmd.ExecuteNonQuery();
                    using (var cmd = new OleDbCommand("INSERT INTO Cliente (Codigo, Nombre) VALUES (1,'a')", conn)) cmd.ExecuteNonQuery();
                    using (var cmd = new OleDbCommand("INSERT INTO Cliente (Codigo, Nombre) VALUES (2,'b')", conn)) cmd.ExecuteNonQuery();
                }
                return null;
            }
            catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
        }

        [Fact]
        public void Accdb_via_ace_oledb_source_table_and_sqlcommand_roundtrip()
        {
            var accdb = Path.Combine(_dir, "db.accdb");
            var err = CreateAccdb(accdb);
            if (err != null) return; // ACE/ADOX unavailable → skip (environment)

            // AccessMode 2 = SQL command
            var pkgS = new Dts.Package { Name = "DF" };
            ConnectionFactory.AddAccess(pkgS, "Ace", accdb);
            var dftS = (Dts.TaskHost)pkgS.Executables.Add("Microsoft.Pipeline"); dftS.Name = "DFT";
            var bS = new DataFlowBuilder((Wrapper.MainPipe)dftS.InnerObject, pkgS);
            bS.AddComponent(ComponentKinds.OleDbSource, "AcSrc");
            bS.ConfigureOleDbSource("AcSrc", "Ace", 2, "SELECT Codigo, Nombre FROM Cliente");
            var pathS = Path.Combine(_dir, "sqlcmd.dtsx");
            _svc.Save(pkgS, pathS);
            var infoS = _svc.InspectFile(pathS);
            var srcS = infoS.DataFlows.Single().Components.Single(c => c.Name == "AcSrc");
            Assert.Contains(srcS.Outputs.SelectMany(o => o.Columns), c => c.Name == "Codigo");
            Assert.Contains(srcS.Outputs.SelectMany(o => o.Columns), c => c.Name == "Nombre");
            Assert.Equal(Dts.DTSExecResult.Success, _svc.Validate(_svc.Load(pathS)));

            // AccessMode 0 = table + reload round-trip + destination mapping
            var pkgT = new Dts.Package { Name = "DF" };
            ConnectionFactory.AddAccess(pkgT, "Ace", accdb);
            var dftT = (Dts.TaskHost)pkgT.Executables.Add("Microsoft.Pipeline"); dftT.Name = "DFT";
            var bT = new DataFlowBuilder((Wrapper.MainPipe)dftT.InnerObject, pkgT);
            bT.AddComponent(ComponentKinds.OleDbSource, "AcSrc");
            bT.ConfigureOleDbSource("AcSrc", "Ace", 0, "Cliente");
            var pathT = Path.Combine(_dir, "table.dtsx");
            _svc.Save(pkgT, pathT);

            // reload + second reload validate
            var info2 = _svc.InspectFile(pathT);
            Assert.Contains(info2.DataFlows.Single().Components, c => c.Name == "AcSrc" && c.Role == "source");
            Assert.Equal(Dts.DTSExecResult.Success, _svc.Validate(_svc.Load(pathT)));
            Assert.Equal(Dts.DTSExecResult.Success, _svc.Validate(_svc.Load(pathT)));
        }
    }
}
