using System;
using System.IO;
using System.Linq;
using SsisMcp.Core.Building;
using SsisMcp.Ssis;
using SsisMcp.Ssis.Building;
using Dts = Microsoft.SqlServer.Dts.Runtime;

namespace SsisMcp.SampleGen
{
    /// <summary>
    /// Generates the visual-benchmark package(s) with the MCP builders so they can be opened in the
    /// VS 2022 SSIS Designer. Output goes to samples/. Control Flow only for the first artifact
    /// (no external sources needed → opens cleanly): SqlBorrar → DFTTipoCliente → DFTCliente →
    /// DFTMascota → DFTEnfermedad (Execute SQL Task + Data Flow Tasks joined by Success constraints).
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var outDir = args.Length > 0 ? args[0] : "samples";
            Directory.CreateDirectory(outDir);
            var path = Path.GetFullPath(Path.Combine(outDir, "VisualBenchmark_ControlFlow.dtsx"));

            var svc = new PackageService();
            // Base package + a SQL Server connection manager (so Execute SQL validates).
            var pkg = new Dts.Package { Name = "IntegracionPractica" };
            ConnectionFactory.AddSqlOleDb(pkg, "Vet", ".", "tempdb");
            svc.Save(pkg, path);

            var editor = new PackageEditor(svc);
            var dfts = new[] { "DFTTipoCliente", "DFTCliente", "DFTMascota", "DFTEnfermedad" };

            var r = editor.Apply(path, b =>
            {
                b.AddTask(TaskKinds.ExecuteSql, "SqlBorrar");
                b.ConfigureExecuteSql("SqlBorrar", connection: "Vet", sqlStatement: "SELECT 1;");
                foreach (var d in dfts) b.AddTask(TaskKinds.DataFlow, d);

                b.Connect("SqlBorrar", "DFTTipoCliente", PrecedenceValue.Success);
                b.Connect("DFTTipoCliente", "DFTCliente", PrecedenceValue.Success);
                b.Connect("DFTCliente", "DFTMascota", PrecedenceValue.Success);
                b.Connect("DFTMascota", "DFTEnfermedad", PrecedenceValue.Success);
            });

            Console.WriteLine($"Generated: {path}");
            Console.WriteLine($"Succeeded={r.Succeeded} State={r.SafetyState} Error={r.ErrorCode} Detail={r.Detail}");
            if (!r.Succeeded) return 1;

            var info = svc.InspectFile(path);
            Console.WriteLine($"Executables: {info.Executables.Count}, PrecedenceConstraints: {info.PrecedenceConstraints.Count}, Connections: {info.Connections.Count}");

            // Apply MCP-computed designer layout (top->bottom) so VS shows our arrangement, not auto-arrange.
            var boxes = new SsisMcp.Designer.ControlFlowLayoutEngine().Apply(path, info, SsisMcp.Designer.LayoutMode.Relayout);
            Console.WriteLine($"Designer layout applied ({boxes.Count} nodes positioned top->bottom).");
            // functional package still loads/validates after layout injection
            Console.WriteLine("Post-layout validate: " + svc.Validate(svc.Load(path)));
            Console.WriteLine("Open this .dtsx in the VS 2022 SSIS Designer to verify the Control Flow visually.");

            GenerateConnectionsSample(svc, outDir);
            GenerateDataFlowSample(svc, outDir);
            return 0;
        }

        /// <summary>
        /// Branching Data Flow benchmark for the Designer gate: OLE DB Source → Conditional Split →
        /// (Valid → OLE DB Destination) / (default → OLE DB Destination). Real metadata + mappings.
        /// Requires local SQL Server; skipped otherwise. No layout yet (for golden capture in VS).
        /// </summary>
        private static void GenerateDataFlowSample(PackageService svc, string outDir)
        {
            try
            {
                using (var c = new System.Data.SqlClient.SqlConnection("Data Source=.;Initial Catalog=tempdb;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=3"))
                {
                    c.Open();
                    using (var cmd = new System.Data.SqlClient.SqlCommand(
                        "IF OBJECT_ID('tempdb.dbo.DfValid') IS NOT NULL DROP TABLE dbo.DfValid;" +
                        "IF OBJECT_ID('tempdb.dbo.DfDefault') IS NOT NULL DROP TABLE dbo.DfDefault;" +
                        "CREATE TABLE dbo.DfValid(Codigo int NULL, Nombre nvarchar(50) NULL);" +
                        "CREATE TABLE dbo.DfDefault(Codigo int NULL, Nombre nvarchar(50) NULL);", c)) cmd.ExecuteNonQuery();
                }
            }
            catch (Exception) { Console.WriteLine("Data Flow sample skipped (local SQL not reachable)."); return; }

            var path = Path.GetFullPath(Path.Combine(outDir, "VisualBenchmark_DataFlow.dtsx"));
            var pkg = new Dts.Package { Name = "DataFlowBenchmark" };
            ConnectionFactory.AddSqlOleDb(pkg, "Db", ".", "tempdb");
            svc.Save(pkg, path);
            var ok1 = new PackageEditor(svc).Apply(path, cb => cb.AddTask(TaskKinds.DataFlow, "DFT")).Succeeded;

            var r = new PackageEditor(svc).ApplyDataFlow(path, "DFT", b =>
            {
                b.AddComponent(ComponentKinds.OleDbSource, "Src");
                b.ConfigureOleDbSource("Src", "Db", 2, "SELECT CAST(1 AS int) AS Codigo, CAST('a' AS nvarchar(50)) AS Nombre");
                b.AddComponent(ComponentKinds.ConditionalSplit, "CS");
                b.Connect("Src", "CS");
                b.ExposeAllInputColumns("CS");
                b.AddConditionalSplitCase("CS", "Valid", "Codigo >= 0", 0);
                b.AddComponent(ComponentKinds.OleDbDestination, "DstValid");
                b.Connect("CS", "DstValid", fromOutput: "Valid");
                b.ConfigureOleDbDestination("DstValid", "Db", "[dbo].[DfValid]");
                new MappingEngine(b).AutoMap("DstValid");
                b.AddComponent(ComponentKinds.OleDbDestination, "DstDefault");
                b.Connect("CS", "DstDefault", fromOutput: "Conditional Split Default Output");
                b.ConfigureOleDbDestination("DstDefault", "Db", "[dbo].[DfDefault]");
                new MappingEngine(b).AutoMap("DstDefault");
            });

            Console.WriteLine($"Generated: {path}  (DataFlow build: ok1={ok1} succeeded={r.Succeeded} {r.ErrorCode})");
            if (r.Succeeded)
            {
                var info2 = svc.InspectFile(path);
                var boxes = new SsisMcp.Designer.DataFlowLayoutEngine().Apply(path, info2, SsisMcp.Designer.LayoutMode.Relayout);
                Console.WriteLine($"   Data Flow layout applied ({boxes.Count} components positioned top->bottom, branches spread on X).");
                Console.WriteLine("   Post-layout validate: " + svc.Validate(svc.Load(path)));
                var df = info2.DataFlows.First();
                Console.WriteLine("   components: " + string.Join(", ", df.Components.Select(c => c.Name)));
                Console.WriteLine("   paths: " + string.Join(", ", df.Paths.Select(p => p.StartComponent + "->" + p.EndComponent)));
            }
        }

        /// <summary>All six connection-manager families the benchmark uses, visible in the VS tray.</summary>
        private static void GenerateConnectionsSample(PackageService svc, string outDir)
        {
            var path = Path.GetFullPath(Path.Combine(outDir, "VisualBenchmark_Connections.dtsx"));
            var pkg = new Dts.Package { Name = "ConnectionManagers" };
            ConnectionFactory.AddSqlOleDb(pkg, "PracticaOrigen", ".", "PracticaOrigen");
            ConnectionFactory.AddSqlOleDb(pkg, "Vet", ".", "Vet");
            ConnectionFactory.AddAdoNetSql(pkg, "OrigenAdoNet", ".", "PracticaOrigen");
            ConnectionFactory.AddExcel(pkg, "CargaExcel", @"C:\Data\Carga de tablas.xls", xlsx: false);
            ConnectionFactory.AddAccess(pkg, "CargaAccess", @"C:\Data\Carga de tablas emergentes.accdb");
            ConnectionFactory.AddFlatFile(pkg, "CargaPlana", @"C:\Data\carga.csv",
                new[] { ("Codigo", Microsoft.SqlServer.Dts.Runtime.Wrapper.DataType.DT_I4, 0),
                        ("Nombre", Microsoft.SqlServer.Dts.Runtime.Wrapper.DataType.DT_STR, 50) });
            svc.Save(pkg, path);
            var info = svc.InspectFile(path);
            Console.WriteLine($"Generated: {path}  (connection managers: {info.Connections.Count})");
            foreach (var c in info.Connections) Console.WriteLine($"   {c.Name}  [{c.CreationName}]");
        }
    }
}
