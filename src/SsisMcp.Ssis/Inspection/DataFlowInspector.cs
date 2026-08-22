using System.Collections.Generic;
using System.Linq;
using SsisMcp.Core.Packages;
using Dts = Microsoft.SqlServer.Dts.Runtime;
using Wrapper = Microsoft.SqlServer.Dts.Pipeline.Wrapper;

namespace SsisMcp.Ssis.Inspection
{
    /// <summary>
    /// Reads Data Flow pipelines via the SSIS Pipeline API: every component with its inputs, outputs,
    /// columns (name, lineage id, SSIS data type + length/precision/scale/codepage), external metadata
    /// columns, referenced connection managers, and the paths connecting outputs to inputs.
    /// </summary>
    internal static class DataFlowInspector
    {
        public static void Populate(Dts.Package package, PackageInfo info, Dictionary<string, string> cmById)
        {
            foreach (Dts.Executable exec in EnumerateAll(package.Executables))
            {
                if (!(exec is Dts.TaskHost th)) continue;
                var pipe = th.InnerObject as Wrapper.MainPipe;
                if (pipe == null) continue;

                var df = new DataFlowInfo { TaskName = th.Name };
                var outputMap = new Dictionary<int, (string comp, string port)>();
                var inputMap = new Dictionary<int, (string comp, string port)>();

                foreach (Wrapper.IDTSComponentMetaData100 c in pipe.ComponentMetaDataCollection)
                {
                    var comp = new ComponentInfo
                    {
                        Name = c.Name,
                        Id = c.ID,
                        ComponentClassId = c.ComponentClassID
                    };

                    foreach (Wrapper.IDTSRuntimeConnection100 rc in c.RuntimeConnectionCollection)
                    {
                        var id = rc.ConnectionManagerID;
                        if (string.IsNullOrEmpty(id)) continue;
                        comp.ConnectionManagers.Add(PackageService.ResolveConnection(cmById, id));
                    }

                    foreach (Wrapper.IDTSInput100 inp in c.InputCollection)
                    {
                        var io = new InputOutputInfo { Name = inp.Name, Id = inp.ID };
                        foreach (Wrapper.IDTSInputColumn100 col in inp.InputColumnCollection)
                            io.Columns.Add(new ColumnInfo
                            {
                                LineageId = col.LineageID,
                                DataType = col.DataType.ToString(),
                                Length = col.Length,
                                Precision = col.Precision,
                                Scale = col.Scale,
                                CodePage = col.CodePage
                            });
                        AddExternalMetadata(inp.ExternalMetadataColumnCollection, io);
                        comp.Inputs.Add(io);
                        inputMap[inp.ID] = (c.Name, inp.Name);
                    }

                    foreach (Wrapper.IDTSOutput100 outp in c.OutputCollection)
                    {
                        var io = new InputOutputInfo { Name = outp.Name, Id = outp.ID, IsErrorOut = outp.IsErrorOut };
                        foreach (Wrapper.IDTSOutputColumn100 col in outp.OutputColumnCollection)
                        {
                            io.Columns.Add(new ColumnInfo
                            {
                                Name = col.Name,
                                LineageId = col.LineageID,
                                DataType = col.DataType.ToString(),
                                Length = col.Length,
                                Precision = col.Precision,
                                Scale = col.Scale,
                                CodePage = col.CodePage
                            });
                        }
                        AddExternalMetadata(outp.ExternalMetadataColumnCollection, io);
                        comp.Outputs.Add(io);
                        outputMap[outp.ID] = (c.Name, outp.Name);
                    }

                    comp.Role = InferRole(comp);
                    df.Components.Add(comp);
                }

                foreach (Wrapper.IDTSPath100 path in pipe.PathCollection)
                {
                    var p = new PathInfo { Name = path.Name, Id = path.ID };
                    if (path.StartPoint != null && outputMap.TryGetValue(path.StartPoint.ID, out var s))
                    {
                        p.StartComponent = s.comp;
                        p.StartOutput = s.port;
                    }
                    if (path.EndPoint != null && inputMap.TryGetValue(path.EndPoint.ID, out var e))
                    {
                        p.EndComponent = e.comp;
                        p.EndInput = e.port;
                    }
                    df.Paths.Add(p);
                }

                info.DataFlows.Add(df);
            }
        }

        private static void AddExternalMetadata(Wrapper.IDTSExternalMetadataColumnCollection100 cols, InputOutputInfo io)
        {
            foreach (Wrapper.IDTSExternalMetadataColumn100 ex in cols)
                io.ExternalMetadataColumns.Add(new ExternalColumnInfo
                {
                    Name = ex.Name,
                    Id = ex.ID,
                    DataType = ex.DataType.ToString(),
                    Length = ex.Length,
                    Precision = ex.Precision,
                    Scale = ex.Scale,
                    CodePage = ex.CodePage
                });
        }

        private static string InferRole(ComponentInfo comp)
        {
            var hasInputs = comp.Inputs.Count > 0;
            var hasNonErrorOutput = comp.Outputs.Any(o => !o.IsErrorOut);
            if (!hasInputs && hasNonErrorOutput) return "source";
            if (hasInputs && !hasNonErrorOutput) return "destination";
            return "transformation";
        }

        private static IEnumerable<Dts.Executable> EnumerateAll(Dts.Executables executables)
        {
            foreach (Dts.Executable exec in executables)
            {
                yield return exec;
                if (exec is Dts.IDTSSequence seq)
                    foreach (var child in EnumerateAll(seq.Executables))
                        yield return child;
            }
        }
    }
}
