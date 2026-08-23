using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SsisMcp.Core.Execution;

namespace SsisMcp.Ssis.Execution
{
    /// <summary>
    /// Executes packages through a Microsoft-SIGNED SSIS host, which is the only way the runtime runs
    /// pipelines with transformations (the standalone in-proc/unsigned path is license-gated). It tries
    /// the available signed <c>dtexec.exe</c> hosts (a licensed SQL Server Integration Services install,
    /// then the SSDT host) and returns a structured result — no mouse/keyboard, fully programmatic.
    ///
    /// Empirically on this machine BOTH current hosts are blocked (SQL Server dtexec = edition license
    /// gate; SSDT dtexec = DTS.Application COM not registered outside VS, 0x80040154). The host reports
    /// that as <see cref="ExecutionOutcome.EnvironmentBlocked"/> with the precise reason — never faked.
    /// Once a licensed Integration Services feature is installed (the engine here is Enterprise
    /// Evaluation), its dtexec runs unblocked and this host returns Success + parsed diagnostics.
    /// </summary>
    public sealed class SsdtDebugExecutionHost : IPackageExecutionHost
    {
        public string Name => "ssdt/signed-dtexec";

        private static IEnumerable<string> Candidates()
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            // Licensed standalone Integration Services (preferred; runs unblocked when installed).
            foreach (var ver in new[] { "170", "160", "150", "130" })
                yield return Path.Combine(pf, "Microsoft SQL Server", ver, "DTS", "Binn", "DTExec.exe");
            // SSDT host (signed; not license-gated but needs the SSIS COM registered/activated).
            var vsRoots = new[] { "Professional", "Enterprise", "Community" };
            foreach (var edn in vsRoots)
            {
                var ssis = Path.Combine(pf, "Microsoft Visual Studio", "2022", edn,
                    @"Common7\IDE\CommonExtensions\Microsoft\SSIS");
                if (!Directory.Exists(ssis)) continue;
                foreach (var vdir in Directory.EnumerateDirectories(ssis).OrderByDescending(d => d))
                {
                    var exe = Path.Combine(vdir, "Binn", "DTExec.exe");
                    if (File.Exists(exe)) yield return exe;
                }
            }
        }

        public bool CanExecuteDataFlow(out string reason)
        {
            reason = "requires a Microsoft-signed licensed Integration Services dtexec (install the IS " +
                     "feature; the SQL 2025 engine here is Enterprise Evaluation) — SSDT dtexec is signed " +
                     "but its DTS COM is not registered for standalone use (0x80040154)";
            return false; // conservative until a licensed host is confirmed at Execute time
        }

        public ExecutionResult Execute(string packagePath)
        {
            var result = new ExecutionResult { Outcome = ExecutionOutcome.EnvironmentBlocked };
            var reasons = new List<string>();

            foreach (var dtexec in Candidates())
            {
                if (!File.Exists(dtexec)) continue;
                var (exit, output) = Run(dtexec, packagePath);

                if (IsLicenseGate(output)) { reasons.Add($"{Short(dtexec)}: license-gated"); continue; }
                if (output.IndexOf("0x80040154", StringComparison.OrdinalIgnoreCase) >= 0)
                { reasons.Add($"{Short(dtexec)}: DTS COM not registered (0x80040154)"); continue; }

                // A signed host actually ran the package.
                result.Detail = output;
                CollectMessages(output, result);
                result.Outcome = exit == 0 ? ExecutionOutcome.Success : ExecutionOutcome.Failure;
                return result;
            }

            result.Detail = reasons.Count > 0 ? string.Join(" | ", reasons) : "no signed dtexec host found";
            result.Errors.Add(result.Detail);
            return result;
        }

        private static (int exit, string output) Run(string dtexec, string packagePath)
        {
            var psi = new ProcessStartInfo(dtexec, $"/FILE \"{packagePath}\" /REPORTING EW")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using (var p = Process.Start(psi)!)
            {
                var outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(120000);
                return (p.ExitCode, outp);
            }
        }

        private static bool IsLicenseGate(string output) =>
            output.IndexOf("Integration Services", StringComparison.OrdinalIgnoreCase) >= 0 &&
            output.IndexOf("Standard Edition", StringComparison.OrdinalIgnoreCase) >= 0;

        private static void CollectMessages(string output, ExecutionResult result)
        {
            foreach (var line in output.Split('\n'))
            {
                var t = line.Trim();
                if (t.IndexOf("Error:", StringComparison.OrdinalIgnoreCase) >= 0) result.Errors.Add(t);
                else if (t.IndexOf("Warning:", StringComparison.OrdinalIgnoreCase) >= 0) result.Warnings.Add(t);
            }
        }

        private static string Short(string p) =>
            Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(p))) + "\\" + Path.GetFileName(p);
    }
}
