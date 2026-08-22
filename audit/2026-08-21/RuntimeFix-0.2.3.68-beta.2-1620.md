# issues#6 新诊断报告：客机持续排队

## 一、证据范围

本次输入为 UMM 导出的两个包，已归档并解包到：

- `issues#6/case-151309-rar/`
- `issues#6/case-151309-summary-zip/`

原始包 SHA-256：

- `UMM-诊断包_20260821_151309.rar`：`C56735FA5148CC546D4B1FA7FACC6EF0BAC1B2DAE4A14CD41D7D772951C1E6A7`
- `UMM-诊断摘要 (1).zip`：`2636B6F82AD0B06CBA3B400BC24C40734DF2D4922D69C382CA7DDC7EA8CD49CC`

两个包并非同一份日志：RAR 是客机侧短日志，ZIP 包含主机侧长日志和多次历史尝试。两端均加载 `SteamP2PFriends 0.2.3.68`，但包内没有插件 DLL，因此当前 DLL 身份仍未由诊断包证明。

## 二、确定事实

### 2.1 主机原生队列未达到接受条件

主机 `Client.log` 在多次尝试中都记录：

```text
Added ... to queue position 0 (shouldVerify: True)
Received authentication request ...
Skipping Steam authentication ... because we are running offline-only
Removing player in queue ... queue state: "hasAuthentication: True hasProof: False hasGroup: True"
```

在最新 07:08:04 尝试中，客机为 `76561198321972542`，主机在 07:08:23 移除连接，状态仍为 `hasProof: False`。U3-SDK `SteamPending.canAcceptYet` 的真实条件是：

```text
hasAuthentication && hasProof && hasGroup
```

因此这次没有进入 `Provider.accept`，也没有 `LocalPlayerCreated`、`Connected` 或审核隔离阶段。日志中的“排队”不是 Route B 的待审核倒计时界面，而是原版连接队列。

### 2.2 审核插件已经允许握手，但没有改变原版 proof 门

主机插件记录：

```text
[P2P-Approval] Route B handshake permit: steamId=76561198321972542
HOST_AUTHENTICATE_RECEIVED ... pendingFound=True
HOST_AUTHENTICATE_HANDLER_RETURNED ...
```

这证明 Route B 入口没有以白名单拒绝该 SteamID。主机也没有记录 `HOST_ACCEPT_RETURNED`，原因与原生 `hasProof=false` 一致；不是“审批成功后被插件踢出”。

### 2.3 客机 watchdog 早于原生 Verify/Authenticate 链完成

RAR 客机插件日志：

```text
CLIENT_CONNECT_CALL_RETURNED t=264.939s
CLIENT_STATE ... Connecting to Timeout t=299.966s
!!! 连接超时 !!! elapsed=35.0s lastStage=Provider.connect called
CLIENT_VERIFY_RECEIVED t=507.119s
CLIENT_AUTHENTICATE_SEND_RETURNED t=507.124s
LOCAL_DISCONNECT_REQUESTED ... clicked queue cancel button t=516.679s
```

客机原生 `Client.log` 对应显示：

```text
07:08:07 Connection pending verification
07:08:07 Calling GetAuthSessionTicket ... ticket handle is valid: True
07:08:16 Disconnecting: clicked queue cancel button
```

所以客户端 watchdog 把一次仍在进行的原生连接标为 35 秒超时；随后 Verify 才到达并发送 Authenticate，用户又点击了取消。该 watchdog 结论不能证明连接已断开，也不能证明主机拒绝。

## 三、U3-SDK 机制核对

- `SteamPending.cs:156-160`：接受条件要求 `hasAuthentication && hasProof && hasGroup`。
- `SteamPending.cs:183-196`：主机向排队客机发送 `EClientMessage.Verify`。
- `ClientMessageHandler_Verify.cs:15-41`：客机收到 Verify 后打开 Steam auth ticket，并发送 `EServerMessage.Authenticate`。
- `ClientMessageHandler_Verify.cs:94-125`：客机将 `wearingResult` 序列化到 Authenticate；失败时应请求断开。
- `ServerMessageHandler_Authenticate.cs:133-170`：主机反序列化 economy details；长度为 0 时才直接设置 `hasProof=true`。
- `SteamPending.inventoryDetailsReady()`：收到有效 economy details 后设置 `hasProof=true` 并在其它门满足时调用 `Provider.accept`。

本包只证明客机发送了 Authenticate，未记录 Authenticate economy payload 长度、`wearingResult` 状态、序列化结果或主机 `DeserializeResult` 结果。因此不能仅凭现有日志判断是 SteamInventory 结果迟迟未就绪、序列化链失败、包被丢弃，还是连接时序/网络延迟。

## 四、候选根因分离

| 候选 | 当前证据 | 判定 |
| --- | --- | --- |
| 白名单硬阻断 | 主机有 `Route B handshake permit`，未出现 `WHITELISTED`；原生队列停在 proof | **排除为主因** |
| 客机未完成 SteamInventory proof | 主机多次稳定记录 `hasProof=False`；无 `HOST_ACCEPT_RETURNED` | **首要候选，仍缺 payload 证据** |
| 客机 watchdog 过早超时 | 35 秒 Timeout 早于 507 秒 Verify，且原生连接仍可继续 | **确定存在的独立缺陷/诊断偏差** |
| 主机网络完全不可达 | 主机能收到 Authenticate，Steam transport 已建立 | **不支持“完全不可达”** |
| 工坊资产污染 | 日志有多项工坊 `.dat` 重复键和缺失 Item 错误，但这些发生在资产加载阶段，未直接解释 `hasProof=False` | **环境干扰项，未证明因果** |

## 五、当前判定

- **issues#6 运行判定：FAIL / 未完成连接**。朋友没有进入世界，也没有进入 Route B 软隔离；原生队列在 `hasProof=false` 阶段结束。
- **插件白名单判定：未发现本次硬阻断证据**。该次日志不再重现历史 `.66` 的 `WHITELISTED` 重新加入阻断。
- **插件 watchdog 判定：存在时序问题**。35 秒超时状态与后续 Verify/Authenticate 不一致，应修复为“仅报告连接进度，不主动让用户看到已失败状态”，或者在收到原生 Verify/Authenticate 后恢复到明确的 native-queue 状态。
- **根因判定：证据不足以关闭**。下一步必须记录 proof 链，而不是继续只增加审批日志。

## 六、下一次 Debug 诊断要求

在客机 `ClientMessageHandler_Verify` 和 `WriteEconomyDetails` 边界增加限流日志：

```text
verifyReceivedAt
wearingResult == Invalid
SerializeResult(firstCallOk, bufferLength)
SerializeResult(secondCallOk, bufferLength)
authenticatePayloadLength
RequestDisconnect reason / failureInfo
```

在主机 `ServerMessageHandler_Authenticate` / `SteamPending.inventoryDetailsReady` 边界增加：

```text
authenticateReceivedAt
economyBufferLength
DeserializeResult(ok, resultHandle)
hasProof before/after
hasGroup / canAcceptYet before/after
Provider.accept decision
```

同时修正连接状态机：

1. `Provider.connect` 返回后不能以 35 秒 watchdog 单独宣告失败；至少要区分“transport 未建立”“Verify 未收到”“Authenticate 已发送、等待 proof/group”“原生队列等待”。
2. 收到 `CLIENT_VERIFY_RECEIVED` 后必须清除/降级旧的 `Timeout` 告警状态，并记录同一 attempt/session。
3. 用户取消时记录 `Provider.pending` / `isWaitingForAuthenticationResponse` / `failureInfo`，区分用户主动取消和主机移除。
4. 采集一份新的 Host/Client 成对日志，绑定当前 DLL SHA-256 与 Case ID；未拿到这些字段前不得宣布修复或归因于网络。
