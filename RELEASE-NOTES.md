# DroidNest v0.1.0 — Windows x64 client

DroidNest / 安卓小窝：无广告的 Windows Android 桌面客户端，真实手机画面和控制面板同窗显示。

## 下载选择

- **推荐：`DroidNest-v0.1.0-win-x64-client.zip`**。包含 EXE、配置、启动前环境检查、运行时导入与组件准备脚本、说明和许可证。
- `DroidNest.exe` 是客户端单独文件，方便已有完整目录的用户使用；**不是单文件 Android 模拟器**。新用户请下载 ZIP。
- `SHA256SUMS.txt` 包含 Release 资产的 SHA-256。

**这个 Release 不包含 Android 系统镜像、Android SDK、scrcpy、WebView APK、手机数据或密钥。需要按 README 自行准备运行时，单独下载 EXE 无法运行 APK。**

## 功能

- 真实 Android 视频窗口集成，鼠标/键盘输入、横竖屏、缩放和全屏。
- APK 安装、主页/返回/最近任务、Android 设置、独立实例与数据持久化。
- 启动前检查 Windows 架构、.NET、目录权限、磁盘、WHPX、组件和端口；失败时显示补齐方法，不自动修改系统。
- 公开客户端便携打包与个人 LOCAL-ONLY 迁移包，手机数据默认不打包。

## 要求与边界

Windows 10/11 x64 Intel/AMD、.NET Framework 4.8、WHPX 和 BIOS 虚拟化。当前验证配置 Android 11 x86_64，无 ARM 翻译和 Google Play 服务。仅在开发电脑实际验证，不承诺所有 APK、Windows 版本和显卡兼容。

开发镜像使用公开 AOSP 测试签名，不适合敏感生产数据；EXE 未作 Windows Authenticode 签名。请核对仓库及校验值，不要关闭安全防护。

客户端代码 MIT；第三方组件许可独立，见 THIRD-PARTY-NOTICES.md。完整运行时及包含数据的 LOCAL-ONLY 包不得作为这个 Release 的附件。
