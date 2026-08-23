using System.Collections.Generic;
using SsisMcp.Core.Lineage;
using SsisMcp.Ssis.Lineage;
using Dts = Microsoft.SqlServer.Dts.Runtime;
using Wrapper = Microsoft.SqlServer.Dts.Pipeline.Wrapper;

namespace SsisMcp.Ssis.Inspection
{
    /// <summary>Structured lineage/metadata snapshot of one Data Flow Task, read from a .dtsx.</summary>
    public sealed class LineageReport
    {
        public string DataFlowTask { get; set; } = "";
        public bool IsValid { get; set; }
        public LineageGraphInfo? Graph { get; set; }
        public List<StaleLineageReference> Stale { get; } = new List<StaleLineageReference>();
    }

    /// <summary>Loads a package and reports the lineage graph + validation of a named Data Flow Task.</summary>
    public sealed class LineageInspector
    {
        private readonly PackageService _svc;
        public LineageInspector(PackageService? svc = null) => _svc = svc ?? new PackageService();

        public LineageReport Inspect(string packagePath, string dataFlowTaskName)
        {
            var pkg = _svc.Load(packagePath);
            var pipe = FindPipeline(pkg, dataFlowTaskName)
                ?? throw new System.InvalidOperationException($"Data Flow Task '{dataFlowTaskName}' not found");
            var engine = new MetadataLineageEngine();
            var validation = engine.Validate(pipe);
            var report = new LineageReport
            {
                DataFlowTask = dataFlowTaskName,
                IsValid = validation.IsValid,
                Graph = engine.BuildGraph(pipe, dataFlowTaskName)
            };
            report.Stale.AddRange(validation.Stale);
            return report;
        }

        private static Wrapper.MainPipe? FindPipeline(Dts.Package pkg, string dftName)
        {
            foreach (var e in Flatten(pkg.Executables))
                if (e is Dts.TaskHost th && th.Name == dftName && th.InnerObject is Wrapper.MainPipe pipe)
                    return pipe;
            return null;
        }

        private static IEnumerable<Dts.Executable> Flatten(Dts.Executables executables)
        {
            foreach (Dts.Executable e in executables)
            {
                yield return e;
                if (e is Dts.IDTSSequence seq)
                    foreach (var c in Flatten(seq.Executables)) yield return c;
            }
        }
    }
}
