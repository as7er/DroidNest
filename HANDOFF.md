# DroidNest 换机交接

交接日期：2026-11-11。用途：换电脑后恢复开发上下文；本次只做交接，不实施优化、不打包私人手机数据。

## 1. 当前状态与任务来源

- 仓库：<https://github.com/as7er/DroidNest>
- 当前电脑工作目录：`F:\DroidNest`。新电脑可以使用其他目录。
- 当前提交：`d82f2134eb22f3f00621a051e8611706479e316f`，提交标题：`Release v0.2.0 with guided runtime installation and persistent phone data`。
- 写入本交接文件之前，`git status --short` 输出为空。本交接文件是此次新增文件；本次没有提交或推送。
- Codex 已完成拉取、代码检查和优化建议，用户回复“可以，按你建议继续”，随后多次回复“继续”。之后模型服务发生 HTTP 530/502 错误，**没有开始实施优化，没有半成品代码需要续写或回滚**。
- 本次用户最新要求：**“先帮我做个换机交接”**。先完成交接，不能把优化建议描述成已完成，也不能默认已经选定本轮实施范围。

### 历史验证与本次核对的区别

|来源|结果|边界|
|---|---|---|
|前次 Codex 记录|`build.ps1` 成功，产物 `F:\DroidNest\DroidNest.exe`|本次交接未重新构建|
|前次 Codex 记录|`New-PortablePackage.ps1` PowerShell 语法解析通过|不代表实际生成过安装包|
|仓库 `VALIDATION.md`|本机在线安装、桌面持续显示、窗口操作、换目录及数据保留、迁移与解压边界验证|是版本已有记录，不是本次执行；不是干净 Windows 验收|
|本次交接|读取当前提交、工作区状态、README、构建和迁移打包脚本|未启动 Android，未进行新机验收，未生成手机备份|

此前还有一次已经完成的桌面 v0.1.0 客户端环境修复；它不是本次待续接的开发任务。旧版手机数据可能位于旧 EXE 相邻的 `data`，不能只复制 Git 仓库就认为手机已备份。

## 2. 换机前要带走什么

### 只继续开发

1. 保存本文件。它目前仅存在于本机；单独在新电脑 `git clone` **不会自动带上未提交、未推送的交接文件**。
2. 推荐将本文件单独复制到新电脑的仓库根目录；也可以自行审核后提交并推送。此次没有替用户执行提交或推送。
3. 源码通过 Git 获取即可。无需复制旧电脑的 `.codex`、模型认证、访问令牌或整个用户目录。本文件已整理恢复任务所需上下文。

### 还要保留当前 Android 手机

源码仓库和手机状态是两份独立资料。v0.2.0 当前 Windows 用户的状态路径为：

```text
%LOCALAPPDATA%\DroidNest\
  runtime\   Android、scrcpy、本地 WebView、可选 installation.json
  data\      手机虚拟磁盘、应用、Android 用户配置
  logs\      诊断日志
```

- 先正常关闭全部 DroidNest，等待手机和视频进程退出；不要强杀后立即复制虚拟磁盘，不要打包期间重新启动。
- 如果正在使用旧版 EXE 旁的 `runtime`/`data`，先依 README 的旧版迁移流程确认需要保留的是哪部手机。下面的 Local 打包脚本读取的是 `%LOCALAPPDATA%\DroidNest`，**不会自动寻找桌面旧版数据**。
- 在仓库根目录用 Windows PowerShell 执行以下命令。第一条构建客户端；第二条明确包含手机数据：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\New-PortablePackage.ps1 -Mode Local -IncludeData
```

输出文件：

```text
dist\DroidNest-v0.2.0-win-x64-LOCAL-ONLY-with-data.zip
dist\DroidNest-v0.2.0-win-x64-LOCAL-ONLY-with-data-SHA256SUMS.txt
```

- 只迁移运行时、不保留应用和手机内容时，去掉 `-IncludeData`；文件名也不再有 `-with-data`。
- 脚本不下载运行时；组件缺失、无法确认已停止、存在占用锁或输出同名文件时会拒绝打包。不要绕过锁检查或随意删除数据。已有同名包应先移到独立备份位置。
- 将 ZIP 和校验文件一起通过可信私人介质传输。含数据包可能有账号、凭据、Android 身份和 ADB 密钥；不能上传公开仓库或 GitHub Release。校验值不等于发布者签名，校验文件本身也应可信。
- Local 包不包含开发源码或本交接文件；开发资料仍按上一节另行转移。日志、缓存、私有准备工具和签名材料也不属于该包的完整备份承诺。
- 新机确认应用和重要数据完整之前，不删除旧机原件和备份。

## 3. 新电脑恢复开发

需要 Windows 10/11 x64、Git、Windows PowerShell 5.1、.NET Framework 4.8。构建使用系统 Framework 编译器，代码为 C# 5/WPF，不依赖 NuGet，也不要求系统 JDK。

在你选择的父目录执行：

```powershell
git clone https://github.com/as7er/DroidNest.git
Set-Location DroidNest
git log -1 --format="%H %s"
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

把带来的 `HANDOFF.md` 放到仓库根目录。上面的 clone 获取远端当前默认分支，可能比本交接提交更新；如有新提交，先阅读变化，不要为了匹配本文件而强制重置工作区。需要研究旧基线时使用上面的完整提交 ID。

运行 Android 另需：BIOS/UEFI 虚拟化启用、Windows Hypervisor Platform（WHPX）启用并重启、足够内存与磁盘，以及本地端口 5048/5580/5581 可用。这些主机条件不会随文件复制迁移。

### 不迁移旧手机

从仓库构建成功后运行 `Start-DroidNest.cmd`，按照图形向导接受所需条款并联网安装运行时。不要使用已删除的 `Setup-Components.ps1` 或 `Import-LocalRuntime.ps1`。

### 使用私人迁移包

1. 校验 ZIP 的 SHA-256 与随包校验文件一致，例如：

   ```powershell
   Get-FileHash -Algorithm SHA256 .\DroidNest-v0.2.0-win-x64-LOCAL-ONLY-with-data.zip
   Get-Content .\DroidNest-v0.2.0-win-x64-LOCAL-ONLY-with-data-SHA256SUMS.txt
   ```

2. 将 ZIP 完整解压到独立目录，不直接在 ZIP 中运行，不覆盖新克隆的源码目录。
3. 在目标 Windows 用户下启动解压目录中的 `Start-DroidNest.cmd`，明确同意迁移到该用户的 `%LOCALAPPDATA%\DroidNest`。
4. 迁移只复制目标不存在的对应目录，保留原件，不合并两部手机。目标已有 `runtime` 或 `data` 时先确认应保留哪份并备份；不要先运行空白手机创建目标数据，也不要直接覆盖现有数据。
5. 确认桌面持续显示、应用和重要数据存在；正常关闭后再次打开，验证仍保留。再关闭该客户端，按需从源码目录启动开发版；同一用户使用相同持久目录，不同时运行两份客户端。

诊断命令（在客户端/仓库根目录执行）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Check-Environment.ps1 -Json
```

默认诊断缺少运行时时返回失败不等于客户端无法打开；`Start-DroidNest.cmd` 使用启动器模式，让图形向导处理运行时缺失。

## 4. 待继续的开发建议：全部尚未实施

下面恢复的是前次 Codex 的建议，不是本次重新确认的缺陷结论。实现前应核对当前代码与调用方；行号容易变化，因此以文件和符号定位。

|优先级|建议|起点与约束|
|---|---|---|
|优先|运行时完整性增加关键文件 SHA-256 校验|`src/RuntimeInstaller.cs` 的安装记录和验证；历史检查指出记录仅校验大小|
|优先|固定 Android 下载组件可信 SHA-256，或使用签名元数据|`src/RuntimeInstaller.cs` 的在线组件计划；不能把同一不可信下载结果计算出的哈希当作预先信任锚|
|优先|统一、动态计算磁盘空间需求|`src/RuntimeInstaller.cs`、`src/AndroidRuntime.cs`；包括下载、解压、缓存和旧运行时，历史指出 12 GiB/8 GiB 提示不一致|
|优先|增加干净 Windows CI/验收矩阵|`VALIDATION.md`；区分自动构建/脚本检查与真实 WHPX/GUI/硬件验收，不可用前者冒充后者|
|稳定性|HTTP Range 断点续传|`src/RuntimeInstaller.cs`；历史指出失败重试重新下载|
|稳定性|日志轮换和清理|`src/AndroidRuntime.cs`|
|稳定性|保留自定义分辨率、内存、CPU、GPU 配置，或提供配置页|`src/AndroidRuntime.cs` 的 `PrepareAvd()`；历史指出每次写入固定配置|
|稳定性|Emulator/scrcpy 异常退出后的恢复入口|`src/AndroidRuntime.cs`、`src/PhoneDisplayHost.cs`、`src/MainWindow.cs`|
|功能|关闭自动启动手机的选项|启动流程和主窗口|
|功能|手机数据一键备份/恢复|基于现有停机、迁移、不覆盖边界设计；现有 Local 打包不等于 GUI 功能已实现|
|功能|APK 安装前展示包名、版本和签名摘要|APK 安装入口；不要为了展示信息先安装待检查 APK|
|功能|运行时修复、更新检查、版本迁移提示|安装器、向导和现有持久数据路径|
|发布|EXE 和发布校验文件签名|正式签名证书及安全保管是前置条件；当前 EXE 未作 Authenticode 签名|
|后续评估|SDK-style/.NET 8 WPF 迁移|原建议仅为“后续可评估”，未决定迁移，不默认执行|

本次曾询问先做优先加固、加固加稳定性或全部可落地项；用户没有选择其中一项，而是要求先换机交接。建议接续时先明确一轮交付边界，再实施。不要擅自删除其余建议，也不要声称已批准某个缩减范围。

### 相关文件导航

- `src/RuntimeInstaller.cs`：组件元数据、下载、校验、解压、本地准备和安装记录。
- `src/SetupWindow.cs`：安装向导。
- `src/AppPaths.cs`：持久路径及旧目录迁移。
- `src/AndroidRuntime.cs`：宿主检查、AVD、Android 进程与操作。
- `src/PhoneDisplayHost.cs`：原生视频子窗口集成。
- `src/MainWindow.cs`、`src/Program.cs`：主 UI 与启动入口。
- `build.ps1`、`New-PortablePackage.ps1`、`Check-Environment.ps1`：构建、白名单打包和诊断。
- `README.md`、`CHANGELOG.md`、`RELEASE-NOTES.md`、`VALIDATION.md`：使用约定、版本记录和验证证据。

## 5. 可以直接发给新电脑上的助手

> 请先阅读仓库根目录 HANDOFF.md、README.md 和 VALIDATION.md，恢复 DroidNest 工作。前次 Codex 已拉取 v0.2.0 并提出优化建议；我批准继续后服务报错，尚未开始修改。上一台电脑最后只完成换机交接，没有实施任何优化。先检查新机仓库的当前状态并保留已有变更；根据交接文件明确本轮优化范围，再继续实现和针对性验证。不要重复已完成的旧版本机运行时修复，不要删除或覆盖手机数据，不要把私人运行时/数据/密钥提交到 Git，不要把历史验证当作新机已通过。

## 6. 历史追溯（仅旧机可选）

原 Codex 会话 ID：`01a1c969-d84e-78d1-bcc4-960f7081b384`。

旧机记录：`C:\Users\WXX\.codex\sessions\2026\11\11\rollout-2026-11-11T15-01-59-01a1c969-d84e-78d1-bcc4-960f7081b384.jsonl`。

关键顺序：先 `git pull` → 用户请求优化建议 → Codex 报告“本次只做检查，没有修改代码” → 用户批准实施 → 服务报错。新机继续任务不依赖这个旧机绝对路径，也不需要复制包含其他历史或凭据的工具配置目录。
