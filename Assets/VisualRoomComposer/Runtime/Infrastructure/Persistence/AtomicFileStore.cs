using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace VisualRoomComposer.Infrastructure.Persistence
{
    public sealed class StorageException : IOException
    {
        public string Code { get; }
        public StorageException(string code, string message, Exception inner = null) : base(message, inner) { Code = code; }
    }

    public enum StorageFaultPoint { AfterTemporaryWrite, BeforeReadBack, BeforeCommit, AfterCommit }

    public sealed class StorageReceipt
    {
        public string Path { get; } public string ContentHash { get; } public long ByteLength { get; }
        public bool Committed => true; public string PostCommitWarning { get; }
        internal StorageReceipt(string path, string hash, long bytes, string warning = null)
        { Path = path; ContentHash = hash; ByteLength = bytes; PostCommitWarning = warning; }
    }

    public sealed class StoredBytes
    {
        public byte[] Bytes { get; } public string ContentHash { get; }
        internal StoredBytes(byte[] bytes) { Bytes = bytes; ContentHash = AtomicFileStore.Hash(bytes); }
    }

    /// <summary>Bounded local-file transactions. Sidecar locks serialize cooperating writers; expected hashes detect external changes before commit.</summary>
    public sealed class AtomicFileStore
    {
        public const int DefaultMaxBytes = 256 * 1024 * 1024;
        public int MaxBytes { get; }
        private readonly Action<StorageFaultPoint, string> fault;
        public AtomicFileStore(int maxBytes = DefaultMaxBytes, Action<StorageFaultPoint, string> fault = null)
        { if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes)); MaxBytes = maxBytes; this.fault = fault; }

        public StoredBytes Read(string path, CancellationToken cancellation = default)
        {
            path = StoragePaths.Absolute(path); cancellation.ThrowIfCancellationRequested();
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length > MaxBytes) throw new StorageException("LimitExceeded", "File exceeds the storage byte budget.");
                var bytes = new byte[checked((int)input.Length)]; int at = 0;
                while (at < bytes.Length)
                { cancellation.ThrowIfCancellationRequested(); int n = input.Read(bytes, at, Math.Min(65536, bytes.Length - at)); if (n == 0) throw new EndOfStreamException(); at += n; }
                if (input.ReadByte() != -1) throw new StorageException("ExternalChange", "File grew while being read.");
                cancellation.ThrowIfCancellationRequested(); return new StoredBytes(bytes);
            }
        }

        public StorageReceipt Save(string path, byte[] bytes, string expectedHash = null,
            Action<byte[]> validate = null, CancellationToken cancellation = default)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (bytes.Length > MaxBytes) throw new StorageException("LimitExceeded", "Document exceeds the storage byte budget.");
            // Own the candidate so a caller cannot change it after validation or during the commit.
            bytes = (byte[])bytes.Clone(); cancellation.ThrowIfCancellationRequested(); validate?.Invoke(bytes);
            string hash = Hash(bytes); path = StoragePaths.Absolute(path);
            string directory = System.IO.Path.GetDirectoryName(path); Directory.CreateDirectory(directory);
            string temporary = System.IO.Path.Combine(directory, "." + System.IO.Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            using (var guard = new FileStream(StoragePaths.Absolute(path + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                CheckExpected(path, expectedHash, cancellation);
                try
                {
                    using (var output = new FileStream(StoragePaths.Absolute(temporary), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        for (int at = 0; at < bytes.Length;)
                        { cancellation.ThrowIfCancellationRequested(); int count = Math.Min(65536, bytes.Length - at); output.Write(bytes, at, count); at += count; }
                        output.Flush(true);
                    }
                    fault?.Invoke(StorageFaultPoint.AfterTemporaryWrite, temporary);
                    fault?.Invoke(StorageFaultPoint.BeforeReadBack, temporary);
                    var readback = Read(temporary, cancellation); validate?.Invoke(readback.Bytes);
                    if (readback.ContentHash != hash) throw new StorageException("HashMismatch", "Temporary file does not match the candidate.");
                    fault?.Invoke(StorageFaultPoint.BeforeCommit, path);
                    int[] delays = { 0, 25, 75, 150 };
                    for (int attempt = 0; ; attempt++)
                    {
                        if (delays[attempt] != 0 && cancellation.WaitHandle.WaitOne(delays[attempt])) cancellation.ThrowIfCancellationRequested();
                        cancellation.ThrowIfCancellationRequested(); CheckExpected(path, expectedHash, cancellation);
                        try
                        {
                            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                            break;
                        }
                        catch (IOException error)
                        {
                            if (!File.Exists(temporary) && File.Exists(path) && Read(path).ContentHash == hash) break;
                            if (attempt == delays.Length - 1 || !Transient(error)) throw;
                        }
                    }
                    // Publication is the commit point. A late cancellation or observer failure cannot make it uncommitted.
                    string warning = null;
                    try { fault?.Invoke(StorageFaultPoint.AfterCommit, path); } catch (Exception error) { warning = error.Message; }
                    return new StorageReceipt(path, hash, bytes.Length, warning);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }

        private void CheckExpected(string path, string expected, CancellationToken cancellation)
        {
            StoragePaths.Absolute(path);
            bool exists = File.Exists(path);
            if (!exists && expected == null) return;
            if (!exists || expected == null || Read(path, cancellation).ContentHash != expected)
                throw new StorageException("ExternalChange", "Expected file hash no longer matches; reload or save a separate copy.");
        }
        internal static bool Transient(IOException error) { int code = error.HResult & 65535; return code == 32 || code == 33 || code == 1175; }
        public static string Hash(byte[] bytes)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        public static bool IsHash(string value)
        { if (value == null || value.Length != 64) return false; foreach (char c in value) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false; return true; }
    }

    public static class StoragePaths
    {
        public static string Absolute(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new StorageException("InvalidPath", "A local path is required.");
            string full = System.IO.Path.GetFullPath(path);
            if (full.StartsWith("\\\\", StringComparison.Ordinal)) throw new StorageException("InvalidPath", "Storage requires a local drive path, not a device or network path.");
            foreach (string segment in full.Substring(System.IO.Path.GetPathRoot(full).Length).Replace('\\', '/').Split('/'))
                if (segment.Length != 0) ValidateSegment(segment);
            for (string cursor = full; !string.IsNullOrEmpty(cursor); cursor = System.IO.Path.GetDirectoryName(cursor))
            {
                try
                {
                    // GetAttributes inspects dangling links too; Exists() intentionally follows a link and can conceal one.
                    if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                        throw new StorageException("InvalidPath", "Reparse points are not accepted for storage transactions: " + cursor);
                }
                catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
            }
            return full;
        }
        public static string Under(string root, string relative)
        {
            root = Absolute(root).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            if (string.IsNullOrEmpty(relative) || System.IO.Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0)
                throw new StorageException("InvalidPath", "A safe relative path is required.");
            foreach (string part in relative.Replace('\\', '/').Split('/')) ValidateSegment(part);
            string result = Absolute(System.IO.Path.Combine(root, relative));
            if (!result.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new StorageException("InvalidPath", "Path escaped the storage root.");
            return result;
        }
        public static string Segment(string id)
        { if (id == null || id.Contains("/") || id.Contains("\\")) throw new StorageException("InvalidPath", "Identity cannot contain a path separator."); Under(System.IO.Path.GetTempPath(), id); return id; }
        private static void ValidateSegment(string part)
        {
            string device = part.Split('.')[0].ToUpperInvariant();
            if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal) || part.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || part.IndexOf(':') >= 0 ||
                device == "CON" || device == "PRN" || device == "AUX" || device == "NUL" || device == "CONIN$" || device == "CONOUT$" ||
                (device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) && device[3] >= '1' && device[3] <= '9'))
                throw new StorageException("InvalidPath", "Invalid or reserved path segment.");
        }
        public static void PublishDirectory(string root, string staged, string destination, CancellationToken cancellation = default)
        {
            staged = Under(root, System.IO.Path.GetRelativePath(root, staged)); destination = Under(root, System.IO.Path.GetRelativePath(root, destination));
            int[] delays = { 0, 25, 75, 150 };
            for (int i = 0; ; i++)
            {
                if (delays[i] != 0 && cancellation.WaitHandle.WaitOne(delays[i])) cancellation.ThrowIfCancellationRequested();
                cancellation.ThrowIfCancellationRequested();
                StoragePaths.Absolute(staged); StoragePaths.Absolute(destination);
                if (Directory.Exists(destination) || File.Exists(destination)) throw new StorageException("VersionConflict", "An immutable version already exists.");
                try { Directory.Move(staged, destination); return; }
                catch (IOException e) { if (i == delays.Length - 1 || !AtomicFileStore.Transient(e)) throw; }
            }
        }
        internal static void DeleteOwnedTree(string root, string directory)
        {
            string checkedPath = Under(root, System.IO.Path.GetRelativePath(root, directory));
            if (!Directory.Exists(checkedPath)) return;
            foreach (string file in Directory.GetFiles(checkedPath)) File.Delete(Absolute(file));
            foreach (string child in Directory.GetDirectories(checkedPath)) DeleteOwnedTree(root, child);
            Directory.Delete(checkedPath, false);
        }
    }
}
