# Rules

CFWR 域名分流库（仓库只保留预压缩的 `Res/Rules.bin.gz`）。

- 构建：以 `NanoClash.Rules.bin.gz` 直接嵌入程序集（见 `NanoClash.csproj`）
- 运行：`RuleDb.LoadDefault()` 优先读 exe 旁旁路 `Rules.bin`；否则 gunzip 到用户数据目录 `Rules.bin`（内容变化时覆盖）再加载
- 用户路径：Windows `%APPDATA%\ArrowMeo\NanoClash\Rules.bin`；Linux `~/.config/ArrowMeo/NanoClash`；macOS `~/Library/Application Support/ArrowMeo/NanoClash`
