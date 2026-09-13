# Tun

Windows **System TCP** 增强模式（对照 sing-tun System stack）。非 Windows 上 `TunService.StartAsync` 抛平台不支持。

| 路径 | 说明 |
|------|------|
| `TunService.cs` | 启停编排；Stop 时在锁外执行 netsh/route；节点切换更新 /32 |
| `Windows/` | WinTUN（`WintunBootstrap` 解压内嵌 DLL）、物理口探测、路由、`TunOsRecovery`（崩溃残留）、防火墙（失败硬失败） |
| `SystemStack/` | TcpNat（Destination 不可变）+ Listen/NAT/Accept → RuleDb → Proxy/Direct；首包 `TlsHelloCoalesce` |
| `Ip/` | IPv4/TCP 头改写与校验和 |

防回环：物理口探测 → 节点 `/32` → Listen → split default → **再** DNS 劫持到 `198.18.0.2`（失败则整段回滚）。UDP/TCP 53 Fake-IP；公网 DoH/DoT 丢弃。未映射 Fake-IP 丢弃；Direct 再 DoH。

启动（`Program`）：无状态删除 split 路由 + 若物理 DNS 仍为 Fake-IP 则 DHCP 恢复。

停止：先拆 split 路由并 DHCP 恢复 DNS（短超时，避免 UI 卡死），再 dispose 栈/适配器。
