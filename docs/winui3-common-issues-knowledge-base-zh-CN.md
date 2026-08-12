# WinUI3 截图工具常见问题与修复知识点（简版）

> 目标：把开发中高频反复出现的问题，沉淀成可复用的排查与修复要点。

## 1) 截图黑屏 / 捕获到全黑帧

### 现象
- 启动截图后背景全黑，或偶发黑屏。

### 常见原因
- 捕获时机过早（窗口切换瞬间）。
- 单一捕获方式在特定环境失败（显卡驱动、叠加层、权限等）。

### 建议修复
- 采用双路径捕获 + 重试：
  - `BitBlt`（含 `CAPTUREBLT`）
  - `CopyFromScreen`
- 增加短间隔重试（如 40ms）与黑帧检测（网格采样像素是否全黑）。
- 成功帧优先返回，最后兜底返回最后一帧或抛出明确错误。

---

## 2) 选区错位（截图区与显示区不一致）

### 现象
- 视觉选中的区域和实际裁剪结果偏移、缩放不一致。

### 根因
- WinUI XAML 交互坐标是 **EPX（有效像素）**；
- 屏幕捕获位图/Win32 窗口坐标是 **物理像素**；
- 二者混用未转换。

### 建议修复
- 使用 `XamlRoot.RasterizationScale` 做 EPX↔物理像素转换。
- 所有裁剪输入统一走转换函数，再做边界 `Clamp`。
- 编辑画布与位图采样要建立映射（尤其马赛克、画笔）。

---

## 3) 无边框窗口仍有 1px 白边/顶边线

### 现象
- 已调用无边框 API，顶部仍偶现细白线。

### 常见原因
- 仅依赖 `SetBorderAndTitleBar(false, false)` 不足以覆盖所有系统样式组合。

### 建议修复
- `OverlappedPresenter.SetBorderAndTitleBar(false, false)` + Win32 样式清理：
  - 去掉 `WS_CAPTION / WS_BORDER / WS_DLGFRAME / WS_THICKFRAME`
  - 设为 `WS_POPUP`
  - `SetWindowPos(..., SWP_FRAMECHANGED)` 刷新非客户区
- 可选：`DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE` 作为辅助。

---

## 4) “透明度”与预期不符

### 现象
- 调了透明度但只是控件变淡，不是相对桌面透明；或整窗透明导致交互观感差。

### 关键区别
- `UIElement.Opacity`：控件级透明（渲染层）。
- `WS_EX_LAYERED + SetLayeredWindowAttributes`：窗口级透明（相对桌面）。

### 建议修复
- 需要“相对桌面透明”时，使用 layered window alpha。
- 透明度参数统一存配置（如 `0~90`），运行时统一应用。

---

## 5) 主题切换引发异常或不生效

### 现象
- 启动时异常；运行时主题更新不完整（标题栏/客户区不同步）。

### 建议修复
- `Application.RequestedTheme` 仅在 `App` 构造早期设置（初始化前）。
- 运行时切换用根元素 `RequestedTheme`（`FrameworkElement.RequestedTheme`）。
- 非客户区（标题栏）用 DWM 属性同步深浅色。

---

## 6) 截图编辑元素越界生效

### 现象
- 矩形/箭头/文字可画出截图区域外。

### 建议修复
- 编辑画布加 `Clip`（矩形几何裁剪）。
- 指针坐标统一 `Clamp` 到画布边界。
- 文本拖动、马赛克采样都做边界限制。

---

## 7) 托盘常驻生命周期被破坏

### 现象
- 执行复制/保存/贴图后弹出设置窗，或误退出。

### 建议修复
- 约定：应用“托盘常驻”，只有托盘“退出”才关闭进程。
- 截图动作完成后：
  - 关闭截图覆盖层
  - 不激活设置窗
  - 设置窗保持隐藏（`SW_HIDE`）

---

## 8) 可操作的最小检查清单

- [ ] 黑屏：双路径捕获 + 重试 + 黑帧检测已启用
- [ ] 坐标：EPX 与物理像素转换统一
- [ ] 边框：Presenter + Win32 样式清理 + FrameChanged
- [ ] 透明：明确是控件透明还是窗口透明
- [ ] 主题：启动设置与运行时切换路径分离
- [ ] 编辑边界：Clip + Clamp + 映射
- [ ] 生命周期：托盘常驻策略一致

---

## 参考关键词（便于后续检索）
- WinUI Window/AppWindow coordinate system
- XamlRoot RasterizationScale
- OverlappedPresenter SetBorderAndTitleBar
- WS_EX_LAYERED / SetLayeredWindowAttributes
- DWM border color / non-client rendering
- WinUI runtime theme switching
