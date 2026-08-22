using System;
using System.IO;
using System.Linq;
using SsisMcp.IntegrationTests.Support;
using SsisMcp.Ssis;
using Xunit;
using Dts = Microsoft.SqlServer.Dts.Runtime;

namespace SsisMcp.IntegrationTests
{
    /// <summary>Inspection tests against REAL SSIS packages produced by the fixture builder.</summary>
    public sealed class InspectionTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "insp-" + Guid.NewGuid().ToString("N"));
        private readonly PackageService _svc = new PackageService();

        public InspectionTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        [Fact]
        public void ControlFlow_reports_tasks_and_precedence_with_value_and_evalop()
        {
            var path = Path.Combine(_dir, "cf.dtsx");
            _svc.Save(FixtureBuilder.BuildControlFlowWithPrecedence(), path);

            var info = _svc.InspectFile(path);

            Assert.Contains(info.Executables, e => e.Name == "SqlBorrar");
            Assert.Contains(info.Executables, e => e.Name == "SqlCargar");
            Assert.Contains(info.Executables, e => e.Name == "SqlBorrar" && e.ConnectionManagers.Contains("Origen"));

            var pc = Assert.Single(info.PrecedenceConstraints);
            Assert.Equal("SqlBorrar", pc.From);
            Assert.Equal("SqlCargar", pc.To);
            Assert.Equal("Completion", pc.Value);
            Assert.Equal("Constraint", pc.EvalOperation);

            Assert.NotNull(info.PackageFormatVersion);
        }

        [Fact]
        public void DataFlow_reports_components_columns_lineage_paths_and_connections()
        {
            if (!LocalSqlAvailable())
                return; // SQL not reachable on this host; skip (real fixture needs a live instance)

            var path = Path.Combine(_dir, "df.dtsx");
            _svc.Save(FixtureBuilder.BuildDataFlowWithOleDbSource(), path);

            var info = _svc.InspectFile(path);

            var df = Assert.Single(info.DataFlows);
            Assert.Equal("DFT_Load", df.TaskName);

            var src = df.Components.Single(c => c.Name == "OLEDB_Src");
            Assert.Equal("source", src.Role);
            Assert.Contains("SrcDb", src.ConnectionManagers);

            var srcOutput = src.Outputs.First(o => !o.IsErrorOut);
            Assert.Contains(srcOutput.Columns, c => c.Name == "name");
            Assert.All(srcOutput.Columns.Where(c => c.Name == "name" || c.Name == "object_id"),
                c => Assert.True(c.LineageId > 0)); // real lineage ids assigned
            Assert.NotEmpty(srcOutput.ExternalMetadataColumns); // schema image present

            var derived = df.Components.Single(c => c.Name == "Derived");
            Assert.Equal("transformation", derived.Role);
            Assert.Single(derived.Inputs); // the input is inspected structurally
            // Input columns reference upstream lineage; when the builder mapped usage they appear here.
            Assert.All(derived.Inputs.Single().Columns, c => Assert.True(c.LineageId != 0));

            var p = Assert.Single(df.Paths);
            Assert.Equal("OLEDB_Src", p.StartComponent);
            Assert.Equal("Derived", p.EndComponent);
        }

        private static bool LocalSqlAvailable()
        {
            try
            {
                var csb = new System.Data.SqlClient.SqlConnectionStringBuilder
                {
                    DataSource = ".", InitialCatalog = "master", IntegratedSecurity = true,
                    ConnectTimeout = 3, TrustServerCertificate = true
                };
                using (var c = new System.Data.SqlClient.SqlConnection(csb.ConnectionString)) { c.Open(); }
                return true;
            }
            catch { return false; }
        }
    }
}
