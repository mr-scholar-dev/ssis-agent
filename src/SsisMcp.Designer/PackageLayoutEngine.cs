using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SsisMcp.Core.Packages;

namespace SsisMcp.Designer
{
    /// <summary>
    /// Unified two-level designer layout for a REAL package: one <c>DesignTimeProperties</c> holding
    /// BOTH the Control Flow task layout (a <c>&lt;Package&gt;</c> GraphLayout, laid out top→bottom by the
    /// precedence DAG) AND an independent Data Flow layout for EVERY Data Flow Task (one
    /// <c>&lt;TaskHost design-time-name="Package\&lt;DFT&gt;"&gt;</c> block per DFT, components top→bottom by the
    /// pipeline path graph, branches spread on X). Supports N DFTs in the same .dtsx.
    ///
    /// This is what the MCP uses for real packages (Package.dtsx with SqlBorrar + DFTCliente + …), so
    /// double-clicking a DFT in VS 2022 shows that DFT's own laid-out pipeline. Writes ONLY layout.
    /// </summary>
    public sealed class PackageLayoutEngine
    {
        private const double NodeW = 170, NodeH = 42, GapMain = 60, GapCross = 40, Margin = 15;
        private const string GraphNs = "clr-namespace:Microsoft.SqlServer.IntegrationServices.Designer.Model.Serialization;assembly=Microsoft.SqlServer.IntegrationServices.Graph";

        public IReadOnlyList<NodeBox> Apply(string dtsxPath, PackageInfo info, LayoutMode mode = LayoutMode.AddMissing)
        {
            var xml = File.ReadAllText(dtsxPath);
            if (mode == LayoutMode.AddMissing && Regex.IsMatch(xml, "<NodeLayout")) return Array.Empty<NodeBox>();

            var boxes = Compute(info);
            var updated = Inject(xml, BuildDesignTimeProperties(info, boxes));
            File.WriteAllText(dtsxPath, updated, new UTF8Encoding(false));
            return boxes;
        }

        /// <summary>All node boxes: control-flow tasks (Id=Package\task) and DFT components (Id=Package\dft\comp).</summary>
        public IReadOnlyList<NodeBox> Compute(PackageInfo info)
        {
            var all = new List<NodeBox>();

            // Control Flow tasks (top→bottom by precedence).
            var cfNames = info.Executables.Select(e => e.Name).Where(n => !string.IsNullOrEmpty(n)).ToList();
            var cfEdges = info.PrecedenceConstraints.Select(p => (p.From, p.To));
            foreach (var (name, layer, index) in Layer(cfNames, cfEdges))
                all.Add(Box("Package\\" + name, name, layer, index));

            // Each Data Flow Task's internal pipeline (top→bottom by paths).
            foreach (var df in info.DataFlows)
            {
                var names = df.Components.Select(c => c.Name).Where(n => !string.IsNullOrEmpty(n)).ToList();
                var edges = df.Paths.Where(p => p.StartComponent != null && p.EndComponent != null)
                                    .Select(p => (p.StartComponent!, p.EndComponent!));
                foreach (var (name, layer, index) in Layer(names, edges))
                    all.Add(Box("Package\\" + df.TaskName + "\\" + name, name, layer, index));
            }
            return all;
        }

        private static NodeBox Box(string id, string name, int layer, int index) => new NodeBox
        {
            Id = id,
            Name = name,
            X = Margin + index * (NodeW + GapCross),   // siblings/branches across X
            Y = Margin + layer * (NodeH + GapMain),     // depth down Y (top→bottom)
            W = NodeW,
            H = NodeH
        };

        /// <summary>Longest-path layering: returns (name, layer, indexWithinLayer).</summary>
        private static IEnumerable<(string name, int layer, int index)> Layer(
            List<string> names, IEnumerable<(string from, string to)> edges)
        {
            var succ = names.ToDictionary(n => n, _ => new List<string>(), StringComparer.Ordinal);
            var indeg = names.ToDictionary(n => n, _ => 0, StringComparer.Ordinal);
            foreach (var (from, to) in edges)
                if (succ.ContainsKey(from) && indeg.ContainsKey(to)) { succ[from].Add(to); indeg[to]++; }

            var depth = names.ToDictionary(n => n, _ => 0, StringComparer.Ordinal);
            var remaining = new Dictionary<string, int>(indeg, StringComparer.Ordinal);
            var q = new Queue<string>(names.Where(n => indeg[n] == 0));
            while (q.Count > 0)
            {
                var n = q.Dequeue();
                foreach (var m in succ[n])
                {
                    if (depth[m] < depth[n] + 1) depth[m] = depth[n] + 1;
                    if (--remaining[m] == 0) q.Enqueue(m);
                }
            }

            var results = new List<(string, int, int)>();
            foreach (var layer in names.GroupBy(n => depth[n]).OrderBy(g => g.Key))
            {
                var ordered = layer.OrderBy(n => n, StringComparer.Ordinal).ToList();
                for (var i = 0; i < ordered.Count; i++) results.Add((ordered[i], layer.Key, i));
            }
            return results;
        }

        private static string BuildDesignTimeProperties(PackageInfo info, IReadOnlyList<NodeBox> boxes)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\"?>\n<!--Layout written by SsisMcp.Designer (Package: Control Flow + per-DFT Data Flow).-->\n");
            sb.Append("<Objects Version=\"8\">\n");

            sb.Append("  <Package design-time-name=\"Package\">\n    <LayoutInfo>\n      <GraphLayout Capacity=\"16\" xmlns=\"").Append(GraphNs).Append("\">\n");
            foreach (var b in boxes.Where(b => b.Id.IndexOf('\\', "Package\\".Length) < 0)) // Package\<task> only
                sb.Append(NodeLayout(b));
            sb.Append("      </GraphLayout>\n    </LayoutInfo>\n  </Package>\n");

            foreach (var df in info.DataFlows)
            {
                var prefix = "Package\\" + df.TaskName + "\\";
                sb.Append("  <TaskHost design-time-name=\"").Append(Esc("Package\\" + df.TaskName)).Append("\">\n    <LayoutInfo>\n");
                sb.Append("      <GraphLayout Capacity=\"16\" xmlns=\"").Append(GraphNs).Append("\">\n");
                foreach (var b in boxes.Where(b => b.Id.StartsWith(prefix, StringComparison.Ordinal)))
                    sb.Append(NodeLayout(b));
                sb.Append("      </GraphLayout>\n    </LayoutInfo>\n  </TaskHost>\n");
            }

            sb.Append("</Objects>");
            return sb.ToString();
        }

        private static string NodeLayout(NodeBox b) =>
            "        <NodeLayout Size=\"" + Num(b.W) + "," + Num(b.H) + "\" Id=\"" + Esc(b.Id) +
            "\" TopLeft=\"" + Num(b.X) + "," + Num(b.Y) + "\" />\n";

        private static string Inject(string xml, string cdata)
        {
            var element = "<DTS:DesignTimeProperties><![CDATA[" + cdata + "]]></DTS:DesignTimeProperties>";
            var existing = Regex.Match(xml, "<DTS:DesignTimeProperties>.*?</DTS:DesignTimeProperties>", RegexOptions.Singleline);
            if (existing.Success) return xml.Substring(0, existing.Index) + element + xml.Substring(existing.Index + existing.Length);
            var close = xml.LastIndexOf("</DTS:Executable>", StringComparison.Ordinal);
            return close < 0 ? xml : xml.Substring(0, close) + "  " + element + "\n" + xml.Substring(close);
        }

        private static string Esc(string s) => System.Security.SecurityElement.Escape(s);
        private static string Num(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
