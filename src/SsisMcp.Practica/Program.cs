using System;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using SsisMcp.Core.Building;
using SsisMcp.Core.Execution;
using SsisMcp.Designer;
using SsisMcp.Ssis;
using SsisMcp.Ssis.Building;
using SsisMcp.Ssis.Execution;
using Dts = Microsoft.SqlServer.Dts.Runtime;
using Rt = Microsoft.SqlServer.Dts.Runtime.Wrapper;
using Pw = Microsoft.SqlServer.Dts.Pipeline.Wrapper;

namespace SsisMcp.Practica
{
    /// <summary>
    /// Fase 28 — IntegracionPractica. Builds the REAL practice package (Control Flow + internal Data
    /// Flows) via the MCP builders, using the practice files as the source of truth. Executes with the
    /// licensed dtexec host and verifies destination business data. Nothing invented — unspecified
    /// rules are reported, not guessed.
    /// </summary>
    internal static class Program
    {
        const string SrcDir = @"C:\Users\serra\Downloads\Archivos de practica";
        static readonly string Xls = Path.Combine(SrcDir, "Carga de tablas enfermedad y enfermedad mascota.xls");
        static readonly string Accdb = Path.Combine(SrcDir, "Carga de tablas emergentes.accdb");
        const int CP = 1252;
        static PackageService _svc = new PackageService();
        static string _path = "";
        static string _reportes = "";
        static string _outDir = "";
        static string _clienteXml = "";
        static string _mascotaXml = "";
        const string Q1 = "SELECT c.nombreCompleto, c.telefono, m.nombre AS mascota, e.nombre AS enfermedad " +
            "FROM Cliente c JOIN Mascota m ON m.idCliente=c.id JOIN EnfermedadMascota em ON em.idMascota=m.id " +
            "JOIN Enfermedad e ON e.id=em.idEnfermedad";
        const string Q2 = "SELECT c.nombreCompleto AS cliente, m.nombre AS mascota, CAST(SUM(em.costo) AS float) AS total " +
            "FROM Cliente c JOIN Mascota m ON m.idCliente=c.id JOIN EnfermedadMascota em ON em.idMascota=m.id " +
            "GROUP BY c.nombreCompleto, m.nombre";

        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "diag") return Diag();
            var outDir = Path.Combine(SrcDir, "IntegracionPractica");
            Directory.CreateDirectory(outDir);
            _path = Path.Combine(outDir, "IntegracionPractica.dtsx");
            _reportes = Path.Combine(outDir, "reportes.xlsx");
            _outDir = outDir;
            _clienteXml = Path.Combine(outDir, "ClienteXML.xml");
            _mascotaXml = Path.Combine(outDir, "MascotaXML.xml");
            foreach (var f in new[] { _clienteXml, _mascotaXml }) if (File.Exists(f)) File.Delete(f); // prove the package writes them
            CreateReportesWorkbook();

            // ---- Package + connection managers (ADO.NET for SQL Server per profesor; ACE for Excel/Access) ----
            var pkg = new Dts.Package { Name = "IntegracionPractica" };
            pkg.Variables.Add("VetConn", false, "User", "Data Source=.;Initial Catalog=Vet;Integrated Security=SSPI;TrustServerCertificate=True;");
            pkg.Variables.Add("OutDir", false, "User", outDir);
            ConnectionFactory.AddAdoNetSql(pkg, "Origen", ".", "PracticaOrigen");
            ConnectionFactory.AddAdoNetSql(pkg, "Vet", ".", "Vet");
            ConnectionFactory.AddSqlOleDb(pkg, "VetOleDb", ".", "Vet");   // for OLE DB keep-identity into Mascota
            ConnectionFactory.AddExcel(pkg, "ExcelH", Xls, xlsx: false, hdr: true);
            ConnectionFactory.AddExcel(pkg, "ExcelNoH", Xls, xlsx: false, hdr: false);
            ConnectionFactory.AddAccess(pkg, "Access", Accdb);
            ConnectionFactory.AddExcel(pkg, "Reportes", _reportes, xlsx: true, hdr: true);
            _svc.Save(pkg, _path);

            // ---- Control flow: SqlBorrar + DFTs + linear precedence chain ----
            var cf = new PackageEditor(_svc).Apply(_path, b =>
            {
                b.AddTask(TaskKinds.ExecuteSql, "SqlBorrar");
                b.ConfigureExecuteSql("SqlBorrar", connection: "Vet", sqlStatement:
                    "DELETE FROM EnfermedadMascota; DELETE FROM Mascota; DELETE FROM Cliente; " +
                    "DELETE FROM Enfermedad; DELETE FROM TipoCliente; DBCC CHECKIDENT('Mascota', RESEED, 0);");
                foreach (var d in new[] { "DFTTipoCliente", "DFTCliente", "DFTMascota", "DFTEnfermedad",
                                          "DFTEnfermedadAccess", "DFTMascotaAccess", "DFTEnfermedadMascota",
                                          "DFTReporte1", "DFTReporte2" })
                    b.AddTask(TaskKinds.DataFlow, d);
                b.AddTask(TaskKinds.Script, "ScriptClienteXML");
                b.AddTask(TaskKinds.Script, "ScriptMascotaXML");
                string[] chain = { "SqlBorrar", "DFTTipoCliente", "DFTCliente", "DFTMascota", "DFTEnfermedad",
                                   "DFTEnfermedadAccess", "DFTMascotaAccess", "DFTEnfermedadMascota",
                                   "DFTReporte1", "DFTReporte2", "ScriptClienteXML", "ScriptMascotaXML" };
                for (int i = 0; i < chain.Length - 1; i++) b.Connect(chain[i], chain[i + 1]);
            });
            Req("control-flow", cf);

            BuildTipoCliente();
            BuildCliente();
            BuildMascota();
            BuildEnfermedad();
            BuildEnfermedadAccess();
            BuildMascotaAccess();
            BuildEnfermedadMascota();
            BuildReporte1();
            BuildReporte2();
            BuildScriptXml("ScriptClienteXML", "Cliente",
                "SELECT * FROM Cliente FOR XML RAW('Cliente'), ROOT('ClienteXML'), ELEMENTS", "ClienteXML.xml");
            BuildScriptXml("ScriptMascotaXML", "Mascota",
                "SELECT * FROM Mascota FOR XML RAW('Mascota'), ROOT('MascotaXML'), ELEMENTS", "MascotaXML.xml");

            // ---- Layout (unified control flow + data flow) ----
            var info = _svc.InspectFile(_path);
            new PackageLayoutEngine().Apply(_path, info, LayoutMode.Relayout);
            Console.WriteLine("== layout applied ==");

            // ---- Execute with licensed host ----
            Console.WriteLine("== executing via SsdtDebugExecutionHost ==");
            var exec = new SsdtDebugExecutionHost().Execute(_path);
            Console.WriteLine("OUTCOME: " + exec.Outcome);
            if (exec.Outcome != ExecutionOutcome.Success)
            {
                Console.WriteLine("DETAIL: " + Trunc(exec.Detail, 1500));
                return 2;
            }
            Verify();
            Console.WriteLine("\nPACKAGE: " + _path);
            return 0;
        }

        // ---------------- DFT builders ----------------

        static void BuildTipoCliente() => Dft("DFTTipoCliente", b =>
        {
            b.AddComponent(ComponentKinds.ExcelSource, "Src");
            b.ConfigureExcelSource("Src", "ExcelNoH", "Tipo Cliente$");
            b.AddComponent(ComponentKinds.DataConversion, "Conv"); b.Connect("Src", "Conv");
            b.ConfigureDataConversion("Conv", "F1", "id", Rt.DataType.DT_I4);
            b.ConfigureDataConversion("Conv", "F2", "nombre", Rt.DataType.DT_STR, 50, 0, 0, CP);
            b.AddComponent(ComponentKinds.AdoNetDestination, "Dst"); b.Connect("Conv", "Dst");
            b.ConfigureAdoNetDestination("Dst", "Vet", "dbo.TipoCliente");
            var m = new MappingEngine(b);
            m.SetMapping("Dst", "id", "id");
            m.SetMapping("Dst", "nombre", "nombre");
        });

        static void BuildCliente() => Dft("DFTCliente", b =>
        {
            b.AddComponent(ComponentKinds.AdoNetSource, "Src");
            b.ConfigureAdoNetSource("Src", "Origen", 1,
                "SELECT id,nombre,apellido1,apellido2,nacimiento,direccion,telefono,idTipo FROM Cliente");
            b.AddComponent(ComponentKinds.DataConversion, "Conv"); b.Connect("Src", "Conv");
            b.ConfigureDataConversion("Conv", "direccion", "cDireccion", Rt.DataType.DT_STR, 100, 0, 0, CP);
            b.ConfigureDataConversion("Conv", "telefono", "cTelefono", Rt.DataType.DT_STR, 8, 0, 0, CP);
            b.AddComponent(ComponentKinds.DerivedColumn, "Der"); b.Connect("Conv", "Der"); b.ExposeAllInputColumns("Der");
            b.ConfigureDerivedColumn("Der", "nombreCompleto",
                "(DT_STR,150,1252)(nombre + \" \" + apellido1 + \" \" + apellido2)", Rt.DataType.DT_STR, 150, 0, 0, CP);
            b.ConfigureDerivedColumn("Der", "annio", "YEAR(nacimiento)", Rt.DataType.DT_I4);
            b.ConfigureDerivedColumn("Der", "mes", "MONTH(nacimiento)", Rt.DataType.DT_I4);
            b.ConfigureDerivedColumn("Der", "dia", "DAY(nacimiento)", Rt.DataType.DT_I4);
            b.ConfigureDerivedColumn("Der", "idTipoCliente", "idTipo == \"F\" ? 1 : 2", Rt.DataType.DT_I4);
            b.AddComponent(ComponentKinds.AdoNetDestination, "Dst"); b.Connect("Der", "Dst");
            b.ConfigureAdoNetDestination("Dst", "Vet", "dbo.Cliente");
            var m = new MappingEngine(b);
            m.SetMapping("Dst", "id", "id");
            m.SetMapping("Dst", "nombreCompleto", "nombreCompleto");
            m.SetMapping("Dst", "nacimiento", "nacimiento");
            m.SetMapping("Dst", "annio", "annio");
            m.SetMapping("Dst", "mes", "mes");
            m.SetMapping("Dst", "dia", "dia");
            m.SetMapping("Dst", "cDireccion", "direccion");
            m.SetMapping("Dst", "cTelefono", "telefono");
            m.SetMapping("Dst", "idTipoCliente", "idTipoCliente");
        });

        // Mascota.nacimiento: origin only has 'edad' (int years); the practice does NOT specify how to
        // derive a birth date. NOT invented -> left NULL and reported as a gap. id is IDENTITY: source
        // read ORDER BY id after a RESEED(0) so identity reassigns 1..5 == original ids (verified).
        static void BuildMascota() => Dft("DFTMascota", b =>
        {
            b.AddComponent(ComponentKinds.AdoNetSource, "Src");
            b.ConfigureAdoNetSource("Src", "Origen", 1, "SELECT id,nombre,edad,idCliente FROM Mascota ORDER BY id");
            b.AddComponent(ComponentKinds.DataConversion, "Conv"); b.Connect("Src", "Conv");
            b.ConfigureDataConversion("Conv", "nombre", "cNombre", Rt.DataType.DT_STR, 50, 0, 0, CP);
            b.AddComponent(ComponentKinds.OleDbDestination, "Dst"); b.Connect("Conv", "Dst");
            b.ConfigureOleDbDestination("Dst", "VetOleDb", "[dbo].[Mascota]", keepIdentity: true);   // preserve id 1..5
            var m = new MappingEngine(b);
            m.SetMapping("Dst", "id", "id");
            m.SetMapping("Dst", "cNombre", "nombre");
            m.SetMapping("Dst", "idCliente", "idCliente");
        });

        static void BuildEnfermedad() => Dft("DFTEnfermedad", b =>
        {
            b.AddComponent(ComponentKinds.ExcelSource, "Src");
            b.ConfigureExcelSource("Src", "ExcelH", "Enfermedad$");
            b.AddComponent(ComponentKinds.DataConversion, "Conv"); b.Connect("Src", "Conv");
            b.ConfigureDataConversion("Conv", "Codigo", "id", Rt.DataType.DT_I4);
            b.ConfigureDataConversion("Conv", "Nombre", "cNombre", Rt.DataType.DT_STR, 100, 0, 0, CP);
            b.AddComponent(ComponentKinds.AdoNetDestination, "Dst"); b.Connect("Conv", "Dst");
            b.ConfigureAdoNetDestination("Dst", "Vet", "dbo.Enfermedad");
            var m = new MappingEngine(b);
            m.SetMapping("Dst", "id", "id");
            m.SetMapping("Dst", "cNombre", "nombre");
        });

        static void BuildEnfermedadAccess() => Dft("DFTEnfermedadAccess", b =>
        {
            b.AddComponent(ComponentKinds.OleDbSource, "Src");
            b.ConfigureOleDbSource("Src", "Access", 0, "Enfermedad");
            b.AddComponent(ComponentKinds.DataConversion, "Conv"); b.Connect("Src", "Conv");
            b.ConfigureDataConversion("Conv", "id", "cId", Rt.DataType.DT_I4);
            b.ConfigureDataConversion("Conv", "nombre", "cNombre", Rt.DataType.DT_STR, 100, 0, 0, CP);
            b.AddComponent(ComponentKinds.AdoNetDestination, "Dst"); b.Connect("Conv", "Dst");
            b.ConfigureAdoNetDestination("Dst", "Vet", "dbo.Enfermedad");
            var m = new MappingEngine(b);
            m.SetMapping("Dst", "cId", "id");
            m.SetMapping("Dst", "cNombre", "nombre");
        });

        static void BuildMascotaAccess() => Dft("DFTMascotaAccess", b =>
        {
            b.AddComponent(ComponentKinds.OleDbSource, "Src");
            b.ConfigureOleDbSource("Src", "Access", 0, "Mascota");
            b.AddComponent(ComponentKinds.DataConversion, "Conv"); b.Connect("Src", "Conv");
            b.ConfigureDataConversion("Conv", "id", "cId", Rt.DataType.DT_I4);
            b.ConfigureDataConversion("Conv", "nombre", "cNombre", Rt.DataType.DT_STR, 50, 0, 0, CP);
            b.ConfigureDataConversion("Conv", "idCliente", "cIdCliente", Rt.DataType.DT_I4);
            b.AddComponent(ComponentKinds.OleDbDestination, "Dst"); b.Connect("Conv", "Dst");
            b.ConfigureOleDbDestination("Dst", "VetOleDb", "[dbo].[Mascota]", keepIdentity: true);   // preserve id 21..25
            var m = new MappingEngine(b);
            m.SetMapping("Dst", "cId", "id");
            m.SetMapping("Dst", "cNombre", "nombre");
            m.SetMapping("Dst", "cIdCliente", "idCliente");
        });

        static void BuildEnfermedadMascota() => Dft("DFTEnfermedadMascota", b =>
        {
            b.AddComponent(ComponentKinds.ExcelSource, "Src");
            b.ConfigureExcelSource("Src", "ExcelH", "EnfermedadMascota$");
            b.AddComponent(ComponentKinds.DerivedColumn, "Der"); b.Connect("Src", "Der"); b.ExposeAllInputColumns("Der");
            b.ConfigureDerivedColumn("Der", "impuesto", "(DT_CY)(Costo * 0.13)", Rt.DataType.DT_CY);   // 13% del monto
            b.AddComponent(ComponentKinds.DataConversion, "Conv"); b.Connect("Der", "Conv");
            b.ConfigureDataConversion("Conv", "Mascota", "idMascota", Rt.DataType.DT_I4);
            b.ConfigureDataConversion("Conv", "Enfermedad", "idEnfermedad", Rt.DataType.DT_I4);
            b.ConfigureDataConversion("Conv", "Costo", "costo", Rt.DataType.DT_CY);
            b.ConfigureDataConversion("Conv", "Fecha", "fecha", Rt.DataType.DT_DBTIMESTAMP);
            b.AddComponent(ComponentKinds.AdoNetDestination, "Dst"); b.Connect("Conv", "Dst");
            b.ConfigureAdoNetDestination("Dst", "Vet", "dbo.EnfermedadMascota");
            var m = new MappingEngine(b);
            m.SetMapping("Dst", "idEnfermedad", "idEnfermedad");
            m.SetMapping("Dst", "idMascota", "idMascota");
            m.SetMapping("Dst", "fecha", "fecha");
            m.SetMapping("Dst", "costo", "costo");
            m.SetMapping("Dst", "impuesto", "impuesto");
        });

        static void BuildReporte1() => Dft("DFTReporte1", b =>
        {
            b.AddComponent(ComponentKinds.AdoNetSource, "Src");
            b.ConfigureAdoNetSource("Src", "Vet", 1, Q1);
            b.AddComponent(ComponentKinds.ExcelDestination, "Dst"); b.Connect("Src", "Dst");
            b.ConfigureExcelDestination("Dst", "Reportes", "Reporte1$");
            new MappingEngine(b).AutoMap("Dst");
        });

        static void BuildReporte2() => Dft("DFTReporte2", b =>
        {
            b.AddComponent(ComponentKinds.AdoNetSource, "Src");
            b.ConfigureAdoNetSource("Src", "Vet", 1, Q2);
            b.AddComponent(ComponentKinds.ExcelDestination, "Dst"); b.Connect("Src", "Dst");
            b.ConfigureExcelDestination("Dst", "Reportes", "Reporte2$");
            new MappingEngine(b).AutoMap("Dst");
        });

        // XML export INSIDE the package: a precompiled Script Task runs FOR XML against Vet (via the
        // ReadOnly package variables) and writes the file. No external process. Reusable capability.
        static void BuildScriptXml(string taskName, string label, string forXmlSql, string fileName)
        {
            var body =
                "            string conn = Dts.Variables[\"User::VetConn\"].Value.ToString();\n" +
                "            string dir = Dts.Variables[\"User::OutDir\"].Value.ToString();\n" +
                "            string sql = @\"" + forXmlSql.Replace("\"", "\"\"") + "\";\n" +
                "            string xml = null;\n" +
                "            using (var c = new System.Data.SqlClient.SqlConnection(conn))\n" +
                "            {\n" +
                "                c.Open();\n" +
                "                using (var cmd = new System.Data.SqlClient.SqlCommand(sql, c))\n" +
                "                using (var rdr = cmd.ExecuteReader())\n" +
                "                {\n" +
                "                    var sb = new System.Text.StringBuilder();\n" +
                "                    while (rdr.Read()) sb.Append(rdr.GetValue(0).ToString());\n" +
                "                    xml = sb.ToString();\n" +
                "                }\n" +
                "            }\n" +
                "            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, \"" + fileName + "\"), xml, new System.Text.UTF8Encoding(false));\n";
            var source = ScriptTaskSource.CSharpMain(body);
            var r = new PackageEditor(_svc).Apply(_path, b =>
                b.ConfigureScriptTask(taskName, source, readOnlyVariables: new[] { "User::VetConn", "User::OutDir" }),
                tool: "controlflow.scripttask");
            Req(taskName, r);
        }

        // Creates reportes.xlsx with the two report sheets (header row) via ACE, so the SSIS Excel
        // destination has a target schema to open. Report ROWS are written by the package at runtime.
        static void CreateReportesWorkbook()
        {
            if (File.Exists(_reportes)) File.Delete(_reportes);
            var cs = $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={_reportes};Extended Properties=\"Excel 12.0 Xml;HDR=YES\";";
            using (var c = new System.Data.OleDb.OleDbConnection(cs))
            {
                c.Open();
                Exec(c, "CREATE TABLE [Reporte1] ([nombreCompleto] VARCHAR(150),[telefono] VARCHAR(8),[mascota] VARCHAR(50),[enfermedad] VARCHAR(100))");
                Exec(c, "CREATE TABLE [Reporte2] ([cliente] VARCHAR(150),[mascota] VARCHAR(50),[total] DOUBLE)");
            }
            Console.WriteLine("== reportes.xlsx created (Reporte1, Reporte2) ==");
            void Exec(System.Data.OleDb.OleDbConnection c, string sql)
            { using (var cmd = new System.Data.OleDb.OleDbCommand(sql, c)) cmd.ExecuteNonQuery(); }
        }

        // ---------------- diagnostics ----------------

        sealed class Events : Dts.DefaultEvents
        {
            public override bool OnError(Dts.DtsObject src, int code, string sub, string desc, string helpFile, int helpCtx, string idof)
            { Console.WriteLine($"  ERR [{code}] {sub}: {desc}"); return false; }
            public override void OnWarning(Dts.DtsObject src, int code, string sub, string desc, string helpFile, int helpCtx, string idof)
            { Console.WriteLine($"  WARN [{code}] {sub}: {desc}"); }
        }

        static int Diag()
        {
            var dir = Path.Combine(Path.GetTempPath(), "practica28diag"); Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "d.dtsx");
            var pkg = new Dts.Package { Name = "d" };
            ConnectionFactory.AddExcel(pkg, "ExcelNoH", Xls, xlsx: false, hdr: false);
            ConnectionFactory.AddAdoNetSql(pkg, "Vet", ".", "Vet");
            var dft = pkg.Executables.Add("STOCK:PipelineTask");
            ((Dts.TaskHost)dft).Name = "DFTTipoCliente";
            var pipe = (Pw.MainPipe)((Dts.TaskHost)dft).InnerObject;
            var b = new DataFlowBuilder(pipe, pkg);
            b.AddComponent(ComponentKinds.ExcelSource, "Src");
            b.ConfigureExcelSource("Src", "ExcelNoH", "Tipo Cliente$");
            b.AddComponent(ComponentKinds.DataConversion, "Conv"); b.Connect("Src", "Conv");
            b.ConfigureDataConversion("Conv", "F1", "id", Rt.DataType.DT_I4);
            b.ConfigureDataConversion("Conv", "F2", "nombre", Rt.DataType.DT_STR, 50, 0, 0, CP);
            b.AddComponent(ComponentKinds.AdoNetDestination, "Dst"); b.Connect("Conv", "Dst");
            b.ConfigureAdoNetDestination("Dst", "Vet", "dbo.TipoCliente");
            var m = new MappingEngine(b);
            m.SetMapping("Dst", "id", "id");
            m.SetMapping("Dst", "nombre", "nombre");
            foreach (var c in new[] { "Src", "Conv", "Dst" })
            {
                var info = b.InspectComponent(c);
                Console.WriteLine($"-- {c}");
                foreach (var o in info.Outputs) { if (o.IsErrorOut) continue; foreach (var col in o.Columns) Console.WriteLine($"     out {col.Name} {col.DataType}"); }
                foreach (var i in info.Inputs) foreach (var col in i.Columns) Console.WriteLine($"     in  {col.Name} {col.DataType} lin={col.LineageId}");
            }
            Console.WriteLine("== in-memory validate with events ==");
            var res = pkg.Validate(pkg.Connections, pkg.Variables, new Events(), null);
            Console.WriteLine("Validate (in-mem) = " + res);
            _svc.Save(pkg, path);
            Console.WriteLine("== reload + validate with events ==");
            var p2 = _svc.Load(path);
            var res2 = p2.Validate(p2.Connections, p2.Variables, new Events(), null);
            Console.WriteLine("Validate (reloaded) = " + res2);
            return 0;
        }

        // ---------------- helpers ----------------

        static void Dft(string name, Action<DataFlowBuilder> op)
        {
            var r = new PackageEditor(_svc).ApplyDataFlow(_path, name, op);
            Req(name, r);
        }

        static void Req(string label, OperationResult r)
        {
            Console.WriteLine((r.Succeeded ? "OK   " : "FAIL ") + label +
                (r.Succeeded ? "" : "  [" + r.ErrorCode + "] " + Trunc(r.Detail, 4000)));
            if (!r.Succeeded) { Console.Error.WriteLine("ABORT at " + label); Environment.Exit(3); }
        }

        static void Verify()
        {
            Console.WriteLine("\n==== BUSINESS VERIFICATION ====");
            long rc(string t) => Convert.ToInt64(Scalar($"SELECT COUNT(*) FROM {t}"));
            Console.WriteLine($"TipoCliente rows       = {rc("TipoCliente")}   (expected 2)");
            Console.WriteLine($"Cliente rows           = {rc("Cliente")}   (expected 5)");
            Console.WriteLine($"Mascota rows           = {rc("Mascota")}   (expected 10: 5 origen + 5 access)");
            Console.WriteLine($"Enfermedad rows        = {rc("Enfermedad")}   (expected 16: 10 excel + 6 access)");
            Console.WriteLine($"EnfermedadMascota rows = {rc("EnfermedadMascota")}   (expected 15)");
            Console.WriteLine("-- TipoCliente: " + Scalar("SELECT STRING_AGG(CONCAT(id,'=',nombre),', ') FROM TipoCliente"));
            Console.WriteLine("-- Cliente id=1 nombreCompleto = " + Scalar("SELECT nombreCompleto FROM Cliente WHERE id=1")
                + " | annio/mes/dia = " + Scalar("SELECT CONCAT(annio,'/',mes,'/',dia) FROM Cliente WHERE id=1")
                + " | idTipoCliente = " + Scalar("SELECT idTipoCliente FROM Cliente WHERE id=1"));
            Console.WriteLine("-- Cliente idTipo map (F->1,O->2): " +
                Scalar("SELECT STRING_AGG(CONCAT(id,':',idTipoCliente),', ') WITHIN GROUP (ORDER BY id) FROM Cliente"));
            Console.WriteLine("-- Mascota id=1 = " + Scalar("SELECT nombre FROM Mascota WHERE id=1") + " (expected Duke)");
            Console.WriteLine("-- Mascota nacimiento NULLs = " + Scalar("SELECT COUNT(*) FROM Mascota WHERE nacimiento IS NULL") + " (edad->nacimiento gap)");
            Console.WriteLine("-- EnfMascota impuesto check (costo*0.13): " +
                Scalar("SELECT STRING_AGG(CONCAT(costo,'->',impuesto),', ') WITHIN GROUP (ORDER BY costo) FROM (SELECT DISTINCT TOP 3 costo,impuesto FROM EnfermedadMascota ORDER BY costo) t"));
            Console.WriteLine("-- impuesto mismatches = " +
                Scalar("SELECT COUNT(*) FROM EnfermedadMascota WHERE ABS(impuesto - costo*0.13) > 0.005") + " (expected 0)");

            Console.WriteLine("-- reportes.xlsx Reporte1 rows = " + ExcelCount("Reporte1$") + " (expected 15)");
            Console.WriteLine("-- reportes.xlsx Reporte2 rows = " + ExcelCount("Reporte2$") + " (expected 5)");

            VerifyXml(_clienteXml, "Cliente", 5);
            VerifyXml(_mascotaXml, "Mascota", 10);
        }

        static void VerifyXml(string file, string element, int expected)
        {
            var exists = File.Exists(file);
            string status = "MISSING";
            if (exists)
            {
                try
                {
                    var doc = new System.Xml.XmlDocument(); doc.Load(file);
                    var n = doc.GetElementsByTagName(element).Count;
                    status = $"valid XML, <{element}> count={n} (expected {expected})" + (n == expected ? " OK" : " MISMATCH");
                }
                catch (Exception ex) { status = "INVALID XML: " + ex.Message; }
            }
            Console.WriteLine($"-- {Path.GetFileName(file)}: exists={exists}; {status}");
        }

        static long ExcelCount(string sheet)
        {
            var cs = $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={_reportes};Extended Properties=\"Excel 12.0 Xml;HDR=YES\";";
            using (var c = new System.Data.OleDb.OleDbConnection(cs))
            { c.Open(); using (var cmd = new System.Data.OleDb.OleDbCommand($"SELECT COUNT(*) FROM [{sheet}]", c)) return Convert.ToInt64(cmd.ExecuteScalar()); }
        }

        static object Scalar(string sql)
        {
            using (var c = new SqlConnection("Data Source=.;Initial Catalog=Vet;Integrated Security=true;TrustServerCertificate=true"))
            { c.Open(); using (var cmd = new SqlCommand(sql, c)) { var o = cmd.ExecuteScalar(); return o ?? "NULL"; } }
        }

        static string Trunc(string? s, int n) => s == null ? "" : (s.Length > n ? s.Substring(0, n) : s).Replace("\r", " ").Replace("\n", " ");
    }
}
