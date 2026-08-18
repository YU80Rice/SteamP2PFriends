# SteamP2PFriends Runtime Validation - v0.2.3.61-beta.2

## 一、结论与范围

- **基础 P2P 功能：PASS。** Case `Beta2-P2P-AHost-20260818-1300` 证明 SteamUser listen-host 可以建房、远端客机可以进入、初始区域同步及后续远端 RPC 可以发送，并能正常断开与清理。
- **完整 Beta 2 发布门禁：FAIL。** 本 Case 没有归档两端的实际配置，不能证明以上运行发生在 `VerboseDiagnostics=false` 且 `RouteDiagnostics=false` 的默认诊断配置；发布文档中也仍有过期 DLL 哈希。
- **产品边界：** 该结论只涵盖双机 SteamUser P2P listen-host。U3DS、服务器列表、GSLT 与公开服务器认证均为本插件明确的 `N/A`，不是本版本的功能或发布门槛。本 Case 未执行长时间压力或会改变存档状态的回归，故不对那两类未执行测试作出结论。

## 二、源码与运行证据溯源

| 需求/门槛 | 证据 | 判定 |
| --- | --- | --- |
| 新版本实际加载且两端部署相同 DLL | `roles/Host|Client/start.json` 和 `finish.json`：版本 `0.2.3.61`，MVID `fc124e8c-11dd-4c67-bd51-22e77013e0d5`，SHA-256 `3031C999138E850AED61636032B1580FAFBC6DC35B2F1F3D673262C43C67FC89` | PASS |
| 归档完整性 | 四份归档运行日志重新计算 SHA-256、尺寸，均与各 `finish.json` 一致；`evidence-summary.json` 为 `AllOK=true` | PASS |
| 房主创建 P2P 会话 | `logs/Host/BepInEx-LogOutput.log:47-97`：PEI 房间创建、`Provider.host()`、SteamUser `CreateListenSocketP2P`、`onServerHosted` 和 listen loop 均成功 | PASS |
| 客机加入与实际同步 | 同一日志 `:147-194`：远端 `76561199721762479` 加入，结构、路障、资源、对象、物品初始区域向 `TransportConnection_SteamNetworkingSockets` 发送成功 | PASS |
| 会话持续及正常退出 | 同一日志 `:207-240`：持续 `clients=2`，之后有 `LeftApproved`、`OnClientDisconnected` 和计数清理；客机日志 `:49` 记录 SteamP2P `UnifiedConnect` | PASS |
| 默认关闭诊断的动态验证 | 源码默认值见 `SteamP2PFriendsPlugin.cs:95-97`，但 Case 未归档两端 cfg，也未留下可审计的启动配置事实 | FAIL / 发布阻断 |
| 候选 DLL 哈希清单一致 | 实际 DLL 为 `3031...FC89`；`README.md:100`、`CHANGELOG.md:22` 和 `TestLogs/README.md:33,50` 仍列出不同哈希 | FAIL / 发布阻断 |

`evidence-summary.json` 仅验证日志存在性、完整性、时间关联和两端 DLL 一致性；上表的功能结论来自对归档 Host/Client 日志的独立语义审查，而不是把 `AllOK=true` 误当作功能成功。

## 三、编译与自测

执行时间：2026-08-18 13:30 CST。

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\MSBuild.exe' SteamP2PFriends.csproj /t:Rebuild /p:Configuration=Release /m /nologo /v:minimal
& 'C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\MSBuild.exe' WhitelistTests\SteamP2PFriends.WhitelistTests.csproj /t:Rebuild /p:Configuration=Release /m /nologo /v:minimal
.\WhitelistTests\bin\Release\SteamP2PFriends.WhitelistTests.exe
```

- 插件 Release 构建：`0 errors / 0 warnings`。
- 测试程序集构建：`0 errors / 0 warnings`。
- 测试：`268 / 268 PASS`。
- 重建后的 `bin/Release/SteamP2PFriends.dll` 仍为 SHA-256 `3031C999138E850AED61636032B1580FAFBC6DC35B2F1F3D673262C43C67FC89`，与双机 Case 一致。

## 四、独立审核记录

独立审核 Agent 判定：**FAIL（完整发布审计门禁）**。

| 审核项 | 判定 | 说明 |
| --- | --- | --- |
| 需求符合性：基础 P2P 运行 | 通过 | 双机日志给出建房、远端加入、同步发送、持续会话和清理事实。 |
| 运行证据完整性 | 通过 | 四份日志的归档哈希和大小已复算。 |
| 默认诊断关闭 | 不通过 | 未归档两端实际 cfg；运行证据不能倒推出配置值。 |
| 发布身份一致性 | 不通过 | 文档与测试示例中的哈希落后于最终候选 DLL。 |
| 环境适配性 | 通过但有风险 | 客机 BepInEx `5.4.22.0` 对插件目标 `5.4.23.5` 给出兼容警告；本 Case 已实际通关，但发布支持矩阵仍应明确。 |

## 五、阻断项与下一步

1. **B-LOG-01 运行证据未闭环：** 下一个全新 Case 必须归档、哈希双方 `com.yu80rice.steamp2pfriends.cfg`，或在不被诊断策略过滤的启动日志中记录两个开关的实际值，然后重跑相同 P2P 路径。
2. **RELEASE-HASH-01：** 在最终 DLL 固定后，统一 `README.md`、`CHANGELOG.md`、`TestLogs/README.md` 及发布包清单到 `3031C999...C67FC89`，并重新生成例程中的预期值。

本轮未修改任何插件业务源码、游戏配置、部署 DLL 或运行证据；仅重建相同哈希的候选 DLL、运行自动化测试并新增本审计报告。`offlineOnly=true` 是该私有 SteamUser P2P listen-host 路线为兼容匿名 GameServer 身份差异采用的运行机制；它不代表服务器列表、GSLT 或公开服务器认证功能。
