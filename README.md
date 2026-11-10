# DroidNest / 安卓小窝

运行在 Windows 上的无广告 Android 桌面客户端。手机画面和控制面板在同一个窗口中，支持鼠标触摸、键盘输入、APK 安装、横竖屏与全屏。

**重要：GitHub Release 是客户端，不是完整模拟器安装包。单独下载 `DroidNest.exe` 不能运行 APK。** Android 引擎、系统镜像和视频组件需要另外准备；公开 ZIP 不包含运行时、手机数据或密钥。

## 下载与启动

仓库：<https://github.com/as7er/DroidNest>

下载 Release 中的 `DroidNest-v0.1.0-win-x64-client.zip`，解压到当前用户可写的目录。优先双击 `Start-DroidNest.cmd`，它会检查依赖并给出错误原因；也可以直接打开 `DroidNest.exe`，客户端会再次检查 Android 运行条件。

- Windows 10/11，x64 Intel/AMD 处理器；不支持 Windows ARM。
- .NET Framework 4.8。尚未安装时，启动脚本会报告缺失，EXE 本身无法在缺失 CLR 时显示自己的提示。
- BIOS/UEFI 开启 Intel VT-x/AMD-V，Windows 功能开启 **Windows Hypervisor Platform / Windows 虚拟机监控程序平台（WHPX）**。更改后可能需要重启。
- 给手机分配 2 个虚拟 CPU、2 GB 内存；宿主机同时运行其他模拟器时可能明显变慢。
- 首次启动需为虚拟磁盘预留至少 8 GB 可用空间；运行时文件还会占用额外空间。
- 本地端口 5048、5580、5581 必须可用。DroidNest 不抢占端口、不结束其他模拟器、不自动开启 Windows 功能。

运行环境满足后点击“启动手机”。关闭窗口会停止本实例的 Android 和视频进程，不清除应用数据。

## 准备本机运行时

当前经过验证的配置是 Android 11 / API 30 `default;x86_64` 镜像 + Android Emulator + WHPX + scrcpy 5.0.1。

1. 从 Android 官方渠道准备 Android SDK，并自行阅读、接受适用条款。安装组件：`emulator`、`platform-tools`、`system-images;android-30;default;x86_64`。不需要安装其他商业模拟器。
2. 在 DroidNest 解压目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Import-LocalRuntime.ps1 -SdkPath "C:\path\to\Android\sdk"
```

导入脚本拒绝覆盖已有 `runtime/sdk`，不复制其他虚拟手机的数据，也不代替用户接受 SDK 许可。

3. 准备本地 JDK 21 或更高版本，以及 Android SDK Build Tools 的 `lib/apksigner.jar`，运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Setup-Components.ps1 -JavaHome "C:\path\to\jdk" -ApkSignerJar "C:\path\to\sdk\build-tools\35.0.0\lib\apksigner.jar"
```

脚本下载并校验固定版本的 scrcpy 和 LineageOS WebView。当前开发镜像内置 WebView 83，无法解析部分现代应用的 JavaScript；本地更新升级到 156.0.8078.47。启动时检查 WebView 提供程序，必要时安装已准备的本地更新，不在启动时联网下载。

**签名边界：** 当前 Android 镜像使用公开 AOSP 测试证书。WebView 使用匹配证书本地重签名，保留 Android 签名校验，不修改业务 APK。公开测试密钥不适用于生产发行或敏感数据；升级 WebView 不会升级 Android 内核和系统安全补丁。

4. 执行环境检查：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Check-Environment.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\Check-Environment.ps1 -Json
```

检查程序不要求管理员权限，不修改虚拟化设置；缺少组件会阻止启动并提示补齐方式。

## 操作

- 点击手机画面模拟触摸，鼠标拖动滑动页面，键盘输入文字。
- 右侧按钮：主页、返回、最近任务、旋转、系统设置、安装 APK。
- APK 拖放使用右侧面板；不支持把 XAPK/APKS 压缩包当成单个 APK 安装。
- F11 切换整个窗口全屏，Esc 退出全屏，手机获得焦点时也有效。
- 窗口缩放和旋转保持画面比例，黑边不是触摸区域。
- 在 Android 应用抽屉打开应用，在 Android 设置中卸载。
- 默认不自动同步 Windows 与 Android 剪贴板。视频限制 30 FPS / 4 Mbps，声音由 Android 引擎输出，不重复转发。

## 便携包与个人迁移

先构建客户端，然后整理公开客户端便携包：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\New-PortablePackage.ps1 -Version 0.1.0
```

产物位于 `dist/`，包含客户端、启动检查、组件准备脚本与说明，**不包含 `runtime/` 或 `data/`**。附带 SHA-256 校验文件。脚本拒绝覆盖已有同名产物。

迁移自己的完整运行环境：先正常关闭 DroidNest，再运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\New-PortablePackage.ps1 -Version 0.1.0 -Mode Local
# 只有明确需要迁移现有手机数据时：
powershell -NoProfile -ExecutionPolicy Bypass -File .\New-PortablePackage.ps1 -Version 0.1.0 -Mode Local -IncludeData
```

`LOCAL-ONLY` 包只用于个人迁移，不是公开分发许可。默认不含手机数据；`-IncludeData` 可能包含账号凭据、应用内容和个人信息，切勿上传 GitHub。不要复制运行中的虚拟磁盘。换电脑后仍需满足硬件虚拟化和系统依赖。

## 构建源码

自有代码使用 C# 5 / WPF / .NET Framework 4.8，不依赖 NuGet。`build.ps1` 使用 Windows 自带 Framework 编译器。也可使用 Visual Studio 打开 `DroidNest.csproj` 并安装 .NET Framework 4.8 开发工具。

`runtime/`、`data/`、`logs/`、`verification/`、`dist/` 和密钥材料都被排除在版本控制之外。不要使用强制添加把这些目录上传。

## 已验证与限制

本机已验证真实 Android 桌面、APK 安装、FMMV 主菜单和球队选择、鼠标交互、设置搜索键盘输入、横竖屏、全屏/退出、窗口缩放、冷重启后包和应用私有数据保留。不是所有应用兼容性保证，也没有完成整个游戏流程验证。

不含 Google Play 服务，不提供 ARM 原生指令翻译、游戏键位映射、多开或真实 SIM 通信。Android 11 不能安装最低版本要求高于 API 30 的应用。银行、DRM、反作弊和仅支持 ARM 的应用可能拒绝运行。客户端没有广告或推广逻辑，但不会去除第三方应用自己的广告；Android 和应用仍可能访问网络。

EXE 尚未取得 Windows Authenticode 商业签名，Windows 可能显示未知发布者/SmartScreen 提示。不要关闭系统防护；核对官方仓库与发布校验值后再自行决定是否运行。仅在本机 Windows 环境实际验证，尚未覆盖所有 Windows 版本和显卡。

## 开源与第三方许可

DroidNest 自有代码采用 [MIT](LICENSE)。第三方组件保持各自许可，详情见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。公开 Release 不捆绑未经确认可分发的 Android 运行时。`runtime-origin.json` 记录组件来源和校验摘要，不是分发授权。
