# Clash.IO

Shared stream helpers used by both HTTP inbound and TUN:

| File | Role |
|------|------|
| `DirectDial.cs` | Happy-Eyeballs TCP dial (system DNS) |
| `Relay.cs` | Bidirectional copy + `TrafficCounters` |
| `TlsHelloCoalesce.cs` | Aggregate ClientHello before Vision first write |

Depends on: `Clash.Net`, `Clash.Rules`, `Clash.Utils`.
Does not depend on Inbound or Tun.
