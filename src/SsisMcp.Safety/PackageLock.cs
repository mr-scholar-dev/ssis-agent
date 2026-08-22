using System;
using System.IO;

namespace SsisMcp.Safety
{
    /// <summary>
    /// Cross-process advisory lock for a package, held via an exclusively-opened sidecar
    /// <c>&lt;package&gt;.lock</c> file. Prevents two safety transactions touching the same package
    /// at once. Dispose releases and removes the lock.
    /// </summary>
    public sealed class PackageLock : IDisposable
    {
        private readonly string _lockPath;
        private FileStream? _stream;

        private PackageLock(string lockPath, FileStream stream)
        {
            _lockPath = lockPath;
            _stream = stream;
        }

        /// <summary>Attempts to acquire the lock. Returns null if another holder has it.</summary>
        public static PackageLock? TryAcquire(string packagePath, string operationId)
        {
            var lockPath = packagePath + ".lock";
            try
            {
                var stream = new FileStream(lockPath, FileMode.Create, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.DeleteOnClose);
                var bytes = System.Text.Encoding.UTF8.GetBytes(
                    operationId + " @ " + DateTime.UtcNow.ToString("O"));
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
                return new PackageLock(lockPath, stream);
            }
            catch (IOException)
            {
                return null; // held by someone else
            }
        }

        public void Dispose()
        {
            _stream?.Dispose(); // DeleteOnClose removes the sidecar
            _stream = null;
        }
    }
}
