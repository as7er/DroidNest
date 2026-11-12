# DroidNest v0.3.0 — Hardening, recovery and data tools / Windows x64

DroidNest / 安卓小窝：无广告的 Windows Android 桌面客户端，真实手机画面和控制面板同窗显示。

## 下载与开始

下载 **`DroidNest-v0.3.0-win-x64-client.zip`**，核对对应 SHA-256，完整解压后双击 `Start-DroidNest.cmd`。不要只下载 EXE，也不要在 ZIP 内运行。

ZIP 包含客户端、配置、启动/诊断/打包/签名脚本、`tools/SignWebView.java`、许可及说明；**不含 Android SDK、系统镜像、scrcpy、WebView APK、JDK、手机数据、日志或密钥**。它是在线安装入口，不是离线完整运行时或第三方再分发授权。

## 本次变化

- 安装记录升级 format 2：记录关键运行文件的 SHA-256，能发现同大小损坏、缺失摘要和旧版记录；旧格式记录通过向导重新准备，手机数据不动。无记录的显式旧版导入保留原有较弱检查，记录本身未签名。
- 下载支持 HTTP Range 断点续传：校验已写入前缀与 `Content-Range`，范围不符或摘要失败时重新完整下载；Platform Tools 固定独立发布的 SHA-256，Emulator、系统镜像、Build Tools 仍只有 Google HTTPS 元数据中的 SHA-1。
- 安装按下载、ZIP 展开、分配余量和签名输出动态检查磁盘空间，统一 8 GiB 手机预留。
- 日志按会话轮换：保留最近 16 个、合计不超过 64 MiB，跳过仍在写入的文件。
- AVD 配置不再每次覆盖：保留内存、CPU 核数、分辨率、DPI、堆大小、数据分区和 GPU 模式，非法值回退默认，启动参数使用同一 GPU 模式。
- 仅画面进程退出时自动重连 scrcpy，Android 与已运行应用不重启；模拟器或私有 ADB 退出时显示原因和模拟器日志，可点击“启动手机”重启。
- 新增“打开客户端时自动启动手机”设置，保存在客户端 `settings.json`，与手机数据分离。
- 新增手机数据备份/恢复：ZIP 与 SHA-256 校验文件，恢复前校验并拒绝覆盖、合并现有数据及路径穿越/链接条目。
- 安装 APK 前显示包名、版本和签名证书 SHA-256；优先使用本地 Build Tools/JDK，缺少工具时用内置二进制 manifest 与 v1 证书回退，确认后才安装。
- 新增运行时健康检查与“修复运行环境…”入口：识别旧安装记录、模拟器/Platform Tools 版本漂移和缺少 Build Tools，修复时保留 `data`。
- 新增 `Sign-Release.ps1`（可选）：支持 PFX 或证书存储/令牌签名并验证 Authenticode。非商业发布不自签名，只提供 ZIP 的 SHA-256 校验。
- 增加 Windows CI 与离线边界测试：安装记录/磁盘/摘要、续传/AVD/日志、设置/备份/健康。

## 升级与数据

运行时、手机数据和日志继续保存在 `%LOCALAPPDATA%\DroidNest`。更新客户端 ZIP 不需要重新安装运行时，也不会覆盖手机数据；升级前建议先正常停止手机并做一次备份。

来自 v0.2.0 或更早的安装记录（format 1）不会自动就地生成“可信”摘要：首次启动会进入安装向导重新准备运行时，旧运行时保留为 `runtime.previous-<id>`，手机 `data` 不变，请预留下载时间与空间。明确导入的无记录旧运行时仍只做依赖存在性检查，不具有 format 2 的完整性保证。

## 宿主要求与安全边界

Windows 10/11 Intel/AMD x64、.NET Framework 4.8、BIOS/UEFI 虚拟化与 WHPX。首次准备需要网络、下载/解压空间及至少 8 GiB 手机磁盘预留；建议主机至少 8 GB 内存。

Android 11 x86_64 无 ARM 翻译和 Google Play 服务。公开 AOSP 测试签名不适合敏感生产数据；WebView 在本机匹配测试证书重签名，不关闭 Android 签名校验，不修改用户 APK，不更新系统安全补丁。

**EXE 未作 Windows Authenticode 签名**（非商业发布选择）。Windows 可能提示“未知发布者”或 SmartScreen；不要关闭系统防护，核对官方仓库与发布页 SHA-256 后自行决定。校验值帮助发现损坏/替换，不证明发布者身份。

## 验证状态

本机 Windows 10 x64 已通过：format 2 安装记录、真实网络续传（206）、日志轮换、AVD 调优生效、scrcpy 重连且 Android uptime 未重置、真实 APK 信息读取、615.5 MiB 数据备份恢复后再次引导，以及 7 步远程 Windows CI。v0.2.0 的在线安装、持续桌面显示、窗口操作和迁移验证仍适用。

**未覆盖**：干净 Windows 用户/虚拟机、全新 format 2 安装后的端到端流程、WHPX 不可用主机、不同 GPU/DPI、真实断网/代理矩阵和真实存储配额。详见 VALIDATION.md。

客户端代码 MIT，第三方许可独立，见 `THIRD-PARTY-NOTICES.md`。完整运行时和 `LOCAL-ONLY` 包不能作为公开 Release 附件；带数据的包可能含凭据，必须保密。更新、重试和迁移细节见 README。
