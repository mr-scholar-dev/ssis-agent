using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using SsisMcp.Core.Packages;
using SsisMcp.Core.Projects;

namespace SsisMcp.Ssis.Inspection
{
    /// <summary>
    /// Parses a .dtproj project file. The format differs across SSIS/VS versions, so parsing is
    /// namespace-agnostic and defensive: we scan by element local name rather than assuming a schema.
    /// </summary>
    public static class DtprojReader
    {
        public static ProjectInfo Read(string dtprojPath)
        {
            if (!File.Exists(dtprojPath)) throw new FileNotFoundException("Project file not found", dtprojPath);

            var projectDir = Path.GetDirectoryName(Path.GetFullPath(dtprojPath)) ?? ".";
            var info = new ProjectInfo
            {
                Name = Path.GetFileNameWithoutExtension(dtprojPath),
                ProjectPath = Path.GetFullPath(dtprojPath)
            };

            var doc = new XmlDocument();
            doc.Load(dtprojPath);

            info.DeploymentModel = FirstText(doc, "DeploymentModel");
            info.TargetServerVersion = FirstText(doc, "TargetServerVersion");
            info.ProtectionLevel = FirstText(doc, "ProtectionLevel");

            foreach (var name in PackageNames(doc))
            {
                var path = Path.Combine(projectDir, name);
                info.Packages.Add(new ProjectPackageRef
                {
                    Name = name,
                    Path = path,
                    FileExists = File.Exists(path)
                });
            }

            foreach (var cm in ConnectionManagerNames(doc, projectDir))
                info.ProjectConnectionManagers.Add(new ConnectionInfo { Name = cm });

            return info;
        }

        private static string? FirstText(XmlDocument doc, string localName)
        {
            var node = doc.GetElementsByTagName("*").Cast<XmlNode>()
                .FirstOrDefault(n => string.Equals(n.LocalName, localName, StringComparison.OrdinalIgnoreCase));
            var text = node?.InnerText?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        private static IEnumerable<string> PackageNames(XmlDocument doc)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlNode node in doc.GetElementsByTagName("*"))
            {
                if (!string.Equals(node.LocalName, "Package", StringComparison.OrdinalIgnoreCase)) continue;
                var name = AttrByLocalName(node, "Name");
                if (!string.IsNullOrEmpty(name) &&
                    name!.EndsWith(".dtsx", StringComparison.OrdinalIgnoreCase) &&
                    seen.Add(name))
                {
                    yield return name;
                }
            }
        }

        private static IEnumerable<string> ConnectionManagerNames(XmlDocument doc, string projectDir)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlNode node in doc.GetElementsByTagName("*"))
            {
                if (!string.Equals(node.LocalName, "ConnectionManager", StringComparison.OrdinalIgnoreCase)) continue;
                var name = AttrByLocalName(node, "Name");
                if (!string.IsNullOrEmpty(name) && seen.Add(name!))
                    yield return name!;
            }
            // Also include .conmgr files present in the project directory.
            if (Directory.Exists(projectDir))
                foreach (var f in Directory.EnumerateFiles(projectDir, "*.conmgr"))
                {
                    var n = Path.GetFileName(f);
                    if (seen.Add(n)) yield return n;
                }
        }

        private static string? AttrByLocalName(XmlNode node, string localName)
        {
            if (node.Attributes == null) return null;
            foreach (XmlAttribute a in node.Attributes)
                if (string.Equals(a.LocalName, localName, StringComparison.OrdinalIgnoreCase))
                    return a.Value;
            return null;
        }
    }
}
