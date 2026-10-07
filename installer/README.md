# 安装包构建

完整包使用 Inno Setup 6.7.3 编译，稳定 AppId 保证所有版本沿用原安装目录。正式安装默认管理员权限、Windows 10 2004+ x64，程序本体也通过自身 manifest 请求管理员权限。

| 文件 | 用途 |
|---|---|
| `AzurAssistant.iss` | 自包含完整包安装、离线VC++依赖、快捷方式/卸载、旧PID等待与互斥检查、仅清理旧包拥有的文件；保留配置/路线/日志，安装后启动跳过自动游戏动作 |
| `ChineseSimplified.isl` | Inno Setup `is-6_7_3/Files/Languages/Unofficial/ChineseSimplified.isl` 原样中文翻译；保留上游作者和许可注释 |
| `README.md` | 构建工具、安装职责和验证说明 |

调用 `scripts/Build-Release.ps1`，指定可信 ISCC.exe 和微软签名的 vc_redist.x64.exe。工具只用于构建，不随源码目录或程序包分发。首次/升级均使用同一完整 EXE；未配置 GitHub 时可手动运行。
