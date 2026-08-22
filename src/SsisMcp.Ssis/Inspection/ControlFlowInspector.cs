using System.Collections.Generic;
using SsisMcp.Core.Packages;
using Dts = Microsoft.SqlServer.Dts.Runtime;

namespace SsisMcp.Ssis.Inspection
{
    /// <summary>
    /// Reads Control Flow structure via the SSIS Object Model: the executable tree (containers keep
    /// their children) plus every precedence constraint (recursively, including inside containers)
    /// with source, target, value, evaluation operation and expression.
    /// </summary>
    internal static class ControlFlowInspector
    {
        public static void Populate(Dts.Package package, PackageInfo info, Dictionary<string, string> cmById)
        {
            foreach (Dts.Executable exec in package.Executables)
                info.Executables.Add(Describe(exec, cmById));

            CollectConstraints(package.PrecedenceConstraints, info.PrecedenceConstraints);
            CollectConstraintsRecursive(package.Executables, info.PrecedenceConstraints);
        }

        private static ExecutableInfo Describe(Dts.Executable exec, Dictionary<string, string> cmById)
        {
            if (exec is Dts.TaskHost th)
            {
                var e = new ExecutableInfo
                {
                    Name = th.Name,
                    Id = th.ID,
                    CreationName = th.CreationName,
                    Description = th.Description,
                    TypeName = SafeInnerTypeName(th),
                    IsDataFlow = IsDataFlow(th)
                };
                AddTaskConnections(th, e, cmById);
                return e;
            }

            if (exec is Dts.DtsContainer c)
            {
                var e = new ExecutableInfo
                {
                    Name = c.Name,
                    Id = c.ID,
                    CreationName = c.CreationName,
                    Description = c.Description,
                    TypeName = c.GetType().Name
                };
                if (exec is Dts.IDTSSequence seq)
                {
                    foreach (Dts.Executable child in seq.Executables)
                        e.Children.Add(Describe(child, cmById));
                }
                return e;
            }

            return new ExecutableInfo { Name = exec.GetType().Name, TypeName = exec.GetType().Name };
        }

        private static void CollectConstraintsRecursive(Dts.Executables executables, List<PrecedenceConstraintInfo> sink)
        {
            foreach (Dts.Executable exec in executables)
            {
                if (exec is Dts.IDTSSequence seq)
                {
                    CollectConstraints(seq.PrecedenceConstraints, sink);
                    CollectConstraintsRecursive(seq.Executables, sink);
                }
            }
        }

        private static void CollectConstraints(Dts.PrecedenceConstraints constraints, List<PrecedenceConstraintInfo> sink)
        {
            foreach (Dts.PrecedenceConstraint pc in constraints)
            {
                sink.Add(new PrecedenceConstraintInfo
                {
                    From = NameOf(pc.PrecedenceExecutable),
                    To = NameOf(pc.ConstrainedExecutable),
                    Value = pc.Value.ToString(),
                    EvalOperation = pc.EvalOp.ToString(),
                    Expression = string.IsNullOrEmpty(pc.Expression) ? null : pc.Expression
                });
            }
        }

        private static string NameOf(Dts.Executable exec)
        {
            if (exec is Dts.TaskHost th) return th.Name;
            if (exec is Dts.DtsContainer c) return c.Name;
            return exec?.GetType().Name ?? "(unknown)";
        }

        private static string? SafeInnerTypeName(Dts.TaskHost th)
        {
            try { return th.InnerObject?.GetType().Name; }
            catch { return null; }
        }

        private static bool IsDataFlow(Dts.TaskHost th)
        {
            var cn = th.CreationName ?? "";
            return cn.IndexOf("Pipeline", System.StringComparison.OrdinalIgnoreCase) >= 0
                || cn.IndexOf("DataFlow", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void AddTaskConnections(Dts.TaskHost th, ExecutableInfo e, Dictionary<string, string> cmById)
        {
            // Robust across task types: scan all task properties and keep any string value that
            // references a known connection manager (by id or name). Deduped, order-preserving.
            var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            object? inner = null;
            try { inner = th.InnerObject; } catch { /* some hosts have no managed inner object */ }

            foreach (Dts.DtsProperty prop in th.Properties)
            {
                // Task-specific properties (e.g. "Connection") throw TargetException via DtsProperty
                // reflection; for COM-backed tasks we fall back to late-bound IDispatch on the inner object.
                var val = TryGetString(prop, th)
                          ?? (inner != null ? TryGetString(prop, inner) : null)
                          ?? (inner != null ? TryGetComString(inner, prop.Name) : null);
                if (string.IsNullOrEmpty(val)) continue;

                var resolved = PackageService.ResolveConnection(cmById, val!);
                // Only keep it when it actually mapped to a connection (resolved != raw, or raw is a CM name).
                if (!ReferenceEquals(resolved, val) || cmById.ContainsKey(val!))
                {
                    if (seen.Add(resolved)) e.ConnectionManagers.Add(resolved);
                }
            }
        }

        private static string? TryGetString(Dts.DtsProperty prop, object target)
        {
            try { return prop.GetValue(target) as string; }
            catch { return null; }
        }

        private static string? TryGetComString(object comObject, string propertyName)
        {
            try
            {
                var v = comObject.GetType().InvokeMember(
                    propertyName,
                    System.Reflection.BindingFlags.GetProperty,
                    null, comObject, null);
                return v as string;
            }
            catch { return null; }
        }
    }
}
