using System;
using System.Collections.Generic;
using SsisMcp.Core.Building;

namespace SsisMcp.Ssis.Building
{
    /// <summary>
    /// Maps logical task kinds to SSIS creation names/monikers. Centralized so future
    /// runtimes/targets can vary the monikers behind one adapter (per requirement #11).
    /// </summary>
    public sealed class SsisComponentCatalog : ISsisComponentCatalog
    {
        private static readonly Dictionary<string, string> Map =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [TaskKinds.ExecuteSql] = "Microsoft.ExecuteSQLTask",
                [TaskKinds.DataFlow]   = "Microsoft.Pipeline",
                [TaskKinds.Script]     = "Microsoft.ScriptTask",
                [TaskKinds.Sequence]   = "STOCK:SEQUENCE",
                [TaskKinds.ForLoop]    = "STOCK:FORLOOP",
                [TaskKinds.ForEachLoop]= "STOCK:FOREACHLOOP",
            };

        public bool TryResolveTask(string logicalKey, out string creationName) =>
            Map.TryGetValue(logicalKey, out creationName!);

        public IReadOnlyCollection<string> SupportedTaskKeys => Map.Keys;
    }
}
