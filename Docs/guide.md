# 使用指南

NanoClash 是面向桌面的轻量代理客户端：本地 HTTP 入站、规则分流、订阅节点、可选系统代理与 Windows 增强（TUN）模式。

## 平台能力

| OS | GUI | 系统代理（代理模式） | 增强模式（TUN） | 本地默认发布 |
|----|-----|----------------------|-----------------|--------------|
| Windows 10/11 | Win32 + Direct2D | WinINET | 支持（需管理员） | `build.bat` → `win-x64` |
| macOS | MacOS + MewVG | `networksetup`（Web / Secure Web） | 不支持（UI 灰显「不可用」） | `./build.sh` → `osx-arm64` |
| Linux | X11 / XWayland + MewVG | 仅 GNOME `gsettings`；其它桌面需手设 | 不支持 | `./build.sh` → `linux-x64` |

代理模式与增强模式**互斥**：开一端会关另一端。退出或关窗时会还原系统代理，并停止 TUN。

CI：`.github/workflows/publish.yml` 在对应 runner 上分别产出 `win-x64` / `linux-x64` / `osx-arm64`（不交叉 AOT）。构建细节见 [`build.md`](build.md)。

## 启动后做什么

1. 用 `build.bat` 或 `./build.sh` 发布，运行 `Publish` 下的主程序。
2. 「添加订阅」导入云端 URL 或本地节点文件。
3. 点选节点；需要时开「代理模式」或（Windows）「增强模式」。
4. 用浏览器或 curl 验证：

```bat
curl -x http://127.0.0.1:7887 https://www.google.com/ -v -o NUL
```

入站地址固定为 `127.0.0.1:7887`（仅 HTTP CONNECT / 明文 HTTP 转发，无 SOCKS）。

## 用户数据目录

所有订阅索引与正文、以及 Windows 下解压的 `wintun.dll`，都在：

| 平台 | 路径 |
|------|------|
| Windows | `%APPDATA%\ArrorMeo\NanoClash` |
| Linux | `~/.config/ArrorMeo/NanoClash`（ApplicationData） |
| macOS | `~/Library/Application Support/ArrorMeo/NanoClash` |

| 相对路径 | 用途 |
|----------|------|
| `config.yaml` | 配置列表 + `default` 名称；云端条目可含 used / total / expire |
| `data/{sha256}` | 订阅或本地文件的原始正文（内容寻址） |
| `wintun.dll` | 从程序内嵌资源解压（仅 Windows；内容变化时覆盖） |

若用户目录尚无 `config.yaml`，而**可执行文件旁**仍有旧版 `config.yaml` + `data/`，启动时会**一次性迁入**（不迁移旁路 `Proxy.yaml`；程序也不再读取它）。

旁路 `proxy-undo.json`（WinINET 崩溃恢复快照）仍写在可执行文件目录。程序**不写** `log.txt`。

## 订阅

### 云端

- 直连 GET，`User-Agent: clash.meta`
- 解析 `subscription-userinfo`（缺字段容错）→ 顶栏显示用量与到期
- 「更新订阅」重新拉取并替换内容哈希；无引用旧 blob 会删除

### 本地

- 选择文件导入；不显示流量/到期，无「更新订阅」
- 名称可留空：云端优先 Content-Disposition / profile-title / URL 段，本地用文件名；冲突则 `名称#n`

### 删除

「删除」移除**当前**配置：选中下一项；若删的是最后一项则选第一项；列表空则清空节点。

### 节点格式

`NodeCatalog` 按**内容**探测（不看扩展名）：Clash `proxies:`（含 flow-style `- { ... }`）、Base64、URI 列表、V2Ray/Xray / sing-box `outbounds`。只抽取节点，**入库条件**：

- `trojan`；或 `vless` 且 `security` ∈ {none, tls, reality}，`flow` 为空或 `xtls-rprx-vision`
- 传输：`tcp` / `ws`（剔除 grpc / xhttp / h2）

## 代理模式

开启后将本机 HTTP(S) 系统代理指到 `127.0.0.1:7887`；失败则回滚且 UI 复位。关闭或退出时恢复。

| 平台 | 实现 |
|------|------|
| Windows | WinINET；改前写入旁路 `proxy-undo.json`；下次启动若仍指向本机 `:7887` 则自动还原 |
| macOS | `networksetup`（启用中的网络服务） |
| Linux GNOME | `gsettings` `org.gnome.system.proxy` |
| 其它 Linux | 不支持自动开关 → 失败并 UI 回滚；请手设或换 GNOME |

## 增强模式（Windows TUN）

- 清单 `requireAdministrator`；`wintun.dll` 内嵌，启动解压到用户数据目录后 `LoadLibrary`
- 适配器名 `NanoClash`，地址 `172.19.0.1/30`；Listen + 包 NAT（System TCP，IPv4 TCP）
- 规则：`RuleDb` → Proxy（当前节点）/ Direct / Reject；非 DNS 的 UDP 丢弃
- **防回环**：探测物理默认网关；节点 `/32` 走物理口；再装 split default；出站 bind + `IP_UNICAST_IF`
- **域名分流**：路由就绪后 DNS 改为 `198.18.0.2`（失败则整段回滚）；劫持 UDP/TCP 53 为 Fake-IP（池从 `198.18.0.4` 起，LRU）；丢弃常见公网 DoH/DoT；未映射 Fake-IP 丢弃；Direct 再经 DoH 解真 IP
- **崩溃恢复**：启动时无状态清 split 路由；物理 DNS 仍为 Fake-IP 则改回 DHCP
- 切换节点：先更新节点 `/32`（串行），再换出站并中断连接

Linux/macOS 上增强开关灰显，文案「不可用」。

## DNS 行为摘要

| 场景 | 解析 |
|------|------|
| Reject | 不解析 |
| Direct（HTTP 入站） | 系统 DNS |
| Direct（TUN Fake-IP） | DoH 解真 IP 后再直连 |
| 节点 `server` | DoH **仅 A（IPv4）** |
| Proxy 目标主机 | 隧道侧解析（优先域名） |

应用内 DoH：主 `https://223.5.5.5/resolve`（AliDNS），备 DNSPod。

## 健康检查

- 并发上限 30；探测 `http://www.google.com/generate_204`，超时 3000ms
- **不经**入站 CONNECT（直连出站测延迟）
- 检查中不重排；结束后按延迟升序

## GUI 布局

- 第 1 行：添加订阅 / 配置下拉 / 更新订阅（仅云端）/ 删除 + 云端用量与到期
- 第 2 行：健康检查 / 代理·增强模式（开绿关红）+ `传输速度 N KB/s`
- 节点区：三列卡片；主窗与添加订阅对话框固定尺寸
- 窗口图标：嵌入 `NanoClash.icon.ico`（源文件 `Res/icon/icon.ico`）

## CONNECT 入站要点

- leftover / early data 在 `200` + Flush 后写入远端
- HTTP 路径会聚合完整 TLS ClientHello 再写 Vision（TUN 路径不在此聚合，避免分片误判）
- 出站走内嵌 ProxyNet；`DirectNetwork` 强制直连，避免系统代理回环

## 节点切换

点选节点会 `SetCurrent`，并**中断所有**入站隧道（含 Direct）。增强开启时先装妥节点 `/32` 再切换（失败则回滚选中）。换订阅 / 重载同样断开现有连接；浏览器需重连后走新节点。

## 规则旁路

默认使用嵌入的 `Rules.bin`。若可执行文件旁存在同名 `Rules.bin`，则优先读该文件（开发替换规则用）。
