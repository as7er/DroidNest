# DroidNest v0.2.0 — Guided online installation / Windows x64

DroidNest / 安卓小窝：无广告的 Windows Android 桌面客户端，真实手机画面和控制面板同窗显示。

## 下载与开始

下载 **`DroidNest-v0.2.0-win-x64-client.zip`**，核对对应 SHA-256，完整解压后双击 `Start-DroidNest.cmd`。不要只下载 EXE，也不要在 ZIP 内运行。

ZIP 包含客户端、配置、启动/诊断与打包脚本、`tools/SignWebView.java`、许可及说明；**不含 Android SDK、系统镜像、scrcpy、WebView APK、JDK、手机数据、日志或密钥**。它是在线安装入口，不是离线完整运行时或第三方再分发授权。

## 本次变化

- 首次缺少运行时会打开图形安装向导，不再被启动脚本阻拦。向导先检查宿主条件；缺少 WHPX 时提示并可打开 Windows 功能，修复后才读取元数据。展示组件版本、来源、实际下载大小、许可和安全边界后，明确勾选同意才安装。
- 自动下载校验并准备 Android Emulator、Platform Tools、Android 11 / API 30 x86_64 镜像、scrcpy 5.0.1 与 WebView 156.0.8078.47。
- 私有 Temurin JDK 21 和 Android Build Tools 35.0.0 自动用于本地 WebView 准备，不要求手动 SDK/JDK 或组件脚本，不改系统 Java 配置。
- 安装界面提供进度、取消及失败重试；安装暂存与手机数据隔离。下载大小以元数据为准，不承诺固定总量。
- 运行时、手机数据和日志改用 `%LOCALAPPDATA%\DroidNest`。更新客户端 ZIP 不覆盖手机数据；旧版 EXE 旁的目录需确认后复制，仅复制持久目录尚不存在的对应文件夹，不覆盖并保留原件。请先关闭旧版并备份，不要复制运行中的磁盘。
- 保留可选的完整环境诊断及 JSON 输出；`-Launch` 仅检查客户端启动条件。移除旧的手动组件准备/SDK 导入入口。
- 白名单公开客户端打包保留签名辅助源码；私人 `LOCAL-ONLY` 包读取持久目录，默认不含手机数据，显式 `-IncludeData` 才携带个人数据。
- 安装完成进入主界面后自动启动手机；后续打开客户端也自动启动已准备的手机。
- 修复视频窗口在首帧到达前接入导致的崩溃；停止手机前刷新 Android 存储，保护刚写入的数据。私人迁移包完整保留模拟器所需的 overlay APK。

## 宿主要求与安全边界

Windows 10/11 Intel/AMD x64、.NET Framework 4.8、BIOS/UEFI 虚拟化与 WHPX。宿主虚拟化需自行启用并重启；安装器不提权或修改 Windows 功能。首次准备需要网络、下载/解压空间及至少 8 GiB 手机磁盘预留；建议主机至少 8 GB 内存。

Android 11 x86_64 无 ARM 翻译和 Google Play 服务。公开 AOSP 测试签名不适合敏感生产数据；WebView 在本机匹配测试证书重签名，不关闭 Android 签名验证，不修改用户 APK，不更新系统安全补丁。EXE 尚未作 Windows Authenticode 签名，请勿关闭系统防护。

**验证状态：本机在线组件准备、真实桌面持续显示、WebView 156、窗口操作及换目录启动保留数据已通过；独立干净 Windows 环境仍待验收。** 详见 VALIDATION.md。本说明不保证所有 APK、Windows 版本或显卡兼容，也不表示已发布此版本。

客户端代码 MIT，第三方许可独立，见 `THIRD-PARTY-NOTICES.md`。完整运行时和 `LOCAL-ONLY` 包不能作为公开 Release 附件；带数据的包可能含凭据，必须保密。更新、重试和迁移细节见 README。
