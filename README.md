# SteamP2PFriends

> 为 Unturned 提供无 U3DS 的便捷 listen-host 联机，同时支持 SteamID P2P 和 IPv4 直连。

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](./LICENSE)
[![Version](https://img.shields.io/badge/version-0.2.3.56-blue.svg)](https://github.com/YU80Rice/SteamP2PFriends/releases/tag/v0.2.3.56)
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
- 房主直接在原版 `U` 玩家列表中点击“允许/撤销允许”，不需要额外审批窗口。
- 客机聊天栏以 5 秒间隔显示审批剩余时间。
- 创意工坊地图与缺失内容下载已通过双机测试。
- listen-host 自然刷新物品由房主统一生成，避免主客机地面物品不一致和幽灵物品。
- 运行时 Harmony 注册自检；关键补丁失效时 fail-closed，禁止不安全联机。

## 安装

1. 房主和所有客机都需要安装相同版本的 BepInEx 和 SteamP2PFriends。
2. 从 [GitHub Releases](https://github.com/YU80Rice/SteamP2PFriends/releases) 下载当前版本的 `SteamP2PFriends.zip`。
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
5. 进入世界后，按 `U` 在玩家列表中审批新玩家。

### 客机通过 SteamID 加入

1. 打开“开始游戏 → 直连”。
2. 在顶部地址栏粘贴房主个人 SteamID。
3. 点击连接。

插件只接管有效的 Steam 个人账户 ID；U3DS Server Code 仍交给原版处理。

### 客机通过 IPv4 加入

1. 打开“开始游戏 → 直连”。
2. 输入房主的局域网或 Radmin IPv4。
3. 端口填写 `27015`。
4. 插件会跳过 U3DS A2S 查询，将实际游戏数据连接到 UDP `27016`。

Windows 防火墙必须允许 Unturned 在相应网络上使用 UDP `27016`。

## 端口说明

| 端口 | 原版用途 | 当前插件用途 |
|---|---|---|
| UDP 27015 | U3DS A2S 查询端口 | 用户分享/输入的逻辑端口；不需要 A2S 应答 |
| UDP 27016 | 游戏连接端口 | Steam Networking Sockets 实际游戏数据 |

SakuraFRP/公网穿透尚未完成独立动态验收，不属于当前 Beta 通过范围。

## 当前版本

| 项目 | 值 |
|---|---|
| BepInPlugin | `0.2.3.56` |
| AssemblyVersion | `0.2.3.56` |
| AssemblyFileVersion | `0.2.3.56` |
| DLL SHA-256 | `1723AFAAE1EB49A5F53B87EA628DA6790F5E317A756D6306A33C3D080F3C9E2F` |
| 静态测试 | `151/151 PASS` |
| 发布状态 | Beta Prerelease |

## 已验证与待验证边界

已验证：

- SteamID P2P 加入。
- Radmin LAN IPv4 直连。
- 30 秒隔离与 `U` 玩家列表审批/撤销。
- PVP、难度、死亡保留和作弊权限投影。
- 缺失创意工坊地图/物品的原版下载流程。
- 地面自然刷新物品的房主权威同步。

待验证/已挂起：

- SakuraFRP 与公网 UDP 穿透。
- 房间设置持久化的下一轮动态冒烟。
- 背包物品移动/丢弃后的“幽灵图标”报告，当前缺少事发日志，未盲目修改。
- Steam 付费外观/玩家装饰资产的专项双端指纹验证。

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
