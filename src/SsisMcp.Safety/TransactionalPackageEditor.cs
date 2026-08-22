using System;
using System.IO;
using SsisMcp.Core.Safety;

namespace SsisMcp.Safety
{
    /// <summary>
    /// The mandatory mutation gate (Fase 4). Every package change runs through here:
    ///
    ///   hash original → lock → temp copy → mutate temp → validate → (commit | rollback)
    ///
    /// The original file is never mutated in place. If the original changes on disk between the
    /// start of the edit and commit, the transaction ABORTS so external/user work is never lost.
    /// Backs <c>changes.preview</c> / <c>changes.apply</c> / <c>changes.rollback</c>.
    /// </summary>
    public sealed class TransactionalPackageEditor
    {
        private readonly IPackageValidator _validator;
        private readonly IAuditTrail _audit;
        private readonly BackupManager _backups;

        public TransactionalPackageEditor(IPackageValidator validator, IAuditTrail audit, BackupManager? backups = null)
        {
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
            _audit = audit ?? throw new ArgumentNullException(nameof(audit));
            _backups = backups ?? new BackupManager();
        }

        /// <summary>Runs the mutation and validation on a temp copy but never writes the original.</summary>
        public EditResult Preview(string path, Action<string> mutateTempCopy, string tool = "changes.preview")
            => Run(path, mutateTempCopy, tool, commit: false);

        /// <summary>Full pipeline: mutate temp, validate, back up the original, then commit.</summary>
        public EditResult Apply(string path, Action<string> mutateTempCopy, string tool = "changes.apply")
            => Run(path, mutateTempCopy, tool, commit: true);

        private EditResult Run(string path, Action<string> mutateTempCopy, string tool, bool commit)
        {
            var opId = "OP-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            var result = new EditResult { OperationId = opId };

            if (!File.Exists(path))
            {
                result.State = TransactionState.Failed;
                result.Detail = "target file does not exist";
                Emit(opId, tool, path, null, null, null, TransactionState.Failed, null, result.Detail);
                return result;
            }

            var beforeHash = FileHasher.Sha256(path);
            result.BeforeHash = beforeHash;

            using (var pkgLock = PackageLock.TryAcquire(path, opId))
            {
                if (pkgLock == null)
                {
                    result.State = TransactionState.Busy;
                    result.Detail = "package is locked by another transaction";
                    Emit(opId, tool, path, beforeHash, null, null, TransactionState.Busy, null, result.Detail);
                    return result;
                }

                var temp = path + "." + opId + ".tmp";
                try
                {
                    File.Copy(path, temp, overwrite: true);

                    try
                    {
                        mutateTempCopy(temp);
                    }
                    catch (Exception ex)
                    {
                        result.State = TransactionState.Failed;
                        result.Detail = "mutation threw: " + ex.GetType().Name + ": " + ex.Message;
                        Emit(opId, tool, path, beforeHash, null, null, TransactionState.Failed, null, result.Detail);
                        return result;
                    }

                    var validation = _validator.Validate(temp);
                    result.Validation = validation;
                    var afterHash = FileHasher.Sha256(temp);
                    result.AfterHash = afterHash;

                    if (!validation.IsValid)
                    {
                        result.State = TransactionState.Failed;
                        result.Detail = "validation failed: " + string.Join("; ", validation.Messages);
                        Emit(opId, tool, path, beforeHash, afterHash, null, TransactionState.Failed, false, result.Detail);
                        return result;
                    }

                    if (!commit)
                    {
                        result.State = TransactionState.PreviewOnly;
                        result.Detail = "validated; not written (preview)";
                        Emit(opId, tool, path, beforeHash, afterHash, null, TransactionState.PreviewOnly, true, result.Detail);
                        return result;
                    }

                    // Re-check the original right before writing: abort if it changed under us.
                    var currentHash = FileHasher.Sha256(path);
                    if (!string.Equals(currentHash, beforeHash, StringComparison.Ordinal))
                    {
                        result.State = TransactionState.Aborted;
                        result.Detail = "original changed on disk during edit — aborting to protect user work";
                        Emit(opId, tool, path, beforeHash, afterHash, null, TransactionState.Aborted, true, result.Detail);
                        return result;
                    }

                    var backupPath = _backups.Backup(path);
                    result.BackupPath = backupPath;
                    File.Copy(temp, path, overwrite: true);

                    result.State = TransactionState.Committed;
                    result.Detail = "committed";
                    Emit(opId, tool, path, beforeHash, afterHash, backupPath, TransactionState.Committed, true, result.Detail);
                    return result;
                }
                finally
                {
                    TryDelete(temp);
                }
            }
        }

        private void Emit(string opId, string tool, string file, string? before, string? after,
            string? backup, TransactionState state, bool? validationPassed, string? detail)
        {
            _audit.Record(new AuditRecord
            {
                OperationId = opId,
                TimestampUtc = DateTime.UtcNow,
                Tool = tool,
                TargetFile = file,
                BeforeHash = before,
                AfterHash = after,
                BackupPath = backup,
                State = state,
                ValidationPassed = validationPassed,
                Detail = detail
            });
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { /* best effort; temp files are ignored by git */ }
        }
    }
}
