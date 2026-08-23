using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SsisMcp.Core.Building;
using SsisMcp.Designer;
using SsisMcp.Ssis;
using SsisMcp.Ssis.Building;
using Xunit;
using Dts = Microsoft.SqlServer.Dts.Runtime;

namespace SsisMcp.IntegrationTests
{
    /// <summary>
    /// DesignerLayoutEngine regressions. Layout format is grounded on a real VS 2022-authored golden
    /// (tests/fixtures/golden/vs2022-control-flow.dtsx). Injecting layout must NOT break functional
    /// validation, must lay out top→bottom without overlap, and must preserve existing layout.
    /// Final visual confirmation in the Designer is a manual step (see samples/README.md).
    /// </summary>
    public sealed class DesignerLayoutTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "lay-" + Guid.NewGuid().ToString("N"));
        private readonly PackageService _svc = new PackageService();

        public DesignerLayoutTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private string BuildChain()
        {
            var pkg = new Dts.Package { Name = "P" };
            ConnectionFactory.AddSqlOleDb(pkg, "Vet", ".", "tempdb");
            var path = Path.Combine(_dir, "chain.dtsx");
            _svc.Save(pkg, path);
            var r = new PackageEditor(_svc).Apply(path, b =>
            {
                b.AddTask(TaskKinds.ExecuteSql, "SqlBorrar");
                b.ConfigureExecuteSql("SqlBorrar", "Vet", "SELECT 1;");
                b.AddTask(TaskKinds.DataFlow, "DFTCliente");
                b.AddTask(TaskKinds.DataFlow, "DFTMascota");
                b.Connect("SqlBorrar", "DFTCliente", PrecedenceValue.Success);
                b.Connect("DFTCliente", "DFTMascota", PrecedenceValue.Success);
            });
            Assert.True(r.Succeeded, r.ErrorCode + ": " + r.Detail);
            return path;
        }

        [Fact]
        public void Layout_injects_nodelayouts_topbottom_and_package_stays_valid()
        {
            var path = BuildChain();
            var info = _svc.InspectFile(path);
            var boxes = new ControlFlowLayoutEngine().Apply(path, info, LayoutMode.Relayout);

            // one NodeLayout per executable, positioned top→bottom following the DAG
            Assert.Equal(3, boxes.Count);
            var y = boxes.ToDictionary(b => b.Name, b => b.Y);
            Assert.True(y["SqlBorrar"] < y["DFTCliente"] && y["DFTCliente"] < y["DFTMascota"]); // top→bottom order

            var xml = File.ReadAllText(path);
            Assert.Contains("<DTS:DesignTimeProperties>", xml);
            Assert.Equal(3, Regex.Matches(xml, "<NodeLayout ").Count);

            // functional package is intact after layout injection (round-trip + validate)
            var reinspected = _svc.InspectFile(path);
            Assert.Contains(reinspected.Executables, e => e.Name == "SqlBorrar");
            Assert.Equal(3, reinspected.Executables.Count);
            Assert.Equal(Dts.DTSExecResult.Success, _svc.Validate(_svc.Load(path)));
        }

        [Fact]
        public void Layout_preserves_existing_arrangement_in_add_missing_mode()
        {
            var path = BuildChain();
            var info = _svc.InspectFile(path);
            var engine = new ControlFlowLayoutEngine();
            engine.Apply(path, info, LayoutMode.Relayout);           // establish a layout
            var before = File.ReadAllText(path);

            var added = engine.Apply(path, info, LayoutMode.AddMissing); // everything already positioned
            Assert.Empty(added);                                      // nothing new to place
            Assert.Equal(before, File.ReadAllText(path));             // existing layout untouched
        }

        [Fact]
        public void Golden_has_a_nodelayout_for_every_executable_and_mcp_matches_semantically()
        {
            var golden = Path.Combine(FindRepoRoot(), "tests", "fixtures", "golden", "vs2022-control-flow.dtsx");
            if (!File.Exists(golden)) return; // golden not captured on this host yet

            var goldenIds = NodeIds(File.ReadAllText(golden));
            Assert.Contains("Package\\SqlBorrar", goldenIds);
            foreach (var t in new[] { "DFTTipoCliente", "DFTCliente", "DFTMascota", "DFTEnfermedad" })
                Assert.Contains("Package\\" + t, goldenIds);

            // The MCP produces the SAME set of node ids for the same package (semantic parity — we do
            // NOT compare GUIDs or exact coordinates, which the designer owns).
            var mcpPath = Path.Combine(_dir, "mcp.dtsx");
            File.Copy(golden, mcpPath, overwrite: true);
            var info = _svc.InspectFile(mcpPath);
            new ControlFlowLayoutEngine().Apply(mcpPath, info, LayoutMode.Relayout);
            var mcpIds = NodeIds(File.ReadAllText(mcpPath));
            Assert.Equal(goldenIds.OrderBy(x => x), mcpIds.OrderBy(x => x));
        }

        private static System.Collections.Generic.HashSet<string> NodeIds(string xml)
        {
            var set = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(xml, "<NodeLayout[^>]*Id=\"([^\"]+)\""))
                set.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            return set;
        }

        // ---------------- Data Flow layout ----------------

        private static bool SqlUp()
        {
            try { using (var c = new System.Data.SqlClient.SqlConnection("Data Source=.;Initial Catalog=tempdb;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=3")) { c.Open(); } return true; }
            catch { return false; }
        }

        private string BuildBranchingDataFlow()
        {
            using (var c = new System.Data.SqlClient.SqlConnection("Data Source=.;Initial Catalog=tempdb;Integrated Security=true;TrustServerCertificate=true"))
            { c.Open(); using (var cmd = new System.Data.SqlClient.SqlCommand(
                "IF OBJECT_ID('tempdb.dbo.DlV') IS NOT NULL DROP TABLE dbo.DlV; IF OBJECT_ID('tempdb.dbo.DlD') IS NOT NULL DROP TABLE dbo.DlD;" +
                "CREATE TABLE dbo.DlV(Codigo int NULL, Nombre nvarchar(50) NULL); CREATE TABLE dbo.DlD(Codigo int NULL, Nombre nvarchar(50) NULL);", c)) cmd.ExecuteNonQuery(); }

            var pkg = new Dts.Package { Name = "DF" };
            ConnectionFactory.AddSqlOleDb(pkg, "Db", ".", "tempdb");
            var path = Path.Combine(_dir, "dflow.dtsx");
            _svc.Save(pkg, path);
            Assert.True(new PackageEditor(_svc).Apply(path, cb => cb.AddTask(TaskKinds.DataFlow, "DFT")).Succeeded);
            var r = new PackageEditor(_svc).ApplyDataFlow(path, "DFT", b =>
            {
                b.AddComponent(ComponentKinds.OleDbSource, "Src");
                b.ConfigureOleDbSource("Src", "Db", 2, "SELECT CAST(1 AS int) AS Codigo, CAST('a' AS nvarchar(50)) AS Nombre");
                b.AddComponent(ComponentKinds.ConditionalSplit, "CS");
                b.Connect("Src", "CS"); b.ExposeAllInputColumns("CS");
                b.AddConditionalSplitCase("CS", "Valid", "Codigo >= 0", 0);
                b.AddComponent(ComponentKinds.OleDbDestination, "DstValid");
                b.Connect("CS", "DstValid", fromOutput: "Valid");
                b.ConfigureOleDbDestination("DstValid", "Db", "[dbo].[DlV]");
                new MappingEngine(b).AutoMap("DstValid");
                b.AddComponent(ComponentKinds.OleDbDestination, "DstDefault");
                b.Connect("CS", "DstDefault", fromOutput: "Conditional Split Default Output");
                b.ConfigureOleDbDestination("DstDefault", "Db", "[dbo].[DlD]");
                new MappingEngine(b).AutoMap("DstDefault");
            });
            Assert.True(r.Succeeded, r.ErrorCode + ": " + r.Detail);
            return path;
        }

        [Fact]
        public void DataFlow_layout_positions_components_left_to_right_with_branches_and_stays_valid()
        {
            if (!SqlUp()) return;
            var path = BuildBranchingDataFlow();
            var info = _svc.InspectFile(path);
            var boxes = new DataFlowLayoutEngine().Apply(path, info, LayoutMode.Relayout);

            var by = boxes.ToDictionary(b => b.Name, b => b);
            Assert.Equal(4, boxes.Count);
            Assert.True(by["Src"].X < by["CS"].X);                       // left → right by depth
            Assert.True(by["CS"].X < by["DstValid"].X);
            Assert.Equal(by["DstValid"].X, by["DstDefault"].X);          // same layer
            Assert.NotEqual(by["DstValid"].Y, by["DstDefault"].Y);       // branches separated on Y (no overlap)

            var xml = File.ReadAllText(path);
            Assert.Contains("<TaskHost design-time-name=\"Package\\DFT\">", xml);
            Assert.Equal(4, ComponentNodeIds(xml).Count); // 4 component NodeLayouts (scoped to <NodeLayout>)

            // functional package intact after layout injection
            Assert.Equal(4, _svc.InspectFile(path).DataFlows.Single().Components.Count);
            Assert.Equal(Dts.DTSExecResult.Success, _svc.Validate(_svc.Load(path)));
        }

        [Fact]
        public void DataFlow_golden_component_nodes_match_mcp_semantically()
        {
            var golden = Path.Combine(FindRepoRoot(), "tests", "fixtures", "golden", "vs2022-data-flow.dtsx");
            if (!File.Exists(golden)) return;

            var goldenComp = ComponentNodeIds(File.ReadAllText(golden));
            foreach (var c in new[] { "Src", "CS", "DstValid", "DstDefault" })
                Assert.Contains("Package\\DFT\\" + c, goldenComp);

            var mcp = Path.Combine(_dir, "mcp-df.dtsx");
            File.Copy(golden, mcp, overwrite: true);
            var info = _svc.InspectFile(mcp);
            new DataFlowLayoutEngine().Apply(mcp, info, LayoutMode.Relayout);
            var mcpComp = ComponentNodeIds(File.ReadAllText(mcp));
            Assert.Equal(goldenComp.OrderBy(x => x), mcpComp.OrderBy(x => x)); // same component node-id set
        }

        private static System.Collections.Generic.HashSet<string> ComponentNodeIds(string xml)
        {
            var set = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(xml, "<NodeLayout[^>]*Id=\"(Package\\\\DFT\\\\[^\"]+)\""))
                set.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            return set;
        }

        private static string FindRepoRoot()
        {
            var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "SSIS-Agent-MCP.slnx"))) d = d.Parent;
            return d?.FullName ?? ".";
        }
    }
}
