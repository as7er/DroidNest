[CmdletBinding()]
param([switch]$Launch, [switch]$Json)

$ErrorActionPreference = 'Stop'
$appRoot = $PSScriptRoot
$stateRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'DroidNest'
$checks = New-Object 'System.Collections.Generic.List[object]'
function Add-Check([string]$Name, [string]$Status, [string]$Message) {
    $checks.Add([pscustomobject]@{ name = $Name; status = $Status; message = $Message })
}

# UTF-8 BOM is intentional: Windows PowerShell 5.1 otherwise misreads Chinese text.
try {
    if (-not ('DroidNestPreflight.Native' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace DroidNestPreflight {
    public static class Native {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct VersionInfo {
            public uint Size, Major, Minor, Build, Platform;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ServicePack;
            public ushort ServicePackMajor, ServicePackMinor, SuiteMask;
            public byte ProductType, Reserved;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct SystemInfo {
            public ushort Architecture, Reserved;
            public uint PageSize;
            public IntPtr MinimumAddress, MaximumAddress, ActiveProcessorMask;
            public uint ProcessorCount, ProcessorType, AllocationGranularity;
            public ushort ProcessorLevel, ProcessorRevision;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct MemoryStatus {
            public uint Length, Load;
            public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
                TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
        }
        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        public static extern int RtlGetVersion(ref VersionInfo info);
        [DllImport("kernel32.dll")] public static extern void GetNativeSystemInfo(out SystemInfo info);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx(ref MemoryStatus info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);
        [DllImport("WinHvPlatform.dll")]
        public static extern int WHvGetCapability(int code, out int value, uint size, out uint written);
        public static ushort NativeMachine() {
            ushort processMachine, nativeMachine;
            try {
                if (!IsWow64Process2(GetCurrentProcess(), out processMachine, out nativeMachine))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                return nativeMachine;
            } catch (EntryPointNotFoundException) {
                SystemInfo info; GetNativeSystemInfo(out info);
                return info.Architecture == 9 ? (ushort)0x8664 : (ushort)0;
            }
        }
    }
}
'@
    }
    $version = New-Object DroidNestPreflight.Native+VersionInfo
    $version.Size = [Runtime.InteropServices.Marshal]::SizeOf($version)
    if ([DroidNestPreflight.Native]::RtlGetVersion([ref]$version) -ne 0) { throw '无法读取 Windows 版本。' }
    if ($version.Major -eq 10 -and $version.Build -ge 10240 -and $version.ProductType -eq 1) {
        Add-Check 'windows' 'pass' "Windows 10/11，内部版本 $($version.Build)。"
    } else { Add-Check 'windows' 'fail' '需要 Windows 10/11 桌面系统；不支持旧版 Windows 或 Windows Server。' }
    if ([DroidNestPreflight.Native]::NativeMachine() -eq 0x8664) {
        Add-Check 'architecture' 'pass' '主机原生架构为 AMD64（Intel/AMD x64）。'
    } else { Add-Check 'architecture' 'fail' '需要 Intel/AMD x64 主机；不支持 ARM/ARM64，即使可以模拟运行 x64 程序。' }
} catch { Add-Check 'host' 'fail' ('无法确认主机系统和架构：' + $_.Exception.Message) }

# The launcher needs only host/CLR and application files; installation is owned by the UI.
try {
    $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $key = $registry.OpenSubKey('SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full')
        $release = 0
        if ($null -ne $key) { try { $release = [int]$key.GetValue('Release', 0) } finally { $key.Dispose() } }
    } finally { $registry.Dispose() }
    if ($release -ge 528040) { Add-Check 'dotnet' 'pass' ".NET Framework 4.8 或更高版本（Release=$release）。" }
    else { Add-Check 'dotnet' 'fail' '缺少 .NET Framework 4.8。请从 Microsoft 安装 .NET Framework 4.8，重启后再次运行此检查。' }
} catch { Add-Check 'dotnet' 'fail' ('无法读取 .NET Framework Release 注册表值：' + $_.Exception.Message) }

foreach ($relative in @('DroidNest.exe', 'DroidNest.exe.config', 'tools\SignWebView.java')) {
    try {
        $file = Get-Item -LiteralPath (Join-Path $appRoot $relative) -ErrorAction Stop
        if ($file.PSIsContainer -or $file.Length -eq 0) { throw '不是有效的非空文件。' }
        Add-Check ('app:' + $relative) 'pass' ('客户端文件存在：' + $relative)
    } catch { Add-Check ('app:' + $relative) 'fail' ('客户端文件缺失或为空：' + $relative + '。请重新完整解压 v0.2.0 客户端 ZIP，不要只下载 EXE。') }
}

if (-not $Launch) {
Add-Check 'state-root' 'pass' ('运行时、手机数据和日志的持久目录：' + $stateRoot)
$probeDirectories = @('')
# Never create phone directories before consent-driven legacy migration.
if (Test-Path -LiteralPath (Join-Path $stateRoot 'runtime\installation.json') -PathType Leaf) {
    foreach ($relative in @('data', 'data\android-home', 'data\avd', 'data\avd\DroidNest.avd', 'logs')) {
        if (Test-Path -LiteralPath (Join-Path $stateRoot $relative) -PathType Container) { $probeDirectories += $relative }
    }
}
foreach ($relative in $probeDirectories) {
    $directory = $stateRoot
    if ($relative) { $directory = Join-Path $stateRoot $relative }
    try {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        $probe = Join-Path $directory ('.droidnest-write-' + [Guid]::NewGuid().ToString('N') + '.tmp')
        $stream = New-Object IO.FileStream($probe, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None, 1, [IO.FileOptions]::DeleteOnClose)
        try { $stream.WriteByte(0) } finally { $stream.Dispose() }
        Add-Check ('write:' + $relative) 'pass' ('可写入：' + $directory)
    } catch { Add-Check ('write:' + $relative) 'fail' ('持久目录不可写，请检查当前用户权限，不要通过自动提权解决：' + $directory + '；' + $_.Exception.Message) }
    try {
        [uint64]$available = 0; [uint64]$total = 0; [uint64]$free = 0
        if (-not [DroidNestPreflight.Native]::GetDiskFreeSpaceEx($directory, [ref]$available, [ref]$total, [ref]$free)) { throw (New-Object ComponentModel.Win32Exception) }
        $space = '{0:N1}' -f ($available / 1GB)
        if ($available -ge 8GB) { Add-Check ('disk:' + $relative) 'pass' "可用磁盘空间 $space GiB：$directory" }
        else { Add-Check ('disk:' + $relative) 'fail' "启动前至少需要 8 GiB 可用空间（当前 $space GiB）：$directory。请释放空间，勿删除需要保留的手机数据。" }
    } catch { Add-Check ('disk:' + $relative) 'fail' ('无法确认至少 8 GiB 可用空间：' + $directory + '；' + $_.Exception.Message) }
}
try {
    $memory = New-Object DroidNestPreflight.Native+MemoryStatus
    $memory.Length = [Runtime.InteropServices.Marshal]::SizeOf($memory)
    if (-not [DroidNestPreflight.Native]::GlobalMemoryStatusEx([ref]$memory)) { throw (New-Object ComponentModel.Win32Exception) }
    $ram = '{0:N1}' -f ($memory.TotalPhysical / 1GB)
    if ($memory.TotalPhysical -lt 8GB) { Add-Check 'memory' 'warn' "主机内存 $ram GiB，建议至少 8 GiB；内存偏低可能影响运行，但不阻止启动。" }
    else { Add-Check 'memory' 'pass' "主机物理内存 $ram GiB。" }
} catch { Add-Check 'memory' 'warn' ('无法读取主机内存，不阻止启动：' + $_.Exception.Message) }

$required = @(
    'runtime\sdk\emulator\emulator.exe',
    'runtime\sdk\emulator\qemu\windows-x86_64\qemu-system-x86_64.exe',
    'runtime\sdk\platform-tools\adb.exe', 'runtime\sdk\platform-tools\AdbWinApi.dll', 'runtime\sdk\platform-tools\AdbWinUsbApi.dll',
    'runtime\webview-local.apk'
)
foreach ($file in @('system.img', 'userdata.img', 'ramdisk.img', 'kernel-ranchu', 'vendor.img', 'encryptionkey.img', 'source.properties', 'advancedFeatures.ini')) {
    $required += 'runtime\sdk\system-images\android-30\default\x86_64\' + $file
}
foreach ($file in @('scrcpy.exe', 'scrcpy-server', 'SDL3.dll', 'avcodec-63.dll', 'avformat-63.dll', 'avutil-61.dll', 'swresample-7.dll', 'libusb-1.0.dll', 'AdbWinApi.dll', 'AdbWinUsbApi.dll')) {
    $required += 'runtime\scrcpy-win64-v5.0.1\' + $file
}
foreach ($relative in $required) {
    try {
        $file = Get-Item -LiteralPath (Join-Path $stateRoot $relative) -ErrorAction Stop
        if ($file.PSIsContainer -or $file.Length -eq 0) { throw '不是有效的非空文件。' }
        Add-Check ('file:' + $relative) 'pass' ('文件存在：' + $relative)
    } catch { Add-Check ('file:' + $relative) 'fail' ('缺失或为空：' + $relative + '。双击 Start-DroidNest.cmd 进入图形安装向导，阅读来源与条款并明确同意后自动下载；不需要手动安装 SDK/JDK。') }
}

try {
    [int]$present = 0; [uint32]$written = 0
    $hr = [DroidNestPreflight.Native]::WHvGetCapability(0, [ref]$present, 4, [ref]$written)
    if ($hr -lt 0 -or $present -eq 0) { throw ('WHPX hypervisor unavailable, HRESULT=0x{0:X8}' -f $hr) }
    Add-Check 'whpx' 'pass' 'Windows Hypervisor Platform 检测到活动的虚拟机监控程序。'
} catch { Add-Check 'whpx' 'fail' ('WHPX 不可用。请在固件中启用 Intel VT-x/AMD-V，在“启用或关闭 Windows 功能”中启用“Windows 虚拟机监控程序平台”，重启 Windows，并确保未禁用 hypervisorlaunchtype。本工具不会自动提权或修改系统功能。详情：' + $_.Exception.Message) }

$emulator = Join-Path $stateRoot 'runtime\sdk\emulator\emulator.exe'
if (Test-Path -LiteralPath $emulator -PathType Leaf) {
    $process = $null
    try {
        $info = New-Object Diagnostics.ProcessStartInfo
        $info.FileName = $emulator; $info.Arguments = '-accel-check'; $info.WorkingDirectory = $stateRoot
        $info.UseShellExecute = $false; $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
        $info.EnvironmentVariables['ANDROID_SDK_ROOT'] = Join-Path $stateRoot 'runtime\sdk'
        $info.EnvironmentVariables['ANDROID_HOME'] = Join-Path $stateRoot 'runtime\sdk'
        $info.EnvironmentVariables['ANDROID_USER_HOME'] = Join-Path $stateRoot 'data\android-home'
        $info.EnvironmentVariables['ANDROID_AVD_HOME'] = Join-Path $stateRoot 'data\avd'
        $process = [Diagnostics.Process]::Start($info)
        # Drain both pipes concurrently; neither exit nor inherited pipes may wait forever.
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(20000)) { throw 'emulator -accel-check 超时（20 秒）。' }
        $drained = [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 2000)
        if (-not $drained) { throw 'emulator 输出管道未在 2 秒内关闭。' }
        $details = ($stdout.Result + [Environment]::NewLine + $stderr.Result).Trim()
        if ($process.ExitCode -ne 0 -or $details -notmatch '(?i)WHPX') { throw ("退出码 $($process.ExitCode)：" + $details) }
        Add-Check 'acceleration' 'pass' ('模拟器 WHPX 加速检查通过：' + $details)
    } catch { Add-Check 'acceleration' 'fail' ('模拟器 WHPX 加速检查失败。请检查本地 SDK 完整性、固件虚拟化和 Windows 虚拟机监控程序平台，重启后重试：' + $_.Exception.Message) }
    finally {
        if ($null -ne $process) {
            try { if (-not $process.HasExited) { $process.Kill(); [void]$process.WaitForExit(2000) } } catch { }
            $process.Dispose()
        }
    }
} else { Add-Check 'acceleration' 'fail' '无法运行加速检查：运行时尚未安装。双击 Start-DroidNest.cmd 进入安装向导。' }

foreach ($port in @(5048, 5580, 5581)) {
    $listener = $null
    try {
        $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Any, $port)
        $listener.Server.ExclusiveAddressUse = $true
        $listener.Start()
        Add-Check ('port:' + $port) 'pass' "TCP 端口 $port 可用。"
    } catch { Add-Check ('port:' + $port) 'fail' "TCP 端口 $port 已占用或被 Windows 保留。请先关闭已有 DroidNest，或自行确认占用进程并释放端口；本工具不会停止其他进程。详情：$($_.Exception.Message)" }
    finally { if ($null -ne $listener) { $listener.Stop() } }
}
}

$ready = @($checks | Where-Object { $_.status -eq 'fail' }).Count -eq 0
$launched = $false
if ($Launch -and $ready) {
    try {
        $info = New-Object Diagnostics.ProcessStartInfo
        $info.FileName = Join-Path $PSScriptRoot 'DroidNest.exe'
        $info.WorkingDirectory = $PSScriptRoot; $info.UseShellExecute = $false
        $app = [Diagnostics.Process]::Start($info)
        $app.Dispose(); $launched = $true
        Add-Check 'launch' 'pass' '已启动 DroidNest；缺少运行时会进入安装向导，安装前需要明确同意。'
    } catch { Add-Check 'launch' 'fail' ('无法启动 DroidNest：' + $_.Exception.Message); $ready = $false }
}
if ($Json) {
    [pscustomobject]@{ ready = $ready; launched = $launched; mode = $(if ($Launch) { 'launch' } else { 'diagnostic' }); root = $stateRoot; appRoot = $appRoot; checks = $checks.ToArray() } | ConvertTo-Json -Depth 5
} else {
    foreach ($check in $checks) {
        $label = switch ($check.status) { 'pass' { '通过' } 'warn' { '警告' } 'fail' { '失败' } }
        Write-Output ('[{0}] {1}' -f $label, $check.message)
    }
    if ($ready) { Write-Output $(if ($Launch) { '客户端已启动。' } else { '环境诊断通过。' }) }
    elseif ($Launch) { Write-Output '客户端启动条件未满足；请解决以上失败项目。缺少 Android 运行时不会阻止打开安装向导。' }
    else { Write-Output '环境诊断未通过（诊断模式不会启动或下载）。缺少组件时请双击 Start-DroidNest.cmd 进入安装向导；虚拟化等宿主条件仍需自行启用。' }
}
if ($ready) { exit 0 } else { exit 1 }
