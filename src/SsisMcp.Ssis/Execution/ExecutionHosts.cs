using System;
using System.Collections.Generic;
using System.IO;
using SsisMcp.Core.Execution;
using Dts = Microsoft.SqlServer.Dts.Runtime;

namespace SsisMcp.Ssis.Execution
{
    internal static class ExecutionGate
    {
        public const string LicenseSignature = "Integration Services";

        public static bool IsLicenseBlock(IEnumerable<string> errors)
        {
            foreach (var e in errors)
                if (e.IndexOf(LicenseSignature, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    e.IndexOf("Standard Edition", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }
    }

    /// <summary>Executes packages in-process via the SSIS runtime (Package.Execute).</summary>
    public sealed class InProcessExecutionHost : IPackageExecutionHost
    {
        private readonly PackageService _svc;
        public InProcessExecutionHost(PackageService? svc = null) => _svc = svc ?? new PackageService();

        public string Name => "in-process";

        public bool CanExecuteDataFlow(out string reason)
        {
            // An EMPTY pipeline runs even on unlicensed hosts; the edition gate only fires once the
            // pipeline initializes real components. So probe with an OLE DB Source that reads one row.
            try
            {
                var pkg = new Dts.Package { Name = "probe" };
                var cm = pkg.Connections.Add("OLEDB"); cm.Name = "Db";
                cm.ConnectionString = "Data Source=.;Initial Catalog=tempdb;Provider=MSOLEDBSQL;Integrated Security=SSPI;";
                var dft = (Dts.TaskHost)pkg.Executables.Add("Microsoft.Pipeline"); dft.Name = "DFT";
                var pipe = (Microsoft.SqlServer.Dts.Pipeline.Wrapper.MainPipe)dft.InnerObject;
                using (var sc = new System.Data.SqlClient.SqlConnection("Data Source=.;Initial Catalog=tempdb;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=3"))
                {
                    sc.Open();
                    using (var cmd = new System.Data.SqlClient.SqlCommand("IF OBJECT_ID('tempdb.dbo.SsisMcpExecProbe') IS NOT NULL DROP TABLE dbo.SsisMcpExecProbe; CREATE TABLE dbo.SsisMcpExecProbe(X int, Y int);", sc)) cmd.ExecuteNonQuery();
                }
                // A representative pipeline WITH a transformation: trivial source→destination runs
                // even on restricted editions, but a transform (Derived Column) triggers the gate.
                var b = new Building.DataFlowBuilder(pipe, pkg);
                b.AddComponent(Core.Building.ComponentKinds.OleDbSource, "Src");
                b.ConfigureOleDbSource("Src", "Db", 2, "SELECT CAST(1 AS int) AS X");
                b.AddComponent(Core.Building.ComponentKinds.DerivedColumn, "Der");
                b.Connect("Src", "Der");
                b.ExposeAllInputColumns("Der");
                b.ConfigureDerivedColumn("Der", "Y", "X * 2", Microsoft.SqlServer.Dts.Runtime.Wrapper.DataType.DT_I4);
                b.AddComponent(Core.Building.ComponentKinds.OleDbDestination, "Dst");
                b.Connect("Der", "Dst");
                b.ConfigureOleDbDestination("Dst", "Db", "[dbo].[SsisMcpExecProbe]");
                new Building.MappingEngine(b).AutoMap("Dst");

                var result = RunInProcess(pkg);
                if (result.Outcome == ExecutionOutcome.EnvironmentBlocked)
                {
                    reason = "Integration Services Standard/Developer or higher required";
                    return false;
                }
                if (result.Outcome == ExecutionOutcome.Success) { reason = ""; return true; }
                reason = "data flow execution failed: " + string.Join("; ", result.Errors);
                return false;
            }
            catch (Exception ex)
            {
                reason = "could not probe (" + ex.GetType().Name + ": " + ex.Message + ")";
                return false;
            }
        }

        public ExecutionResult Execute(string packagePath)
        {
            var pkg = _svc.Load(packagePath);
            return RunInProcess(pkg);
        }

        private static ExecutionResult RunInProcess(Dts.Package pkg)
        {
            var events = new CollectingEvents();
            var result = new ExecutionResult();
            var dtsResult = pkg.Execute(null, null, events, null, null);
            result.Errors.AddRange(events.Errors);
            result.Warnings.AddRange(events.Warnings);

            if (dtsResult == Dts.DTSExecResult.Success) result.Outcome = ExecutionOutcome.Success;
            else if (ExecutionGate.IsLicenseBlock(events.Errors)) result.Outcome = ExecutionOutcome.EnvironmentBlocked;
            else result.Outcome = ExecutionOutcome.Failure;
            result.Detail = dtsResult.ToString();
            return result;
        }

        private sealed class CollectingEvents : Dts.DefaultEvents
        {
            public List<string> Errors { get; } = new List<string>();
            public List<string> Warnings { get; } = new List<string>();
            public override bool OnError(Dts.DtsObject s, int code, string sub, string desc, string hf, int hc, string id)
            { Errors.Add($"[{sub}] {desc}"); return false; }
            public override void OnWarning(Dts.DtsObject s, int code, string sub, string desc, string hf, int hc, string id)
            { Warnings.Add($"[{sub}] {desc}"); }
        }
    }

    /// <summary>Executes packages via dtexec.exe on the licensed Integration Services instance.</summary>
    public sealed class DtexecExecutionHost : IPackageExecutionHost
    {
        private static readonly string[] Candidates =
        {
            @"C:\Program Files\Microsoft SQL Server\170\DTS\Binn\DTExec.exe",
            @"C:\Program Files\Microsoft SQL Server\160\DTS\Binn\DTExec.exe",
            @"C:\Program Files\Microsoft SQL Server\150\DTS\Binn\DTExec.exe",
        };

        public string Name => "dtexec";

        private static string? FindDtexec()
        {
            foreach (var c in Candidates) if (File.Exists(c)) return c;
            return null;
        }

        public bool CanExecuteDataFlow(out string reason)
        {
            // dtexec on this host hits the same edition gate; a definitive probe would run a data flow.
            if (FindDtexec() == null) { reason = "dtexec.exe not found"; return false; }
            reason = "requires a licensed Integration Services edition to run data flows";
            return false; // conservative until a licensed edition is confirmed
        }

        public ExecutionResult Execute(string packagePath)
        {
            var dtexec = FindDtexec();
            var result = new ExecutionResult();
            if (dtexec == null) { result.Outcome = ExecutionOutcome.Failure; result.Detail = "dtexec not found"; return result; }

            var psi = new System.Diagnostics.ProcessStartInfo(dtexec, $"/FILE \"{packagePath}\" /REPORTING E")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            using (var p = System.Diagnostics.Process.Start(psi)!)
            {
                var stdout = p.StandardOutput.ReadToEnd();
                p.WaitForExit(120000);
                result.Detail = stdout;
                if (stdout.IndexOf(ExecutionGate.LicenseSignature, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    stdout.IndexOf("Standard Edition", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result.Errors.Add(stdout);
                    result.Outcome = ExecutionOutcome.EnvironmentBlocked;
                }
                else result.Outcome = p.ExitCode == 0 ? ExecutionOutcome.Success : ExecutionOutcome.Failure;
            }
            return result;
        }
    }
}
