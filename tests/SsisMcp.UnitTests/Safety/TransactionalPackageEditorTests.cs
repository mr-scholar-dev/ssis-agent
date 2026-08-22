using System;
using System.IO;
using System.Linq;
using SsisMcp.Core.Safety;
using SsisMcp.Safety;
using Xunit;

namespace SsisMcp.UnitTests.Safety
{
    /// <summary>
    /// Exercises the safety pipeline with a fake validator (no SSIS). The "package" is a plain
    /// text file — these tests cover the transactional file mechanics, not SSIS semantics.
    /// </summary>
    public sealed class TransactionalPackageEditorTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "safety-" + Guid.NewGuid().ToString("N"));
        private readonly string _pkg;

        public TransactionalPackageEditorTests()
        {
            Directory.CreateDirectory(_dir);
            _pkg = Path.Combine(_dir, "Package.dtsx");
            File.WriteAllText(_pkg, "ORIGINAL");
        }

        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private TransactionalPackageEditor Editor(bool valid, out InMemoryAuditTrail audit)
        {
            audit = new InMemoryAuditTrail();
            return new TransactionalPackageEditor(new FakeValidator(valid), audit);
        }

        [Fact]
        public void Apply_commits_valid_change_and_backs_up_original()
        {
            var editor = Editor(valid: true, out var audit);

            var result = editor.Apply(_pkg, temp => File.WriteAllText(temp, "MODIFIED"));

            Assert.Equal(TransactionState.Committed, result.State);
            Assert.Equal("MODIFIED", File.ReadAllText(_pkg));
            Assert.NotNull(result.BackupPath);
            Assert.Equal("ORIGINAL", File.ReadAllText(result.BackupPath!)); // backup holds the pre-edit content
            Assert.NotEqual(result.BeforeHash, result.AfterHash);
            Assert.Contains(audit.Records, r => r.State == TransactionState.Committed);
        }

        [Fact]
        public void Apply_rolls_back_when_validation_fails_leaving_original_untouched()
        {
            var editor = Editor(valid: false, out var audit);

            var result = editor.Apply(_pkg, temp => File.WriteAllText(temp, "MODIFIED"));

            Assert.Equal(TransactionState.Failed, result.State);
            Assert.Equal("ORIGINAL", File.ReadAllText(_pkg)); // untouched
            Assert.False(result.Validation!.IsValid);
            Assert.Contains(audit.Records, r => r.State == TransactionState.Failed);
        }

        [Fact]
        public void Preview_validates_but_never_writes_original()
        {
            var editor = Editor(valid: true, out var audit);

            var result = editor.Preview(_pkg, temp => File.WriteAllText(temp, "MODIFIED"));

            Assert.Equal(TransactionState.PreviewOnly, result.State);
            Assert.Equal("ORIGINAL", File.ReadAllText(_pkg)); // never written
            Assert.NotNull(result.AfterHash);                 // would-be hash computed
            Assert.Contains(audit.Records, r => r.State == TransactionState.PreviewOnly);
        }

        [Fact]
        public void Apply_aborts_when_original_changes_during_edit()
        {
            var editor = Editor(valid: true, out var audit);

            // The mutation callback simulates an external writer touching the ORIGINAL mid-edit.
            var result = editor.Apply(_pkg, temp =>
            {
                File.WriteAllText(temp, "MODIFIED");
                File.WriteAllText(_pkg, "EXTERNALLY CHANGED");
            });

            Assert.Equal(TransactionState.Aborted, result.State);
            Assert.Equal("EXTERNALLY CHANGED", File.ReadAllText(_pkg)); // external work preserved
            Assert.Contains(audit.Records, r => r.State == TransactionState.Aborted);
        }

        [Fact]
        public void Apply_reports_failure_when_mutation_throws()
        {
            var editor = Editor(valid: true, out _);

            var result = editor.Apply(_pkg, temp => throw new InvalidOperationException("boom"));

            Assert.Equal(TransactionState.Failed, result.State);
            Assert.Equal("ORIGINAL", File.ReadAllText(_pkg));
            Assert.Contains("boom", result.Detail);
        }

        [Fact]
        public void Concurrent_transaction_sees_busy_when_locked()
        {
            var editor = Editor(valid: true, out _);
            using (var held = PackageLock.TryAcquire(_pkg, "OP-holder"))
            {
                Assert.NotNull(held);
                var result = editor.Apply(_pkg, temp => File.WriteAllText(temp, "MODIFIED"));
                Assert.Equal(TransactionState.Busy, result.State);
                Assert.Equal("ORIGINAL", File.ReadAllText(_pkg));
            }
        }

        [Fact]
        public void Undo_restores_latest_backup()
        {
            var editor = Editor(valid: true, out _);
            editor.Apply(_pkg, temp => File.WriteAllText(temp, "MODIFIED"));
            Assert.Equal("MODIFIED", File.ReadAllText(_pkg));

            var backups = new BackupManager();
            Assert.True(backups.RestoreLatest(_pkg));
            Assert.Equal("ORIGINAL", File.ReadAllText(_pkg));
        }
    }
}
