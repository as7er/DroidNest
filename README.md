# DroidNest / 安卓小窝

运行在 Windows 上的无广告 Android 桌面客户端。手机画面和控制面板在同一个窗口中，支持鼠标触摸、键盘输入、APK 安装、横竖屏与全屏。

**v0.3.0：下载客户端 ZIP，首次打开后由图形向导在线安装运行时，无需自己准备 SDK、JDK 或运行组件准备脚本。** 公开 ZIP 仍不捆绑第三方运行时，也不是离线完整模拟器；单独下载 EXE 不足以完成首次安装。

## 下载与首次启动

仓库：<https://github.com/as7er/DroidNest>

1. 下载 `DroidNest-v0.3.0-win-x64-client.zip`，核对发布页的 SHA-256，完整解压（不要在 ZIP 内直接运行）。保留 `tools/SignWebView.java` 等随包文件。
2. 双击 `Start-DroidNest.cmd`。启动器检查 Windows/x64、.NET 和客户端文件；**不会因为尚未安装 Android 运行时而拒绝打开向导**。
3. 向导先检查安装所需的宿主条件。WHPX 未启用时会说明原因并提供打开“Windows 功能”的按钮；先按提示启用并重启，条件满足后才读取在线元数据，不会先下载组件。随后显示组件、版本、来源、实际下载大小、条款/许可链接及公开测试密钥风险。阅读后明确勾选同意，才开始下载、解压及本地准备；取消或不接受不会开始组件安装。
4. 保持网络连接并观察进度。向导自动获取 Android Emulator、Platform Tools、Android 11 / API 30 `default;x86_64` 镜像、scrcpy 和 WebView，以及仅供本地准备使用的私有 JDK 与 Android Build Tools；无需修改系统 Java/SDK 配置。下载失败时按界面提示重试；关闭后可重新打开客户端继续安装流程。不要为了重试删除手机数据目录。
5. 安装完成后进入主界面并自动启动已准备的手机；以后打开客户端默认也会自动启动手机，可在右侧关闭“打开客户端时自动启动手机”。首次冷启动需要等待 Android 就绪，手动停止后可点击“启动手机”重新启动。

客户端不会静默代替用户接受组件条款，不会自动提权、关闭安全防护或更改虚拟化功能。网络代理、企业防火墙或上游不可用可能导致元数据/下载失败；保留错误信息排查，不要绕过 HTTPS 证书校验。

### 宿主要求

- Windows 10/11，Intel/AMD x64；不支持 Windows ARM。
- [.NET Framework 4.8](https://dotnet.microsoft.com/download/dotnet-framework/net48)。缺失时启动器会报告，EXE 自身无法在缺失 CLR 时显示向导。
- BIOS/UEFI 开启 Intel VT-x/AMD-V；Windows 功能开启 **Windows Hypervisor Platform / Windows 虚拟机监控程序平台（WHPX）**，更改后重启。向导安装组件不能替代这些宿主设置。
- 建议至少 8 GB 主机内存；手机使用 2 个虚拟 CPU、2 GB 内存。同时运行其他模拟器可能明显变慢。
- 启动手机统一要求至少 8 GiB 当前用户可用空间（考虑磁盘配额）。安装时逐个下载前另计完整归档空间；全部下载校验后，按 ZIP 实际展开字节数、每项 64 KiB 分配余量、原始及签名 WebView、8 MiB 安装记录余量和 8 GiB 手机预留重新检查，空间不足不开始解压。旧运行时与缓存保持占用，不假设可回收，也不将重命名备份重复计为一份副本。
- 本地端口 5048、5580、5581 必须可用；DroidNest 不抢占端口、不结束其他模拟器。

## 数据位置、更新与旧版迁移

程序目录只放客户端、配置及随包辅助文件。运行状态保存在当前 Windows 用户的固定目录：

```text
%LOCALAPPDATA%\DroidNest\
  runtime\   已安装的 Android / scrcpy / 本地 WebView
  data\      手机虚拟磁盘、应用和 Android 用户配置
  logs\      运行诊断记录
```

**更新客户端不需要重新安装运行时，也不应覆盖或删除 `data`。** 正常关闭 DroidNest 后，把新版客户端 ZIP 解压到新目录并启动即可；同一 Windows 用户继续使用上述持久目录。删除旧客户端目录之前，确认旧版数据已经迁移并另做备份。不要同时运行两份客户端。

旧版将 `runtime`、`data` 放在 EXE 旁边。先关闭旧版及其手机进程，把新版客户端文件放入保留旧目录的解压位置，再由新版启动时的迁移提示明确同意后复制；不要把运行中的虚拟磁盘手工搬走。迁移只复制持久目录中尚不存在的对应文件夹，保留旧目录原件，不覆盖已有持久运行时或手机数据。若目标已经存在，不会自动合并两部手机；先备份并确认需保留的数据。完整的旧版运行时可以直接复用；缺少必要文件时会进入安装向导修复，不删除已迁移的手机数据。

新安装记录采用 format 2：保留完整文件大小清单，并校验必要可执行文件、依赖库、系统镜像和 WebView 的 SHA-256，可发现同大小文件损坏。原有 format 1 记录不自动就地生成“可信”摘要，升级后进入向导重新准备运行时，旧运行时保留为备份，手机 `data` 不变。请为此预留下载时间与空间。明确导入的无记录旧运行时仍沿用依赖存在性检查，**不具有新记录的完整性保证**；安装记录本身没有签名，无法防御能同时改写文件与记录的本地攻击者。

关闭窗口时先刷新 Android 存储，再停止本实例 Android 和视频进程，不清除手机应用数据。备份或迁移之前必须正常停止手机；备份 `data` 可能包含账号、凭据及个人信息。

### 下载续传、日志与手机配置

- 组件下载中断或失败后重试时，从 `.partial` 已写入的字节继续（HTTP Range）；服务器不支持或返回的范围不符时自动改为完整下载。每次成功仍按固定大小与 SHA-256 校验，校验失败会丢弃并重下，不会执行未通过校验的文件。
- `logs` 目录只保留最近 16 个日志文件、合计最多 64 MiB；每次启动手机前清理更旧的日志，最近 10 分钟内仍在写入的文件不会被删除。可直接删除整个 `logs` 目录，不影响手机数据。
- 手机进程异常退出时，状态区会显示退出的是模拟器、私有 ADB 服务还是画面进程以及模拟器日志路径；修复或重试后点击“启动手机”即可，数据保留。
- 自定义手机配置写在 `data\avd\DroidNest.avd\config.ini`。修改前先停止手机，并保留下列可调项：`hw.ramSize`（512–8192 MB）、`hw.cpu.ncore`（1–8）、`hw.lcd.width` / `hw.lcd.height`（320–4096）、`hw.lcd.density`（120–640）、`vm.heapSize`（16–1024 MB）、`disk.dataPartition.size`（512M–64G）、`hw.gpu.mode`（auto、host、swiftshader、swiftshader_indirect、angle、angle_indirect）。下次启动保留这些值，非法或缺失值回退默认；`image.sysdir.1`、`AvdId`、`target` 等结构键仍由客户端写入。

## 操作

- 点击手机画面模拟触摸，鼠标拖动滑动页面，键盘输入文字。
- 右侧按钮：主页、返回、最近任务、旋转、系统设置、安装 APK。
- 安装 APK 前会显示包名、版本和签名证书 SHA-256；只有确认后才安装。缺 Build Tools/JDK 或签名格式不受支持时会明确提示，由你决定是否继续。
- APK 拖放使用右侧面板；不支持把 XAPK/APKS 压缩包当成单个 APK 安装。
- “备份手机数据…”在手机停止后可生成含 `data` 的 ZIP 和同目录 SHA-256 文件；“恢复手机数据…”先校验同名校验文件，只解压到空的 `data`，不覆盖、不合并现有手机数据。
- “打开客户端时自动启动手机”可关闭；设置保存在客户端的 `settings.json`，与手机数据分开。
- F11 切换整个窗口全屏，Esc 退出全屏，手机获得焦点时也有效。
- 窗口缩放和旋转保持画面比例，黑边不是触摸区域。
- 手机画面进程异常退出时自动重连并继续运行 Android；模拟器或私有 ADB 退出时状态区显示原因和日志路径，可点击“启动手机”重启。
- 在 Android 应用抽屉打开应用，在 Android 设置中卸载。
- 默认不自动同步 Windows 与 Android 剪贴板。视频限制 30 FPS / 4 Mbps，声音由 Android 引擎输出，不重复转发。

## 诊断（可选，不是安装步骤）

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Check-Environment.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\Check-Environment.ps1 -Json
```

默认模式诊断完整运行条件，包括持久目录权限/空间、安装文件、WHPX、内存与端口；不会下载或启动客户端，缺失组件返回非零退出码。JSON 同时报告 `root`（持久目录）和 `appRoot`（客户端目录）。诊断可能创建持久根目录及即时删除的写入探针，不预先创建手机数据目录。`-Launch` 是启动器模式，仅检查打开客户端所需条件，缺少运行时由图形向导处理，不重复实现 PowerShell 安装器。

## 开发打包与个人迁移

以下命令只用于开发/个人备份，不是新用户安装要求。先构建客户端，再生成公开 ZIP：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\New-PortablePackage.ps1 -Version 0.3.0
```

`dist/` 产物采用客户端文件白名单，包含 EXE、配置、启动与诊断脚本、`tools/SignWebView.java`、`Sign-Release.ps1`、许可和说明；**不包含运行时、私有准备工具、下载缓存、手机数据、日志或密钥**。附带 SHA-256 校验文件，拒绝覆盖同名产物。旧版 `Setup-Components.ps1` 和 `Import-LocalRuntime.ps1` 已移除，安装由客户端完成。

主界面“修复运行环境…”会在保留手机数据的前提下重新准备运行时：旧运行时移动到 `runtime.previous-<id>`，不动 `data`。启动时若发现安装记录格式过旧、模拟器/Platform Tools 版本与客户端固定版本不一致，或缺少 Build Tools，会显示修复提示；修复后需重新打开客户端。

非商业发布采用**未签名 EXE + SHA-256 校验文件**，不要求购买代码签名证书；Windows 可能提示“未知发布者”，请核对校验值并自行判断。如果以后希望 Windows 显示可信发布者，可选做 Authenticode 签名：2023 年 6 月后签发的 OV/EV 代码签名证书通常把私钥放在硬件令牌或 HSM 中，不能导出 PFX；这种情况下用证书存储模式，私钥不会离开令牌：

```powershell
# 可导出的 PFX；省略 -PfxPassword 时会交互式输入，不写入命令历史
.\Sign-Release.ps1 -PfxPath D:\secure\DroidNest.pfx

# 已通过厂商工具导入的证书（硬件令牌 / HSM）
.\Sign-Release.ps1 -CertificateThumbprint <40位SHA1指纹> -StoreLocation CurrentUser
```

`Sign-Release.ps1` 只会使用你提供的真实证书，缺少证书或签名验证失败时直接报错，不会生成自签名证书冒充发布签名。云签名服务（如 Azure Trusted Signing）需要按其 `dlib` 流程另行调用，不能直接套用本脚本。签名后仍需人工核对证书主题与时间戳；SHA-256 校验文件只证明完整性，不等于发布者签名。

迁移自己的已安装环境：正常关闭全部 DroidNest，再运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\New-PortablePackage.ps1 -Mode Local
# 仅明确需要迁移当前手机数据时：
powershell -NoProfile -ExecutionPolicy Bypass -File .\New-PortablePackage.ps1 -Mode Local -IncludeData
```

Local 模式从 `%LOCALAPPDATA%\DroidNest` 读取已安装运行时，若有安装记录则一并保留；完整旧运行时不强制要求记录。默认不含手机数据，也不包含私有 JDK、构建工具、下载缓存或签名密钥。目标电脑完整解压后启动，由迁移提示导入持久目录；目标已有数据时不要覆盖。`LOCAL-ONLY` 包只用于个人迁移，**不是第三方再分发许可，不得上传 GitHub Release**。`-IncludeData` 可能携带账号凭据和个人信息，必须保密；换电脑仍需满足宿主条件。

## 构建源码

自有代码使用 C# 5 / WPF / .NET Framework 4.8，不依赖 NuGet。`build.ps1` 使用 Windows 自带 Framework 编译器。也可用 Visual Studio 打开 `DroidNest.csproj` 并安装 .NET Framework 4.8 开发工具。

不要把运行时、数据、日志、下载缓存、验证截图、打包产物或密钥材料强制添加到版本控制。

仓库中的 `runtime-origin.json` 仅保留早期开发环境来源记录，不作为当前版本安装依据，也不进入公开客户端 ZIP。在线安装以官方元数据为准：API 30 镜像选择官方 revision 11；旧本机镜像的 package.xml revision 11 与 source.properties revision 10 不一致，不能据此推断在线安装版本。

## 安全边界与验证状态

Android 镜像使用**公开 AOSP 测试证书**。向导使用匹配的公开测试密钥在本机重签名 WebView，保留 Android 签名校验，不修改业务 APK。测试密钥不是秘密，不能提供生产签名的信任边界；此镜像不适合敏感生产数据。更新 WebView 不等于更新 Android 内核或系统安全补丁。

下载校验信任边界：Platform Tools 37.0.1 已固定独立发布的 SHA-256（[scrcpy v5.0.1 的上游记录](https://github.com/Genymobile/scrcpy/blob/v5.0.1/app/deps/adb_windows.sh)），在线元数据不能覆盖该值。Emulator 37.2.13、Build Tools 35.0.0 和 API 30 revision 11 镜像目前仍使用 Google HTTPS 元数据中的 SHA-1，**尚未完成这三个组件的可信 SHA-256 固定**；不把同一次下载自行计算的摘要当作信任锚。其余既有 SHA-256 固定保持不变。

v0.2.0 已在本机 Windows 10 x64 上完成独立客户端目录的在线组件准备，确认 Android 桌面持续显示、WebView 156 生效，以及系统设置、横竖屏、全屏和最小化恢复。另已验证写入 Android 文件后立即正常关闭，再从含中文和空格的新客户端目录启动，文件仍在且运行时安装记录不变。迁移原件保留、目标数据不覆盖、取消与解压路径边界检查通过。详见 [验证记录](VALIDATION.md)。

v0.3.0 另外验证了：下载断点续传（真实 206 响应）、日志轮换、AVD 调优值保留并生效、终止 scrcpy 后自动重连且 Android 不重启、真实 APK 安装前读取包名/版本/证书、615.5 MiB 手机数据备份恢复后再次启动、7 步远程 Windows CI。仍未完成干净 Windows 环境、全新 format 2 安装后的端到端验收和不同硬件覆盖。

**这些结果来自现有开发电脑，不等于干净 Windows 机器验收；独立 Windows 用户/虚拟机首次安装和不同硬件仍待验证。** 旧版的 APK 安装、FMMV 主菜单和球队选择、鼠标/键盘测试属于历史记录，不保证所有应用兼容或完整游戏流程。

不含 Google Play 服务，不提供 ARM 原生指令翻译、游戏键位映射、多开或真实 SIM 通信。Android 11 不能安装最低版本要求高于 API 30 的应用。银行、DRM、反作弊和仅支持 ARM 的应用可能拒绝运行。客户端没有广告或推广逻辑，但不会去除第三方应用自己的广告；Android 和应用仍可能访问网络。

EXE 尚未取得 Windows Authenticode 签名，Windows 可能提示未知发布者/SmartScreen。不要关闭防护；核对官方仓库与发布校验值后自行决定。校验值帮助发现损坏，不替代可信发布者签名。尚未覆盖所有 Windows 版本和显卡。

## 许可

DroidNest 自有代码采用 [MIT](LICENSE)。第三方组件保持各自许可，详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。向导的来源、摘要与许可证提示不授予再分发权；公开 Release 不捆绑未经确认可分发的 Android 运行时。
