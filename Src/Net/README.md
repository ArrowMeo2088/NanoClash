# Net

进程级网络策略：强制直连、忽略系统与环境代理，避免本机 `:7887` 回环。

| 类型 | 说明 |
|------|------|
| `DirectNetwork` | `HttpClient.DefaultProxy` / 环境变量清零 |
| `InterfaceBinder` | 增强模式开启后，出站 socket bind 物理口 + `IP_UNICAST_IF` |
