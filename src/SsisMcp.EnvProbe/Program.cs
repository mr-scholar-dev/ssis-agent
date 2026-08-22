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
