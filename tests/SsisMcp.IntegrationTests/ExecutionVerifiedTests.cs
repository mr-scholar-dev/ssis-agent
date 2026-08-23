using System;
using System.Data.SqlClient;
using System.IO;
using SsisMcp.Core.Building;
using SsisMcp.Ssis;
using SsisMcp.Ssis.Building;
using SsisMcp.Ssis.Execution;
using Xunit;
using Xunit.Abstractions;
using Dts = Microsoft.SqlServer.Dts.Runtime;
using Rt = Microsoft.SqlServer.Dts.Runtime.Wrapper;

namespace SsisMcp.IntegrationTests
{
    /// <summary>
    /// ExecutionVerified: with a licensed Integration Services dtexec present, SsdtDebugExecutionHost
    /// EXECUTES real transformation pipelines and the destination data is verified. Portable: if no
    /// signed/licensed host is available the run reports EnvironmentBlocked and the test skips.
    /// </summary>
    public sealed class ExecutionVerifiedTests
    {
        private readonly ITestOutputHelper _o;
        public ExecutionVerifiedTests(ITestOutputHelper o) => _o = o;
        private static void E(string s){using(var c=new SqlConnection("Data Source=.;Initial Catalog=tempdb;Integrated Security=true;TrustServerCertificate=true")){c.Open();using(var cmd=new SqlCommand(s,c))cmd.ExecuteNonQuery();}}
        private static object? S(string s){using(var c=new SqlConnection("Data Source=.;Initial Catalog=tempdb;Integrated Security=true;TrustServerCertificate=true")){c.Open();using(var cmd=new SqlCommand(s,c))return cmd.ExecuteScalar();}}

        [Fact]
        public void Derived_and_lookup_execute_and_verify()
        {
            var svc = new PackageService();
            var dir = Path.Combine(Path.GetTempPath(), "en-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);

            // Derived
            E("IF OBJECT_ID('tempdb.dbo.EnSrc') IS NOT NULL DROP TABLE dbo.EnSrc; IF OBJECT_ID('tempdb.dbo.EnDst') IS NOT NULL DROP TABLE dbo.EnDst;" +
              "CREATE TABLE dbo.EnSrc(Monto decimal(10,2)); INSERT dbo.EnSrc VALUES(100.00); CREATE TABLE dbo.EnDst(Monto decimal(10,2) NULL, Impuesto decimal(10,2) NULL);");
            var p1 = Path.Combine(dir, "der.dtsx"); var pkg1=new Dts.Package{Name="D"}; ConnectionFactory.AddSqlOleDb(pkg1,"Db",".","tempdb"); svc.Save(pkg1,p1);
            new PackageEditor(svc).Apply(p1, cb=>cb.AddTask(TaskKinds.DataFlow,"DFT"));
            new PackageEditor(svc).ApplyDataFlow(p1,"DFT",b=>{
                b.AddComponent(ComponentKinds.OleDbSource,"Src"); b.ConfigureOleDbSource("Src","Db",2,"SELECT Monto FROM dbo.EnSrc");
                b.AddComponent(ComponentKinds.DerivedColumn,"Der"); b.Connect("Src","Der"); b.ExposeAllInputColumns("Der");
                b.ConfigureDerivedColumn("Der","Impuesto","(DT_NUMERIC,10,2)(Monto * 0.13)",Rt.DataType.DT_NUMERIC,0,10,2,0);
                b.AddComponent(ComponentKinds.OleDbDestination,"Dst"); b.Connect("Der","Dst"); b.ConfigureOleDbDestination("Dst","Db","[dbo].[EnDst]"); new MappingEngine(b).AutoMap("Dst");});
            var r1 = new SsdtDebugExecutionHost().Execute(p1);
            _o.WriteLine($"DERIVED outcome={r1.Outcome} detail={Trunc(r1.Detail)}");
            if (r1.Outcome == SsisMcp.Core.Execution.ExecutionOutcome.EnvironmentBlocked) return; // no licensed host -> skip
            Assert.Equal(SsisMcp.Core.Execution.ExecutionOutcome.Success, r1.Outcome);
            Assert.Equal(1, Convert.ToInt32(S("SELECT COUNT(*) FROM dbo.EnDst")));
            Assert.Equal(13.00m, Convert.ToDecimal(S("SELECT Impuesto FROM dbo.EnDst WHERE Monto=100.00"))); // 100 * 0.13
            _o.WriteLine("  DERIVED verified: rows=1 impuesto=13.00");

            // Lookup
            E(@"IF OBJECT_ID('tempdb.dbo.EnIn') IS NOT NULL DROP TABLE dbo.EnIn; IF OBJECT_ID('tempdb.dbo.EnRef') IS NOT NULL DROP TABLE dbo.EnRef;
                IF OBJECT_ID('tempdb.dbo.EnM') IS NOT NULL DROP TABLE dbo.EnM; IF OBJECT_ID('tempdb.dbo.EnNM') IS NOT NULL DROP TABLE dbo.EnNM;
                CREATE TABLE dbo.EnIn(Codigo int, Nombre nvarchar(50)); CREATE TABLE dbo.EnRef(Codigo int, Tipo nvarchar(20));
                CREATE TABLE dbo.EnM(Codigo int, Nombre nvarchar(50), TipoCliente nvarchar(20)); CREATE TABLE dbo.EnNM(Codigo int, Nombre nvarchar(50));
                INSERT dbo.EnIn VALUES(1,'a'),(2,'b'),(999,'z'); INSERT dbo.EnRef VALUES(1,'Premium'),(2,'Basic');");
            var p2=Path.Combine(dir,"lk.dtsx"); var pkg2=new Dts.Package{Name="L"}; ConnectionFactory.AddSqlOleDb(pkg2,"Db",".","tempdb"); svc.Save(pkg2,p2);
            new PackageEditor(svc).Apply(p2, cb=>cb.AddTask(TaskKinds.DataFlow,"DFT"));
            new PackageEditor(svc).ApplyDataFlow(p2,"DFT",b=>{
                b.AddComponent(ComponentKinds.OleDbSource,"Src"); b.ConfigureOleDbSource("Src","Db",2,"SELECT Codigo, Nombre FROM dbo.EnIn");
                b.AddComponent(ComponentKinds.Lookup,"Lk"); b.Connect("Src","Lk");
                b.ConfigureLookup("Lk","Db","SELECT Codigo, Tipo FROM dbo.EnRef", new[]{("Codigo","Codigo")}, new[]{("Tipo","TipoCliente",Rt.DataType.DT_WSTR,20,0,0,0)});
                b.AddComponent(ComponentKinds.OleDbDestination,"M"); b.Connect("Lk","M",fromOutput:DataFlowBuilder.LookupMatchOutput); b.ConfigureOleDbDestination("M","Db","[dbo].[EnM]"); new MappingEngine(b).AutoMap("M");
                b.AddComponent(ComponentKinds.OleDbDestination,"NM"); b.Connect("Lk","NM",fromOutput:DataFlowBuilder.LookupNoMatchOutput); b.ConfigureOleDbDestination("NM","Db","[dbo].[EnNM]"); new MappingEngine(b).AutoMap("NM");});
            var r2 = new SsdtDebugExecutionHost().Execute(p2);
            _o.WriteLine($"LOOKUP outcome={r2.Outcome} detail={Trunc(r2.Detail)}");
            Assert.Equal(SsisMcp.Core.Execution.ExecutionOutcome.Success, r2.Outcome);
            Assert.Equal(2, Convert.ToInt32(S("SELECT COUNT(*) FROM dbo.EnM")));    // Codigo 1,2 -> Match
            Assert.Equal(1, Convert.ToInt32(S("SELECT COUNT(*) FROM dbo.EnNM")));   // Codigo 999 -> No Match
            Assert.Equal("Premium", (string)S("SELECT TipoCliente FROM dbo.EnM WHERE Codigo=1")!);
            _o.WriteLine("  LOOKUP verified: Match=2 NoMatch=1 tipo1=Premium");
            try{Directory.Delete(dir,true);}catch{}
        }
        private static string Trunc(string? s)=> s==null?"":(s.Length>200?s.Substring(0,200):s).Replace("\n"," ");
    }
}
