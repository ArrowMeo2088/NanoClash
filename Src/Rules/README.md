# Rules

CFWR 域名分流库（`Res/Rules.bin`）。

- 构建：以 `NanoClash.Rules.bin` 嵌入程序集（见 `NanoClash.csproj`）
- 运行：`RuleDb.LoadDefault()` 优先读旁路 `Rules.bin`，否则 `GetManifestResourceStream` 直接解析，不写临时文件
