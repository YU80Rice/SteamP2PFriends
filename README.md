# SteamP2PFriends

> 为 Unturned 提供无 U3DS 的便捷 listen-host 联机，同时支持 SteamID P2P 和 IPv4 直连。

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](./LICENSE)
[![Version](https://img.shields.io/badge/version-0.2.3.61--beta.2-blue.svg)](https://github.com/YU80Rice/SteamP2PFriends/releases)
[![Status](https://img.shields.io/badge/status-Beta%20Prerelease-orange.svg)](https://github.com/YU80Rice/SteamP2PFriends/releases)

## 项目简介

SteamP2PFriends 是一个双端 BepInEx 插件。房主可直接从原版单人地图创建多人房间，不需要安装、配置或启动 U3DS。

当前插件提供两条并存的联机路径：

- **SteamID P2P**：客机在原版直连地址栏输入房主个人 SteamID。
- **IPv4 直连**：用于真实局域网、Radmin LAN 等虚拟局域网。客机输入房主 IPv4 和共享端口。

IP 只负责寻址。玩家身份、审批与白名单始终使用 Steam Networking Sockets 握手获取的 SteamID。

## 已实现功能

- 无 U3DS 的 listen-host 开房。
- SteamID P2P 联机，不要求先添加 Steam 好友。
- IPv4 直连，已通过 Radmin LAN 双机测试。
- 房主可设置房间名称、最大玩家数、难度、PVP、作弊权限和死亡保留规则。
- 自动保存上一次启动的房间设置。
- 新玩家进入世界后先进入 30 秒审批隔离：禁止移动、交互、指令和伤害，并保持受保护状态。
- 房主直接在原版玩家列表（默认按 `P` 打开，可重绑）中点击“允许/撤销允许”，不需要额外审批窗口。
- 客机聊天栏以 5 秒间隔显示审批剩余时间。
- 创意工坊地图与缺失内容下载已通过双机测试。
- listen-host 自然刷新物品由房主统一生成，避免主客机地面物品不一致和幽灵物品。
- 运行时 Harmony 注册与兼容性自检；自身关键补丁失效时 fail-closed，第三方补丁按风险分级告警或阻断。

## 安装

1. 房主和所有客机都需要安装相同版本的 BepInEx 和 SteamP2PFriends。
2. 从 [GitHub Releases](https://github.com/YU80Rice/SteamP2PFriends/releases) 下载与双方一致的 `SteamP2PFriends-v<版本>.zip`。
3. 将压缩包解压到 Unturned 游戏根目录。
4. 确认 DLL 最终位于：

```text
Unturned/
└── BepInEx/
    └── plugins/
        └── SteamP2PFriends.dll
```

## 使用方法

### 房主开房

1. 进入原版单人地图选择界面。
2. 点击插件的“多人联机”。
3. 设置玩家数、房间难度、PVP、作弊与死亡保留规则。
4. 复制房主 SteamID，或将房主的 LAN/Radmin IPv4 发送给客机。
5. 进入世界后，按 `P`（原版默认键，可重绑）在玩家列表中审批新玩家。

### 客机通过 SteamID 加入

1. 打开“开始游戏 → 直连”。
2. 在顶部地址栏粘贴房主个人 SteamID。
3. 点击连接。

插件只接管有效的 Steam 个人账户 ID；U3DS Server Code 仍交给原版处理。

### 客机通过 IPv4 加入

1. 打开“开始游戏 → 直连”。
2. 输入房主的局域网或 Radmin IPv4。
3. 端口填写房主实际监听的 UDP 端口（局域网/Radmin 默认 `27016`）。
4. 插件会跳过 U3DS A2S 查询，直接连接该 UDP 端口（单端口语义，query 与 connection 端口相同）。

Windows 防火墙必须允许 Unturned 在相应网络上使用 UDP `27016`。

### SakuraFRP / 公网穿透（诊断候选）

1. 房主在 SakuraFRP 创建 **一条** UDP 隧道：本地 `127.0.0.1:27016` → 远端 UDP 端口 `R`。
2. 客机打开“开始游戏 → 直连”，输入 Sakura 节点分配的**域名**或数值 IPv4，端口填远端端口 `R`。
3. 若 SakuraFRP 只提供随机域名（无法稳定取得节点 IPv4），勾选直连页的 **“插件域名直连（FRP）”**，插件会将域名解析为 IPv4 并直接连接填写的 UDP 端口（跳过原版 U3DS/A2S 查询）。
4. 插件不会自动把端口改成 `27016` 或计算 `R±1`；输入的就是实际可达端口。

> ⚠️ SakuraFRP 公网 UDP 穿透依赖第三方网络环境；本次 Beta 总回归已完成项目侧 P2P/IPv4/域名直连流程验证，但不保证第三方节点、运营商网络或 NAT 环境始终可用。

## 端口说明

| 端口 | 原版用途 | 当前插件用途 |
|---|---|---|
| UDP 27015 | U3DS A2S 查询端口 | 不再作为客机输入端口使用（无 A2S 应答器） |
| UDP 27016 | 游戏连接端口 | Steam Networking Sockets 实际游戏数据；局域网/Radmin 客机输入此端口 |

单端口语义：客机输入的端口既是 query 端口也是 connection 端口。SakuraFRP 可将任意远端 UDP 端口 `R` 映射到房主本地 `27016`。开启“插件域名直连（FRP）”后，域名由玩家填写、端口为实际可达 UDP 端口，不写死任何供应商域名。

## 当前版本

| 项目 | 值 |
|---|---|
| BepInPlugin | `0.2.3.61` |
| AssemblyVersion | `0.2.3.61` |
| AssemblyFileVersion | `0.2.3.61` |
| 当前构建 SHA-256 | `3031C999138E850AED61636032B1580FAFBC6DC35B2F1F3D673262C43C67FC89` |
| 静态状态 | 当前 Unturned/BepInEx ABI 零警告构建；自动化测试 `268/268 PASS` |
| 当前手动测试归档 | `Beta2-P2P-AHost-20260818-1300`：`AllOK=true` |
| 发布标识 | `v0.2.3.61-beta.2` |
| 发布状态 | Beta 2 公开预发布版 |

## 已验证边界与免责声明

已验证：

- SteamID P2P 加入。
- Radmin LAN IPv4 直连。
- 30 秒隔离与原版玩家列表（默认 `P`）审批/撤销。
- PVP、难度、死亡保留和作弊权限投影。
- 缺失创意工坊地图/物品的原版下载流程。
- 地面自然刷新物品的房主权威同步。

已验证边界与免责声明：

- 当前版本的双端手动测试归档为 `Beta2-P2P-AHost-20260818-1300`，双方使用同一 DLL，归档摘要为 `AllOK=true`。部署与日志归档由测试人员手动控制；`TestLogs` 中的 CFG 哈希工具仅作可选辅助记录，不构成额外发布门。
- SakuraFRP/公网穿透的最终可用性受第三方节点、NAT、运营商和防火墙影响；测试通过不构成网络可达性保证。
- Steam 付费外观、第三方内容和未安装资源的表现取决于双方本地资源与原版下载机制，不构成插件对第三方资产的担保。
- 不承诺与任意第三方 BepInEx/Harmony/原生注入工具共存。启动后可从 `BepInEx/config/SteamP2PFriends/p2p-harmony-compatibility.json` 获取 Harmony 冲突清单；连接、认证与 transport 关键目标上的冲突会在创建或加入 P2P 会话前阻断。
- 本插件为 Beta 预发布版本，可能包含未覆盖的地图、模组、网络环境或 Unturned 更新兼容性问题；使用者应备份存档并自行承担联机、数据丢失和服务中断风险。

## 从源码构建

需要 Windows、.NET SDK/MSBuild，以及项目 `Libs` 目录中的 Unturned/BepInEx/Harmony 引用。

```powershell
dotnet msbuild SteamP2PFriends.csproj /t:Rebuild /p:Configuration=Release
dotnet msbuild WhitelistTests/SteamP2PFriends.WhitelistTests.csproj /t:Rebuild /p:Configuration=Release
./WhitelistTests/bin/Release/SteamP2PFriends.WhitelistTests.exe
```

## 文档

- [CHANGELOG.md](./CHANGELOG.md)：发布变更。
- [AUDIT_CHECKLIST.md](./AUDIT_CHECKLIST.md)：当前裁决摘要与历史审计记录。
- [DEDICATED_SYNC_COMPARISON_CHECKLIST.md](./DEDICATED_SYNC_COMPARISON_CHECKLIST.md)：U3DS 权威实现的历史对照参考。
- [VIBECODING.md](./VIBECODING.md)：人机协作开发与审计原则。
- [CONTRIBUTORS.md](./CONTRIBUTORS.md)：贡献者与工具声明。

## 许可证

[MIT License](./LICENSE) © 2026 YU80Rice
