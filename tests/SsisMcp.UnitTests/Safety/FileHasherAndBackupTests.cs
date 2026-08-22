using System;
using System.IO;
using System.Linq;
using SsisMcp.Safety;
using Xunit;

namespace SsisMcp.UnitTests.Safety
{
    public sealed class FileHasherAndBackupTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "hash-" + Guid.NewGuid().ToString("N"));

        public FileHasherAndBackupTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        [Fact]
        public void Sha256_is_stable_and_matches_known_vector()
        {
            var f = Path.Combine(_dir, "a.txt");
            File.WriteAllText(f, "abc");
            // Known SHA-256("abc")
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                FileHasher.Sha256(f));
        }

        [Fact]
        public void Sha256_differs_for_different_content()
        {
            var a = Path.Combine(_dir, "a"); var b = Path.Combine(_dir, "b");
            File.WriteAllText(a, "one"); File.WriteAllText(b, "two");
            Assert.NotEqual(FileHasher.Sha256(a), FileHasher.Sha256(b));
        }

        [Fact]
        public void Backup_creates_file_and_latest_returns_most_recent()
        {
            var pkg = Path.Combine(_dir, "P.dtsx");
            File.WriteAllText(pkg, "v1");
            var mgr = new BackupManager();

            var b1 = mgr.Backup(pkg);
            Assert.True(File.Exists(b1));
            Assert.Equal("v1", File.ReadAllText(b1));

            File.WriteAllText(pkg, "v2");
            var b2 = mgr.Backup(pkg);

            Assert.Equal(b2, mgr.LatestBackup(pkg));
            Assert.Contains(BackupManager.BackupFolderName, b1);
        }

        [Fact]
        public void RestoreLatest_returns_false_with_no_backups()
        {
            var pkg = Path.Combine(_dir, "Q.dtsx");
            File.WriteAllText(pkg, "x");
            Assert.False(new BackupManager().RestoreLatest(pkg));
        }
    }
}
