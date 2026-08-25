using System.Collections.Generic;

namespace SsisMcp.Core.Packages
{
    /// <summary>Structured, serializable snapshot of a package. Backs <c>package.inspect</c>.</summary>
    public sealed class PackageInfo
    {
        public string Name { get; set; } = "";
        public string? Id { get; set; }
        public string? TargetServerVersion { get; set; }
        public int? PackageFormatVersion { get; set; }
        public string? ProtectionLevel { get; set; }

        /// <summary>Top-level Control Flow executables (each may have Children for containers).</summary>
        public List<ExecutableInfo> Executables { get; } = new List<ExecutableInfo>();

        /// <summary>All precedence constraints in the package (recursively, including inside containers).</summary>
        public List<PrecedenceConstraintInfo> PrecedenceConstraints { get; } = new List<PrecedenceConstraintInfo>();

        public List<ConnectionInfo> Connections { get; } = new List<ConnectionInfo>();

        /// <summary>Data Flow details, one per Data Flow Task in the package.</summary>
        public List<DataFlowInfo> DataFlows { get; } = new List<DataFlowInfo>();
    }

    /// <summary>A Control Flow executable (task or container). Hierarchical via <see cref="Children"/>.</summary>
    public sealed class ExecutableInfo
    {
        public string Name { get; set; } = "";
        public string? Id { get; set; }
        public string? TypeName { get; set; }
        public string? CreationName { get; set; }
        public string? Description { get; set; }

        /// <summary>Connection manager names referenced by this task, when discoverable.</summary>
        public List<string> ConnectionManagers { get; } = new List<string>();

        /// <summary>True when this executable hosts a Data Flow (its detail is in <see cref="PackageInfo.DataFlows"/>).</summary>
        public bool IsDataFlow { get; set; }

        /// <summary>Child executables when this is a container (Sequence, ForEach, ForLoop).</summary>
        public List<ExecutableInfo> Children { get; } = new List<ExecutableInfo>();
    }

    /// <summary>A connection manager.</summary>
    public sealed class ConnectionInfo
    {
        public string Name { get; set; } = "";
        public string? CreationName { get; set; }
        public string? Id { get; set; }
    }

    /// <summary>A precedence constraint edge between two executables.</summary>
    public sealed class PrecedenceConstraintInfo
    {
        public string From { get; set; } = "";
        public string To { get; set; } = "";

        /// <summary>Success / Failure / Completion.</summary>
        public string Value { get; set; } = "";

        /// <summary>Constraint / Expression / ExpressionAndConstraint / ExpressionOrConstraint.</summary>
        public string EvalOperation { get; set; } = "";

        public string? Expression { get; set; }
    }
}
