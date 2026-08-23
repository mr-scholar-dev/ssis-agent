namespace SsisMcp.Core.Building
{
    /// <summary>
    /// Explicit capability verification levels (per project policy). A capability is only
    /// <see cref="ExecutionVerified"/> when package execution + row counts + destination data have
    /// been verified — which is impossible on a host where SSIS execution is license-gated
    /// (that state is <see cref="EnvironmentBlocked"/> for the execution dimension).
    /// </summary>
    public enum VerificationLevel
    {
        Unsupported,
        Partial,
        /// <summary>build + Validate + Safety + save + reload (+ second reload) + metadata + lineage + mappings + inspector round-trip.</summary>
        StructurallyVerified,
        /// <summary>Additionally: package execution + row counts + destination data verification.</summary>
        ExecutionVerified,
        /// <summary>Would be verifiable but the current environment blocks it (e.g. SSIS execution licensing).</summary>
        EnvironmentBlocked
    }
}
