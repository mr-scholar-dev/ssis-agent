using System.IO;
using System.Security.Cryptography;

namespace SsisMcp.Safety
{
    /// <summary>SHA-256 hashing of package files — the basis for change detection.</summary>
    public static class FileHasher
    {
        /// <summary>Lowercase hex SHA-256 of the file's bytes.</summary>
        public static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                var hash = sha.ComputeHash(stream);
                var chars = new char[hash.Length * 2];
                for (var i = 0; i < hash.Length; i++)
                {
                    var b = hash[i];
                    chars[i * 2] = ToHex(b >> 4);
                    chars[i * 2 + 1] = ToHex(b & 0xF);
                }
                return new string(chars);
            }
        }

        private static char ToHex(int nibble) => (char)(nibble < 10 ? '0' + nibble : 'a' + (nibble - 10));
    }
}
