using System.Collections.Generic;
using SsisMcp.Core.Lineage;
using Wrapper = Microsoft.SqlServer.Dts.Pipeline.Wrapper;

namespace SsisMcp.Ssis.Lineage
{
    /// <summary>
    /// Generic SSIS Metadata &amp; Lineage engine. Builds a lineage graph, validates lineage and
    /// external metadata, and repairs stale references by stable identity — NOT by re-writing old
    /// lineage IDs (SSIS may have reassigned them on reload). Repairs are bounded and confidence-rated.
    /// Backs the internal metadata.*/lineage.*/mapping.rebind tools (not exposed over MCP yet).
    /// </summary>
    public sealed class MetadataLineageEngine
    {
        private readonly IReadOnlyList<ILineageReferenceHandler> _handlers;

        public MetadataLineageEngine(IEnumerable<ILineageReferenceHandler>? handlers = null)
        {
            _handlers = handlers != null
                ? new List<ILineageReferenceHandler>(handlers)
                : new List<ILineageReferenceHandler> { new DataConversionLineageHandler(), new LookupLineageReferenceHandler() };
        }

        /// <summary>Builds a serializable lineage graph for a pipeline (lineage.inspect).</summary>
        public LineageGraphInfo BuildGraph(Wrapper.MainPipe pipe, string dataFlowTaskName)
        {
            var graph = new LineageGraphInfo { DataFlowTask = dataFlowTaskName };
            var producedLineages = new HashSet<int>();

            foreach (Wrapper.IDTSComponentMetaData100 c in pipe.ComponentMetaDataCollection)
                foreach (Wrapper.IDTSOutput100 o in c.OutputCollection)
                    foreach (Wrapper.IDTSOutputColumn100 col in o.OutputColumnCollection)
                    {
                        producedLineages.Add(col.LineageID);
                        graph.Columns.Add(new LineageColumnNode
                        {
                            LineageId = col.LineageID,
                            Column = col.Name,
                            ProducingComponent = c.Name,
                            DataType = col.DataType.ToString()
                        });
                    }

            foreach (Wrapper.IDTSComponentMetaData100 c in pipe.ComponentMetaDataCollection)
                foreach (Wrapper.IDTSInput100 input in c.InputCollection)
                    foreach (Wrapper.IDTSInputColumn100 ic in input.InputColumnCollection)
                        graph.Edges.Add(new LineageEdge
                        {
                            LineageId = ic.LineageID,
                            ConsumingComponent = c.Name,
                            ConsumingInput = input.Name,
                            Orphaned = !producedLineages.Contains(ic.LineageID)
                        });

            return graph;
        }

        /// <summary>Validates lineage references (via handlers) and basic external metadata.</summary>
        public LineageValidationResult Validate(Wrapper.MainPipe pipe)
        {
            var result = new LineageValidationResult();

            foreach (Wrapper.IDTSComponentMetaData100 c in pipe.ComponentMetaDataCollection)
            {
                var handler = HandlerFor(c);
                if (handler != null)
                    foreach (var stale in handler.FindStale(c))
                        result.Stale.Add(stale);
            }

            // Orphaned input-column references (input consumes a lineage no upstream output produces).
            var produced = new HashSet<int>();
            foreach (Wrapper.IDTSComponentMetaData100 c in pipe.ComponentMetaDataCollection)
                foreach (Wrapper.IDTSOutput100 o in c.OutputCollection)
                    foreach (Wrapper.IDTSOutputColumn100 col in o.OutputColumnCollection)
                        produced.Add(col.LineageID);

            foreach (Wrapper.IDTSComponentMetaData100 c in pipe.ComponentMetaDataCollection)
                foreach (Wrapper.IDTSInput100 input in c.InputCollection)
                    foreach (Wrapper.IDTSInputColumn100 ic in input.InputColumnCollection)
                        if (!produced.Contains(ic.LineageID))
                            result.ExternalMetadataIssues.Add(
                                $"{c.Name}.{input.Name}: input column references lineage {ic.LineageID} not produced upstream");

            return result;
        }

        /// <summary>
        /// Bounded repair loop: validate → rebind stale references → re-validate, up to
        /// <paramref name="maxPasses"/>. Never loops forever. Returns a full diagnostic report.
        /// </summary>
        public LineageRepairReport Repair(Wrapper.MainPipe pipe, RepairMode mode, int maxPasses = 3)
        {
            var report = new LineageRepairReport();
            for (var pass = 0; pass < maxPasses; pass++)
            {
                report.Passes = pass + 1;
                var validation = Validate(pipe);
                if (validation.IsValid) { report.FinalValid = true; break; }

                if (mode == RepairMode.DiagnoseOnly)
                {
                    foreach (var s in validation.Stale)
                        report.Actions.Add(new RepairAction
                        {
                            RepairType = "Diagnose", Component = s.Component,
                            OldReference = s.Carrier, Confidence = RepairConfidence.None, Reason = s.Reason
                        });
                    break;
                }

                var appliedAny = false;
                foreach (Wrapper.IDTSComponentMetaData100 c in pipe.ComponentMetaDataCollection)
                {
                    var handler = HandlerFor(c);
                    if (handler == null) continue;
                    foreach (var action in handler.Rebind(c, mode))
                    {
                        report.Actions.Add(action);
                        if (action.Applied) appliedAny = true;
                    }
                }
                if (!appliedAny) break; // nothing safely repairable — stop, don't spin
            }

            if (!report.FinalValid)
            {
                var finalCheck = Validate(pipe);
                report.FinalValid = finalCheck.IsValid;
            }
            report.ManualInterventionRequired = !report.FinalValid;
            return report;
        }

        private ILineageReferenceHandler? HandlerFor(Wrapper.IDTSComponentMetaData100 c)
        {
            foreach (var h in _handlers) if (h.Handles(c)) return h;
            return null;
        }
    }
}
