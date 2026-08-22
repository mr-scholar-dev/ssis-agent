using System;
using System.IO;
using System.Linq;

namespace SsisMcp.Safety
{
    /// <summary>
    /// Timestamped backups next to the package under a git-ignored <c>_backups</c> folder.
    /// Backups are never overwritten; each is stamped with UTC time + short hash so history is kept.
    /// Backs <c>package.backup</c> / <c>package.restore</c> / <c>package.undo</c>.
    /// </summary>
    public sealed class BackupManager
    {
        public const string BackupFolderName = "_backups";

        /// <summary>Creates a backup of <paramref name="path"/> and returns the backup file path.</summary>
        public string Backup(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Cannot back up missing file", path);
            var dir = BackupDir(path);
            Directory.CreateDirectory(dir);

            var name = Path.GetFileName(path);
            var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
            var shortHash = FileHasher.Sha256(path).Substring(0, 8);
            var backupPath = Path.Combine(dir, $"{name}.{stamp}.{shortHash}.bak");
            File.Copy(path, backupPath, overwrite: false);
            return backupPath;
        }

        /// <summary>Restores a specific backup onto <paramref name="targetPath"/>.</summary>
        public void Restore(string backupPath, string targetPath)
        {
            if (!File.Exists(backupPath)) throw new FileNotFoundException("Backup not found", backupPath);
            var dir = Path.GetDirectoryName(Path.GetFullPath(targetPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(backupPath, targetPath, overwrite: true);
        }

        /// <summary>The most recent backup for <paramref name="path"/>, or null if none exist.</summary>
        public string? LatestBackup(string path)
        {
            var dir = BackupDir(path);
            if (!Directory.Exists(dir)) return null;
            var prefix = Path.GetFileName(path) + ".";
            return Directory.EnumerateFiles(dir, prefix + "*.bak")
                .OrderBy(f => f, StringComparer.Ordinal) // stamp is lexicographically sortable
                .LastOrDefault();
        }

        /// <summary>Restores the latest backup (undo). Returns false when there is nothing to undo.</summary>
        public bool RestoreLatest(string path)
        {
            var latest = LatestBackup(path);
            if (latest == null) return false;
            Restore(latest, path);
            return true;
        }

        private static string BackupDir(string path)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            return Path.Combine(dir, BackupFolderName);
        }
    }
}
