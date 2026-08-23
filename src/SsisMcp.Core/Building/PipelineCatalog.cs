using System.Collections.Generic;

namespace SsisMcp.Core.Building
{
    /// <summary>Logical Data Flow component kinds resolved to version-specific class ids by the catalog.</summary>
    public static class ComponentKinds
    {
        public const string OleDbSource = "OleDbSource";
        public const string OleDbDestination = "OleDbDestination";
        public const string ExcelSource = "ExcelSource";
        public const string ExcelDestination = "ExcelDestination";
        public const string FlatFileSource = "FlatFileSource";
        public const string FlatFileDestination = "FlatFileDestination";
        public const string AdoNetSource = "AdoNetSource";
        public const string AdoNetDestination = "AdoNetDestination";
        public const string DataConversion = "DataConversion";
        public const string DerivedColumn = "DerivedColumn";
        public const string ConditionalSplit = "ConditionalSplit";
        public const string Lookup = "Lookup";
        // Second tier (implemented only after the above are green):
        public const string Aggregate = "Aggregate";
        public const string Sort = "Sort";
        public const string UnionAll = "UnionAll";
        public const string Merge = "Merge";
        public const string MergeJoin = "MergeJoin";
        public const string Multicast = "Multicast";
        public const string RowCount = "RowCount";
    }

    /// <summary>
    /// Resolves logical pipeline component kinds to runtime component class ids/monikers.
    /// Centralized so a future runtime/TargetServerVersion can vary the ids behind one adapter.
    /// </summary>
    public interface ISsisPipelineComponentCatalog
    {
        bool TryResolve(string logicalKey, out string componentClassId);
        IReadOnlyCollection<string> SupportedKinds { get; }
    }
}
