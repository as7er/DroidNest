using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DroidNest
{
    internal static class AppPaths
    {
        internal static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DroidNest");

        internal static bool HasLegacy(string appRoot)
        {
            return !String.Equals(Path.GetFullPath(appRoot).TrimEnd('\\'), Root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) &&
                ((Directory.Exists(Path.Combine(appRoot, "runtime")) && !Directory.Exists(Path.Combine(Root, "runtime"))) ||
                 (Directory.Exists(Path.Combine(appRoot, "data")) && !Directory.Exists(Path.Combine(Root, "data"))));
        }

        // Copy, never move/delete the original phone. Publish each folder only after a complete copy.
        internal static Task ImportLegacyAsync(string appRoot, IProgress<string> progress, CancellationToken token)
        {
            return Task.Run(delegate
            {
                string sourcePrefix = Path.GetFullPath(appRoot).TrimEnd('\\') + "\\";
                int currentId;
                using (Process current = Process.GetCurrentProcess()) currentId = current.Id;
                foreach (Process process in Process.GetProcesses())
                {
                    using (process)
                    {
                        if (process.Id == currentId) continue;
                        string name = process.ProcessName;
                        if (name != "DroidNest" && name != "emulator" && name != "qemu-system-x86_64" && name != "scrcpy" && name != "adb") continue;
                        string executable;
                        try { executable = process.MainModule.FileName; }
                        catch (Exception ex) { throw new IOException("无法确认旧手机已停止。请关闭其他模拟器后重试：" + name, ex); }
                        if (executable.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("请先关闭旧目录中的 DroidNest、Android 和视频进程，再导入。原数据未修改。");
                    }
                }
                Directory.CreateDirectory(Root);
                byte[] buffer = new byte[131072];
                foreach (string folder in new string[] { "runtime", "data" })
                {
                    token.ThrowIfCancellationRequested();
                    string source = Path.Combine(appRoot, folder), destination = Path.Combine(Root, folder);
                    if (!Directory.Exists(source) || Directory.Exists(destination)) continue;
                    string staging = Path.Combine(Root, ".import-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        CopyDirectory(source, staging, buffer, progress, token);
                        token.ThrowIfCancellationRequested();
                        Directory.Move(staging, destination);
                    }
                    finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
                }
            }, token);
        }

        private static void CopyDirectory(string source, string destination, byte[] buffer, IProgress<string> progress, CancellationToken token)
        {
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("不导入符号链接或目录联接：" + source);
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.EnumerateFiles(source))
            {
                token.ThrowIfCancellationRequested();
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("不导入链接：" + file);
                progress.Report("正在保留旧运行环境：" + Path.GetFileName(file));
                using (FileStream input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
                using (FileStream output = new FileStream(Path.Combine(destination, Path.GetFileName(file)), FileMode.CreateNew))
                {
                    int count;
                    while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        token.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, count);
                    }
                }
            }
            foreach (string directory in Directory.EnumerateDirectories(source))
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)), buffer, progress, token);
        }
    }
}
