using System;
using System.Globalization;
using System.IO;
using System.Text;
using SsisMcp.Core.Safety;

namespace SsisMcp.Safety
{
    /// <summary>
    /// Append-only JSONL audit trail. One line per operation. Hand-rolled serialization keeps the
    /// Safety layer dependency-free and the format stable/greppable (audit-*.jsonl, git-ignored).
    /// </summary>
    public sealed class FileSystemAuditTrail : IAuditTrail
    {
        private readonly string _path;
        private readonly object _gate = new object();

        public FileSystemAuditTrail(string path)
        {
            _path = path;
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        public void Record(AuditRecord r)
        {
            var line = ToJson(r);
            lock (_gate)
            {
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
        }

        private static string ToJson(AuditRecord r)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            Field(sb, "operationId", r.OperationId, first: true);
            Field(sb, "timestampUtc", r.TimestampUtc.ToString("O", CultureInfo.InvariantCulture));
            Field(sb, "tool", r.Tool);
            Field(sb, "targetFile", r.TargetFile);
            Field(sb, "beforeHash", r.BeforeHash);
            Field(sb, "afterHash", r.AfterHash);
            Field(sb, "backupPath", r.BackupPath);
            Field(sb, "state", r.State.ToString());
            RawField(sb, "validationPassed",
                r.ValidationPassed.HasValue ? (r.ValidationPassed.Value ? "true" : "false") : "null");
            Field(sb, "detail", r.Detail);
            sb.Append('}');
            return sb.ToString();
        }

        private static void Field(StringBuilder sb, string key, string? value, bool first = false)
        {
            if (!first) sb.Append(',');
            sb.Append('"').Append(key).Append("\":");
            if (value == null) sb.Append("null");
            else sb.Append('"').Append(Escape(value)).Append('"');
        }

        private static void RawField(StringBuilder sb, string key, string rawJson)
        {
            sb.Append(',').Append('"').Append(key).Append("\":").Append(rawJson);
        }

        private static string Escape(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>In-memory audit sink for tests.</summary>
    public sealed class InMemoryAuditTrail : IAuditTrail
    {
        public System.Collections.Generic.List<AuditRecord> Records { get; } =
            new System.Collections.Generic.List<AuditRecord>();

        public void Record(AuditRecord record) => Records.Add(record);
    }
}
