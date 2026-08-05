# SteamP2PFriends

> Unturned P2P 联机 BepInEx 插件 · 向原版 SteamP2PFriends 致敬的双端重构

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](./LICENSE)
[![Vibecoding](https://img.shields.io/badge/Development-Vibecoding-blueviolet.svg)](./VIBECODING.md)

## 项目简介

SteamP2PFriends 是一个 Unturned 的 BepInEx 插件，通过 Steam P2P 网络实现房主-客机联机。本项目是对原版 SteamP2PFriends 模块的重新设计与实现，向原版致敬的同时解决其未完成的问题。

### 核心特性

- **双端插件**：房主和客机均安装，双方都有完整日志
- **SteamID 直连**：客机通过房主 SteamID 直连，无需 Steam 好友列表
- **Workshop 资产兼容**：支持 Workshop 地图、作者声明依赖、OBJECT/ITEM/VEHICLE 自动纳入
- **原生白名单准入**（设计阶段）：复用 Unturned 原生 `SteamWhitelist` 实现 fail-closed 准入
- **诊断构建**：`DiagnosticBuildValid` 运行时自检 + D-Vis-* 诊断补丁 + FullFixBuild A/B 对比

### 当前版本

| 项 | 值 |
|---|---|
| 版本 | v0.2.3.37 stage6A-1 |
| BepInPlugin GUID | `com.yu80rice.steamp2pfriends` |
| AssemblyVersion | 0.2.3.37 |
| AssemblyFileVersion | 0.2.3.37 |
| Stage 6B 测试 DLL SHA-256 | `4C8321018295B1650B7CCF0356EF238F7E358A349046410AC9DF5D6AD3C3A195` |
| DiagnosticBuildValid | true |

### 当前授权状态（Codex 127th）

- 🟢 受限封闭 α 部署获准（仅 SteamP2PFriends.dll v0.2.3.37）
- 🔴 C# 修改、重新编译、认证改动、公开 Beta 发布继续冻结
- 🔴 不与 LaunchMultiplayerNet / LaunchHordeTracker / LaunchInPlaceReload / LaunchInventoryTidy 同时部署

详见审计清单：[AUDIT_CHECKLIST.md](./AUDIT_CHECKLIST.md) §14.112。

## 目录结构

```
SteamP2PFriends/
├── SteamP2PFriendsPlugin.cs        # BepInEx 入口
├── SteamP2PFriends.csproj          # 项目文件
├── Properties/AssemblyInfo.cs      # 程序集信息
├── Shared/                         # 共享层
│   ├── SteamRuntime.cs             # 反射绑定
│   ├── ReflectionUtil.cs           # AccessTools 薄封装
│   ├── RoleLogger.cs               # [Host]/[Client]/[Shared] 前缀路由
│   └── Enums/                      # EHostMode / EP2PLobbyState / EJoinState
├── Host/                           # 房主层
│   ├── HostManager.cs              # 开房序列核心
│   ├── HostLobbyService.cs         # Lobby 绑定
│   ├── HostSteamIdDisplayService.cs# Server Code 显示
│   ├── GsltInputModal.cs           # GSLT 输入
│   ├── HasCheatsGuardWatcher.cs    # 反作弊守门
│   └── SteamGameServerCallbacksWatcher.cs
├── Client/                         # 客机层
│   ├── P2PJoinManager.cs           # 连接状态机
│   ├── SteamIdInputModal.cs        # SteamID 输入
│   └── ClientLobbyListener.cs      # LobbyGameCreated 回调
├── Patches/                        # Harmony 补丁
│   ├── SteamUserP2PRedirectPatch.cs
│   ├── CallbackCreateGameServerRedirectPatch.cs
│   ├── LobbiesGameCreatedPatch.cs
│   ├── SteamworksServerMultiplayerServiceOpenPatch.cs
│   ├── SteamworksServerMultiplayerServiceClosePatch.cs
│   ├── ProviderDisconnectPatch.cs
│   ├── ProviderInitializeDedicatedUGCPatch.cs
│   ├── MenuPlaySingleplayerUIPatch.cs
│   ├── ClientMethodLoopbackPatch.cs
│   ├── LanTestSteamPlayerPatch.cs
│   └── ProviderRejectDiagnosticPatch.cs
├── Tools/                          # 辅助工具
├── AUDIT_CHECKLIST.md              # 审计清单（§14.1 - §14.115）
├── LICENSE                         # MIT
├── VIBECODING.md                   # 人机协同开发宣示
└── CONTRIBUTORS.md                 # 贡献者声明
```

## 开发模式

本项目采用 **Vibecoding** 人机协同开发模式。详见 [VIBECODING.md](./VIBECODING.md)。

## 贡献者

本项目由人类导演 YU80Rice 与 AI 导师（Claude / Kimi / Codex）及本地 Agent（Cherry Claw）协同完成。详见 [CONTRIBUTORS.md](./CONTRIBUTORS.md)。

## 许可证

[MIT License](./LICENSE) © 2026 YU80Rice
