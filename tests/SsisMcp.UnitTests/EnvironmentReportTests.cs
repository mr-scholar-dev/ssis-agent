using SsisMcp.Core.Environment;
using Xunit;

namespace SsisMcp.UnitTests
{
    public class EnvironmentReportTests
    {
        [Fact]
        public void CoreUsable_is_false_when_a_critical_check_fails()
        {
            var report = new EnvironmentReport();
            report.Checks.Add(new CheckResult("ssis.runtime", CheckStatus.Fail, "missing", critical: true));
            report.Checks.Add(new CheckResult("os", CheckStatus.Pass));
            Assert.False(report.CoreUsable);
        }

        [Fact]
        public void CoreUsable_is_true_when_only_noncritical_checks_fail()
        {
            var report = new EnvironmentReport();
            report.Checks.Add(new CheckResult("sqlserver.connectivity", CheckStatus.Fail, "down", critical: false));
            report.Checks.Add(new CheckResult("ssis.runtime", CheckStatus.Pass, critical: true));
            Assert.True(report.CoreUsable);
        }

        [Fact]
        public void DisplayString_contains_header_and_check_names()
        {
            var report = new EnvironmentReport();
            report.Checks.Add(new CheckResult("ssis.runtime", CheckStatus.Pass, "ManagedDTS v17", critical: true));
            var text = report.ToDisplayString();
            Assert.Contains("Environment", text);
            Assert.Contains("ssis.runtime", text);
            Assert.Contains("PASS", text);
        }
    }
}
