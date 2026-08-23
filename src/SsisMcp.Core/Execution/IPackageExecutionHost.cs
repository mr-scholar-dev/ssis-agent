using System.Collections.Generic;

namespace SsisMcp.Core.Execution
{
    /// <summary>Outcome of attempting to execute a package.</summary>
    public enum ExecutionOutcome
    {
        Success,
        Failure,
        /// <summary>Execution could not run because the environment blocks it (e.g. SSIS edition licensing).</summary>
        EnvironmentBlocked
    }

    /// <summary>Result of a package execution attempt.</summary>
    public sealed class ExecutionResult
    {
        public ExecutionOutcome Outcome { get; set; }
        public List<string> Errors { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public string? Detail { get; set; }
        public bool Succeeded => Outcome == ExecutionOutcome.Success;
    }

    /// <summary>
    /// Abstraction over "how a package gets executed", so the builder/planner never couple to a
    /// concrete execution mechanism. Future implementations: InProcessExecutionHost,
    /// DtexecExecutionHost, RemoteExecutionHost. Remote infrastructure is NOT built yet.
    /// </summary>
    public interface IPackageExecutionHost
    {
        /// <summary>Human-readable name of the host (for diagnostics).</summary>
        string Name { get; }

        /// <summary>Whether this host can currently execute a Data Flow (pipeline) on this machine.</summary>
        bool CanExecuteDataFlow(out string reason);

        /// <summary>Executes the package at <paramref name="packagePath"/>.</summary>
        ExecutionResult Execute(string packagePath);
    }
}
