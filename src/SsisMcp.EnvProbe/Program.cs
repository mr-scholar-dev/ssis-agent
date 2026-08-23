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

            // Visual Studio / SSIS Designer breakdown (four distinct facts, never one boolean).
            try
            {
                var instances = new SsisMcp.VisualStudioBridge.WindowsVisualStudioLocator().DetectAll();
                var anyReady = false;
                foreach (var vs in instances)
                {
                    var name = "vs." + (vs.Generation == SsisMcp.Core.Ide.TargetIde.VS2022 ? "2022"
                        : vs.Generation == SsisMcp.Core.Ide.TargetIde.VS2026 ? "2026" : vs.Version);
                    var status = vs.SsisDesignerAvailable ? CheckStatus.Pass : CheckStatus.Warn;
                    var c = new CheckResult(name, status,
                        $"role={vs.Role}; VisualStudioInstalled={vs.VisualStudioInstalled}; IdeAvailable={vs.VisualStudioIdeAvailable}; " +
                        $"SsisProjectsExtensionInstalled={vs.SsisProjectsExtensionInstalled}; SsisDesignerAvailable={vs.SsisDesignerAvailable}; " +
                        $"DesignerLayoutTesting={vs.DesignerLayoutTesting}");
                    c.Data["version"] = vs.Version;
                    c.Data["edition"] = vs.Edition ?? "";
                    report.Checks.Add(c);
                    if (vs.DesignerLayoutTesting == "Ready") anyReady = true;
                }
                report.Checks.Add(new CheckResult("designer.layout.testing",
                    anyReady ? CheckStatus.Pass : CheckStatus.Warn,
                    anyReady ? "READY (a VS 2022 instance has the SSIS Designer)"
                             : "EnvironmentBlocked (no VS 2022 instance with the SSIS Projects extension)"));
            }
            catch (Exception ex)
            {
                report.Checks.Add(new CheckResult("visualstudio.detect", CheckStatus.Unknown, "probe error: " + ex.Message));
            }

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
