using System;
using System.Collections.Generic;
using System.Xml;
using SsisMcp.Core.Packages;

namespace SsisMcp.Ssis.Inspection
{
    /// <summary>
    /// Determines which connection managers each Control Flow task references by reading the .dtsx.
    /// The runtime Object Model cannot expose task-specific connection properties for COM-backed tasks
    /// (DtsProperty reflection throws TargetException), so — as with PackageFormatVersion — we read
    /// this single fact from the file. This is read-only inspection, never a mutation mechanism.
    /// </summary>
    internal static class ConnectionUsageXmlReader
    {
        /// <summary>Maps executable ObjectName -> distinct connection manager names it references.</summary>
        public static Dictionary<string, List<string>> FromFile(string path, IEnumerable<ConnectionInfo> connections)
        {
            var idToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in connections)
                if (!string.IsNullOrEmpty(c.Id)) idToName[Normalize(c.Id!)] = c.Name;

            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var doc = new XmlDocument();
            doc.Load(path);

            var stack = new Stack<string>();
            Walk(doc.DocumentElement, stack, idToName, result);
            return result;
        }

        private static void Walk(XmlNode? node, Stack<string> stack,
            Dictionary<string, string> idToName, Dictionary<string, List<string>> result)
        {
            if (node == null) return;
            var pushed = false;

            if (node.NodeType == XmlNodeType.Element &&
                string.Equals(node.LocalName, "Executable", StringComparison.OrdinalIgnoreCase))
            {
                var name = AttrByLocalName(node, "ObjectName");
                stack.Push(name ?? "");
                pushed = true;
            }

            if (node.Attributes != null && stack.Count > 0)
            {
                var owner = stack.Peek();
                if (!string.IsNullOrEmpty(owner))
                {
                    foreach (XmlAttribute a in node.Attributes)
                    {
                        if (idToName.TryGetValue(Normalize(a.Value), out var cmName))
                            Add(result, owner, cmName);
                    }
                }
            }

            foreach (XmlNode child in node.ChildNodes)
                Walk(child, stack, idToName, result);

            if (pushed) stack.Pop();
        }

        private static void Add(Dictionary<string, List<string>> result, string owner, string cmName)
        {
            if (!result.TryGetValue(owner, out var list))
            {
                list = new List<string>();
                result[owner] = list;
            }
            if (!list.Contains(cmName)) list.Add(cmName);
        }

        private static string? AttrByLocalName(XmlNode node, string localName)
        {
            if (node.Attributes == null) return null;
            foreach (XmlAttribute a in node.Attributes)
                if (string.Equals(a.LocalName, localName, StringComparison.OrdinalIgnoreCase))
                    return a.Value;
            return null;
        }

        private static string Normalize(string id) => id.Trim('{', '}').ToUpperInvariant();
    }
}
