参考实现存档（AGPL-3.0）
=========================
本目录保存移植参考用的原始实现，来源：
  https://github.com/longer-sausage/OK-AzurPromilia  （AGPL-3.0）

文件说明：
  rhythm_detector.py          花瓣音游识别（颜色/形状/图例校准）
  jenga_detector.py           祖赞卡之梯识别（灰度高通模板 + 鸟/承接面）
  test_auto_rhythm_task.py    原项目的音游任务测试（了解期望行为用）
  test_auto_jenga_task.py     原项目的卡之梯任务测试
  LICENSE                     原始项目的 AGPL-3.0 全文

移植状态：
  已完成  Runtime/RhythmDetector.cs —— 布局校准（图例优先 + 搜索回退）、颜色掩膜、
          圆形/长条音符检测、轨道带；对应常量（HSL 范围、判定线 587、图例 678/726/774）一致
  未移植  _covered_body / _colored_hold / _tail_cap / _glyph_heads（命中特效遮挡时的补正）
          jenga_detector 整体（下一步，需要 assets/jenga 下的模板图）

许可：本项目因此采用 AGPL-3.0（见仓库根目录 LICENSE、README）。分发或通过网络提供服务时，
需一并提供完整源码。