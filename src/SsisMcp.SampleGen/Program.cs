using System;
using System.IO;
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
            return 0;
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
