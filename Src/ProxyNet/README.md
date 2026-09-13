# ProxyNet

内嵌出站协议栈。命名空间与目录对齐：`Clash.ProxyNet.{Vless,Trojan,Vision,Reality}`；根命名空间保留 Options / Transport / Crypto / `ProxyConnect`。

| 路径 | 说明 |
|------|------|
| `ProxyConnect.cs` | 按 `ProxyNode` 拨号入口 |
| `Vless/VlessProtocol.cs` | Dialer + Helper（合并） |
| `Trojan/TrojanProtocol.cs` | Dialer + Helper（合并） |
| `Vision/` | `xtls-rprx-vision`；上行 Continue→End 后仍写 REALITY；下行 Direct 只切读到 raw TCP |
| `Reality/` | REALITY TLS 1.3（ClientHello **非**浏览器指纹；`fp` 仅 Warn） |
| `Transport/` | tcp / ws / httpupgrade |
| `Crypto/` | UUID、SHA224 等 |

HTTP / TUN 首包依赖 `Clash.IO.TlsHelloCoalesce`。
