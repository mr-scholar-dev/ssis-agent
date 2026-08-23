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
    /// <summary>How to apply layout to a package that may already have designer coordinates.</summary>
    public enum LayoutMode
    {
        /// <summary>Keep every existing NodeLayout; only position nodes that have none.</summary>
        AddMissing,
        /// <summary>Recompute positions for all nodes (ignores existing layout).</summary>
        Relayout
    }

    /// <summary>Computed position/size of a node (design-time coordinates).</summary>
    public sealed class NodeBox
    {
        public string Id { get; set; } = "";      // e.g. "Package\\SqlBorrar"
        public string Name { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double H { get; set; }
    }

    /// <summary>
    /// Computes a deterministic top→bottom Control Flow layout from the precedence DAG and writes it
    /// into the package's <c>&lt;DTS:DesignTimeProperties&gt;</c> GraphLayout (NodeLayout per task).
    /// EdgeLayouts are intentionally omitted so the designer auto-routes connectors. This edits ONLY
    /// the layout annotation — never functional configuration.
    ///
    /// Format grounded on a real VS 2022-authored golden (tests/fixtures/golden/vs2022-control-flow.dtsx).
    /// </summary>
    public sealed class ControlFlowLayoutEngine
    {
        private const double NodeW = 180, NodeH = 42, VGap = 60, HGap = 40, MarginX = 20, MarginY = 20;
        private const string DesignNs = "clr-namespace:Microsoft.SqlServer.IntegrationServices.Designer.Model.Serialization;assembly=Microsoft.SqlServer.IntegrationServices.Graph";

        /// <summary>Applies layout to the .dtsx on disk. Returns the boxes it positioned.</summary>
        public IReadOnlyList<NodeBox> Apply(string dtsxPath, PackageInfo info, LayoutMode mode = LayoutMode.AddMissing)
        {
            var xml = File.ReadAllText(dtsxPath);
            var existing = ExtractExistingNodeIds(xml);

            var boxes = Compute(info);
            if (mode == LayoutMode.AddMissing && existing.Count > 0)
            {
                // Preserve manually-arranged nodes: only inject when there is no layout at all.
                // (Merging into an existing GraphLayout is a later refinement.)
                return boxes.Where(b => !existing.Contains(b.Id)).ToList();
            }

            var cdata = BuildDesignTimeProperties(boxes);
            var updated = InjectDesignTimeProperties(xml, cdata);
            File.WriteAllText(dtsxPath, updated, new UTF8Encoding(false));
            return boxes;
        }

        /// <summary>Top→bottom layered layout: layer = longest-path depth; siblings spread on X.</summary>
        public IReadOnlyList<NodeBox> Compute(PackageInfo info)
        {
            var names = TopLevelNames(info);
            var succ = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var indeg = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var n in names) { succ[n] = new List<string>(); indeg[n] = 0; }
            foreach (var pc in info.PrecedenceConstraints)
                if (succ.ContainsKey(pc.From) && indeg.ContainsKey(pc.To))
                { succ[pc.From].Add(pc.To); indeg[pc.To]++; }

            // longest-path layering (Kahn-style with depth propagation)
            var depth = names.ToDictionary(n => n, _ => 0, StringComparer.Ordinal);
            var queue = new Queue<string>(names.Where(n => indeg[n] == 0));
            var remaining = new Dictionary<string, int>(indeg, StringComparer.Ordinal);
            while (queue.Count > 0)
            {
                var n = queue.Dequeue();
                foreach (var m in succ[n])
                {
                    if (depth[m] < depth[n] + 1) depth[m] = depth[n] + 1;
                    if (--remaining[m] == 0) queue.Enqueue(m);
                }
            }

            var byLayer = names.GroupBy(n => depth[n]).OrderBy(g => g.Key);
            var boxes = new List<NodeBox>();
            foreach (var layer in byLayer)
            {
                var ordered = layer.OrderBy(n => n, StringComparer.Ordinal).ToList();
                for (var i = 0; i < ordered.Count; i++)
                {
                    boxes.Add(new NodeBox
                    {
                        Id = "Package\\" + ordered[i],
                        Name = ordered[i],
                        X = MarginX + i * (NodeW + HGap),
                        Y = MarginY + layer.Key * (NodeH + VGap),
                        W = NodeW,
                        H = NodeH
                    });
                }
            }
            return boxes;
        }

        private static List<string> TopLevelNames(PackageInfo info) =>
            info.Executables.Select(e => e.Name).Where(n => !string.IsNullOrEmpty(n)).ToList();

        private static HashSet<string> ExtractExistingNodeIds(string xml)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(xml, "<NodeLayout[^>]*Id=\"([^\"]+)\""))
                ids.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            return ids;
        }

        private static string BuildDesignTimeProperties(IReadOnlyList<NodeBox> boxes)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\"?>\n");
            sb.Append("<!--This CDATA section contains the layout information of the package, written by SsisMcp.Designer.-->\n");
            sb.Append("<Objects Version=\"8\">\n  <Package design-time-name=\"Package\">\n    <LayoutInfo>\n");
            sb.Append("      <GraphLayout Capacity=\"16\" xmlns=\"").Append(DesignNs).Append("\" ");
            sb.Append("xmlns:mssgle=\"clr-namespace:Microsoft.SqlServer.Graph.LayoutEngine;assembly=Microsoft.SqlServer.Graph\" ");
            sb.Append("xmlns:assembly=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n");
            foreach (var b in boxes)
                sb.Append("        <NodeLayout Size=\"")
                  .Append(Num(b.W)).Append(',').Append(Num(b.H))
                  .Append("\" Id=\"").Append(System.Security.SecurityElement.Escape(b.Id))
                  .Append("\" TopLeft=\"").Append(Num(b.X)).Append(',').Append(Num(b.Y)).Append("\" />\n");
            sb.Append("      </GraphLayout>\n    </LayoutInfo>\n  </Package>\n</Objects>");
            return sb.ToString();
        }

        private static string InjectDesignTimeProperties(string xml, string cdata)
        {
            var element = "<DTS:DesignTimeProperties><![CDATA[" + cdata + "]]></DTS:DesignTimeProperties>";
            var existing = Regex.Match(xml, "<DTS:DesignTimeProperties>.*?</DTS:DesignTimeProperties>", RegexOptions.Singleline);
            if (existing.Success)
                return xml.Substring(0, existing.Index) + element + xml.Substring(existing.Index + existing.Length);

            // Insert before the final closing </DTS:Executable> (package root close).
            var close = xml.LastIndexOf("</DTS:Executable>", StringComparison.Ordinal);
            if (close < 0) return xml; // not a package we recognize; leave untouched
            return xml.Substring(0, close) + "  " + element + "\n" + xml.Substring(close);
        }

        private static string Num(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
