# Gui

主界面与对话框（Aprillz.MewUI）。

| 文件 | 说明 |
|------|------|
| `MainView.cs` | 字段、`CreateWindow`、生命周期 |
| `MainView.Chrome.cs` | 顶栏 / 模式框 / 订阅配额 / 更新·删除（partial） |
| `MainView.Nodes.cs` | 节点卡片网格 / 选中 / 健康检查（partial） |
| `AddProfileDialog.cs` | 添加云端或本地订阅 |

- 主窗与对话框使用 `.Fixed(...)` + `CanMaximize(false)`，不可拉伸
- 增强模式仅 `OperatingSystem.IsWindowsVersionAtLeast(10)`；其它平台灰显「不可用」
- 窗口图标：嵌入资源 `NanoClash.icon.ico`
