# 缺陷修复执行报告 - 0.2.3.70-beta.2

## 一、问题定位与修复策略

### 根因 A：主机队列 proof 永久未完成

`20260821_151309` 证据显示客机已收到 Verify 并调用 Authenticate，主机也收到 Authenticate，但 pending 长期为 `hasAuthentication=True, hasProof=False, hasGroup=True`，没有进入 `Provider.accept`。

U3-SDK 源码确认：

- `SteamPending.canAcceptYet` 严格等于 `hasAuthentication && hasProof && hasGroup`。
- 客机 `ClientMessageHandler_Verify.WriteEconomyDetails` 会序列化 SteamUser inventory result。
- 主机 `ServerMessageHandler_Authenticate.ReadEconomyDetails` 使用 SteamGameServer inventory context 反序列化；零长度 buffer 是原版支持路径，会直接初始化空外观数组并设置 `hasProof=true`。

本修复仅在插件发起且仍处于 `Connecting` 的 SteamUser P2P 握手中，写入原版支持的 `UInt16(0)` economy proof。它不读取或修改票据，不伪造 `hasAuthentication`/`hasGroup`，不直接调用 `Provider.accept`，最终接受条件仍由原版决定。

### 根因 B：插件 watchdog 早于原生握手

客机时间线为：`Provider.connect` 返回约 35 秒后插件先进入 Timeout，而 Verify/Authenticate 约 242 秒后才到达。旧 watchdog 将插件状态机与仍在进行的原生连接分叉。

本修复将 35 秒阈值降级为一次性观测告警，不再写 `EJoinState.Timeout`、不弹失败提示、不主动断开。真正失败由 `Provider.onClientDisconnected` 与 `connectionFailureInfo` 收敛；Verify/Auth 到达时更新 `_lastStage`。

## 二、源码溯源清单

| 需求 | 实现位置 |
|---|---|
| P2P economy proof 兼容 | `Patches/AuthHandshakeJournalPatch.cs` / `WriteEconomyDetails_Prefix` |
| proof 前后证据 | `Shared/P2PConnectionJournal.cs` / `HostAuthenticateState` |
| 分阶段握手记录 | `Client/P2PJoinManager.cs` / `NotifyVerifyReceived`, `NotifyAuthenticateSending` |
| watchdog 不误判 | `Client/P2PJoinManager.cs` / `HandleConnectingTick` |
| 功能补丁 fail-closed | `SteamP2PFriendsPlugin.VerifyCriticalPatches`, `IsP2PEntryReady`, `P2PEntryReadinessGate` |
| 注册失败回归 | `WhitelistTests/P2PEntryReadinessGateTests.cs` / `Test_E4_HandshakeCompatibilityFailureCannotExposeEntry` |

## 三、编译与自测状态

- MSBuild: Visual Studio 18 Insiders, .NET Framework 4.7.2.
- Debug: 0 errors, 0 warnings.
- Release: 0 errors, 0 warnings.
- Debug tests: 61/61 PASS.
- Release tests: 61/61 PASS.
- `git diff --check`: 无空白错误；仅现有 CRLF 提示。

最终 Debug 身份：

- SHA-256: `A575A0F72DB8C1C1837223F03A3973F51043044D4B5BEFA7035C9AC7B5365E37`
- MVID: `833c5831-1d40-432d-a900-f9bf4ab7a63a`
- Size: `966656` bytes
- Archive: `issues#6/builds/0.2.3.70-beta.2-debug/`

## 四、子智能体审核记录

| 轮次 | 判定 | 结果 |
|---|---|---|
| 1 | FAIL | `AuthHandshakeJournalPatch.RegistrationValid` 未汇入全局 fail-closed 门。 |
| 2 | PASS | 全局 `DiagnosticBuildValid` 与入口 readiness 双重检查注册状态；E4 回归覆盖关闭阻断项。 |

## 五、偏离、风险与运行验收

- 无需求偏离；没有修改 Route B 审批、白名单、踢出或再次申请语义。
- 零长度 economy proof 表示本次连接不向主机证明 Steam 饰品/武器皮肤。需在双机回归中检查客机外观是否出现可接受的默认化；基础角色衣物状态需单独观察。
- 静态与自动化结论不能证明 Listen-Host 当前 DLL 已运行治愈。必须使用上述同一 SHA-256 的成对主客机日志确认：`CLIENT_ECONOMY_PROOF_EMPTY`、主机 `hasProof False -> True`、`HOST_ACCEPT_RETURNED`、客机进入世界。

## 六、QA 建议

1. 首次加入：客机不预订阅房主内容，验证下载、Verify/Auth、进入世界和 30 秒软隔离。
2. 审核通过：验证移动、攻击、背包、交互和仇恨恢复。
3. 撤销并踢出：再次连接并重新申请，验证不被历史白名单硬阻断。
4. 延迟网络：超过 35 秒仍不得出现插件 Timeout；用户取消应记录为人工取消。
5. 外观：对比客机本人、房主观察到的衣物/皮肤，记录零 proof 的影响范围。

## 七、最终结论

静态修复、编译、自动化回归和独立审核均已闭环，`0.2.3.70-beta.2` 可移交双机运行验证。issues#6 仍不能在缺少当前哈希双机证据时标记为运行时关闭。
