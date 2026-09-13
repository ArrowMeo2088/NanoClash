# Config

订阅与节点配置：

| 文件 | 说明 |
|------|------|
| `ProfileStore` / `ProfileModels` | `%APPDATA%\ArrorMeo\NanoClash\config.yaml`；Add/Update/Remove（删后选下一项） |
| `ContentStore` | 同目录 `data/` SHA-256 落盘；`TryRead` 仅接受 64 位 hex，防路径穿越 |
| `SubscriptionClient` | 直连拉取 + `SubscriptionUserInfo` / `SubscriptionFetchResult` |
| `NodeCatalog` | 多格式探测 |
| `NodeFilter` | 仅保留 trojan 与合规 vless |
| `ProxyYamlLoader` | Clash `proxies:`（含 flow-style `- { ... }`） |
| `ShareLinkParser` / `ShareLinkBuilder` | URI ↔ `ProxyNode` |
