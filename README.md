<p align="center">
  <img src="AzurAssistant/assets/branding/cabbird-64.png" width="128" height="128" alt="蔚蓝助手 Logo">
</p>

# 蔚蓝助手

蓝色星原：旅谣自动化辅助 · 公测适配开发预览版 v1.0.0_rc2_20261127

蔚蓝助手（AzurAssistant）是一款基于计算机视觉的《蓝色星原：旅谣》自动化辅助，通过图像识别、OCR 与模拟键鼠操作，处理日常领奖、副本挑战、剧情与采集交互，并提供地图定位和路线管理。

**QQ 交流群：1129014322**

[功能](#功能) · [下载与安装](#下载与安装) · [使用方法](#使用方法) · [更新](#更新) · [当前支持范围](#当前支持范围) · [构建与发布](#构建与发布)

## 功能

### 一条龙

勾选需要的项目，按顺序执行。启动与进入游戏固定在前，关闭游戏固定在末尾；中间项目可拖动调整顺序。相邻项目都从主菜单进入时，助手保留主菜单交给下一项，最后一项完成后返回大世界。运行时拖动游戏窗口后，助手会重新取帧。加载时可在后台继续识别画面。发送操作前会检查焦点，短暂切换会等待新画面。30 秒内连续三次置前都被其他操作打断时，一条龙停止。

- **启动游戏**：打开或复用设置中指定的官方启动器，识别并点击“启动游戏”。新游戏窗口出现后，助手会主动切回游戏。点击“开始旅程”前会确认游戏持有输入焦点，并用新画面定位按钮。切换未成功时，日志会显示当时的前台进程名和 PID。
- **进入游戏**会确认资源下载，等待更新，并在出现重启提示时点击一次「确定」。更新期间客户端直接退出也会进入重连。游戏自行重启时直接接回新窗口；若游戏窗口和进程持续消失约 8 秒，助手会通过设置中的官方启动器补启动一次。重连最长等待 2 分钟。随后点击「开始旅程」，处理礼赠、月卡和其他可识别的登录奖励。大世界连续稳定 5 秒后才开始邮箱等后续任务。期间若有奖励弹出，处理完回到大世界后重新计时。「来自星海的礼赠」逐列识别七张卡片上方浮动的倒三角，同一张未领取卡片连续稳定可领满 1 秒才点击。普通模式下，首击后 3 秒仍持续显示该卡可领时补点一次；出现勾选或奖励展示就转入领取确认。普通「获得」按底部提示关闭，有「返回」的已知展示点返回，其他已切换画面且没有右上角关闭按钮的样式尝试点一次空白处。重新看到当天勾选和关闭按钮后才关闭礼赠面板。
- 领取后出现额外奖励展示时，助手识别「返回」和「前往更换／前往装配」按钮，点击「返回」后继续当前任务。
- **领取邮件**：领取当前可领取的邮件附件并关闭邮箱。
- **商城**在邮件之后，可单独勾选。助手在「甄选星品 → 限时珍藏」查找第一格的每日惊喜盒子，核对详情中的「免费领取」后领取。首格已换成标价商品时直接关闭商城。
- **家园中枢**：检查奇波餐桌，收取建筑与牧场产出。展开可勾选「奇波餐桌只使用波波饼」。开启后会翻找货架上的两种波波饼，有饼时添加并核对饱腹值，无饼时继续建筑与牧场。默认使用餐桌的一键添加。牧场显示锁标或空篮时跳过。
- **日常周常领取**：领取已完成任务的活跃度和可领取的活跃度宝箱奖励。
- **通行证领取**：领取通行证经验与当前可领取奖励，处理升级提示。
- **往厄残影**：选择副本、难度与队伍，执行挑战和领奖；记录本周已确认领取次数，达到周上限时跳过。
- **日常委托**：按基础材料、首领挑战、武备获取选择副本，设置难度与队伍，执行挑战和领奖。
- **危机讨伐**：按难度从预设名称中选择惊喜副本、普通副本和各自的领取上限。六档难度各有两个惊喜副本。旧配置使用已移除名称时，打开委托设置重新选择。
- **主线委托**：选择章节、关卡与队伍，重复挑战所选关卡并领取奖励。
- **自定义路线**位于关闭游戏之前。可重复添加已保存的路线、调整执行顺序，并为每次运行单独勾选拾取辅助。
- **关闭游戏**：一条龙结束后按设定等待时间关闭游戏，可同时关闭助手。

副本挑战使用**游戏内自动战斗**。往厄残影、日常委托、主线委托和两类危机讨伐均可设置最多领取份数；奖励倍率计入领取份数。运行前请确认副本、队伍和领取上限。

### 实时辅助

- **隐藏 UID** 可单独开启，也可与其他实时辅助一起运行。左下角 UID 由边缘渐隐的淡白圆角矩形遮挡，遮罩跟随游戏窗口移动，鼠标可穿透。助手预览与 OCR 使用游戏原始画面。只捕获游戏窗口的录制仍可能显示 UID，分享前可先检查录制预览。
- **自动跳过对话**：识别剧情快进箭头及确认弹窗。第一下松开后，如同一按钮仍在，间隔至少 30 毫秒再点一次。按钮持续可见时，冷却结束后继续尝试。
- **自动拾取采集物、自动挖矿、自动砍树**可分别勾选。在大世界和家园场景中，助手识别 `F` 选中行的交互图标，再读取同一行名称，只执行已开启的类别。选中伐木场、元素祭坛、寻觅潜影、修复、坐骑挑战、奇波变身、建造、星辉矿场、采石场、采集小屋、调查、阅读或城镇商店时跳过。名称无法确认时也跳过。路线拾取共用这份屏蔽名单。剧情期间暂停采集。

### 路线管理

- 点击「路线录制」可查看当前地图、坐标及朝向。选择「记录当前点」添加步行点。保存前可命名和删除任一点。
- 录制时点击「前往目标传送点」，选择地图和节点。助手确认落地后，会把目的地地图和落点坐标一起记入路线。可直接记录下一步的目标，保存时会检查它与落地地图是否衔接。
- 右侧按执行顺序显示点位。「编辑路线」可改名和调整点位，点击垃圾桶后仍可取消，保存后才写回。路线选择框旁的文件夹按钮可打开本地目录。「导出路线」可分享 JSON，也可导入旧版单地图和多地图路线。选中路线并点击「删除路线」，确认后会删除本地路线文件，同时移除一条龙队列中对它的所有引用。如果队列因此清空，助手会关闭「自定义路线」步骤。
- 在「运行路线」旁可勾选拾取辅助。到点停稳后，砍树和挖矿按一次 F 并原地等待 10 秒，再前往下一点。普通采集每 250 毫秒检查一次，连续两次确认交互界面消失后前往下一点。
- 左下方的「定位测试」「前往传送点」和「前往指定坐标」分别用于持续定位、节点传送和同地图短距离移动。地图目录包含夏露露村 1 个、新芽山谷 15 个和弗利斯城邦 13 个传送目标。与事件重叠时，助手先选择具名节点，再核对传送详情。旧路线若提示某个节点已移除，请在当前地图重新录制该传送步骤。
- 暂不支持坐骑状态下使用路线功能。步行路线请在转弯和障碍附近补充点位。

### 通用设置

设置会自动保存，无需重启助手。界面和快捷键在当前会话生效。启动选项在下次启动时使用，任务参数在下次运行对应任务时使用。

- **随机延迟**默认关闭。开启后，从下次运行一条龙起，每次游戏交互前独立增加 0–100 毫秒，包含流程内的自定义路线。
- **启动行为**：可选择启动助手后自动连接游戏画面，或自动运行已保存的一条龙配置。
- **任务控制**：支持暂停、继续、停止和自定义快捷键。
- **画面与日志**：控制台显示游戏预览和运行日志；可关闭预览，日志及异常留证保存在本地。
- **低配模式**：即时降低预览刷新频率，下次运行任务时使用低配时序。
- **深色模式**：立即切换当前窗口的配色。
- **游戏状态边框**默认开启。绿色表示待机，蓝色表示任务运行，出错后短暂黄色提示。可在设置中关闭。支持无边框捕获的 Windows 还需允许助手隐藏系统捕获边框。系统权限或其他捕获程序可能使系统黄框继续显示。
- **悬浮状态栏**在游戏左侧任务列表与聊天入口之间显示近期日志和运行中的实时辅助。最新日志位于底部，较早的日志按剩余空间向上排列，长消息最多显示三行。可在设置中将文字和栏宽缩放至 50%–200%，宽度达到上限后仅放大文字。调整立即生效，背景透明，鼠标可穿透，助手预览和识别仍读取游戏原图。
- **关闭窗口时收至系统托盘**默认关闭。开启后点击右上角 × 可隐藏主窗口，任务继续运行。单击通知区域的助手图标恢复窗口，或从右键菜单退出程序。
- **更新检查**：支持每日首次启动检查和手动检查，发现新版本后提示安装。

## 下载与安装

需要 **Windows 10 2004 或更高版本（x64）**，目标进程为 `AzurPromilia.exe`。助手自动读取游戏客户区的实际像素尺寸。请使用中文游戏界面，分辨率设为 **1600×900、1920×1080、2560×1440 或 3840×2160**。遇到其他尺寸时，助手会显示实际尺寸并停止处理游戏画面。调整游戏窗口后可重新连接。

使用名称以 `-win-x64-setup.exe` 结尾的**完整 EXE 安装包**。安装包内置 .NET 运行环境，并在需要时安装 Visual C++ 运行库；使用程序无需安装 .NET SDK。

安装向导列出缺失或版本过低的 Visual C++ x64 组件。目标目录无法写入时会显示目录和安装日志位置；发现程序文件被占用时，请先关闭向导列出的占用程序，再继续安装。

从 [GitHub Releases](https://github.com/1848639272/AzurAssistant/releases) 下载完整 EXE 安装包。展开对应版本的 **Assets**，选择名称以 `-win-x64-setup.exe` 结尾的文件。

## 使用方法

1. 安装并启动蔚蓝助手，在 Windows 权限提示中允许管理员权限。
2. 打开「设置」，选择游戏桌面快捷方式指向的官方启动器 `launcher.exe`，下次启动游戏时使用所选路径。
3. 在「一条龙」或「实时辅助」中勾选功能；运行副本前展开对应设置，选择副本、队伍与领取上限。
4. 点击「开始」或「运行一条龙」，在控制台查看画面、状态与日志。
5. 使用快捷键暂停或停止任务。在设置页录入新快捷键，离开录入框后生效。

| 默认快捷键 | 操作 |
|---|---|
| `F9` | 暂停 / 继续当前任务 |
| `F10` | 停止所有任务 |

全新安装后首次启动会显示《蔚蓝助手使用说明》。关闭后可在「设置」的「检查更新」旁点击「版本说明」再次查看。首次启动默认不连接游戏，也不自动运行任务。快捷键可在设置中更改。停止后重新开始会从任务起点执行。

路线以步行点开头时，先让角色进入该点所属地图。也可录制以传送开头的路线，由助手确认落地后继续。

## 更新

- **自动检查**：开启「自动更新」后，助手会在每天首次启动时检查 GitHub；发现新版本时弹窗提醒，由你确认下载和安装。
- **手动检查**：点击「检查更新」可随时再次检查，关闭当天的更新提醒后也可使用。连接 GitHub 失败时会提示检查网络。
- **完整包覆盖升级**：关闭助手后也可直接运行最新安装包。曾用安装器安装、且安装模式相同时，安装器从 Windows 安装记录读取原目录。便携副本可手动选回原目录。升级后按新旧程序文件清单清理已移除的运行文件，保留配置、路线、记录与日志。

更新保留设置、委托记录、周本记录、用户路线和日志。安装完成后的首次启动会跳过自动连接与自动一条龙；之后正常启动按已保存设置执行。

更新源为 [1848639272/AzurAssistant](https://github.com/1848639272/AzurAssistant) 的已发布 Releases。beta 版本也参与更新检查，忽略草稿。按主、次、补丁版本比较；同一版本的正式版优先于 RC，再比较预览序号、日期和同版构建号。

## 当前支持范围

当前版本面向公测中文客户端。新芽山谷和弗利斯城邦使用公测地图的预计算特征。29 个传送点含落地坐标。尚未解锁的难度需在游戏内解锁后才能挑战。

- 1920×1080 画面直接用于识别。其余三档等比缩放至 1920×1080，点击和拖动映射回实际客户区。小字号文字在 1600×900 下更依赖原始画面的清晰度。画面缩放与坐标映射基于 [jinitaimei-114514 的贡献](https://github.com/1848639272/AzurAssistant/commit/a03e3fba6ab6165f0bb8767b2dbe6d9ffc8b145e)。
- HDR、独占全屏及长期无人值守尚待验证。
- 导航和路线暂不支持自动绕障；长距离路线、部分传送目标仍需实机验证。
- 切换账号后，请在一条龙的往厄残影设置中重置周本记录，避免沿用上一账号的记录。
- 识别失败、画面过期或窗口状态异常时，程序会停止当前操作或等待重新观察。遇到问题可查看程序目录中的 `logs/`。

## 构建与发布

### 从源码构建

安装 .NET SDK 10.0.400（或同系列更新补丁），在仓库根目录运行：

```powershell
dotnet build AzurAssistant.sln -c Release
```

构建后的可执行文件位于 `AzurAssistant/bin/Release/net10.0-windows10.0.19041.0/AzurAssistant.exe`。源码仓库不预置编译结果；构建目录、缓存和用户数据已由 `.gitignore` 排除。

### 生成完整安装包

完整发布使用 PowerShell 7：

```powershell
pwsh -File scripts/Build-Release.ps1 -CompilerPath C:/Tools/Inno/ISCC.exe -VCRedistPath C:/Tools/vc_redist.x64.exe -OutputDirectory D:/Releases/AzurAssistant/v1.0.0_rc2_20261127
```

Inno Setup 6.7.3 和微软签名的 x64 Visual C++ v14 完整离线运行库仅在打包时使用；完整安装包内置 .NET 和 VC++ 依赖。`OutputDirectory` 需为新的空目录，省略时默认 `out/v1.0.0_rc2_20261127`；仅输出 `program/`、`release/` 和发布说明。再次发布使用递增 BuildNumber（默认 UTC 秒），GitHub Release 标签使用清单中的 ReleaseId，附件上传 `release/` 内的 EXE、`release-manifest.json` 和 `SHA256SUMS.txt`。

### 接入 GitHub 更新

默认更新仓库已在 [update-source.json](AzurAssistant/update-source.json) 配置为 `1848639272/AzurAssistant`。已发布的 beta Release 参与更新检查，草稿不参与。早期未配置地址的安装包需要先手动安装本仓库发布的完整 EXE，之后才能通过程序检查后续更新。

## 文件说明

| 文件或目录 | 用途 |
|---|---|
| `AzurAssistant.sln` | 正式程序解决方案 |
| `AzurAssistant/` | WPF 程序源码及项目文件 |
| `AzurAssistant/Runtime/ForegroundRecovery.cs` | 按一条龙输入会话限制置前次数，恢复后要求重新观察 |
| `AzurAssistant/Composition/UidOverlayTask.cs` | 管理实时辅助遮罩的创建与销毁，复用游戏捕获服务 |
| `AzurAssistant/UI/VersionNotesWindow.xaml`、`VersionNotesWindow.xaml.cs` | 设置和首次安装共用的使用说明、风险提示与项目链接弹窗 |
| `AzurAssistant/Features/Routes/RouteRecordingTask.cs` | 在录制会话中持续定位，处理记录点及前往传送点的请求 |
| `AzurAssistant/UI/RouteTeleportWindow.xaml`、`RouteTeleportWindow.xaml.cs` | 显示录制用地图与节点选择框并接收确认 |
| `AzurAssistant/assets/navigation/world-map-title-compact.png`、`adventure-title-wide.png` | 分别识别紧凑版世界地图标题和宽布局冒险地图标题 |
| `AzurAssistant/assets/navigation/maps.json`、`recognition.json`、各节点 `*-title.png` | 地图坐标、目标和落点目录，以及选择传送前核对完整名称的模板 |
| `AzurAssistant/assets/navigation/village-landing-features.bin`、`valley-landing-features.bin`、`city-landing-features.bin` | 夏露露村、新芽山谷和弗利斯城邦落地附近的小地图地形特征，用于在当前画面中恢复定位 |
| `AzurAssistant/assets/navigation/map-sun-tower-icon.png`、`overlap-row-divider.png`、`overlap-city-node-label.png` | 日渡塔遗迹图标、事件重叠选择行及城邦节点行名称 |
| `AzurAssistant/UI/UidMaskWindow.cs` | 绘制边缘渐隐的圆角 UID 遮罩，跟随游戏客户区并允许鼠标穿透 |
| `AzurAssistant/UI/GameOverlaySurface.cs` | 管理跟随游戏客户区的独立透明窗口、屏幕坐标、鼠标穿透及资源释放 |
| `AzurAssistant/UI/GameStatusWindow.cs` | 在游戏窗口边缘显示状态颜色，在左侧显示实时辅助名称与最近日志 |
| `AzurAssistant/UI/MainWindow.Presentation.cs` | 应用游戏状态显示选项、申请系统捕获边框权限、处理关闭到托盘与恢复 |
| `AzurAssistant/Runtime/ReferenceSpaceFrameSource.cs` | 检查游戏客户区是否支持，向识别模块提供参考画面并保留原始尺寸 |
| `AzurAssistant/Vision/FrameNormalizer.cs` | 将 1600×900 画面等比放大、1440p 和 4K 画面等比缩小至 1920×1080 识别基准 |
| `AzurAssistant/Composition/JourneyRestartConnection.cs` | 游戏自重启后的窗口查找、输入激活与画面重连 |
| `AzurAssistant/Composition/LauncherPreparation.cs`、`AzurAssistant/Features/LaunchGame/LauncherButtonRecognizer.cs` | 打开或复用官方启动器，在启动器窗口识别可用的「启动游戏」按钮 |
| `AzurAssistant/assets/start-journey/update-progress.png`、`update-restart.png` | 更新中的状态文字、更新完成的重启提示正文 |
| `AzurAssistant/Game/Ui/ExtraRewardTask.cs` | 在当前任务输入租约内确认奖励展示、点击返回并继续取帧 |
| `AzurAssistant/assets/daily/extra-return.png`、`extra-change.png`、`extra-equip.png` | 特殊奖励展示的返回、更换和装配按钮 |
| `AzurAssistant/assets/realtime/dialogue-summary-continue.png`、`dialogue-summary-skip-edge.png`、`dialogue-prompt-cancel.png`、`dialogue-prompt-confirm-edge.png` | 剧情确认文字被遮挡时核对另一按钮和确认按钮外缘 |
| `AzurAssistant/assets/daily/ranch-management.png` | 用右下角的「牧场管理」文字确认牧场内部页面 |
| `AzurAssistant/assets/daily/buildings-back.png`、`home-ranch-locked.png` | 建筑管理返回箭头和中枢牧场快捷入口锁标 |
| `AzurAssistant/assets/daily/home-close-hover.png`、`dining-cake-one.png`、`dining-cake-two.png` | 家园关闭按钮悬停外观、食材栏一星和二星波波饼 |
| `AzurAssistant/assets/commissions/weekly-title-public.png`、`catalog-back-story.png`、`crisis-solo.png` | 往厄残影标题、主线目录返回箭头、危机讨伐单人挑战按钮 |
| `AzurAssistant/assets/commissions/crisis-tier-*-public.png`、`crisis-glyph-*-public.png`、`crisis-level-tag.png` | 危机难度选中状态、可选罗马数字和所选难度下方的等级标签 |
| `AzurAssistant/assets/commissions/crisis-result-complete-public.png` | 危机讨伐结算页的「完成委托」文字，与右下角「返回」共同确认结算完成 |
| `AzurAssistant/assets/daily/mail-empty-banner.png`、`menu-party.png`、`reward-dismiss-faint.png` | 空邮件横幅、主菜单编队标记、普通奖励关闭提示 |
| `AzurAssistant/assets/realtime/home-leave-label.png` | 识别家园探索画面右侧的「离开家园」操作提示 |
| `AzurAssistant/Features/Shop/ShopTask.cs`、`ShopRecognizer.cs` | 每日商城免费领取流程、分类选中状态、首格与详情页验证 |
| `AzurAssistant/assets/shop/` | 商城分类、首格商品、免费详情、奖励提示模板及识别清单 |
| `AzurAssistant/assets/realtime/blocked-read.png`、`blocked-town-shop.png` | 阅读和城镇商店的选中行屏蔽模板 |
| `AzurAssistant/assets/realtime/mining-icon.png`、`logging-icon.png`、`dialogue-prompt-confirm-hover.png` | 挖矿、砍树交互和剧情确认按钮高亮状态 |
| `AzurAssistant/assets/realtime/blocked-logging-camp.png`、`blocked-element-altar.png`、`blocked-hidden-shadow.png`、`blocked-repair.png` | 选中行右侧的屏蔽名称，包括设备「修复」 |
| `AzurAssistant/assets/realtime/blocked-mount-challenge.png`、`blocked-transformation.png`、`blocked-construction.png` | 坐骑挑战、奇波变身、建造的选中行名称 |
| `AzurAssistant/assets/realtime/blocked-starlight-mine.png`、`blocked-quarry.png`、`blocked-gathering-hut.png`、`blocked-investigate.png` | 星辉矿场、采石场、采集小屋、调查的选中行名称 |
| `AzurAssistant/assets/battle-pass/title.png`、`close.png` | 通行证标题文字与右上角关闭按钮 |
| `AzurAssistant/assets/activity/row-go-day.png`、`row-go-highlighted.png`、`row-locked.png`、`zero-activity.png` | 日常前往按钮、未解锁任务和零活跃度标记 |
| `AzurAssistant/assets/commissions/team-list-hover.png` | 委托准备页中鼠标停留时的「队伍列表」文字，与开始按钮共同确认页面 |
| `AzurAssistant/assets/commissions/catalog-back.png`、`catalog-back-light.png` | 委托副本选择页右上角返回箭头的深、浅背景模板 |
| `AzurAssistant/assets/commissions/hub-close.png` | 委托总览右上角关闭按钮 |
| `AzurAssistant/assets/commissions/reward-claim-and-settle.png`、`reward-skip-and-settle.png` | 委托领取或跳过并结算的完整按钮文字 |
| `AzurAssistant/assets/start-journey/reward-dismiss-faint.png`、`login-card-edge-future-variant-4.png` 至 `login-card-edge-future-variant-8.png` | 入场奖励关闭提示和签到后续卡片边缘 |
| `AzurAssistant/assets/` | 程序图标、裁剪识别模板及导航特征索引 |
| `AzurAssistant/models/` | 本地 OCR 模型、清单和字典 |
| `LICENSE` | 蔚蓝助手程序代码与构建脚本的 GNU GPL 第 3 版许可全文 |
| `AzurAssistant/licenses/` | 第三方许可与通知 |
| `AzurAssistant/update-source.json` | GitHub 更新仓库配置 |
| `AzurAssistant/release.json` | 开发版本标识；打包时在临时副本生成正式构建号 |
| `scripts/Build-Release.ps1` | 完整程序与 EXE 安装包构建入口 |
| `installer/` | 安装、覆盖升级和卸载脚本及中文翻译 |
| `global.json`、`NuGet.config` | SDK 版本与依赖还原配置 |
| `.gitignore` | 排除构建输出、缓存和用户数据 |
| `.gitattributes` | 保留 GNU 许可证和 .NET 第三方通知的原始换行格式 |

从源码构建或安装时需保留识别模板、导航特征索引和 OCR 模型。助手用索引匹配当前游戏画面，得到地图坐标。

## 社区贡献

- **o柴v柴o** 负责蔚蓝助手的开发与维护。[GitHub 主页](https://github.com/1848639272)
- **jinitaimei-114514** 贡献了游戏画面缩放与坐标映射，使助手能以统一尺寸识别画面，再将点击位置映射回游戏窗口。[原始贡献](https://github.com/1848639272/AzurAssistant/commit/a03e3fba6ab6165f0bb8767b2dbe6d9ffc8b145e)

## 许可

[![GNU GPLv3](https://www.gnu.org/graphics/gplv3-127x51.png)](LICENSE)

Copyright © 2026 o柴v柴o、jinitaimei-114514（各自贡献的部分）。

蔚蓝助手的自有程序代码与构建脚本采用 GNU 通用公共许可证第 3 版（`GPL-3.0-only`），包括 jinitaimei-114514 贡献的画面缩放与坐标映射代码。你可以按该许可证使用、研究、修改和再分发这些代码。分发二进制版本时，请依照第 6 条提供对应源码。安装后可在程序目录打开 `LICENSE` 阅读英文原文。[许可证全文](LICENSE)

随程序分发的第三方组件与 OCR 模型沿用各自的许可和版权声明。安装包内的 .NET 运行时使用 MIT 许可，正文和该版本的第三方通知分别见 `DotNetRuntime-MIT.txt` 与 `DotNetRuntime-ThirdPartyNotices.txt`。[第三方许可与通知](AzurAssistant/licenses/)

《蓝色星原：旅谣》的游戏画面、地图和标识归各自权利人所有。游戏截图裁剪、地图数据及派生的导航特征须按相应权利人的授权使用。
