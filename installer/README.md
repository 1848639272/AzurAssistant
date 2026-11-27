# 安装包构建

完整包使用 Inno Setup 6.7.3 编译。向导通过注册表读取相同 AppId、安装权限模式和架构的旧版安装目录。通过安装包装过旧版时，双击新安装包即可沿用原目录。手工复制的便携目录需要自行选择。正式安装默认管理员权限、Windows 10 2004+ x64，程序本体通过自身 manifest 请求管理员权限。

准备安装页面显示 Visual C++ x64 检查结果。安装器逐项检查 msvcp140.dll、msvcp140_1.dll、vcruntime140.dll、vcruntime140_1.dll 的版本。在缺少文件或低于 14.44 时使用内置的微软安装程序，随后复核。需要重启或修复失败时给出缺项与安装日志路径。.NET 桌面运行时随完整包提供。

安装前检查目标目录能否创建、改名和删除临时文件。覆盖时通过 Windows Restart Manager 列出占用程序，允许用户正常关闭后再试。安装器可替换包内已变为只读的程序文件。程序目录无修改权限、仍被占用或遇到错误 5 时，可打开用户临时目录中的 `Setup Log *.txt` 查看目标文件和 Windows 错误码。若安装成功但启动时在写入助手日志前退出，可在“事件查看器 → Windows 日志 → 应用程序”中查看 .NET Runtime / Application Error 的版本和异常代码。

全新安装在程序目录写入 `first-launch-notice.pending`。首次启动显示使用说明并在关闭后移除标记。安装时未启动、静默安装均会保留标记到下次启动。覆盖升级保留当前状态，卸载清理未读标记。

升级后按旧包和新包的清单比较程序文件，仅清理旧包独有的受管文件。清理会跳过目录联接与其他重解析点。设置、日志、路线和其他用户文件保留。

安装完成页通过系统 Shell 启动程序，由程序 manifest 触发 UAC 提升，与双击桌面快捷方式一致。`postinstall` 默认使用安装前的普通用户权限，不能直接用 CreateProcess 启动要求管理员权限的程序；否则会返回错误码 740。首次启动仍传入 `--skip-startup-actions`，静默安装不自动启动。

| 文件 | 用途 |
|---|---|
| `AzurAssistant.iss` | 自包含完整包安装、离线VC++依赖、快捷方式/卸载、旧PID等待与互斥检查、仅清理旧包拥有的文件；保留配置/路线/日志，安装后启动跳过自动游戏动作 |
| `ChineseSimplified.isl` | Inno Setup `is-6_7_3/Files/Languages/Unofficial/ChineseSimplified.isl` 原样中文翻译；保留上游作者和许可注释 |
| `README.md` | 构建工具、安装职责和验证说明 |

调用 `scripts/Build-Release.ps1`，指定可信 ISCC.exe 和微软签名的 vc_redist.x64.exe。工具只用于构建，不随源码目录或程序包分发。首次/升级均使用同一完整 EXE；未配置 GitHub 时可手动运行。
