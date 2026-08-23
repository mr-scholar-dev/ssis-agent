using System;
using System.Collections.Generic;
using SsisMcp.Core.Building;

namespace SsisMcp.Ssis.Building
{
    /// <summary>
    /// Maps logical Data Flow component kinds to SSIS component class ids (monikers). Centralized
    /// per requirement #2 so future runtimes/targets can vary the ids behind one adapter.
    /// </summary>
    public sealed class SsisPipelineComponentCatalog : ISsisPipelineComponentCatalog
    {
        private static readonly Dictionary<string, string> Map =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ComponentKinds.OleDbSource]        = "Microsoft.OLEDBSource",
                [ComponentKinds.OleDbDestination]   = "Microsoft.OLEDBDestination",
                [ComponentKinds.ExcelSource]        = "Microsoft.ExcelSource",
                [ComponentKinds.ExcelDestination]   = "Microsoft.ExcelDestination",
                [ComponentKinds.FlatFileSource]     = "Microsoft.FlatFileSource",
                [ComponentKinds.FlatFileDestination]= "Microsoft.FlatFileDestination",
                [ComponentKinds.DataConversion]     = "Microsoft.DataConvert",
                [ComponentKinds.DerivedColumn]      = "Microsoft.DerivedColumn",
                [ComponentKinds.ConditionalSplit]   = "Microsoft.ConditionalSplit",
                [ComponentKinds.Lookup]             = "Microsoft.Lookup",
                [ComponentKinds.Aggregate]          = "Microsoft.Aggregate",
                [ComponentKinds.Sort]               = "Microsoft.Sort",
                [ComponentKinds.UnionAll]           = "Microsoft.UnionAll",
                [ComponentKinds.Merge]              = "Microsoft.Merge",
                [ComponentKinds.MergeJoin]          = "Microsoft.MergeJoin",
                [ComponentKinds.Multicast]          = "Microsoft.Multicast",
                [ComponentKinds.RowCount]           = "Microsoft.RowCount",
            };

        public bool TryResolve(string logicalKey, out string componentClassId) =>
            Map.TryGetValue(logicalKey, out componentClassId!);

        public IReadOnlyCollection<string> SupportedKinds => Map.Keys;
    }
}
