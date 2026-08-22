using System.IO;
using System.Xml;

namespace SsisMcp.Ssis.Inspection
{
    /// <summary>
    /// Reads the <c>PackageFormatVersion</c> value from a .dtsx. The runtime Package object does not
    /// reliably expose it, so we read it from the file as a last resort. This is read-only inspection
    /// of a single value — never a mechanism for mutating packages (mutations go through the SSIS OM).
    /// </summary>
    internal static class PackageFormatVersionReader
    {
        public static int? FromFile(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                using (var reader = XmlReader.Create(path, new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true }))
                {
                    while (reader.Read())
                    {
                        if (reader.NodeType == XmlNodeType.Element &&
                            reader.LocalName == "Property")
                        {
                            var name = reader.GetAttribute("Name", "www.microsoft.com/SqlServer/Dts")
                                       ?? reader.GetAttribute("DTS:Name");
                            if (name == "PackageFormatVersion")
                            {
                                var text = reader.ReadElementContentAsString();
                                if (int.TryParse(text, out var v)) return v;
                            }
                        }
                    }
                }
            }
            catch (XmlException)
            {
                return null;
            }
            return null;
        }
    }
}
