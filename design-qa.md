# 磁盘清理助手设计 QA

## 对照目标

- Source visual truth: `design-reference/stitch-technician-console.png`
- Source structure: `design-reference/stitch-technician-console.html`
- Implementation screenshot: `design-reference/implementation-home-v2-dpi.png`
- Normalized source: `design-reference/source-normalized.png`
- Normalized implementation: `design-reference/implementation-normalized.png`
- Side-by-side evidence: `design-reference/comparison-v2.png`
- State: 应用首次启动，首页，普通权限，未执行扫描。
- CSS/DIP viewport: 860 x 580。
- Source pixels: 512 x 410；其中应用窗口裁切为 344 x 232，并按 860 x 580 归一化。Stitch 原始设计声明尺寸为 860 x 580。
- Implementation pixels: 1075 x 725，Windows DPI 为 120（125%）；按 860 x 580 归一化后比较。

## Full-view comparison evidence

`design-reference/comparison-v2.png` 将 Stitch 目标放在左侧、WPF 实现放在右侧。两侧已经归一化到同一窗口尺寸和首页状态。

整体结构一致：40 DIP 标题栏、约 64 DIP 图标侧栏、260 DIP 磁盘分栏、右侧扫描目标列表，以及底部安全提示和主操作按钮。主内容区、分隔线、留白密度和主按钮位置均与目标一致。

## Focused region comparison evidence

源截图只以约 40% 比例嵌在 Stitch 画布中，放大后细字已模糊，因此没有对不可辨识的小字号像素做虚假精确比较。细节核对改用同屏对照图配合 `stitch-technician-console.html` 中的明确结构和文案：64px 侧栏、260px 左栏、4px 磁盘进度条、扫描目标行、64px 底部操作区。

## Required fidelity surfaces

- Fonts and typography: WPF 使用 Microsoft YaHei UI，英文和数据使用系统字体/Consolas；标题、正文、辅助文字层级与原稿一致。中文渲染清晰，无截断或乱码。
- Spacing and layout rhythm: 默认窗口已调整为 860 x 580；首页隐藏额外页标题和全局状态条，使磁盘区、扫描目标区及底部操作区的纵向比例与原稿一致。
- Colors and visual tokens: 使用雾白工作区、浅蓝灰侧栏、Windows 蓝主操作、珊瑚红磁盘警告和青绿色正常状态；未使用渐变、玻璃拟态或重阴影。
- Image quality and asset fidelity: 目标没有照片或插画。图标由 Windows 自带 Segoe MDL2 Assets 提供，没有使用 emoji、占位图或自绘 SVG。
- Copy and content: 产品名固定为“磁盘清理助手”；保留“扫描不会删除任何文件”，并补充缓存可重建、回收站不自动选择等产品安全信息。

## Comparison history

### Iteration 1

- [P2] 窗口和内容比例偏大：首轮实现为 900 x 620，首页还保留 46 DIP 页标题和 34 DIP 全局状态条，导致双栏主内容被压缩。
- Fix: 默认窗口改为 860 x 580；首页折叠额外页标题和全局状态条，保留其他功能页需要的标题与状态反馈。
- Post-fix evidence: `design-reference/implementation-home-v2-dpi.png` 和 `design-reference/comparison-v2.png`。

### Iteration 2

未发现仍需处理的 P0/P1/P2 差异。

## Findings

- [P3] 原稿标题为占位名称 `SystemMaster`，实现使用产品正式名称“磁盘清理助手”。这是有意的产品约束，不需修复。
- [P3] 原稿高亮“快速清理”，实现首次启动高亮“首页”。这是有意的导航语义；点击“开始快速扫描”后会进入快速清理页。
- [P3] 实现的扫描目标行增加了简短解释和“回收站不自动选择”状态，比原稿信息略多，但符合产品必须说明“究竟是什么、删除影响”的安全原则。

## Primary interaction checks

- 通过 Windows UI Automation 调用并验证“快速清理”“设置”“首页”三个导航入口。
- 每次导航后均确认目标页标题或首页核心内容可见。
- 未触发真实扫描或清理，避免在视觉 QA 中修改用户磁盘状态。
- 快速清理操作已进一步收敛为“开始/重新扫描”“保守选择”“全部选择/取消全选”“清理已选”；四个主按钮统一为 108 px，状态文案变化不会改变按钮宽度，也不会让后三个按钮显得过重。取消扫描仅在扫描期间出现，不再提供“更多操作”菜单。旧截图 `design-reference/implementation-quick-clean-simple.png` 仅作为上一版布局记录。
- 快速清理工具栏增加紧凑的磁盘选择器，默认 Windows 所在盘（通常为 C:），也可以主动选择 D: 或所有本地磁盘；选择器与四个主按钮保持在同一行，不增加额外操作层级。
- 首页磁盘卡采用整卡点击热区：点击只选择对应磁盘并前往快速清理页，不自动开始扫描。当前目标使用浅蓝底、蓝色描边和状态圆点显示，悬停只提供轻量反馈；容量信息仍保持原来的数据密度。ComboBox 统一设置垂直内容居中，底部安全提示的图标和小字也使用固定行高居中对齐。
- 首页底部不再重复放置“开始快速扫描”按钮，改为深度扫盘、大文件、重复文件三个带图标和说明的快捷卡片；卡片具有悬停、按下和键盘焦点反馈，右侧仅保留“只读检查”安全提示。
- 快速扫描进度条使用规则总数作为确定进度，逐条从 0 推进到总数；深度与重复扫描因总量未知，继续显示系统的活动进度动画。
- 权限标识只显示当前事实状态：“普通权限”或“管理员权限”，不再夹带操作说明。
- 权限状态改为带 Windows 风格盾牌的紧凑按钮；普通权限使用灰色未激活状态，管理员权限才显示 Windows 蓝。普通权限时点击会调用系统 UAC，以管理员权限重新启动同一 EXE，取消授权则保留当前窗口。
- 快速扫描完成后会高亮首个候选；身份与路径直接在表格阅读，下方紧凑卡只保留不重复的判断依据、清理影响和建议，列表因此增加约 46 px 可用高度。
- 应用图标采用蓝色工具底板、白色垃圾桶和薄荷色清理星芒；SVG 为设计源，ICO 内含 16–256 px 多尺寸图层，并已嵌入窗口和 EXE。`design-reference/app-icon-exe-preview.png` 是从成品 EXE 实际提取的校验图。

## Implementation checklist

- [x] 使用正式中文产品名。
- [x] 默认窗口保持 860 x 580 的小工具尺寸。
- [x] 首页与 Stitch 双栏结构一致。
- [x] 导航和主操作保持可用。
- [x] 所有扫描结果默认不勾选的功能逻辑保持不变。
- [x] 22 项自动化功能测试通过。

## Follow-up polish

- 可在后续版本为标题栏最大化按钮增加“还原”图标切换；不影响当前功能和视觉验收。

final result: passed
