# 项目初始化

历史记录：仓库最初搭建时的约定，现行说明以 [`README.md`](README.md) / [`architecture.md`](architecture.md) / [`build.md`](build.md) 为准。

- 创建 `NanoClash.slnx` 单项目解决方案，源码位于 `Src/`。
- 配置 `Directory.Build.props`（`Build/bin`、`Build/obj`）、`nuget.config`（`Packages/`）。
- 提供 `build.bat` / `build.sh`：`restore` / `build` / `publish` / `clean`。
- 各目录含 `README.md`；`.cursor/rules/nanoclash.mdc` 固化约定。
