using System;
using SsisMcp.Core.Environment;

namespace SsisMcp.EnvProbe
{
    /// <summary>
    /// Phase 0 environment probe (Fase 0). Prints a human report and exits non-zero when a
    /// critical requirement is missing, so it can gate CI / setup scripts. It never "continues
    /// silently" past a missing dependency.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            IEnvironmentDetector detector = new WindowsEnvironmentDetector();
            var report = detector.Detect();

            // Data Flow execution availability (license-gated on some hosts). Non-critical.
            var execCheck = new CheckResult("execution.dataFlow", CheckStatus.Warn);
            try
            {
                var host = new SsisMcp.Ssis.Execution.InProcessExecutionHost();
                var available = host.CanExecuteDataFlow(out var reason);
                execCheck.Status = available ? CheckStatus.Pass : CheckStatus.Warn;
                execCheck.Detail = available
                    ? "available"
                    : "available = false; reason = " + reason;
                execCheck.Data["available"] = available ? "true" : "false";
                if (!available) execCheck.Data["reason"] = reason;
            }
            catch (Exception ex)
            {
                execCheck.Status = CheckStatus.Unknown;
                execCheck.Detail = "probe error: " + ex.Message;
            }
            report.Checks.Add(execCheck);

            Console.WriteLine(report.ToDisplayString());

            if (!report.CoreUsable)
            {
                Console.Error.WriteLine("One or more CRITICAL checks failed. Fix them before building/running the SSIS MCP core.");
                return 1;
            }
            return 0;
        }
    }
}
