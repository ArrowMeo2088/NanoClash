# Dns

| 文件 | 说明 |
|------|------|
| `DohResolver.cs` | 节点 / Direct Fake-IP 真 IP：主 AliDNS `223.5.5.5`，备 DNSPod JSON DoH（仅 A） |
| `FakeIpPool.cs` | `198.18.0.0/16` 双向 host↔IP；保留 `.0–.3`，从 `.4` 分配；满则 LRU 淘汰 |
| `DnsMessage.cs` | 最小 DNS 查询解析与 A / 空应答构造 |
| `DohBlocklist.cs` | 常见公网 DoH/DoT IP；TUN 上丢弃以免绕过 Fake-IP |
