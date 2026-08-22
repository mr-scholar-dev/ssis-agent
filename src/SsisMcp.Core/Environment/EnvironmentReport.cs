using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SsisMcp.Core.Environment
{
    /// <summary>Aggregated result of all environment probes. Serializable DTO for MCP.</summary>
    public sealed class EnvironmentReport
    {
        public List<CheckResult> Checks { get; } = new List<CheckResult>();

        /// <summary>True when no critical check failed. The core may run.</summary>
        public bool CoreUsable => !Checks.Any(c => c.Critical && c.Status == CheckStatus.Fail);

        public CheckResult? Find(string name) =>
            Checks.FirstOrDefault(c => c.Name == name);

        /// <summary>Renders the human-readable report in the format requested in Fase 0.</summary>
        public string ToDisplayString()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Environment");
            sb.AppendLine("-----------");
            var width = Checks.Count == 0 ? 0 : Checks.Max(c => c.Name.Length);
            foreach (var c in Checks)
            {
                sb.Append(c.Name.PadRight(width));
                sb.Append(" : ");
                sb.Append(c.Status.ToString().ToUpperInvariant());
                if (!string.IsNullOrWhiteSpace(c.Detail))
                {
                    sb.Append("  (");
                    sb.Append(c.Detail);
                    sb.Append(')');
                }
                sb.AppendLine();
            }
            sb.AppendLine();
            sb.AppendLine(CoreUsable
                ? "RESULT: core is usable (no critical failures)."
                : "RESULT: core is NOT usable — a critical requirement is missing (see FAIL above).");
            return sb.ToString();
        }
    }
}
