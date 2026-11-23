蔚蓝助手（AzurAssistant）
========================
《蓝色星原：旅谣》的 Windows 端辅助工具：画面识别、自动操作、养成计算、抽卡分析、数据记账与日常工具。

许可证
------
本项目按 **GNU Affero General Public License v3.0（AGPL-3.0）** 发布，全文见 LICENSE。

之所以采用 AGPL-3.0：本项目的「司凤小游戏」模块（花瓣音游、祖赞卡之梯）在获得授权的前提下，
参考并使用了 [longer-sausage/OK-AzurPromilia](https://github.com/longer-sausage/OK-AzurPromilia)
（同样为 AGPL-3.0）的识别思路与实现细节。AGPL 属于强 copyleft 许可证：分发本程序、或通过网络
提供本程序的功能时，必须同样以 AGPL-3.0 提供完整源码。

第三方组件
----------
- OpenCvSharp4（模板匹配）— Apache-2.0
- RapidOcrNet（OCR）— 见其仓库许可
- OK-AzurPromilia 的小游戏识别思路与测试基准 — AGPL-3.0

数据来源
--------
养成数据与材料图标来自 promilia.wiki 的养成规划器（progression-planner-v2）。
抽卡概率模型参考 OneBST/GGanalysis 的 PityBernoulliModel（按模型用 C# 重写）。

构建与测试
----------
    dotnet build AzurAssistant\AzurAssistant.csproj -c Release -warnaserror
    dotnet run --project tests\RegressionTests -c Release

功能
----
完整功能列表见 docs 目录下的「功能总览.txt」。