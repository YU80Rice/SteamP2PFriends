# SteamP2PFriends 两起独立故障复核 - v0.2.3.61-beta.2

## 范围

2026-08-18 收到两起互相独立的反馈，来自不同用户和不同机器：

1. GitHub Issue #2：客机排队后提示服务器未响应。
2. `E:\下载内容\QQ下载\UMM-诊断摘要.zip`：反馈者可加入他人房间，但其开房后其他人无法加入。

本报告不合并两起案例的复现、环境判断或发布结论。

## 案例 A：GitHub Issue #2

### 证据与定位

- 主机与客机的可靠 BepInEx 加载行均为 `Loading [SteamP2PFriends 0.2.3.60]`，即 `v0.2.3.60-beta.1`，不是 `0.2.3.61`。
- 其后的 `v0.2.3.51 Alpha-1` 与 `v0.2.3.37-P0-B-6-P0-D-ESC-2` 是 beta.1 遗留的自定义横幅，不能作为 DLL 版本判据。
- 主机的 `SetConnectionPollGroup` 与 `AcceptConnection` 均成功，后者返回 `k_EResultOK`；`ReadyToConnect` 中已经写入 `[P2P-Quarantine] admission reserved`。
- 主机在 `t=187.869s` 发送原版可靠消息 `Verify(8)`；日志同时记录该发送调用已返回。
- 原版 `ClientMessageHandler_Verify` 收到 `Verify` 后会设置认证等待状态，并以可靠通道发送 `Authenticate`。原版 `ServerMessageHandler_Authenticate` 成功处理时必定写入 `Received authentication request from queued player ...`。
- 本次主机日志没有该认证处理记录；约 30 秒后主机执行 `Provider.reject(..., LATE_PENDING(20))`，客机随后因 `Server did not reply to authentication request` 进入 `TIMED_OUT_LOGIN(64)`。

结论：这不是初始白名单拒绝。该次 beta.1 已建立传输、完成准入保留并发送 `Verify`，但主机没有观察到 `Authenticate` 被处理，最后因 pending 超时而拒绝。现有日志只能把故障收窄到“客机回传的原版可靠认证消息未抵达或未被主机处理”的区间；它不是 SDR、NAT 或端口不可达的直接证据，也不能从日志单独确定是传输、线程调度还是其他共存插件造成。

### 当前版本覆盖

`Patches/P2PQuarantineAdmissionPatches.cs` 在 `ServerMessageHandler_ReadyToConnect` 建立作用域，将原生白名单未命中转为 `P2PQuarantineAdmissionService.TryReserve(...)` 的一次性准入保留。beta.2 将该 scope 的手动登记改为由隔离 patch 自身拥有 Prefix/Finalizer 并核验真实登记；beta.1 则复用了 LAN duplicate-bypass patch。

但这不是本案例的已证实根因：beta.1 日志已经出现 `admission reserved`，说明旧 scope 在本次调用中实际生效。beta.2 没有 patch `ClientMessageHandler_Verify`、`ServerMessageHandler_Authenticate` 或 `EServerMessage.Authenticate` 的客机到主机交付；相关重定向、消息诊断的差异是诊断清理和日志卫生，未形成对认证回传缺口的行为修复。因此 beta.2 是合理的低诊断负载复测候选，**但不能被宣称为已修复 Issue #2**。

### 独立复测要求

两端均部署发布的 `v0.2.3.61-beta.2` ZIP，并采集同一次尝试的新版主机、客机日志，两端都必须出现 `Loading [SteamP2PFriends 0.2.3.61]`。主机证据必须覆盖 `ReadyToConnect -> reservation/whitelist -> Verify -> Received authentication request -> Provider.accept`；失败时保留 `Verify` 后无 `Authenticate` 处理和 `LATE_PENDING` 的上下文。客机必须覆盖 `ConnectP2P`、收到 `Verify`、发送 `Authenticate` 或明确未收到 `Verify` 的证据。首次按 beta.2 默认 `VerboseDiagnostics=false`、`RouteDiagnostics=false` 重测；只有仍失败才保留现场后开启诊断复现。

## 案例 B：UMM 诊断包

### 证据与定位

- 诊断包的可靠 BepInEx 加载行是 `Loading [SteamP2PFriends 0.2.3.60]`，即 `v0.2.3.60-beta.1`；之后的 Alpha/`0.2.3.37` 是遗留横幅。
- 全程仅有菜单/客机状态 `isServer=False`；退出前没有实际 `StartP2PServer`、`Provider.host`、`CreateListenSocketP2P`、入站连接或对端 `ConnectP2P` 尝试。

结论：该包证明部署仍是 beta.1，且未包含该反馈者的失败开房尝试。因此不能据此区分开房失败、主机侧准入拒绝或对端连接失败，更不能判断 beta.2 是否会修复该用户的问题。包内的 `CreateListenSocketP2P`/`AcceptConnection` 只是启动期补丁登记和自检，不是实际开房或入站连接。出现的 thread-starvation 警告位于非房主、无连接尝试会话，不能作为因果结论。

### 独立复测要求

先让两台参与者均部署 `v0.2.3.61-beta.2`。然后只针对该反馈者开房失败的一次尝试，采集两份新日志：从点击开房前到对端失败后的主机日志，以及同一次尝试的对端客机日志。不能以该反馈者加入其他房间的日志代替。

## 构建与回归验证

命令：

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\MSBuild.exe' SteamP2PFriends.csproj /t:Rebuild /p:Configuration=Release /m /nologo
& 'C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\MSBuild.exe' WhitelistTests\SteamP2PFriends.WhitelistTests.csproj /t:Rebuild /p:Configuration=Release /m /nologo
& 'WhitelistTests\bin\Release\SteamP2PFriends.WhitelistTests.exe'
```

- 构建：0 warnings，0 errors。
- 回归控制台：268 / 268 通过，包括 Q1-Q10 准入保留、ReadyToConnect/白名单 patch ABI 检查。
- 重建 DLL SHA-256：`3031C999138E850AED61636032B1580FAFBC6DC35B2F1F3D673262C43C67FC89`。
- 发布 ZIP 内 DLL SHA-256：完全一致。
- 发布 ZIP SHA-256：`A9212F4467BC8442624077A4EF45F859D6B8C1733FD650CB037E180159E8AD5A`。

## 独立审计

本轮把“已归档 beta.2 正常双机案例”与“两个用户反馈是否已被 beta.2 修复”分开判定：

- `TestLogs/artifacts/Beta2-P2P-AHost-20260818-1300/evidence-summary.json` 记录主机、客机均 PASS，且 DLL/MVID 相同。
- 已归档的正常 P2P 会话中不存在 `LATE_PENDING`、`TIMED_OUT_LOGIN`、`Error` 或 `Fatal`；主机达到 `clients=2/pending=0` 后正常断线。
- beta.2 受控双机 P2P 运行证据：PASS。结论仅覆盖 SteamID 本地 P2P listen-host；不涉及 U3DS、公开服务器发现或公开服务器认证。
- Issue #2 的“beta.2 已修复”判定：FAIL。存在 `ISSUE2-AUTH-RETURN-REPRO` 验证缺口，且静态差异没有覆盖 `Verify -> Authenticate` 回传路径。
- UMM 反馈的“beta.2 已修复”判定：FAIL。存在 `UMM-HOST-ATTEMPT-EVIDENCE` 验证缺口，诊断包没有房主开房尝试。

## 最终结论

这两份独立的 beta.1 诊断输入不足以证明 beta.2 在两个用户环境中已经修复，也不足以证明 beta.2 必然仍有同一缺陷。现有 `v0.2.3.61-beta.2` 已完成构建、回归和独立受控双机 P2P 验证，允许作为复测版本继续存在；但不得在 Issue 或发布说明中写“已修复这两案”。下一动作是两位反馈者分别升级并重测；只有当前 DLL 复现，才进入新的源码修复循环。
