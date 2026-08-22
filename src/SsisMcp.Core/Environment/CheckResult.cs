using System.Collections.Generic;

namespace SsisMcp.Core.Environment
{
    /// <summary>
    /// Result of a single environment probe. Designed as a stable DTO so it can be
    /// serialized straight into an MCP <c>environment.detect</c> response.
    /// </summary>
    public sealed class CheckResult
    {
        public CheckResult(string name, CheckStatus status, string? detail = null, bool critical = false)
        {
            Name = name;
            Status = status;
            Detail = detail;
            Critical = critical;
            Data = new Dictionary<string, string>();
        }

        /// <summary>Stable identifier for the check, e.g. <c>ssis.runtime</c>.</summary>
        public string Name { get; }

        public CheckStatus Status { get; set; }

        /// <summary>Human-readable explanation (esp. for Warn/Fail).</summary>
        public string? Detail { get; set; }

        /// <summary>If true, a Fail here means the core cannot function.</summary>
        public bool Critical { get; }

        /// <summary>Structured extras (versions, paths) for machine consumption.</summary>
        public Dictionary<string, string> Data { get; }
    }
}
