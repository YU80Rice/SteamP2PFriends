# 实现与发布审计报告 - v0.2.3.62-beta.2

## 需求执行概述

在不修改 SteamUser P2P listen-host 联机、认证、准入或重传行为的前提下，新增默认开启、低频的连接生命周期日志；保留 `VerboseDiagnostics=false` 和 `RouteDiagnostics=false` 的默认配置。

## 源码溯源矩阵

| 需求点 | 落实位置 |
| --- | --- |
| 默认输出低频连接事件，不依赖详细诊断开关 | `Shared/P2PConnectionJournal.cs` 的事件写入器；`RoleLogger.Info` 输出 |
| 客机连接、状态迁移、Verify、Authenticate、Accepted、断开可归档 | `Client/P2PJoinManager.cs`、`Patches/AuthHandshakeJournalPatch.cs`、`Patches/ClientAcceptedHandlerDiagnosticPatch.cs`、`Patches/DisconnectTracerPatch.cs` |
| 房主认证接收、处理、接受与拒绝可归档 | `Patches/AuthHandshakeJournalPatch.cs`、`Patches/ProviderAcceptDiagnosticPatch.cs`、`Patches/ProviderRejectDiagnosticPatch.cs` |
| 精确反射目标与登记所有权验证 | `Patches/AuthHandshakeJournalPatch.cs`；`WhitelistTests/HarmonyMetadataTests.cs` 的 H15 |
| 保持详细诊断默认关闭 | `SteamP2PFriendsPlugin.cs`；`WhitelistTests/LoggingPolicyTests.cs` |
| 版本和使用说明一致 | `Properties/AssemblyInfo.cs`、`SteamP2PFriendsPlugin.cs`、`README.md`、`CHANGELOG.md`、`DEVELOPMENT_TIMELINE.md`、`TestLogs/README.md` |

## 代码变更清单

- 新增：`Shared/P2PConnectionJournal.cs`、`Patches/AuthHandshakeJournalPatch.cs`。
- 修改：`Client/P2PJoinManager.cs`，将原有直接状态赋值封装为“先赋值、后写观察日志”的 `SetState`。
- 修改：认证接收、接受、拒绝、已接受、断开相关的既有观察补丁，以写入低频事件。
- 修改：工程文件、版本元数据、自动化测试与发布文档。
- 保留：`audit/2026-08-18/RuntimeFix-v0.2.3.61-beta.2-1716.md`，其为先前两起独立用户反馈的审计记录，未被本版本覆盖或替代。

## 编译与自测记录

| 项目 | 命令/结果 |
| --- | --- |
| 插件 Release | `MSBuild.exe SteamP2PFriends.csproj /t:Rebuild /p:Configuration=Release /p:Platform=AnyCPU /nologo`；`0 warnings / 0 errors` |
| 测试程序集 Release | `MSBuild.exe WhitelistTests/SteamP2PFriends.WhitelistTests.csproj /t:Rebuild /p:Configuration=Release /p:Platform=AnyCPU /nologo`；`0 warnings / 0 errors` |
| 自动化回归 | `WhitelistTests/bin/Release/SteamP2PFriends.WhitelistTests.exe`；`269/269 PASS`，含 H15 认证握手目标解析 |
| CFG 归档工具 | `TestLogs/tests/Test-DiagnosticConfigArchive.ps1`；PASS |
| 证据校验工具 | `TestLogs/tests/Test-EvidenceVerification.ps1`；PASS |
| DLL SHA-256 | `0198DBC963ACF0DC769136E737D265D38D0CF2665C53A0A652DA20F949681777` |
| 发布 ZIP | `publish/SteamP2PFriends-v0.2.3.62-beta.2.zip`；唯一条目 `BepInEx/plugins/SteamP2PFriends.dll`；条目 SHA-256 与 Release DLL 一致；ZIP SHA-256 `87A337EF040A6BBC5FFECDBB89E747A06956245DC92AC95F88ECD1374C1C78E4` |

## 独立审核记录

审核轮次：1。

| 审核项 | 判定 | 说明 |
| --- | --- | --- |
| 需求符合性 | 通过 | 默认低频事件覆盖连接生命周期，原有两个详细诊断开关仍默认关闭。 |
| 联机行为不变 | 通过 | 新 Prefix/Postfix/Finalizer 均为 `void`；不设置返回值、不吞原始异常、不重传或断连。 |
| 认证与隐私 | 通过 | 不读取 `NetPakReader` 或回调载荷；日志只含阶段、SteamID、传输类型、拒绝枚举和异常类型。 |
| Harmony ABI | 通过 | 使用精确签名解析并在 H15 验证；登记后核验本插件 owner 和 PatchMethod。 |
| 版本与产物一致 | 通过 | 插件/Assembly/File 版本均为 `0.2.3.62`；ZIP 内 DLL 与 Release DLL 哈希一致。 |

阻断项：无。

## 偏离与边界

无功能偏离。当前构建没有新的双机运行时采样；先前 `0.2.3.61` 的成功归档不能证明本 DLL 在真实两机上的动态表现，也不能宣称已修复 Issue #2 或另一位用户的开房反馈。现场复测时应首先核对启动日志中的 `[P2P-Connection] auth handshake journal registered (event-only)`。

## 测试建议

以发布 ZIP 在房主与客机同时部署 `0.2.3.62` 后，分别归档正常接受、收到 Verify 后未到 Authenticate、认证发送异常、房主拒绝和主动断开五类双端日志。
