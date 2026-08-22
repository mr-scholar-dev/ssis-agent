namespace SsisMcp.Core.Safety
{
    /// <summary>Result of a safety-managed edit. Serializable DTO for MCP (changes.apply / preview).</summary>
    public sealed class EditResult
    {
        public string OperationId { get; set; } = "";
        public TransactionState State { get; set; }
        public string? BeforeHash { get; set; }
        public string? AfterHash { get; set; }
        public string? BackupPath { get; set; }
        public ValidationResult? Validation { get; set; }
        public string? Detail { get; set; }

        public bool Succeeded => State == TransactionState.Committed || State == TransactionState.PreviewOnly;
    }
}
