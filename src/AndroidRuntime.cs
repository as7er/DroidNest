using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace DroidNest
{
    public sealed class AndroidRuntime : IDisposable
    {
        private const int AdbPort = 5048;
        private const int EmulatorPort = 5580;
        private const string Serial = "emulator-5580";
        private const int RetainedLogFiles = 16;
        private const long RetainedLogBytes = 64L * 1024 * 1024;
        private readonly string root;
        private readonly string sdk;
        private readonly string avdHome;
        private readonly string androidHome;
        private readonly string imageDirectory;
        private readonly string emulatorPath;
        private readonly string adbPath;
        private readonly string logsDirectory;
        private readonly SemaphoreSlim operations = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly object stateLock = new object();
        private CancellationTokenSource starting;
        private OwnedProcess emulator;
        private OwnedProcess adbServer;
        private OwnedProcess display;
        private IntPtr displayWindow;
        private volatile bool ready;
        private volatile bool disposed;
        private string emulatorLog;
        private string avdGpuMode = AvdConfig.DefaultGpuMode;
        private string exitReason;
        private bool recovering;

        public event Action<string> Log;

        public AndroidRuntime(string root)
        {
            if (String.IsNullOrWhiteSpace(root)) throw new ArgumentException("An application directory is required.", "root");
            this.root = Path.GetFullPath(root);
            sdk = Path.Combine(this.root, "runtime", "sdk");
            DataDirectory = Path.Combine(this.root, "data");
            avdHome = Path.Combine(DataDirectory, "avd");
            androidHome = Path.Combine(DataDirectory, "android-home");
            imageDirectory = Path.Combine(sdk, "system-images", "android-30", "default", "x86_64");
            emulatorPath = Path.Combine(sdk, "emulator", "emulator.exe");
            adbPath = Path.Combine(sdk, "platform-tools", "adb.exe");
            logsDirectory = Path.Combine(this.root, "logs");
        }

        public string DataDirectory { get; private set; }

        public IntPtr DisplayWindowHandle { get { lock (stateLock) { return displayWindow; } } }

        public bool IsRunning
        {
            get
            {
                lock (stateLock)
                {
                    return !disposed && ready && emulator != null && !emulator.HasExited &&
                        adbServer != null && !adbServer.HasExited && display != null && !display.HasExited;
                }
            }
        }

        // Set when a process of the current session exits on its own, so the UI can say what ended.
        public string LastExitReason { get { lock (stateLock) { return exitReason; } } }

        // True while only the video window is being reconnected; the phone itself keeps running.
        public bool IsRecovering { get { lock (stateLock) { return recovering; } } }

        public async Task<string> CheckAsync()
        {
            ThrowIfDisposed();
            await operations.WaitAsync(lifetime.Token).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                Preflight result = await CheckCoreAsync(lifetime.Token).ConfigureAwait(false);
                Emit(result.Report);
                if (!result.Ready) throw new InvalidOperationException(result.Report);
                return result.Report;
            }
            finally { operations.Release(); }
        }

        public async Task StartAsync()
        {
            ThrowIfDisposed();
            await operations.WaitAsync(lifetime.Token).ConfigureAwait(false);
            CancellationTokenSource cancellation = null;
            try
            {
                ThrowIfDisposed();
                if (IsRunning) return;
                cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                lock (stateLock) { starting = cancellation; }
                ExceptionDispatchInfo failure = null;
                try { await StartCoreAsync(cancellation.Token).ConfigureAwait(false); }
                catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
                // C# 5 cannot await inside a catch/finally block.
                if (failure != null)
                {
                    await StopCoreAsync(false).ConfigureAwait(false);
                    failure.Throw();
                }
            }
            finally
            {
                lock (stateLock) { if (starting == cancellation) starting = null; }
                if (cancellation != null) cancellation.Dispose();
                operations.Release();
            }
        }

        private async Task StartCoreAsync(CancellationToken token)
        {
            await StopCoreAsync(false).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Preflight check = await CheckCoreAsync(token).ConfigureAwait(false);
            Emit(check.Report);
            if (!check.Ready) throw new InvalidOperationException(check.Report);
            PrepareAvd();
            token.ThrowIfCancellationRequested();

            // Never attach to an existing listener, even if it appears to be another ADB.
            RequireFreePort(AdbPort);
            RequireFreePort(EmulatorPort);
            RequireFreePort(EmulatorPort + 1);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            Directory.CreateDirectory(logsDirectory);
            int rotated = TrimLogs(logsDirectory, RetainedLogFiles, RetainedLogBytes, DateTime.UtcNow);
            if (rotated > 0)
                Emit("已清理 " + rotated.ToString(CultureInfo.InvariantCulture) + " 个旧日志；保留最近 " +
                    RetainedLogFiles.ToString(CultureInfo.InvariantCulture) + " 个、最多 " +
                    (RetainedLogBytes / 1048576).ToString(CultureInfo.InvariantCulture) + " MiB。");
            lock (stateLock) { exitReason = null; }
            OwnedProcess server = Launch(adbPath, new string[] { "-P", "5048", "nodaemon", "server" },
                Path.Combine(logsDirectory, "adb-" + stamp + ".log"), "adb");
            lock (stateLock) { adbServer = server; }
            await WaitForAdbServerAsync(server, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            RequireFreePort(EmulatorPort);
            RequireFreePort(EmulatorPort + 1);
            emulatorLog = Path.Combine(logsDirectory, "emulator-" + stamp + ".log");
            OwnedProcess phone = Launch(emulatorPath, new string[] {
                "-avd", "DroidNest", "-port", "5580", "-no-snapshot", "-no-boot-anim",
                "-gpu", avdGpuMode, "-accel", "on", "-no-window", "-no-metrics"
            }, emulatorLog, "emulator");
            lock (stateLock) { emulator = phone; }
            Emit("Starting Android 11 in the integrated display. Log: " + emulatorLog);
            Stopwatch boot = Stopwatch.StartNew();
            string lastAdb = "The Android device has not yet connected.";
            while (boot.ElapsedMilliseconds < 180000)
            {
                token.ThrowIfCancellationRequested();
                RequireAlive(phone, "The Android emulator exited before completing boot");
                RequireAlive(server, "The private ADB server exited during boot");
                int remaining = 180000 - (int)boot.ElapsedMilliseconds;
                CommandResult result;
                try
                {
                    result = await RunAdbAsync(new string[] { "shell", "getprop", "sys.boot_completed" },
                        Math.Min(8000, remaining), token).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    lastAdb = ex.Message;
                    continue;
                }
                if (result.ExitCode == 0 && result.Output.Trim() == "1")
                {
                    RequireAlive(phone, "The Android emulator exited immediately after boot");
                    token.ThrowIfCancellationRequested();
                    CommandResult userState = await RunAdbAsync(new string[] { "shell", "am", "get-started-user-state", "0" },
                        Math.Min(8000, Math.Max(1, 180000 - (int)boot.ElapsedMilliseconds)), token).ConfigureAwait(false);
                    if (userState.ExitCode != 0 || userState.Output.Trim() != "RUNNING_UNLOCKED")
                    {
                        lastAdb = "Android is booted but user storage is not unlocked: " + userState.Details;
                        await Task.Delay(1000, token).ConfigureAwait(false);
                        continue;
                    }
                    await EnsureWebViewAsync(token).ConfigureAwait(false);
                    await StartDisplayAsync(token).ConfigureAwait(false);
                    ready = true;
                    Emit("Android is ready. Apps and phone data are saved in " + DataDirectory);
                    ObserveExitAsync(phone, server);
                    return;
                }
                lastAdb = result.Details;
                remaining = 180000 - (int)boot.ElapsedMilliseconds;
                if (remaining > 0) await Task.Delay(Math.Min(1500, remaining), token).ConfigureAwait(false);
            }
            throw new TimeoutException("Android did not complete boot within 180 seconds. " + lastAdb +
                Environment.NewLine + "Emulator log: " + emulatorLog + Environment.NewLine + phone.Tail);
        }

        private async Task EnsureWebViewAsync(CancellationToken token)
        {
            CommandResult current = await RunAdbAsync(new string[] { "shell", "dumpsys", "webviewupdate" }, 15000, token).ConfigureAwait(false);
            EnsureSuccess(current, "Reading Android WebView failed");
            var version = System.Text.RegularExpressions.Regex.Match(current.Output,
                @"Current WebView package \(name, version\): \(com\.android\.webview, (\d+)\.");
            int major;
            if (version.Success && Int32.TryParse(version.Groups[1].Value, out major) && major >= 156) return;
            string apk = Path.Combine(root, "runtime", "webview-local.apk");
            if (!File.Exists(apk)) throw new FileNotFoundException("缺少新版 WebView。请关闭并重新打开 DroidNest，进入组件安装向导修复。", apk);
            Emit("正在升级 Android WebView，保留应用和手机数据…");
            CommandResult installed = await RunAdbAsync(new string[] { "install", "-r", apk }, 180000, token).ConfigureAwait(false);
            EnsureSuccess(installed, "WebView upgrade failed");
            if (!installed.Output.Contains("Success")) throw new InvalidOperationException("Android 未确认 WebView 更新成功：" + installed.Details);
            CommandResult updated = await RunAdbAsync(new string[] { "shell", "dumpsys", "webviewupdate" }, 15000, token).ConfigureAwait(false);
            version = System.Text.RegularExpressions.Regex.Match(updated.Output,
                @"Current WebView package \(name, version\): \(com\.android\.webview, (\d+)\.");
            if (!version.Success || !Int32.TryParse(version.Groups[1].Value, out major) || major < 156)
                throw new InvalidOperationException("WebView 更新已安装但尚未成为有效提供程序：" + updated.Details);
            Emit("Android WebView 已升级至 " + major + "，现代网页应用可以使用更新后的脚本引擎。");
        }

        private async Task StartDisplayAsync(CancellationToken token)
        {
            string path = Path.Combine(root, "runtime", "scrcpy-win64-v5.0.1", "scrcpy.exe");
            if (!File.Exists(path)) throw new FileNotFoundException("缺少 scrcpy 视频组件。请关闭并重新打开 DroidNest，进入组件安装向导修复。", path);
            OwnedProcess video = Launch(path, new string[] {
                "--serial=" + Serial, "--window-title=DroidNest.Display", "--window-borderless",
                "--no-window-aspect-ratio-lock", "--window-x=-16000", "--window-y=-16000",
                "--window-width=360", "--window-height=640", "--no-audio", "--max-fps=30",
                "--video-bit-rate=4M", "--no-clipboard-autosync", "--shortcut-mod=rctrl"
            }, Path.Combine(logsDirectory, "display-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".log"), "display");
            lock (stateLock) { display = video; }
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 30000)
            {
                token.ThrowIfCancellationRequested();
                RequireAlive(video, "Android 视频连接失败");
                IntPtr found = IntPtr.Zero;
                Native.EnumWindows(delegate(IntPtr window, IntPtr unused) {
                    uint owner;
                    Native.GetWindowThreadProcessId(window, out owner);
                    // scrcpy creates a hidden HWND before receiving its first frame.
                    // Reparenting/showing it early can render with zero content dimensions.
                    // Its own first-frame handler makes the off-screen window visible.
                    if (owner == video.ProcessId && Native.IsWindowVisible(window))
                    {
                        StringBuilder title = new StringBuilder(128);
                        Native.GetWindowText(window, title, title.Capacity);
                        if (title.ToString() == "DroidNest.Display") { found = window; return false; }
                    }
                    return true;
                }, IntPtr.Zero);
                if (found != IntPtr.Zero) { lock (stateLock) { displayWindow = found; } return; }
                await Task.Delay(100, token).ConfigureAwait(false);
            }
            throw new TimeoutException("Android 视频窗口未就绪。" + video.Tail);
        }

        public async Task StopAsync()
        {
            lock (stateLock)
            {
                if (starting != null) starting.Cancel();
            }
            await operations.WaitAsync().ConfigureAwait(false);
            try { await StopCoreAsync(true).ConfigureAwait(false); }
            finally { operations.Release(); }
        }

        public async Task InstallAsync(string apkPath)
        {
            ThrowIfDisposed();
            if (String.IsNullOrWhiteSpace(apkPath) ||
                !String.Equals(Path.GetExtension(apkPath), ".apk", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose an existing Android .apk file.", "apkPath");
            string fullPath = Path.GetFullPath(apkPath);
            if (!File.Exists(fullPath)) throw new FileNotFoundException("The APK file was not found.", fullPath);
            await operations.WaitAsync(lifetime.Token).ConfigureAwait(false);
            try
            {
                RequireRunning();
                Emit("Installing " + Path.GetFileName(fullPath) + "...");
                CommandResult result = await RunAdbAsync(new string[] { "install", "-r", fullPath },
                    180000, lifetime.Token).ConfigureAwait(false);
                EnsureSuccess(result, "APK installation failed");
                bool success = false;
                foreach (string line in result.Output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    if (line.Trim() == "Success") success = true;
                if (!success) throw new InvalidOperationException("ADB did not confirm installation. " + result.Details);
                Emit("Installed " + Path.GetFileName(fullPath) + ". Open it from the phone's app drawer.");
            }
            finally { operations.Release(); }
        }

        public Task KeyAsync(int key)
        {
            if (key < 0) throw new ArgumentOutOfRangeException("key", "An Android key code must be nonnegative.");
            return DeviceCommandAsync(new string[] { "shell", "input", "keyevent", key.ToString(CultureInfo.InvariantCulture) },
                "Sending the Android key failed");
        }

        public Task SettingsAsync()
        {
            return DeviceCommandAsync(new string[] { "shell", "am", "start", "-a", "android.settings.SETTINGS" },
                "Opening Android Settings failed");
        }

        public async Task RotateAsync()
        {
            ThrowIfDisposed();
            await operations.WaitAsync(lifetime.Token).ConfigureAwait(false);
            try
            {
                RequireRunning();
                CommandResult current = await RunAdbAsync(new string[] { "shell", "settings", "get", "system", "user_rotation" },
                    15000, lifetime.Token).ConfigureAwait(false);
                EnsureSuccess(current, "Reading screen orientation failed");
                int rotation;
                string value = current.Output.Trim();
                if (value == "null") rotation = 0;
                else if (!Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out rotation))
                    throw new InvalidOperationException("Android returned an invalid screen orientation: " + current.Details);
                EnsureSuccess(await RunAdbAsync(new string[] { "shell", "settings", "put", "system", "accelerometer_rotation", "0" },
                    15000, lifetime.Token).ConfigureAwait(false), "Disabling automatic rotation failed");
                string next = rotation == 0 ? "1" : "0";
                EnsureSuccess(await RunAdbAsync(new string[] { "shell", "settings", "put", "system", "user_rotation", next },
                    15000, lifetime.Token).ConfigureAwait(false), "Rotating the screen failed");
                Emit(next == "1" ? "Screen set to landscape." : "Screen set to portrait.");
            }
            finally { operations.Release(); }
        }

        private async Task DeviceCommandAsync(string[] arguments, string failure)
        {
            ThrowIfDisposed();
            await operations.WaitAsync(lifetime.Token).ConfigureAwait(false);
            try
            {
                RequireRunning();
                EnsureSuccess(await RunAdbAsync(arguments, 15000, lifetime.Token).ConfigureAwait(false), failure);
            }
            finally { operations.Release(); }
        }

        private bool CheckHost(StringBuilder report, bool installation = false)
        {
            bool usable = true;
            try
            {
                Native.VersionInformation version = new Native.VersionInformation();
                version.Size = (uint)Marshal.SizeOf(typeof(Native.VersionInformation));
                if (Native.RtlGetVersion(ref version) != 0) throw new InvalidOperationException("无法读取 Windows 版本。");
                if (version.Major != 10 || version.Build < 10240 || version.ProductType != 1)
                {
                    report.AppendLine("需要 Windows 10/11 桌面系统，不支持旧版 Windows 或 Windows Server。");
                    usable = false;
                }
                else report.AppendLine("Windows 10/11，内部版本 " + version.Build.ToString(CultureInfo.InvariantCulture) + "。");
                ushort processMachine, nativeMachine;
                bool amd64;
                try
                {
                    if (!Native.IsWow64Process2(Native.GetCurrentProcess(), out processMachine, out nativeMachine))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    amd64 = nativeMachine == 0x8664;
                }
                catch (EntryPointNotFoundException)
                {
                    Native.SystemInformation system;
                    Native.GetNativeSystemInfo(out system);
                    amd64 = system.Architecture == 9;
                }
                if (!amd64)
                {
                    report.AppendLine("需要 Intel/AMD x64 主机，不支持 ARM/ARM64（包括 x64 模拟运行）。");
                    usable = false;
                }
                else report.AppendLine("主机原生架构为 AMD64。");
            }
            catch (Exception ex)
            {
                report.AppendLine("无法确认受支持的主机系统和架构：" + ex.Message);
                usable = false;
            }
            string[] directories = installation ? new string[] { root } : new string[] { root, DataDirectory, androidHome, avdHome,
                Path.Combine(avdHome, "DroidNest.avd"), logsDirectory };
            foreach (string directory in directories)
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    string probe = Path.Combine(directory, ".droidnest-write-" + Guid.NewGuid().ToString("N") + ".tmp");
                    using (FileStream stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 1, FileOptions.DeleteOnClose)) { stream.WriteByte(0); }
                }
                catch (Exception ex)
                {
                    report.AppendLine("目录不可写，请将完整目录移到当前用户可写的位置：" + directory + "；" + ex.Message);
                    usable = false;
                }
                try
                {
                    RuntimeDiskSpace.Require(directory, RuntimeDiskSpace.PhoneReserveBytes);
                }
                catch (Exception ex)
                {
                    report.AppendLine("启动前磁盘空间检查失败：" + ex.Message);
                    usable = false;
                }
            }
            Native.MemoryStatus memory = new Native.MemoryStatus();
            memory.Length = (uint)Marshal.SizeOf(typeof(Native.MemoryStatus));
            if (!Native.GlobalMemoryStatusEx(ref memory)) report.AppendLine("警告：无法读取主机内存，不阻止启动。");
            else if (memory.TotalPhysical < 8UL * 1024 * 1024 * 1024)
                report.AppendLine("警告：主机内存不足 8 GiB，运行可能较慢；此建议不阻止启动。");
            return usable;
        }

        internal Task<string> CheckInstallationHostAsync(CancellationToken token)
        {
            return Task.Run(delegate
            {
                token.ThrowIfCancellationRequested();
                StringBuilder report = new StringBuilder();
                bool usable = CheckHost(report, true);
                string whpx;
                if (!CheckWhpx(out whpx)) usable = false;
                report.AppendLine(whpx);
                if (!usable) throw new InvalidOperationException(report.ToString());
                return report.ToString();
            }, token);
        }

        private async Task<Preflight> CheckCoreAsync(CancellationToken token)
        {
            StringBuilder report = new StringBuilder();
            bool usable = CheckHost(report);
            report.AppendLine("DroidNest 本地运行时：" + sdk);
            string[] required = new string[] {
                emulatorPath,
                Path.Combine(root, "runtime", "scrcpy-win64-v5.0.1", "scrcpy.exe"),
                Path.Combine(root, "runtime", "scrcpy-win64-v5.0.1", "scrcpy-server"),
                Path.Combine(root, "runtime", "scrcpy-win64-v5.0.1", "SDL3.dll"),
                Path.Combine(root, "runtime", "scrcpy-win64-v5.0.1", "avcodec-63.dll"),
                Path.Combine(root, "runtime", "scrcpy-win64-v5.0.1", "avformat-63.dll"),
                Path.Combine(root, "runtime", "scrcpy-win64-v5.0.1", "avutil-61.dll"),
                Path.Combine(root, "runtime", "scrcpy-win64-v5.0.1", "swresample-7.dll"),
                Path.Combine(root, "runtime", "scrcpy-win64-v5.0.1", "libusb-1.0.dll"),
                Path.Combine(root, "runtime", "scrcpy-win64-v5.0.1", "AdbWinApi.dll"),
                Path.Combine(root, "runtime", "scrcpy-win64-v5.0.1", "AdbWinUsbApi.dll"),
                Path.Combine(root, "runtime", "webview-local.apk"),
                Path.Combine(sdk, "emulator", "qemu", "windows-x86_64", "qemu-system-x86_64.exe"),
                adbPath, Path.Combine(sdk, "platform-tools", "AdbWinApi.dll"),
                Path.Combine(sdk, "platform-tools", "AdbWinUsbApi.dll"),
                Path.Combine(imageDirectory, "system.img"), Path.Combine(imageDirectory, "userdata.img"),
                Path.Combine(imageDirectory, "ramdisk.img"), Path.Combine(imageDirectory, "kernel-ranchu"),
                Path.Combine(imageDirectory, "vendor.img"), Path.Combine(imageDirectory, "encryptionkey.img"),
                Path.Combine(imageDirectory, "source.properties"), Path.Combine(imageDirectory, "advancedFeatures.ini")
            };
            bool filesPresent = true;
            foreach (string path in required)
            {
                try
                {
                    if (File.Exists(path) && new FileInfo(path).Length > 0) continue;
                    report.AppendLine("缺少文件或文件为空：" + path);
                }
                catch (Exception ex) { report.AppendLine("无法读取运行时文件：" + path + "；" + ex.Message); }
                filesPresent = false;
                usable = false;
            }
            if (filesPresent) report.AppendLine("本地模拟器、platform-tools、Android 11 x86_64 镜像、scrcpy 和本地签名 WebView 文件齐全。");
            else report.AppendLine("请关闭并重新打开 DroidNest，使用首次安装向导下载或修复组件；手机数据将保留。");

            string whpx;
            bool hypervisor = CheckWhpx(out whpx);
            report.AppendLine(whpx);
            if (!hypervisor) usable = false;
            if (File.Exists(emulatorPath))
            {
                try
                {
                    Directory.CreateDirectory(androidHome);
                    Directory.CreateDirectory(avdHome);
                    CommandResult acceleration = await RunCommandAsync(emulatorPath, new string[] { "-accel-check" },
                        20000, token).ConfigureAwait(false);
                    report.AppendLine("模拟器加速检查（退出码 " + acceleration.ExitCode.ToString(CultureInfo.InvariantCulture) + "）：" + acceleration.Details);
                    if (acceleration.ExitCode != 0 || acceleration.Details.IndexOf("WHPX", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        report.AppendLine("模拟器必须报告 WHPX 已安装且可用。请启用 Windows 虚拟机监控程序平台和固件虚拟化（Intel VT-x/AMD-V），然后重启 Windows。");
                        usable = false;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    report.AppendLine("加速检查失败：" + ex.Message);
                    usable = false;
                }
            }
            if (!IsRunning)
            {
                foreach (int port in new int[] { AdbPort, EmulatorPort, EmulatorPort + 1 })
                {
                    try { RequireFreePort(port); }
                    catch (InvalidOperationException ex) { report.AppendLine("端口 " + port.ToString(CultureInfo.InvariantCulture) + " 已占用或被 Windows 保留。请关闭已有 DroidNest，或自行释放端口；不会停止其他进程。" + ex.Message); usable = false; }
                }
            }
            report.AppendLine(usable ? "环境检查通过：本地 Android 运行时和 WHPX 可用。" : "环境检查未通过：请解决以上项目后再启动 Android，安装步骤见 README.md。不会自动提权或修改 Windows 功能。");
            return new Preflight { Ready = usable, Report = report.ToString().TrimEnd() };
        }

        private static bool CheckWhpx(out string message)
        {
            try
            {
                int present;
                uint written;
                int result = Native.WHvGetCapability(0, out present, 4, out written);
                if (result >= 0 && present != 0)
                {
                    message = "WHPX：Windows 虚拟机监控程序平台已报告活动的虚拟机监控程序。";
                    return true;
                }
                message = "WHPX 不可用（HRESULT 0x" + result.ToString("X8", CultureInfo.InvariantCulture) +
                    "）。请在 Windows 功能中启用 Windows 虚拟机监控程序平台，在固件中启用 Intel VT-x/AMD-V，并重启。确保未禁用 hypervisorlaunchtype。";
            }
            catch (DllNotFoundException)
            {
                message = "WHPX 不可用：缺少 WinHvPlatform.dll。请在 Windows 功能中启用 Windows 虚拟机监控程序平台并重启。";
            }
            catch (EntryPointNotFoundException)
            {
                message = "WHPX 不可用：系统未提供 WHvGetCapability。请使用受支持的 Windows 10/11 x64，并启用 Windows 虚拟机监控程序平台。";
            }
            catch (BadImageFormatException)
            {
                message = "WHPX 不可用：平台库的架构不匹配。请使用 Intel/AMD x64 Windows 和 64 位 DroidNest。";
            }
            return false;
        }

        private void PrepareAvd()
        {
            string phoneDirectory = Path.Combine(avdHome, "DroidNest.avd");
            Directory.CreateDirectory(phoneDirectory);
            Directory.CreateDirectory(androidHome);
            // Only configuration is written. Existing userdata, APKs and app storage are never deleted.
            // Tuning keys a user edited in config.ini are carried over; structural keys are client-owned.
            string configPath = Path.Combine(phoneDirectory, "config.ini");
            string config = AvdConfig.Merge(File.Exists(configPath) ? File.ReadAllText(configPath) : null, imageDirectory);
            AvdConfig.WriteAtomic(configPath, config);
            avdGpuMode = AvdConfig.GpuMode(config);
            AvdConfig.WriteAtomic(Path.Combine(avdHome, "DroidNest.ini"),
                "avd.ini.encoding=UTF-8\npath=" + phoneDirectory.Replace('\\', '/') + "\ntarget=android-30\n");
        }

        // Old sessions keep one log per process. Rotation runs before a new session creates its logs,
        // never deletes a log that is still being written, and ignores files it cannot delete.
        internal static int TrimLogs(string directory, int keepFiles, long keepBytes, DateTime nowUtc)
        {
            FileInfo[] files;
            try { files = new DirectoryInfo(directory).GetFiles("*.log"); }
            catch (IOException) { return 0; }
            catch (UnauthorizedAccessException) { return 0; }
            Array.Sort(files, delegate(FileInfo left, FileInfo right) { return left.LastWriteTimeUtc.CompareTo(right.LastWriteTimeUtc); });
            long total = 0;
            foreach (FileInfo file in files) total += file.Length;
            int remaining = files.Length;
            int deleted = 0;
            foreach (FileInfo file in files)
            {
                if (remaining <= keepFiles && total <= keepBytes) break;
                if (nowUtc - file.LastWriteTimeUtc < TimeSpan.FromMinutes(10)) continue;
                try
                {
                    file.Delete();
                    remaining--;
                    total -= file.Length;
                    deleted++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return deleted;
        }

        private IDictionary<string, string> ProcessEnvironment()
        {
            SortedDictionary<string, string> values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
                values[(string)entry.Key] = (string)entry.Value;
            values.Remove("ANDROID_SERIAL");
            values.Remove("ANDROID_SDK_HOME");
            values["ANDROID_SDK_ROOT"] = sdk;
            values["ANDROID_HOME"] = sdk;
            values["ANDROID_AVD_HOME"] = avdHome;
            values["ANDROID_USER_HOME"] = androidHome;
            values["ANDROID_EMULATOR_HOME"] = androidHome;
            values["ANDROID_ADB_SERVER_PORT"] = AdbPort.ToString(CultureInfo.InvariantCulture);
            values["ADB_SERVER_SOCKET"] = "tcp:127.0.0.1:5048";
            values["ADB"] = adbPath;
            values["SCRCPY_SERVER_PATH"] = Path.Combine(root, "runtime", "scrcpy-win64-v5.0.1", "scrcpy-server");
            return values;
        }

        private OwnedProcess Launch(string executable, string[] arguments, string logFile, string label)
        {
            return OwnedProcess.Start(executable, arguments, root, ProcessEnvironment(), logFile,
                delegate(string line) { Emit("[" + label + "] " + line); });
        }

        private async Task<CommandResult> RunCommandAsync(string executable, string[] arguments, int timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (OwnedProcess command = OwnedProcess.Start(executable, arguments, root, ProcessEnvironment(), null, null))
            {
                if (!await command.WaitAsync(timeout, token).ConfigureAwait(false))
                    throw new TimeoutException(Path.GetFileName(executable) + " timed out after " +
                        (timeout / 1000).ToString(CultureInfo.InvariantCulture) + " seconds. " + command.Tail);
                await command.DrainAsync().ConfigureAwait(false);
                return new CommandResult { ExitCode = command.ExitCode, Output = command.Output, Error = command.Error };
            }
        }

        private Task<CommandResult> RunAdbAsync(string[] arguments, int timeout, CancellationToken token)
        {
            OwnedProcess server;
            lock (stateLock) { server = adbServer; }
            if (server == null || server.HasExited)
                throw new InvalidOperationException("DroidNest's private ADB server is not running. Stop and start the phone again.");
            if (GetListenerOwner(AdbPort) != server.ProcessId)
                throw new InvalidOperationException("Port 5048 is no longer owned by DroidNest's private ADB server. Stop and restart the phone; no command was sent to the other listener.");
            OwnedProcess phone;
            lock (stateLock) { phone = emulator; }
            if (phone == null || phone.HasExited)
                throw new InvalidOperationException("DroidNest's Android emulator is no longer running.");
            foreach (int port in new int[] { EmulatorPort, EmulatorPort + 1 })
            {
                uint owner = GetListenerOwner(port);
                if (owner != 0 && !phone.OwnsProcess(owner))
                    throw new InvalidOperationException("Reserved emulator port " + port + " is owned by another process. No Android command was sent to it.");
            }
            string[] complete = new string[arguments.Length + 4];
            complete[0] = "-P";
            complete[1] = "5048";
            complete[2] = "-s";
            complete[3] = Serial;
            Array.Copy(arguments, 0, complete, 4, arguments.Length);
            return RunCommandAsync(adbPath, complete, timeout, token);
        }

        private static async Task WaitForAdbServerAsync(OwnedProcess server, CancellationToken token)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 10000)
            {
                token.ThrowIfCancellationRequested();
                RequireAlive(server, "The private ADB server could not start");
                uint owner = GetListenerOwner(AdbPort);
                if (owner == server.ProcessId) return;
                if (owner != 0)
                    throw new InvalidOperationException("Another process claimed DroidNest port 5048 during startup. It has not been stopped; close that process before retrying.");
                await Task.Delay(100, token).ConfigureAwait(false);
            }
            throw new TimeoutException("The private ADB server did not listen on port 5048 within 10 seconds. " + server.Tail);
        }

        private async Task ReleaseDisplayAsync()
        {
            OwnedProcess video;
            IntPtr window;
            lock (stateLock) { video = display; window = displayWindow; displayWindow = IntPtr.Zero; }
            if (video == null) return;
            if (!video.HasExited && window != IntPtr.Zero) Native.PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
            await video.WaitAsync(3000, CancellationToken.None).ConfigureAwait(false);
            video.Dispose();
            lock (stateLock) { if (display == video) display = null; }
        }

        private async Task StopCoreAsync(bool announce)
        {
            ready = false;
            await ReleaseDisplayAsync().ConfigureAwait(false);
            OwnedProcess phone;
            OwnedProcess server;
            lock (stateLock) { phone = emulator; server = adbServer; }
            try
            {
                if (phone != null && !phone.HasExited)
                {
                    if (server != null && !server.HasExited)
                    {
                        try
                        {
                            // emu kill stops the VM without an Android shutdown. Flush guest
                            // filesystem writes first so a just-written app file survives.
                            CommandResult synced = await RunAdbAsync(new string[] { "shell", "sync" }, 15000,
                                CancellationToken.None).ConfigureAwait(false);
                            if (synced.ExitCode != 0) Emit("Flushing Android storage: " + synced.Details);
                        }
                        catch (Exception ex) { Emit("Flushing Android storage unavailable: " + ex.Message); }
                        try
                        {
                            CommandResult result = await RunAdbAsync(new string[] { "emu", "kill" }, 4000,
                                CancellationToken.None).ConfigureAwait(false);
                            if (result.ExitCode != 0) Emit("Graceful emulator stop: " + result.Details);
                        }
                        catch (Exception ex) { Emit("Graceful emulator stop unavailable: " + ex.Message); }
                    }
                    if (!await phone.WaitAsync(6000, CancellationToken.None).ConfigureAwait(false))
                        Emit("Android did not exit promptly; closing only DroidNest's owned emulator process tree.");
                }
            }
            finally
            {
                lock (stateLock)
                {
                    emulator = null;
                    adbServer = null;
                }
                // Closing a kill-on-close job terminates only the processes launched into that job.
                // In particular, never issue adb kill-server against a possibly replaced/shared server.
                if (phone != null) phone.Dispose();
                if (server != null) server.Dispose();
            }
            if (announce && (phone != null || server != null)) Emit("Android stopped. Phone data has been kept.");
        }

        private async Task<bool> TryReconnectDisplayAsync()
        {
            lock (stateLock) { recovering = true; }
            try
            {
                await ReleaseDisplayAsync().ConfigureAwait(false);
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (lifetime.IsCancellationRequested) return false;
                    Emit("手机画面连接已中断，正在重新连接（" + (attempt + 1).ToString(CultureInfo.InvariantCulture) + "/3）…");
                    try
                    {
                        await StartDisplayAsync(lifetime.Token).ConfigureAwait(false);
                        Emit("手机画面已重新连接；Android 未重启，应用和手机数据保持原状。");
                        return true;
                    }
                    catch (OperationCanceledException) { return false; }
                    catch (Exception ex) { Emit("重新连接手机画面失败：" + ex.Message); }
                    try { await Task.Delay(1500, lifetime.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return false; }
                }
                lock (stateLock) { exitReason = "手机画面多次重连失败。点击“启动手机”可重启运行环境；手机数据保留。"; }
                return false;
            }
            finally { lock (stateLock) { recovering = false; } }
        }

        private async void ObserveExitAsync(OwnedProcess phone, OwnedProcess server)
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    await Task.Delay(1000, lifetime.Token).ConfigureAwait(false);
                    lock (stateLock) { if (emulator != phone || adbServer != server) return; }
                    OwnedProcess video;
                    lock (stateLock) { video = display; }
                    bool phoneAlive = !phone.HasExited && !server.HasExited;
                    if (phoneAlive && video != null && !video.HasExited) continue;
                    await operations.WaitAsync(lifetime.Token).ConfigureAwait(false);
                    try
                    {
                        lock (stateLock) { if (emulator != phone || adbServer != server) return; }
                        if (phoneAlive)
                        {
                            // Only the video window died: reconnecting keeps the running Android session.
                            if (await TryReconnectDisplayAsync().ConfigureAwait(false)) continue;
                        }
                        else
                        {
                            string reason = phone.HasExited
                                ? (server.HasExited ? "Android 模拟器和私有 ADB 服务已退出" : "Android 模拟器已退出（代码 " + phone.ExitCode.ToString(CultureInfo.InvariantCulture) + "）")
                                : ("私有 ADB 服务已退出（代码 " + server.ExitCode.ToString(CultureInfo.InvariantCulture) + "）");
                            string log = emulatorLog ?? Path.Combine(logsDirectory, "emulator-*.log");
                            lock (stateLock) { exitReason = reason + "。日志：" + log; }
                            Emit(reason + "。Stopping only the phone this session started; data is kept.");
                        }
                        await StopCoreAsync(false).ConfigureAwait(false);
                    }
                    finally { operations.Release(); }
                    return;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Emit("Runtime shutdown: " + ex.Message); }
        }

        private static void RequireFreePort(int port)
        {
            TcpListener probe = new TcpListener(IPAddress.Any, port);
            try
            {
                probe.Server.ExclusiveAddressUse = true;
                probe.Start();
            }
            catch (SocketException ex)
            {
                throw new InvalidOperationException("Reserved DroidNest port " + port.ToString(CultureInfo.InvariantCulture) +
                    " is occupied or unavailable. Close the other owner or release the Windows port reservation, then retry. DroidNest will not stop an unrelated process. " + ex.Message, ex);
            }
            finally { probe.Stop(); }
        }

        private static uint GetListenerOwner(int port)
        {
            int size = 0;
            uint status = Native.GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
            if (status != 0 && status != 122)
                throw new Win32Exception((int)status, "Cannot determine ownership of the reserved DroidNest TCP port.");
            for (int attempt = 0; attempt < 3; attempt++)
            {
                IntPtr table = Marshal.AllocHGlobal(size);
                try
                {
                    status = Native.GetExtendedTcpTable(table, ref size, false, 2, 3, 0);
                    if (status == 122) continue;
                    if (status != 0) throw new Win32Exception((int)status, "Cannot inspect the reserved DroidNest TCP port.");
                    int rows = Marshal.ReadInt32(table);
                    for (int index = 0; index < rows; index++)
                    {
                        int offset = 4 + index * 24;
                        int networkPort = Marshal.ReadInt32(table, offset + 8);
                        int localPort = ((networkPort & 255) << 8) | ((networkPort >> 8) & 255);
                        if (localPort == port) return unchecked((uint)Marshal.ReadInt32(table, offset + 20));
                    }
                    return 0;
                }
                finally { Marshal.FreeHGlobal(table); }
            }
            throw new InvalidOperationException("Windows TCP listeners changed repeatedly. Retry starting DroidNest when port 5048 is available.");
        }

        private static void RequireAlive(OwnedProcess process, string message)
        {
            if (process.HasExited)
                throw new InvalidOperationException(message + " (exit " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + "). " + process.Tail);
        }

        private static void EnsureSuccess(CommandResult result, string message)
        {
            if (result.ExitCode != 0 || result.Details.IndexOf("Error:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                result.Details.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0 ||
                result.Details.IndexOf("Failure [", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new InvalidOperationException(message + ": " + result.Details);
        }

        private void RequireRunning()
        {
            ThrowIfDisposed();
            if (!IsRunning) throw new InvalidOperationException("Start Android and wait for it to finish booting before using this control.");
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException("AndroidRuntime");
        }

        private void Emit(string message)
        {
            Action<string> handler = Log;
            if (handler == null) return;
            // UI subscribers must marshal without blocking; a subscriber cannot break process draining.
            try { handler(message); }
            catch { }
        }

        public void Dispose()
        {
            lock (stateLock)
            {
                if (disposed) return;
                disposed = true;
                lifetime.Cancel();
                if (starting != null) starting.Cancel();
            }
            if (operations.Wait(15000))
            {
                try { StopCoreAsync(false).GetAwaiter().GetResult(); }
                finally { operations.Release(); }
            }
            else
            {
                // The kernel-owned jobs also cover qemu descendants if a command is stuck.
                lock (stateLock)
                {
                    ready = false;
                    if (display != null) display.Dispose();
                    display = null;
                    displayWindow = IntPtr.Zero;
                    if (emulator != null) emulator.Dispose();
                    if (adbServer != null) adbServer.Dispose();
                    emulator = null;
                    adbServer = null;
                }
            }
        }

        private sealed class Preflight
        {
            internal bool Ready;
            internal string Report;
        }

        private sealed class CommandResult
        {
            internal int ExitCode;
            internal string Output;
            internal string Error;
            internal string Details
            {
                get { return (Output + Environment.NewLine + Error).Trim(); }
            }
        }

        // A process starts suspended and enters its private kill-on-close job BEFORE it can spawn
        // qemu or an ADB child. This avoids the Process.Start/AssignProcessToJobObject race.
        private sealed class OwnedProcess : IDisposable
        {
            private static readonly object launchLock = new object();
            private readonly object outputLock = new object();
            private readonly StringBuilder stdout = new StringBuilder();
            private readonly StringBuilder stderr = new StringBuilder();
            private IntPtr processHandle;
            private IntPtr jobHandle;
            private StreamReader outputReader;
            private StreamReader errorReader;
            private StreamWriter logWriter;
            private Task outputDrain;
            private Task errorDrain;
            private Action<string> notify;
            private int closed;
            internal uint ProcessId { get; private set; }

            internal bool HasExited
            {
                get { return processHandle == IntPtr.Zero || Native.WaitForSingleObject(processHandle, 0) != 258; }
            }

            internal int ExitCode
            {
                get
                {
                    uint code;
                    if (processHandle == IntPtr.Zero || !Native.GetExitCodeProcess(processHandle, out code)) return -1;
                    return unchecked((int)code);
                }
            }

            internal string Output { get { lock (outputLock) { return stdout.ToString(); } } }
            internal string Error { get { lock (outputLock) { return stderr.ToString(); } } }
            internal string Tail { get { lock (outputLock) { return (stdout.ToString() + "\n" + stderr.ToString()).Trim(); } } }

            internal bool OwnsProcess(uint processId)
            {
                if (processId == ProcessId) return !HasExited;
                IntPtr candidate = Native.OpenProcess(0x1000, false, processId);
                if (candidate == IntPtr.Zero) return false;
                try
                {
                    bool belongs;
                    return jobHandle != IntPtr.Zero && Native.IsProcessInJob(candidate, jobHandle, out belongs) && belongs;
                }
                finally { Native.CloseHandle(candidate); }
            }

            internal static OwnedProcess Start(string executable, string[] arguments, string directory,
                IDictionary<string, string> environment, string logFile, Action<string> notify)
            {
                lock (launchLock)
                {
                    OwnedProcess child = new OwnedProcess();
                    child.notify = notify;
                    IntPtr outputRead = IntPtr.Zero, outputWrite = IntPtr.Zero;
                    IntPtr errorRead = IntPtr.Zero, errorWrite = IntPtr.Zero;
                    IntPtr input = IntPtr.Zero, environmentBlock = IntPtr.Zero;
                    Native.ProcessInformation information = new Native.ProcessInformation();
                    try
                    {
                        if (logFile != null)
                        {
                            child.logWriter = new StreamWriter(new FileStream(logFile, FileMode.CreateNew, FileAccess.Write,
                                FileShare.ReadWrite), new UTF8Encoding(false));
                            child.logWriter.AutoFlush = true;
                            child.logWriter.WriteLine(DateTime.Now.ToString("O", CultureInfo.InvariantCulture) + " " +
                                BuildCommandLine(executable, arguments));
                        }
                        child.jobHandle = Native.CreateJobObject(IntPtr.Zero, null);
                        if (child.jobHandle == IntPtr.Zero) throw Win32Failure("Creating an isolated process job");
                        Native.JobExtendedLimitInformation limits = new Native.JobExtendedLimitInformation();
                        limits.BasicLimitInformation.LimitFlags = 0x00002000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                        if (!Native.SetInformationJobObject(child.jobHandle, 9, ref limits,
                            (uint)Marshal.SizeOf(typeof(Native.JobExtendedLimitInformation))))
                            throw Win32Failure("Configuring the isolated process job");
                        Native.SecurityAttributes attributes = new Native.SecurityAttributes();
                        attributes.Length = Marshal.SizeOf(typeof(Native.SecurityAttributes));
                        attributes.InheritHandle = 1;
                        CreateOutputPipe(ref attributes, out outputRead, out outputWrite);
                        CreateOutputPipe(ref attributes, out errorRead, out errorWrite);
                        input = Native.CreateFile("NUL", 0x80000000, 3, ref attributes, 3, 0, IntPtr.Zero);
                        if (input == new IntPtr(-1)) throw Win32Failure("Opening process standard input");
                        StringBuilder environmentText = new StringBuilder();
                        foreach (KeyValuePair<string, string> entry in environment)
                            environmentText.Append(entry.Key).Append('=').Append(entry.Value).Append('\0');
                        environmentText.Append('\0');
                        environmentBlock = Marshal.StringToHGlobalUni(environmentText.ToString());
                        Native.StartupInformation startup = new Native.StartupInformation();
                        startup.Size = Marshal.SizeOf(typeof(Native.StartupInformation));
                        startup.Flags = 0x00000100; // STARTF_USESTDHANDLES; do not hide the GUI phone window.
                        startup.StandardInput = input;
                        startup.StandardOutput = outputWrite;
                        startup.StandardError = errorWrite;
                        if (!Native.CreateProcess(executable, new StringBuilder(BuildCommandLine(executable, arguments)),
                            IntPtr.Zero, IntPtr.Zero, true, 0x08000404, environmentBlock, directory, ref startup, out information))
                            throw Win32Failure("Starting " + executable);
                        child.processHandle = information.Process;
                        child.ProcessId = information.ProcessId;
                        if (!Native.AssignProcessToJobObject(child.jobHandle, child.processHandle))
                            throw Win32Failure("Assigning " + Path.GetFileName(executable) + " to its isolated process job");
                        child.outputReader = new StreamReader(new FileStream(new SafeFileHandle(outputRead, true), FileAccess.Read), Encoding.UTF8);
                        outputRead = IntPtr.Zero;
                        child.errorReader = new StreamReader(new FileStream(new SafeFileHandle(errorRead, true), FileAccess.Read), Encoding.UTF8);
                        errorRead = IntPtr.Zero;
                        if (Native.ResumeThread(information.Thread) == UInt32.MaxValue)
                            throw Win32Failure("Resuming " + Path.GetFileName(executable));
                        // Anonymous pipe reads are deliberately drained on dedicated background tasks;
                        // FileStream's synchronous Windows pipe handle must never block the WPF thread.
                        child.outputDrain = Task.Run(delegate { child.Drain(child.outputReader, false); });
                        child.errorDrain = Task.Run(delegate { child.Drain(child.errorReader, true); });
                        return child;
                    }
                    catch
                    {
                        if (child.processHandle != IntPtr.Zero) Native.TerminateProcess(child.processHandle, 1);
                        child.Dispose();
                        throw;
                    }
                    finally
                    {
                        CloseHandle(ref outputRead);
                        CloseHandle(ref outputWrite);
                        CloseHandle(ref errorRead);
                        CloseHandle(ref errorWrite);
                        if (input != new IntPtr(-1)) CloseHandle(ref input);
                        CloseHandle(ref information.Thread);
                        if (environmentBlock != IntPtr.Zero) Marshal.FreeHGlobal(environmentBlock);
                    }
                }
            }

            private void Drain(StreamReader reader, bool error)
            {
                try
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        string logFailure = null;
                        lock (outputLock)
                        {
                            StringBuilder buffer = error ? stderr : stdout;
                            buffer.AppendLine(line.Length <= 16384 ? line : line.Substring(line.Length - 16384));
                            if (buffer.Length > 32768) buffer.Remove(0, buffer.Length - 32768);
                            if (logWriter != null)
                            {
                                try
                                {
                                    logWriter.WriteLine(DateTime.Now.ToString("O", CultureInfo.InvariantCulture) +
                                        (error ? " [stderr] " : " [stdout] ") + line);
                                }
                                catch (IOException ex)
                                {
                                    logFailure = "Cannot continue writing the process log: " + ex.Message;
                                    try { logWriter.Dispose(); } catch (IOException) { }
                                    logWriter = null;
                                }
                            }
                        }
                        if (notify != null)
                        {
                            notify(line.Length <= 2000 ? line : line.Substring(0, 2000) + " [truncated in UI]");
                            if (logFailure != null) notify(logFailure);
                        }
                    }
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }

            internal async Task<bool> WaitAsync(int milliseconds, CancellationToken token)
            {
                Stopwatch timer = Stopwatch.StartNew();
                while (!HasExited)
                {
                    token.ThrowIfCancellationRequested();
                    int remaining = milliseconds - (int)timer.ElapsedMilliseconds;
                    if (remaining <= 0) return false;
                    await Task.Delay(Math.Min(100, remaining), token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                return true;
            }

            internal async Task DrainAsync()
            {
                // A malformed tool that leaves a pipe open in a descendant cannot hang its caller.
                Task drains = Task.WhenAll(outputDrain, errorDrain);
                await Task.WhenAny(drains, Task.Delay(2000)).ConfigureAwait(false);
                if (drains.IsCompleted) await drains.ConfigureAwait(false);
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref closed, 1) != 0) return;
                CloseHandle(ref jobHandle);
                CloseHandle(ref processHandle);
                if (outputReader != null) outputReader.Dispose();
                if (errorReader != null) errorReader.Dispose();
                lock (outputLock)
                {
                    if (logWriter != null)
                    {
                        try { logWriter.Dispose(); } catch (IOException) { }
                        logWriter = null;
                    }
                }
            }

            private static void CreateOutputPipe(ref Native.SecurityAttributes attributes, out IntPtr read, out IntPtr write)
            {
                if (!Native.CreatePipe(out read, out write, ref attributes, 0)) throw Win32Failure("Creating process output pipe");
                if (!Native.SetHandleInformation(read, 1, 0)) throw Win32Failure("Protecting process output pipe");
            }

            private static string BuildCommandLine(string executable, string[] arguments)
            {
                StringBuilder command = new StringBuilder(QuoteArgument(executable));
                foreach (string argument in arguments) command.Append(' ').Append(QuoteArgument(argument));
                return command.ToString();
            }

            private static string QuoteArgument(string value)
            {
                // CommandLineToArgvW / Microsoft CRT escaping, including trailing backslashes.
                StringBuilder quoted = new StringBuilder("\"");
                int slashes = 0;
                foreach (char character in value)
                {
                    if (character == '\\') { slashes++; continue; }
                    if (character == '"') quoted.Append('\\', slashes * 2 + 1).Append('"');
                    else quoted.Append('\\', slashes).Append(character);
                    slashes = 0;
                }
                return quoted.Append('\\', slashes * 2).Append('"').ToString();
            }

            private static Win32Exception Win32Failure(string action)
            {
                int code = Marshal.GetLastWin32Error();
                return new Win32Exception(code, action + " failed: " + new Win32Exception(code).Message);
            }

            private static void CloseHandle(ref IntPtr handle)
            {
                IntPtr current = handle;
                handle = IntPtr.Zero;
                if (current != IntPtr.Zero && current != new IntPtr(-1)) Native.CloseHandle(current);
            }
        }

        private static class Native
        {
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            internal struct VersionInformation
            {
                internal uint Size, Major, Minor, Build, Platform;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string ServicePack;
                internal ushort ServicePackMajor, ServicePackMinor, SuiteMask;
                internal byte ProductType, Reserved;
            }
            [StructLayout(LayoutKind.Sequential)]
            internal struct SystemInformation
            {
                internal ushort Architecture, Reserved;
                internal uint PageSize;
                internal IntPtr MinimumAddress, MaximumAddress, ActiveProcessorMask;
                internal uint ProcessorCount, ProcessorType, AllocationGranularity;
                internal ushort ProcessorLevel, ProcessorRevision;
            }
            [StructLayout(LayoutKind.Sequential)]
            internal struct MemoryStatus
            {
                internal uint Length, Load;
                internal ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
                    TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
            }
            [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
            internal static extern int RtlGetVersion(ref VersionInformation information);
            [DllImport("kernel32.dll")]
            internal static extern void GetNativeSystemInfo(out SystemInformation information);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
            [DllImport("kernel32.dll")]
            internal static extern IntPtr GetCurrentProcess();
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

            internal delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
            [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
            [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
            [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
            [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
            [StructLayout(LayoutKind.Sequential)]
            internal struct SecurityAttributes
            {
                internal int Length;
                internal IntPtr SecurityDescriptor;
                internal int InheritHandle;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            internal struct StartupInformation
            {
                internal int Size;
                internal string Reserved;
                internal string Desktop;
                internal string Title;
                internal uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
                internal ushort ShowWindow, ReservedSize;
                internal IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct ProcessInformation
            {
                internal IntPtr Process, Thread;
                internal uint ProcessId, ThreadId;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct JobBasicLimitInformation
            {
                internal long PerProcessUserTimeLimit, PerJobUserTimeLimit;
                internal uint LimitFlags;
                internal UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
                internal uint ActiveProcessLimit;
                internal UIntPtr Affinity;
                internal uint PriorityClass, SchedulingClass;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct IoCounters
            {
                internal ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
                internal ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct JobExtendedLimitInformation
            {
                internal JobBasicLimitInformation BasicLimitInformation;
                internal IoCounters IoInfo;
                internal UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
            }

            [DllImport("WinHvPlatform.dll")]
            internal static extern int WHvGetCapability(int capabilityCode, out int capabilityBuffer, uint bufferSize, out uint writtenSize);
            [DllImport("iphlpapi.dll", SetLastError = true)]
            internal static extern uint GetExtendedTcpTable(IntPtr table, ref int size,
                [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern IntPtr CreateJobObject(IntPtr attributes, string name);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetInformationJobObject(IntPtr job, int informationClass,
                ref JobExtendedLimitInformation information, uint informationLength);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes,
                IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment,
                string currentDirectory, ref StartupInformation startup, out ProcessInformation information);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SecurityAttributes attributes, uint size);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern IntPtr CreateFile(string filename, uint access, uint share, ref SecurityAttributes attributes,
                uint creation, uint flags, IntPtr template);
            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern uint ResumeThread(IntPtr thread);
            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetExitCodeProcess(IntPtr process, out uint code);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool TerminateProcess(IntPtr process, uint code);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CloseHandle(IntPtr handle);
            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool belongs);
        }
    }
}
