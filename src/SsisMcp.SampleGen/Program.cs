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
            GeneratePracticaPackage(svc, outDir);
            GenerateAdoNetSample(svc, outDir);
            return 0;
        }

        /// <summary>ADO.NET benchmark: DFTCliente with ADO NET Source -> Derived Column -> ADO NET
        /// Destination (real metadata + mappings), unified layout, for VS 2022 visual confirmation.</summary>
        private static void GenerateAdoNetSample(PackageService svc, string outDir)
        {
            try
            {
                using (var c = new System.Data.SqlClient.SqlConnection("Data Source=.;Initial Catalog=tempdb;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=3"))
                {
                    c.Open();
                    using (var cmd = new System.Data.SqlClient.SqlCommand(
                        "IF OBJECT_ID('tempdb.dbo.AdoBenchSrc') IS NOT NULL DROP TABLE dbo.AdoBenchSrc;" +
                        "IF OBJECT_ID('tempdb.dbo.AdoBenchDst') IS NOT NULL DROP TABLE dbo.AdoBenchDst;" +
                        "CREATE TABLE dbo.AdoBenchSrc(Codigo int, Monto decimal(10,2)); INSERT dbo.AdoBenchSrc VALUES(1,100.00);" +
                        "CREATE TABLE dbo.AdoBenchDst(Codigo int NULL, Impuesto decimal(10,2) NULL);", c)) cmd.ExecuteNonQuery();
                }
            }
            catch (Exception) { Console.WriteLine("ADO.NET sample skipped (local SQL not reachable)."); return; }

            var path = Path.GetFullPath(Path.Combine(outDir, "VisualBenchmark_AdoNet.dtsx"));
            var pkg = new Dts.Package { Name = "AdoNetBenchmark" };
            ConnectionFactory.AddAdoNetSql(pkg, "OrigenAdoNet", ".", "tempdb");
            svc.Save(pkg, path);
            new PackageEditor(svc).Apply(path, b => b.AddTask(TaskKinds.DataFlow, "DFTCliente"));
            var r = new PackageEditor(svc).ApplyDataFlow(path, "DFTCliente", b =>
            {
                b.AddComponent(ComponentKinds.AdoNetSource, "AdoSrc");
                b.ConfigureAdoNetSource("AdoSrc", "OrigenAdoNet", 1, "SELECT Codigo, Monto FROM dbo.AdoBenchSrc");
                b.AddComponent(ComponentKinds.DerivedColumn, "Der"); b.Connect("AdoSrc", "Der"); b.ExposeAllInputColumns("Der");
                b.ConfigureDerivedColumn("Der", "Impuesto", "(DT_NUMERIC,10,2)(Monto * 0.13)", Microsoft.SqlServer.Dts.Runtime.Wrapper.DataType.DT_NUMERIC, 0, 10, 2, 0);
                b.AddComponent(ComponentKinds.AdoNetDestination, "AdoDst"); b.Connect("Der", "AdoDst");
                b.ConfigureAdoNetDestination("AdoDst", "OrigenAdoNet", "dbo.AdoBenchDst");
                new MappingEngine(b).AutoMap("AdoDst");
            });
            if (!r.Succeeded) { Console.WriteLine("   ADO.NET sample failed: " + r.ErrorCode + " " + r.Detail); return; }
            var info = svc.InspectFile(path);
            new SsisMcp.Designer.PackageLayoutEngine().Apply(path, info, SsisMcp.Designer.LayoutMode.Relayout);
            Console.WriteLine($"Generated: {path}  (ADO NET Source->Derived->ADO NET Destination; validate " + svc.Validate(svc.Load(path)) + ")");
            Console.WriteLine("   Open in VS 2022; double-click DFTCliente; open ADO NET Destination -> Mappings to see the column mappings.");
        }

        /// <summary>
        /// The REAL-shape package: one Package.dtsx with SqlBorrar + four Data Flow Tasks, EACH with
        /// its own internal pipeline (Source → Destination), plus unified two-level designer layout.
        /// Double-clicking any DFT in VS 2022 opens that DFT's own laid-out Data Flow.
        /// </summary>
        private static void GeneratePracticaPackage(PackageService svc, string outDir)
        {
            var dfts = new[] { "DFTTipoCliente", "DFTCliente", "DFTMascota", "DFTEnfermedad" };
            try
            {
                using (var c = new System.Data.SqlClient.SqlConnection("Data Source=.;Initial Catalog=tempdb;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=3"))
                {
                    c.Open();
                    foreach (var d in dfts)
                        using (var cmd = new System.Data.SqlClient.SqlCommand(
                            $"IF OBJECT_ID('tempdb.dbo.T_{d}') IS NOT NULL DROP TABLE dbo.T_{d}; CREATE TABLE dbo.T_{d}(Codigo int NULL);", c)) cmd.ExecuteNonQuery();
                }
            }
            catch (Exception) { Console.WriteLine("Practica package skipped (local SQL not reachable)."); return; }

            var path = Path.GetFullPath(Path.Combine(outDir, "Package.dtsx"));
            var pkg = new Dts.Package { Name = "IntegracionPractica" };
            ConnectionFactory.AddSqlOleDb(pkg, "Vet", ".", "tempdb");
            svc.Save(pkg, path);
            var ed = new PackageEditor(svc);

            ed.Apply(path, b =>
            {
                b.AddTask(TaskKinds.ExecuteSql, "SqlBorrar");
                b.ConfigureExecuteSql("SqlBorrar", "Vet", "SELECT 1;");
                foreach (var d in dfts) b.AddTask(TaskKinds.DataFlow, d);
                b.Connect("SqlBorrar", dfts[0], PrecedenceValue.Success);
                for (var i = 0; i < dfts.Length - 1; i++) b.Connect(dfts[i], dfts[i + 1], PrecedenceValue.Success);
            });

            foreach (var d in dfts)
            {
                var r = ed.ApplyDataFlow(path, d, b =>
                {
                    b.AddComponent(ComponentKinds.OleDbSource, "Src_" + d);
                    b.ConfigureOleDbSource("Src_" + d, "Vet", 2, "SELECT CAST(1 AS int) AS Codigo");
                    b.AddComponent(ComponentKinds.OleDbDestination, "Dst_" + d);
                    b.Connect("Src_" + d, "Dst_" + d);
                    b.ConfigureOleDbDestination("Dst_" + d, "Vet", $"[dbo].[T_{d}]");
                    new MappingEngine(b).AutoMap("Dst_" + d);
                });
                if (!r.Succeeded) Console.WriteLine($"   {d} pipeline: {r.ErrorCode} {r.Detail}");
            }

            var info = svc.InspectFile(path);
            new SsisMcp.Designer.PackageLayoutEngine().Apply(path, info, SsisMcp.Designer.LayoutMode.Relayout);
            var final = svc.InspectFile(path);
            Console.WriteLine($"Generated: {path}");
            Console.WriteLine($"   Control Flow: {final.Executables.Count} tasks; Data Flows: {final.DataFlows.Count} (each with its own pipeline)");
            Console.WriteLine("   Post-layout validate: " + svc.Validate(svc.Load(path)));
            Console.WriteLine("   Open Package.dtsx in VS 2022; double-click any DFT to see its own laid-out Data Flow.");
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
