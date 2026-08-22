# RuntimeFix - 0.2.3.64

## 一、问题定位与修复策略

- **目标**：恢复 Route B 的 P 键“复制ID / 允许 / 拒绝 / 撤销允许”前端，并彻底移除旧握手前待审批、预约隔离和自动重连等待链路。
- **阻断根因**：U3-SDK `SteamWhitelist.unwhitelist` 会在移除条目前执行 `Provider.kick`。因此常规白名单移除无法满足“保存成功后才踢出，保存失败不踢出”的撤销语义。
- **修复策略**：`NativeWhitelistStore.Remove` 仅从公开的 `SteamWhitelist.list` 移除条目；新增 `TryRemoveForApprovalRevoke`，严格执行 `Snapshot -> Remove -> Save -> Load -> !Contains`。只有该事务成功返回后，`P2PApprovalManager.RevokePlayer` 才执行一次定向踢出；失败时恢复内存快照、锁存故障、保持客机连接。

## 二、源码溯源

| 需求点 | 落实位置 |
| --- | --- |
| 房主行复制 SteamID | `Patches/Patch_PlayerDashboardPlayersUI.cs` 的 `IsLocalHost`、`OnCopyHostId` |
| 待审行允许/拒绝 | `Patches/Patch_PlayerDashboardPlayersUI.cs` 的 `BuildPendingRow`、`OnApprove`、`OnReject` |
| 已授权行撤销允许 | `Patches/Patch_PlayerDashboardPlayersUI.cs` 的 `TryGetRemoteAction`、`OnRevoke` |
| 成功提交后再踢出 | `Host/P2PWhitelistService.cs` 的 `TryRemoveForApprovalRevoke`；`Host/P2PApprovalManager.cs` 的 `RevokePlayer` |
| 清理旧流程 | `SteamP2PFriends.csproj`、`SteamP2PFriendsPlugin.cs`、`Client/P2PJoinManager.cs`、`Host/HostManager.cs` |
| 更新说明 | `CHANGELOG.md` 的 `v0.2.3.64-beta.2` 条目 |

## 三、代码变更

- 新 Route B 状态机与 P 键补丁保留为唯一生产实现；旧 `P2PJoinApprovalService`、`P2PQuarantineAdmissionService`、审批等待控制器、捕获补丁、旧 ESC 审批面板和重复 P 键装饰器已移除。
- 服务端仍保留 Route B 必需的握手放行、世界内隔离、服务端调用门、防伤害与客机输入限制。
- 新增撤销成功、撤销保存失败不踢出、事务保存前置证明的单元测试。

## 四、编译与自测

- 主项目：`MSBuild SteamP2PFriends.csproj /t:Rebuild /p:Configuration=Release /p:Platform=AnyCPU`。
  - 结果：`0 errors / 0 warnings`。
- 测试项目：`MSBuild WhitelistTests/SteamP2PFriends.WhitelistTests.csproj /t:Rebuild /p:Configuration=Release /p:Platform=AnyCPU`。
  - 结果：`0 errors / 0 warnings`。
- 测试执行：`SteamP2PFriends.WhitelistTests.exe`。
  - 结果：`52/52 PASS`。
- 产物：`bin/Release/SteamP2PFriends.dll`，Assembly 版本 `0.2.3.64`，SHA-256 `92D74BCBEFFCD7B332D430853F0020820F30D0946FF71DCAF5407278B35CFD10`。

## 五、独立审核记录

| 审核项 | 判定 | 证据 |
| --- | --- | --- |
| 撤销的提交顺序 | 通过 | 独立审计确认 `Snapshot -> Remove -> Save -> Load -> !Contains -> SafeKick`，且生产代码不调用 `unwhitelist`。 |
| 持久化失败不踢出 | 通过 | `TryRemoveForApprovalRevoke` 恢复并返回失败；`WL9b` 与 `B10` 通过。 |
| Route B 前端三态 | 通过 | 双行工厂均注册；主机、待审远端、已授权远端及其余原版行的分流均已审计。 |
| 旧链路移除 | 通过 | 旧服务、捕获补丁、等待控制器和旧 UI 不在生产源码或编译项。 |
| 编译和单测 | 通过 | 独立 Rebuild 及 `52/52 PASS`。 |

独立审核最终判定：**PASS**。

## 六、残余风险与测试建议

- 本报告只证明静态路径、构建和单元回归；尚未替代真实 Unturned 双机运行证据。
- 使用本 DLL 哈希双端测试：待审客机进入世界后 P 键显示“允许 / 拒绝”；批准后刷新列表显示“撤销允许”；撤销后仅目标客机断开；人为制造保存失败时目标客机保持连接且白名单条目恢复。
- 本轮移除了属于旧审批架构的历史测试，当前 `52/52` 是 Route B 当前工程的精简回归集，不能与此前 `277/277` 的历史数量直接比较。
