using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace DroidNest
{
    internal sealed class ApkInfo
    {
        internal string Package;
        internal string Version;
        internal string Signature;
        internal string Notice;
    }

    // Reads package metadata and signing certificate before anything is installed. The Android
    // build tools and private JDK are used when present; a v1 certificate is read as a fallback.
    internal static class ApkInspector
    {
        internal static ApkInfo Inspect(string root, string apkFile)
        {
            if (!File.Exists(apkFile)) throw new FileNotFoundException("找不到 APK 文件。", apkFile);
            var info = new ApkInfo();
            string buildTools = Path.Combine(root, "runtime", "tools", "build-tools");
            string aapt2 = Path.Combine(buildTools, "aapt2.exe");
            string apksigner = Path.Combine(buildTools, "lib", "apksigner.jar");
            string java = Path.Combine(root, "runtime", "tools", "jdk", "bin", "java.exe");

            if (File.Exists(aapt2))
            {
                int badgingExit;
                string output = Run(aapt2, new[] { "dump", "badging", apkFile }, 20000, out badgingExit);
                if (badgingExit != 0 && String.IsNullOrEmpty(output)) info.Notice = "Build Tools 无法读取该 APK：" + Path.GetFileName(apkFile);
                Match match = Regex.Match(output, @"package: name='([^']+)' versionCode='([^']+)' versionName='([^']*)'");
                if (match.Success)
                {
                    info.Package = match.Groups[1].Value;
                    info.Version = match.Groups[3].Value + "（versionCode " + match.Groups[2].Value + "）";
                }
            }

            if (File.Exists(java) && File.Exists(apksigner))
            {
                int exit;
                string output = Run(java, new[] { "-jar", apksigner, "verify", "--print-certs", apkFile }, 60000, out exit);
                Match match = Regex.Match(output, @"SHA-256 digest:\s*([0-9a-fA-F]{64})");
                if (match.Success) info.Signature = match.Groups[1].Value.ToLowerInvariant();
                else if (exit != 0) info.Notice = FirstLine(output) ?? "APK 签名校验未通过。";
                else if (info.Signature == null) info.Notice = "APK 没有可读取的签名证书。";
            }

            if (info.Signature == null) info.Signature = ReadV1Signature(apkFile);
            if (String.IsNullOrEmpty(info.Package))
            {
                // Older or partial runtimes may not have Build Tools; read the binary manifest directly.
                string package, version;
                if (TryReadManifest(apkFile, out package, out version))
                {
                    info.Package = package;
                    info.Version = version;
                }
            }
            if (String.IsNullOrEmpty(info.Package) && String.IsNullOrEmpty(info.Notice))
                info.Notice = "无法读取 APK 的包名或版本；签名检查结果仍显示在下方。";
            if (info.Signature == null && String.IsNullOrEmpty(info.Notice))
                info.Notice = "缺少 Build Tools/JDK 或签名格式不受支持，无法校验签名；请在安装向导修复运行时。";
            return info;
        }

        private static bool TryReadManifest(string apkFile, out string package, out string version)
        {
            package = null; version = null;
            try
            {
                using (var file = File.OpenRead(apkFile))
                using (var archive = new ZipArchive(file, ZipArchiveMode.Read))
                {
                    ZipArchiveEntry entry = archive.GetEntry("AndroidManifest.xml");
                    if (entry == null || entry.Length <= 0 || entry.Length > 8 * 1024 * 1024) return false;
                    using (Stream input = entry.Open())
                    using (var buffer = new MemoryStream())
                    {
                        input.CopyTo(buffer);
                        var manifest = new BinaryManifest(buffer.ToArray());
                        string declaredVersionCode;
                        if (!manifest.TryReadEmulatorAttributes(out package, out version, out declaredVersionCode)) return false;
                        if (!String.IsNullOrEmpty(declaredVersionCode))
                            version = (version ?? "未知") + "（versionCode " + declaredVersionCode + "）";
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                if (ex is IOException || ex is InvalidDataException || ex is ArgumentException) return false;
                throw;
            }
        }

        // Minimal Android binary XML reader: enough to read package/versionName/versionCode from
        // AndroidManifest.xml without installing or executing the APK and without Build Tools.
        private sealed class BinaryManifest
        {
            private const ushort ChunkStringPool = 0x0001;
            private const ushort ChunkXml = 0x0003;
            private const ushort ChunkStartElement = 0x0102;
            private readonly byte[] data;
            private string[] strings;

            internal BinaryManifest(byte[] bytes)
            {
                data = bytes;
                if (data.Length < 8 || U16(0) != ChunkXml) throw new InvalidDataException("Not an Android binary XML file.");
            }

            internal bool TryReadEmulatorAttributes(out string package, out string version, out string versionCode)
            {
                package = null; version = null; versionCode = null;
                int total = Math.Min(data.Length, I32(4));
                int position = 8;
                while (position + 8 <= total)
                {
                    ushort type = U16(position);
                    int headerSize = U16(position + 2);
                    int chunkSize = I32(position + 4);
                    if (chunkSize < 8 || position + chunkSize > total) break;
                    if (type == ChunkStringPool && strings == null) strings = ReadPool(position);
                    else if (type == ChunkStartElement && headerSize >= 16 && TryReadManifestElement(position, out package, out version, out versionCode))
                        return true;
                    position += chunkSize;
                }
                return false;
            }

            private bool TryReadManifestElement(int offset, out string package, out string version, out string versionCode)
            {
                package = null; version = null; versionCode = null;
                if (strings == null) return false;
                string element = Get(U32(offset + 20));
                if (element != "manifest") return false;
                int attributeStart = U16(offset + 24);
                int attributeSize = U16(offset + 26);
                int attributeCount = U16(offset + 28);
                if (attributeSize < 20) return false;
                for (int index = 0; index < attributeCount; index++)
                {
                    int attribute = offset + 16 + attributeStart + index * attributeSize;
                    if (attribute + 20 > data.Length) return false;
                    string name = Get(U32(attribute + 4));
                    int raw = (int)U32(attribute + 8);
                    int valueType = data[attribute + 15];
                    uint value = U32(attribute + 16);
                    string text = raw >= 0 ? Get((uint)raw) : (valueType == 3 ? Get(value) : null);
                    if (name == "package") package = text;
                    else if (name == "versionName") version = text;
                    else if (name == "versionCode" && valueType >= 0x10 && valueType <= 0x11) versionCode = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                return !String.IsNullOrEmpty(package);
            }

            private string[] ReadPool(int offset)
            {
                if (U16(offset) != ChunkStringPool) throw new InvalidDataException("Missing string pool.");
                int headerSize = U16(offset + 2);
                int count = I32(offset + 8);
                int flags = I32(offset + 16);
                int stringsStart = I32(offset + 20);
                if (count < 0 || count > 100000) throw new InvalidDataException("Invalid string pool.");
                bool utf8 = (flags & 0x100) != 0;
                var values = new string[count];
                for (int index = 0; index < count; index++)
                {
                    int relative = I32(offset + headerSize + index * 4);
                    int at = offset + stringsStart + relative;
                    values[index] = utf8 ? ReadUtf8(at) : ReadUtf16(at);
                }
                return values;
            }

            private string ReadUtf8(int offset)
            {
                int length = data[offset++];
                if ((length & 0x80) != 0) length = ((length & 0x7f) << 8) | data[offset++];
                int characterLength = data[offset++];
                if ((characterLength & 0x80) != 0) characterLength = ((characterLength & 0x7f) << 8) | data[offset++];
                if (length < 0 || offset + length > data.Length) throw new InvalidDataException("Invalid UTF-8 string pool entry.");
                return Encoding.UTF8.GetString(data, offset, length);
            }

            private string ReadUtf16(int offset)
            {
                int length = U16(offset);
                offset += 2;
                if ((length & 0x8000) != 0) { length = ((length & 0x7fff) << 16) | U16(offset); offset += 2; }
                if (length < 0 || offset + length * 2 > data.Length) throw new InvalidDataException("Invalid UTF-16 string pool entry.");
                return Encoding.Unicode.GetString(data, offset, length * 2);
            }

            private string Get(uint index)
            {
                if (index == UInt32.MaxValue) return null;
                if (strings == null || index >= strings.Length) return null;
                return strings[index];
            }

            private ushort U16(int offset)
            {
                if (offset < 0 || offset + 2 > data.Length) throw new InvalidDataException("Truncated manifest.");
                return (ushort)(data[offset] | (data[offset + 1] << 8));
            }

            private uint U32(int offset)
            {
                if (offset < 0 || offset + 4 > data.Length) throw new InvalidDataException("Truncated manifest.");
                return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
            }

            private int I32(int offset) { return unchecked((int)U32(offset)); }
        }

        private static string ReadV1Signature(string apkFile)
        {
            try
            {
                using (var file = File.OpenRead(apkFile))
                using (var archive = new ZipArchive(file, ZipArchiveMode.Read))
                {
                    ZipArchiveEntry entry = archive.Entries.FirstOrDefault(e =>
                        e.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) &&
                        Regex.IsMatch(e.FullName, @"\.(RSA|DSA|EC)$", RegexOptions.IgnoreCase));
                    if (entry == null) return null;
                    using (Stream input = entry.Open())
                    using (var buffer = new MemoryStream())
                    {
                        input.CopyTo(buffer);
                        var certificate = new X509Certificate2(buffer.ToArray());
                        using (var sha = SHA256.Create())
                            return BitConverter.ToString(sha.ComputeHash(certificate.RawData)).Replace("-", "").ToLowerInvariant();
                    }
                }
            }
            catch (Exception ex)
            {
                if (ex is IOException || ex is InvalidDataException || ex is CryptographicException)
                    return null;
                throw;
            }
        }

        private static string FirstLine(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return null;
            return text.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0);
        }

        private static string Run(string executable, string[] arguments, int timeoutMilliseconds, out int exitCode)
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.Arguments = Join(arguments);
            using (Process process = Process.Start(start))
            {
                StringBuilder output = new StringBuilder();
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(timeoutMilliseconds))
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    exitCode = -1;
                    throw new TimeoutException(Path.GetFileName(executable) + " 超时，未读取到应用信息。");
                }
                process.WaitForExit();
                exitCode = process.ExitCode;
                lock (output) return output.ToString();
            }
        }

        private static string Join(string[] arguments)
        {
            var text = new StringBuilder();
            foreach (string argument in arguments)
            {
                if (text.Length > 0) text.Append(' ');
                if (argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) { text.Append(argument); continue; }
                text.Append('"').Append(argument.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
            }
            return text.ToString();
        }
    }
}
