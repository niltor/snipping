# Windows 截图功能产品设计与实现方案

本文以当前仓库实现为基线，区分正式运行链路、验收目标和后续规划。当前应用是 WinForms 桌面程序，不是 WinUI 3 应用。

## 1. 目标与范围

目标是提供低延迟、隐私优先的 Windows 截图工具，覆盖：

- 全局快捷键或托盘入口启动截图；
- 屏幕选区、智能选区和多显示器坐标处理；
- 矩形、椭圆、箭头、直线、文字、高亮、马赛克和自由画笔标注；
- PNG/JPEG 保存、剪贴板复制、置顶贴图和 Windows OCR；
- 快捷键、保存目录、主题、语言、OCR 偏好和开机自启设置。

当前设计目标：截图到编辑窗口可见 P50 < 200ms、P95 < 400ms；导出成功率 > 99.5%；全局热键成功率 > 99.9%；8 小时运行无内存泄漏级增长；4K 单屏和双屏可稳定使用。仓库当前没有这些指标的实测记录，目标不应当当作已达成结果。

## 2. 正式 WinForms/GDI 运行链路

```text
Program.Main
  -> 单实例互斥锁 + PerMonitorV2 高 DPI + WinForms 初始化
  -> Application.Run(SnippingApplicationContext)
  -> 加载 %LocalAppData%\\Snipping\\settings.ini
  -> NotifyIcon / RegisterHotKey
  -> 托盘双击、托盘“立即截图”或 Ctrl+Shift+S
  -> DesktopSnippingOverlayForm.ShowDialog
  -> ShowDialog 前: GDI+ Graphics.CopyFromScreen(virtual screen)
  -> 智能选区或自由拖拽
  -> 选区确认、移动/缩放、标注、OCR
  -> 保存 PNG/JPEG、复制剪贴板、置顶或取消
```

### 2.1 当前捕获实现

- `Program.cs` 的正式入口创建 `SnippingApplicationContext`，不会创建 `Form1`。
- `SnippingApplicationContext.StartCapture()` 创建覆盖窗体，在显示前准备底图，然后以模态方式显示。
- `StartCapture()` 创建覆盖整个 `SystemInformation.VirtualScreen` 的 32 位 ARGB 位图并同步调用 `Graphics.CopyFromScreen`；`OnShown` 仅作为底图未准备时的兜底。
- 覆盖层保留原始屏幕位图；选区、标注、OCR 高亮和工具栏作为绘制层叠加。导出时重新从原始位图裁剪选区并回放标注。
- 选区确认后的工具栏组优先放在选区下方；下方空间不足时翻到上方；上下都不足时放入选区内部，并对横纵坐标做虚拟屏幕边界修正。主工具栏和增强选项栏按一个整体计算高度。
- 当前正式流程默认先进入区域选择。`SnippingSettings.DefaultCaptureMode` 虽然存在且默认是 `Region`，但尚未接入 `SnippingApplicationContext` 的入口分派。
- `Form1` 中另有 `CopyFromScreen`、活动窗口和全屏捕获代码，但它不是当前正式运行链路。

### 2.2 与 Desktop Duplication 的边界

Desktop Duplication 只属于后续捕获引擎规划。当前代码中没有 `IDXGIOutputDuplication`、帧轮询、GPU 纹理复制或 Desktop Duplication 回退实现，因此当前版本不能描述为“Desktop Duplication 首选、GDI 回退”，也不能宣称已有性能降级模式。

后续接入时应通过独立捕获抽象提供统一结果：像素缓冲、尺寸、DPI、显示器标识和时间戳。建议策略是优先使用 Desktop Duplication，遇到不支持、设备丢失、锁屏/安全桌面或初始化失败时回退 GDI，并仅在实际发生回退时显示降级提示。现有 `ShowPerformanceDegradeTip=true` 只是持久化设置字段，当前没有消费逻辑。

## 3. 模块职责

| 模块 | 当前职责 |
|---|---|
| `Program` / `SnippingApplicationContext` | 单实例、托盘、全局热键、设置加载、截图会话生命周期 |
| `DesktopSnippingOverlayForm` | 屏幕底图、选区交互、标注工具栏、OCR、保存/复制/置顶动作 |
| `SmartSelectionDetector` | 在截图会话开始时建立 Win32 窗口/子 HWND 快照；鼠标热路径做本地矩形命中，宿主区域再按根窗口缓存 UI Automation 和局部视觉候选，失败时回退窗口边界 |
| `Annotations` | 标注对象的 GDI+ 绘制，包括文字、几何图形、马赛克和自由画笔 |
| `WindowsOcrService` | Windows OCR 适配、语言选择、结果和错误信息 |
| `ExportManager` | 按前缀、时间和格式生成文件名并异步写入文件 |
| `SettingsManager` | INI 解析、默认值、范围限制和版本迁移 |

## 4. 智能选区性能优化方案

### 4.1 已实现的保护措施

当前智能选区在覆盖窗体选择阶段工作：

1. 鼠标位置按 6px 网格去重，使用 30ms 静默期和 90ms 最大等待，避免连续移动时无限等待尾部防抖。
2. 覆盖层显示前先使用 `EnumWindows`/`EnumChildWindows` 建立可见顶层窗口、子 HWND 矩形、类名和顺序快照；鼠标移动只在内存候选列表中命中。
3. 使用一个专用后台 MTA worker，只保留最新请求，同时最多运行一个 UI Automation/视觉精细任务；原生叶子 HWND 候选不进入精细查询。
4. 面积超过根窗口 55% 的 HWND 宿主只作为临时候选，鼠标停留约 55ms 后才进入精确阶段；候选来源包含 `NativeHwnd`、`Automation`、`Visual` 和 `WindowFallback`，并携带置信度。
5. 精确阶段按根窗口缓存 UI Automation Raw View 属性，最多访问 2048 个元素、预算约 120ms；后续鼠标移动只在快照中命中，非空快照有效期约 2 秒，空快照有效期约 750ms。
6. UIA 仍只有容器时，在当前 HWND 容器内执行多扫描线、连续边缘支持度和面积比例分析；视觉候选低于可靠叶子控件，置信度不足时不替换窗口/容器候选。
7. 候选质量比较禁止较大容器或整窗回退覆盖已命中的可靠叶子；以请求版本、检测屏幕点和当前候选质量校验结果，过期结果不会回写 UI。
8. 候选变化只刷新旧/新矩形并集，`OnPaint` 只绘制无效区域，UIA 异常时保留自由拖拽；诊断记录快照构建、UIA 缓存、视觉扫描、候选数量和回退次数。

### 4.2 后续优化步骤

- **捕获引擎**：评估 Desktop Duplication/Windows Graphics Capture，保持 GDI 回退和统一坐标契约。
- **可观测性扩展**：把当前内部诊断指标接入按需日志或性能面板，记录 P50/P95 与窗口复杂度关联。
- **UIA 提供程序适配**：针对浏览器、Office、管理员窗口和自绘窗口补充 provider-specific 策略；当前精细化仍受目标应用可访问性树质量限制。

优化顺序应先保证指针移动时 UI 不阻塞，再提高候选精度；任何查询超时都必须保留手动拖拽路径。

## 5. 标注增强与付费能力预留

### 5.1 当前标注能力

覆盖编辑器当前提供矩形、椭圆、箭头、直线、文字、高亮、马赛克和自由画笔；支持 5 种颜色、文字编辑/拖动/缩放、选区移动/缩放和最近一次撤销。标注在内存中作为对象列表保存，导出时回放到裁剪后的位图；当前没有重做、会话序列化或二次打开编辑。

当前版本已加入按工具保存于单次截图会话的增强选项：矩形/椭圆支持 10–100 的透明度（默认 100）及边框/填充（默认边框）；箭头支持单/双箭头（默认单箭头）；直线支持实线/虚线（默认实线）；文字支持 10–100 像素字号（默认 18）、粗体和斜体（默认关闭）；马赛克画笔范围支持 5–50（默认 20）。选项栏不显示当前工具名称，按内容自适应宽度，模式按钮使用图标并通过悬停提示说明。这些选项不写入用户设置，且不改变高亮和自由画笔的现有工具模型。

### 5.2 增强方向

在本次计划的选项基础上，后续可按以下顺序增强：

- 将标注操作统一为命令，补充重做、批量选择、删除、层级和快捷键提示；
- 为笔画、文字和马赛克提供粗细、透明度、字体及马赛克块大小设置；
- 引入可版本化的会话模型，保存原始底图引用、DPI、标注集合和创建时间；
- 将合成和大图导出移出 UI 线程，并对超大位图限制历史缓存。

增强选项由 `FeatureEntitlements.AnnotationEnhancementsEnabled` 控制，默认值为 `true`，属于运行时能力开关，不写入 `settings.ini`。未来接入授权服务时，应由授权层提供该能力状态；当前开关不等同于已完成的商店购买校验。

### 5.3 Microsoft Store 付费能力占位

设置窗口的“高级”标签已经预留 Microsoft Store 付费版区域，但当前明确显示为未开放。可以预留能力边界，不应在当前版本宣称已实现或启用付费功能。候选方向包括高级标注样式、批量导出、会话恢复和扩展自动化，但正式开关、授权校验、购买状态和离线策略需在单独设计后确定。

## 6. 设置与默认值

设置文件为 `%LocalAppData%\\Snipping\\settings.ini`，当前版本为 `1`。核心默认值如下：

| 配置 | 默认值 |
|---|---:|
| 全局热键 | `Ctrl+Shift+S` |
| 默认捕获模式 | `Region`（当前正式入口尚未消费） |
| 默认导出格式 | `Png` |
| JPEG 质量 | `90` |
| 任务栏显示编辑器 | `false` |
| 保存目录 | `%UserProfile%\\Pictures\\Snipping` |
| 文件名前缀 | `snip` |
| 置顶快捷键 / 不透明度 | `Ctrl+T` / `90` |
| 主题 / 语言 | `System` / `zh-CN` |
| OCR 偏好语言 | 空（自动选择） |
| 开机自启 | `false` |
| 性能降级提示字段 | `true`（当前无对应捕获降级逻辑） |

JPEG 质量和置顶不透明度会分别限制在 1–100；未知主题和语言会回退到 `System` 与 `zh-CN`。

## 7. 异常、隐私与验收

- 当前截图和标注在本地内存中处理；OCR 使用 Windows 提供的 OCR 能力。
- UI Automation 无法访问提升权限或自绘窗口时，回退到窗口候选或自由选区，不阻断截图。
- 剪贴板或文件写入失败时，应保留可诊断的错误信息；当前正式覆盖窗体的保存/复制错误处理仍需继续补齐统一提示。
- 多显示器和高 DPI 需要在真实 Windows 环境进行手工验收；仓库现有测试不覆盖 WinForms、GDI、OCR 语言包、UI Automation、剪贴板或实机性能。
- 当前 `tests/Snipping.Core.Tests` 为 `net10.0` xUnit 单元测试，另有 Windows 目标的 `tests/Snipping.App.Tests` 覆盖标注渲染和 worker 调度；它们仍不能替代多显示器、UI Automation provider 和真实 P95 性能验收。
