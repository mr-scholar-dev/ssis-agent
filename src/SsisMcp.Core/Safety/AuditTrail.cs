using System;

namespace SsisMcp.Core.Safety
{
    /// <summary>State a safety transaction ended in.</summary>
    public enum TransactionState
    {
        /// <summary>Change validated and written to the original file.</summary>
        Committed,

        /// <summary>Change discarded on purpose (preview, or caller rollback).</summary>
        RolledBack,

        /// <summary>Aborted because the original changed on disk mid-edit — user work must not be lost.</summary>
        Aborted,

        /// <summary>The target file is locked by another safety transaction.</summary>
        Busy,

        /// <summary>Validation failed; the original was left untouched.</summary>
        Failed,

        /// <summary>Preview only — the change was computed and validated but never written.</summary>
        PreviewOnly
    }

    /// <summary>One immutable audit-trail entry (Fase 25). Emitted for every safety operation.</summary>
    public sealed class AuditRecord
    {
        public string OperationId { get; set; } = "";
        public DateTime TimestampUtc { get; set; }
        public string Tool { get; set; } = "";
        public string TargetFile { get; set; } = "";
        public string? BeforeHash { get; set; }
        public string? AfterHash { get; set; }
        public string? BackupPath { get; set; }
        public TransactionState State { get; set; }
        public bool? ValidationPassed { get; set; }
        public string? Detail { get; set; }
    }

    /// <summary>Sink for audit records. File-backed in production, in-memory in tests.</summary>
    public interface IAuditTrail
    {
        void Record(AuditRecord record);
    }
}
