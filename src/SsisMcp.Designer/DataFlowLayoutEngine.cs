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
    /// Computes a deterministic LEFT→RIGHT Data Flow layout for each Data Flow Task's pipeline and
    /// writes it into <c>DesignTimeProperties</c>. Grounded on a real VS 2022 golden
    /// (tests/fixtures/golden/vs2022-data-flow.dtsx): layout is two-level —
    ///   &lt;Package&gt;…&lt;NodeLayout Id="Package\&lt;DFT&gt;"/&gt; (the DFT in the control flow), and
    ///   &lt;TaskHost design-time-name="Package\&lt;DFT&gt;"&gt;…&lt;NodeLayout Id="Package\&lt;DFT&gt;\&lt;comp&gt;"/&gt; per component.
    /// Branching components (Conditional Split, Lookup) spread their downstream nodes on Y so branches
    /// (Match / No Match / cases) don't overlap. EdgeLayouts are omitted so the designer auto-routes.
    /// Writes ONLY layout — never functional configuration.
    /// </summary>
    public sealed class DataFlowLayoutEngine
    {
        private const double NodeW = 150, NodeH = 42, HGap = 90, VGap = 60, MarginX = 12, MarginY = 12;
        private const string GraphNs = "clr-namespace:Microsoft.SqlServer.IntegrationServices.Designer.Model.Serialization;assembly=Microsoft.SqlServer.IntegrationServices.Graph";

        public IReadOnlyList<NodeBox> Apply(string dtsxPath, PackageInfo info, LayoutMode mode = LayoutMode.AddMissing)
        {
            var xml = File.ReadAllText(dtsxPath);
            if (mode == LayoutMode.AddMissing && Regex.IsMatch(xml, "<NodeLayout"))
                return Array.Empty<NodeBox>(); // preserve any existing arrangement

            var boxes = Compute(info);
            var cdata = BuildDesignTimeProperties(info, boxes);
            var updated = Inject(xml, cdata);
            File.WriteAllText(dtsxPath, updated, new UTF8Encoding(false));
            return boxes;
        }

        /// <summary>Left→right layered positions for every component of every data flow.</summary>
        public IReadOnlyList<NodeBox> Compute(PackageInfo info)
        {
            var all = new List<NodeBox>();
            foreach (var df in info.DataFlows)
            {
                var names = df.Components.Select(c => c.Name).Where(n => !string.IsNullOrEmpty(n)).ToList();
                var succ = names.ToDictionary(n => n, _ => new List<string>(), StringComparer.Ordinal);
                var indeg = names.ToDictionary(n => n, _ => 0, StringComparer.Ordinal);
                foreach (var p in df.Paths)
                    if (p.StartComponent != null && p.EndComponent != null
                        && succ.ContainsKey(p.StartComponent) && indeg.ContainsKey(p.EndComponent))
                    { succ[p.StartComponent].Add(p.EndComponent); indeg[p.EndComponent]++; }

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

                foreach (var layer in names.GroupBy(n => depth[n]).OrderBy(g => g.Key))
                {
                    var ordered = layer.OrderBy(n => n, StringComparer.Ordinal).ToList();
                    for (var i = 0; i < ordered.Count; i++)
                        all.Add(new NodeBox
                        {
                            Id = "Package\\" + df.TaskName + "\\" + ordered[i],
                            Name = ordered[i],
                            X = MarginX + layer.Key * (NodeW + HGap),   // left → right by depth
                            Y = MarginY + i * (NodeH + VGap),           // siblings/branches spread on Y
                            W = NodeW,
                            H = NodeH
                        });
                }
            }
            return all;
        }

        private static string BuildDesignTimeProperties(PackageInfo info, IReadOnlyList<NodeBox> compBoxes)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\"?>\n<!--Layout written by SsisMcp.Designer (Data Flow).-->\n");
            sb.Append("<Objects Version=\"8\">\n");

            // Package block: place each top-level executable (the DFT tasks) simply, top→bottom.
            sb.Append("  <Package design-time-name=\"Package\">\n    <LayoutInfo>\n      <GraphLayout Capacity=\"16\" xmlns=\"").Append(GraphNs).Append("\">\n");
            var y = MarginY;
            foreach (var e in info.Executables)
            {
                sb.Append("        <NodeLayout Size=\"150,41.6\" Id=\"").Append(Esc("Package\\" + e.Name))
                  .Append("\" TopLeft=\"12,").Append(Num(y)).Append("\" />\n");
                y += 90;
            }
            sb.Append("      </GraphLayout>\n    </LayoutInfo>\n  </Package>\n");

            // One TaskHost block per data flow, with its component NodeLayouts (left→right).
            foreach (var df in info.DataFlows)
            {
                sb.Append("  <TaskHost design-time-name=\"").Append(Esc("Package\\" + df.TaskName)).Append("\">\n    <LayoutInfo>\n");
                sb.Append("      <GraphLayout Capacity=\"16\" xmlns=\"").Append(GraphNs).Append("\">\n");
                foreach (var b in compBoxes.Where(b => b.Id.StartsWith("Package\\" + df.TaskName + "\\", StringComparison.Ordinal)))
                    sb.Append("        <NodeLayout Size=\"").Append(Num(b.W)).Append(',').Append(Num(b.H))
                      .Append("\" Id=\"").Append(Esc(b.Id)).Append("\" TopLeft=\"").Append(Num(b.X)).Append(',').Append(Num(b.Y)).Append("\" />\n");
                sb.Append("      </GraphLayout>\n    </LayoutInfo>\n  </TaskHost>\n");
            }

            sb.Append("</Objects>");
            return sb.ToString();
        }

        private static string Inject(string xml, string cdata)
        {
            var element = "<DTS:DesignTimeProperties><![CDATA[" + cdata + "]]></DTS:DesignTimeProperties>";
            var existing = Regex.Match(xml, "<DTS:DesignTimeProperties>.*?</DTS:DesignTimeProperties>", RegexOptions.Singleline);
            if (existing.Success)
                return xml.Substring(0, existing.Index) + element + xml.Substring(existing.Index + existing.Length);
            var close = xml.LastIndexOf("</DTS:Executable>", StringComparison.Ordinal);
            if (close < 0) return xml;
            return xml.Substring(0, close) + "  " + element + "\n" + xml.Substring(close);
        }

        private static string Esc(string s) => System.Security.SecurityElement.Escape(s);
        private static string Num(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
