# 构建与发布

## 工具

- `dotnet`（.NET 10 SDK）
- Windows：`build.bat`；Linux/macOS：`build.sh`
- 可选：`upx`（在 PATH 中；AOT 同步到 `Publish/` 后尝试 `--best --lzma`；CI 会安装 UPX）
- 可选：MSBuild（Visual Studio Build Tools）、`gh`（CI/PR）

NuGet 包落在仓库 `Packages/`（见 `nuget.config`）。编译输出在 `Build/`（`bin` / `obj` / 中间 `pub/`）。

## 常用命令

| 命令 | 作用 |
|------|------|
| `build.bat` / `./build.sh` | 默认等同 `publish` |
| `build.bat publish [RID]` | NativeAOT 发布 → `Build/pub/`，再同步主程序到 `Publish/`，并尝试 UPX |
| `build.bat build` | 仅托管 Release 编译（非 AOT） |
| `build.bat restore` | 还原 NuGet |
| `build.bat clean` | 清理 `Build/` 与 `Publish/` 中的应用二进制 |

默认 RID：Windows `win-x64`，Linux `linux-x64`，macOS `osx-arm64`。AOT 须在**目标 OS** 上构建（不交叉）。

示例：

```bat
build.bat publish win-x64
```

```bash
./build.sh publish osx-arm64
```

本地最终产物为**扁平** `Publish/`（无 RID 子目录）。CI 产物在 `artifacts/<rid>/`。

## 发布形态

- `OutputType=WinExe`（Windows 无控制台窗）
- NativeAOT + full Trim
- 体积向：`OptimizationPreference=Size`、折叠相同方法体、InvariantGlobalization、剥离符号；关闭 debugger / stack / EventSource 等开关
- 发布后可选 UPX 压缩主程序（`--best --lzma`；无 `upx` 或失败则保留未压缩产物）
- **不**附带 PDB；**不**旁路发布 `Rules.bin` / `wintun.dll` / `Proxy.yaml`

### 嵌入资源

| 源文件 | 逻辑名 / 用法 |
|--------|----------------|
| `Res/Rules.bin.gz` | `NanoClash.Rules.bin.gz` — 预压缩嵌入；运行时解压到用户数据目录再加载 |
| `Res/icon/icon.ico` | PE `ApplicationIcon` + `NanoClash.icon.ico` 窗口图标 |
| `Res/wintun/wintun.dll` | `NanoClash.wintun.dll` — 仅 Windows 目标嵌入；启动解压到用户数据目录 |

`Res/Proxy.yaml` 仅本地样例（gitignore），**不会**复制到 `Publish/`，运行时也不再检测旁路文件。订阅正文里的 Clash YAML 仍由 `ProxyYamlLoader` 解析。

### Windows UAC

`Src/app.manifest` 为 `requireAdministrator`（增强模式改路由 / 装适配器需要）。链接时对 win RID 使用 `/MANIFESTUAC:NO`，避免与清单冲突。

## CI

工作流：`.github/workflows/publish.yml`。安装 UPX 到 PATH 后，用 `build.bat` / `build.sh` 发布对应 RID（含 UPX），再打包 `Publish/` 并上传 artifacts。

## 验证建议

1. `build.bat publish` 成功，`Publish/` 仅有主程序（及可选残留用户文件，非构建产出）。
2. 启动后用户目录出现 `Rules.bin` / `config.yaml` / `data/`（导入订阅后）；Windows 出现 `wintun.dll`。
3. 代理模式或手动代理后：`curl -x http://127.0.0.1:7887 https://www.google.com/`。
4. Windows 增强模式：管理员启动 → 开增强 → 域名按规则分流；关闭后路由与 DNS 恢复。
