# 设计文档

## 架构

- **框架**：WPF (Windows Presentation Foundation)
- **目标框架**：.NET 6.0-windows
- **UI 层**：XAML，支持透明背景 + 点击穿透

## 状态管理

- **独立状态**：量角器和直尺各自维护独立的坐标（`_pVertex/_pEnd1/_pEnd2` vs `_rEnd1/_rEnd2`）
- **持久化**：
  - 保存位置：`%AppData%\ScreenProtractor\state.json`
  - 内容：当前模式、量角器三点、直尺两点
  - 自动保存触发：拖动结束、模式切换、重置位置
  - 容错：读写失败自动回退默认值

## 点击穿透实现

- Win32 扩展窗口样式 `WS_EX_TRANSPARENT`（鼠标事件穿透到下层窗口）
- 定时器（30ms 轮询）检测光标位置：
  - 若靠近控制点或在直尺内部 → 临时关闭穿透，允许拖动
  - 拖动中 → 保持交互式
  - 其他位置 → 重新开启穿透

## 物理测量

- **DPI 获取**：
  - `GetDpiForMonitor` 查询显示器两个 DPI 值：
    - `MDT_EFFECTIVE_DPI`：系统缩放比例（96 DPI 为基准）
    - `MDT_RAW_DPI`：显示器 EDID 上报的物理像素密度
  - 换算公式：`DIP × (有效DPI/96) = 物理像素 ÷ 原始DPI = 英寸 × 2.54 = 厘米`

## 竖直防抖（直尺模式）

- 用迟滞状态 `_rulerFlip` 记住刻度朝向
- 法向量 Y 分量双门限：
  - `Y > +0.20` → 刻度朝下（不翻）
  - `Y < -0.20` → 翻转
  - `|Y| ≤ 0.20` → 保持上次方向
- 避免接近垂直时法向频繁翻转导致视觉抖动

## 文件结构

```
.
├── App.xaml              # 应用入口 UI 定义
├── App.xaml.cs           # 应用逻辑、托盘管理
├── OverlayWindow.xaml    # 工具浮窗 UI
├── OverlayWindow.xaml.cs # 工具核心逻辑
├── Protractor.csproj     # 项目配置
├── Protractor.sln        # 解决方案文件
├── app.manifest          # Windows 清单（DPI 感知、工具窗口）
├── CHANGELOG.md          # 版本历史
└── README.md             # 本文件
```
