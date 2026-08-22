using System;
using System.Collections.Generic;
using SsisMcp.Core.Packages;

namespace SsisMcp.Core.Building
{
    /// <summary>Structured error codes for Control Flow builder operations.</summary>
    public enum BuilderErrorCode
    {
        NameCollision,
        TaskNotFound,
        InvalidPrecedence,
        InvalidExpression,
        HasDependents,
        Unsupported,
        ValidationFailed,
        ExternalChange,
        Busy,
        MutationError
    }

    /// <summary>Thrown by builder operations for pre-commit, structurally-detectable failures.</summary>
    public sealed class BuilderException : Exception
    {
        public BuilderException(BuilderErrorCode code, string message) : base(message) => Code = code;
        public BuilderErrorCode Code { get; }
    }

    /// <summary>Precedence outcome the source task must reach.</summary>
    public enum PrecedenceValue { Success, Failure, Completion }

    /// <summary>How a precedence constraint is evaluated.</summary>
    public enum PrecedenceEval { Constraint, Expression, ExpressionAndConstraint, ExpressionOrConstraint }

    /// <summary>Logical task kinds resolved to version-specific creation names by the catalog.</summary>
    public static class TaskKinds
    {
        public const string ExecuteSql = "ExecuteSql";
        public const string DataFlow = "DataFlow";
        public const string Script = "Script";
        public const string Sequence = "Sequence";
        public const string ForLoop = "ForLoop";
        public const string ForEachLoop = "ForEachLoop";
    }

    /// <summary>Resolves logical task kinds to runtime creation names/monikers (version-specific).</summary>
    public interface ISsisComponentCatalog
    {
        bool TryResolveTask(string logicalKey, out string creationName);
        IReadOnlyCollection<string> SupportedTaskKeys { get; }
    }

    /// <summary>Result of a Control Flow builder operation applied through the Safety layer.</summary>
    public sealed class OperationResult
    {
        public bool Succeeded { get; set; }
        public string? ErrorCode { get; set; }
        public string? Detail { get; set; }

        /// <summary>Safety transaction state (Committed/Failed/Aborted/Busy/…), when a write was attempted.</summary>
        public string? SafetyState { get; set; }
        public string? BackupPath { get; set; }

        /// <summary>Re-inspected package after a successful commit (reload-from-disk snapshot).</summary>
        public PackageInfo? Package { get; set; }

        public static OperationResult Fail(BuilderErrorCode code, string detail) =>
            new OperationResult { Succeeded = false, ErrorCode = code.ToString(), Detail = detail };
    }
}
