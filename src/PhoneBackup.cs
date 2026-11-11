using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace DroidNest
{
    // Phone data backup/restore works only while the phone is stopped. Restore never merges or
    // overwrites an existing data directory; the caller must back up and move the old one first.
    internal static class PhoneBackup
    {
        internal const long MaxRestoreBytes = 256L * 1024 * 1024 * 1024;

        internal static string Create(string root, string destination, CancellationToken token)
        {
            string data = Path.Combine(root, "data");
            if (!Directory.Exists(data)) throw new DirectoryNotFoundException("手机数据目录不存在：" + data);
            if (String.IsNullOrWhiteSpace(destination)) throw new ArgumentException("请选择备份文件位置。", "destination");
            destination = Path.GetFullPath(destination);
            if (!destination.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("备份文件名必须以 .zip 结尾。", "destination");
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            string temporary = destination + ".partial";
            if (File.Exists(temporary)) File.Delete(temporary);
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
                {
                    string[] files = Directory.GetFiles(data, "*", SearchOption.AllDirectories);
                    foreach (string file in files)
                    {
                        token.ThrowIfCancellationRequested();
                        string relative = file.Substring(data.Length + 1).Replace('\\', '/');
                        ZipArchiveEntry entry = archive.CreateEntry("data/" + relative, CompressionLevel.Fastest);
                        entry.LastWriteTime = File.GetLastWriteTime(file);
                        using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
                        using (Stream target = entry.Open())
                            CopyBounded(input, target, new FileInfo(file).Length, token);
                    }
                }
                if (File.Exists(destination))
                {
                    token.ThrowIfCancellationRequested();
                    File.Delete(destination);
                }
                File.Move(temporary, destination);
            }
            catch
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                throw;
            }
            File.WriteAllText(destination + ".sha256",
                HashFile(destination, token) + "  " + Path.GetFileName(destination) + Environment.NewLine,
                new UTF8Encoding(false));
            return destination;
        }

        internal static void Restore(string root, string archivePath, CancellationToken token)
        {
            if (!File.Exists(archivePath)) throw new FileNotFoundException("找不到备份文件。", archivePath);
            VerifySidecar(archivePath, token);
            string data = Path.Combine(root, "data");
            bool dataWasEmpty = false;
            if (Directory.Exists(data))
            {
                if (Directory.EnumerateFileSystemEntries(data).Any())
                    throw new InvalidOperationException("目标已有手机数据。请先备份，再把 data 目录移走后重试；DroidNest 不会覆盖现有数据。");
                dataWasEmpty = true;
            }
            string staging = Path.Combine(root, "data.restore-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(staging);
                long expanded = 0;
                using (var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var archive = new ZipArchive(file, ZipArchiveMode.Read))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        token.ThrowIfCancellationRequested();
                        string name = entry.FullName.Replace('\\', '/');
                        if (name == "data" || name == "data/") continue;
                        if (!name.StartsWith("data/", StringComparison.Ordinal))
                            throw new InvalidDataException("备份包包含意外的顶层路径：" + entry.FullName);
                        string relative = name.Substring("data/".Length);
                        if (relative.Length == 0) continue;
                        int type = (entry.ExternalAttributes >> 16) & 0xf000;
                        if (type == 0xa000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                            throw new InvalidDataException("备份包包含链接，已拒绝：" + entry.FullName);
                        bool directory = relative.EndsWith("/", StringComparison.Ordinal);
                        string target = RuntimeInstaller.SafePath(staging, directory ? relative.TrimEnd('/') : relative);
                        if (directory) { Directory.CreateDirectory(target); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        expanded = checked(expanded + entry.Length);
                        if (expanded > MaxRestoreBytes) throw new InvalidDataException("备份包超过 256 GiB 解压上限，已拒绝。");
                        using (Stream input = entry.Open())
                        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                        {
                            long written = CopyBounded(input, output, entry.Length, token);
                            if (written != entry.Length) throw new InvalidDataException("备份条目不完整：" + entry.FullName);
                        }
                    }
                }
                if (dataWasEmpty) Directory.Delete(data, false);
                Directory.Move(staging, data);
            }
            catch
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                throw;
            }
        }

        private static void VerifySidecar(string archivePath, CancellationToken token)
        {
            string sidecar = archivePath + ".sha256";
            if (!File.Exists(sidecar)) return;
            string expected = File.ReadAllText(sidecar).Split(' ').FirstOrDefault();
            if (String.IsNullOrEmpty(expected) || expected.Length != 64 ||
                HashFile(archivePath, token) != expected.ToLowerInvariant())
                throw new InvalidDataException("备份文件与同名校验文件不一致，已拒绝恢复。");
        }

        private static string HashFile(string path, CancellationToken token)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
            using (var sha = SHA256.Create())
            {
                byte[] buffer = new byte[131072];
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    sha.TransformBlock(buffer, 0, count, buffer, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static long CopyBounded(Stream input, Stream output, long expected, CancellationToken token)
        {
            byte[] buffer = new byte[131072];
            long total = 0;
            while (total < expected)
            {
                token.ThrowIfCancellationRequested();
                int count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, expected - total));
                if (count <= 0) break;
                output.Write(buffer, 0, count);
                total += count;
            }
            return total;
        }
    }
}
