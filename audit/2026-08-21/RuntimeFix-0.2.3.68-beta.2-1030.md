# 缺陷修复执行报告 - 0.2.3.68-beta.2

### 一、问题定位与修复策略

- **证据**：UMM 诊断包 `_20260821_101915` 的客机已出现 `Accepting queued player` 与“玩家已连接”，随后 `PlayerInput.ReceiveInputs` 连续 1321 次异常；堆栈指向 `P2PQuarantineActionGatePatch.OwnerPrefix -> ShouldBlock`。同包 `LogOutput.log` 标记为 `trusted player entered world`，因此不是排队未进入世界。
- **根因**：`IsBlockedOwnerTarget` 未排除首参 `ServerInvocationContext&`，把 U3-SDK 的 `PlayerInput.ReceiveInputs(in ServerInvocationContext)` 错误注册为 OwnerPrefix。`ShouldBlock` 使用 `SteamPlayerID == null`，命中 U3-SDK 对该类型不安全的 `operator ==`，产生 NRE。
- **修复策略**：Owner 扫描先排除 Context RPC，并显式排除 `PlayerInput.ReceiveInputs`；所有相关空值判定改为 `ReferenceEquals`，保留输入接收/确认链，仅在 Postfix 对待审核远端包做中和。

### 二、核心代码变更对比 (Diff)

- `Patches/P2PQuarantineAdmissionPatches.cs`
  - `IsBlockedOwnerTarget` 排除 `ServerInvocationContext&` 与 `PlayerInput.ReceiveInputs`。
  - `ShouldBlock` 对 `SteamPlayer`/`playerID` 使用 `ReferenceEquals`。
- `Patches/P2PQuarantineClientInputPatch.cs`
  - 对实例、玩家、频道、owner、playerID、队列逐级 `ReferenceEquals` 判空，避免再次触发重载比较。
- `WhitelistTests/RouteBApprovalTests.cs`
  - 按真实 `ServerInvocationContext&` 签名解析 `ReceiveInputs`，并同时断言 Context/Owner 两类扫描器均不拦截。

### 三、编译与自测状态

- **插件编译**：`dotnet build SteamP2PFriends.csproj -c Release --no-restore`，0 errors / 0 warnings。
- **测试编译**：`dotnet build SteamP2PFriends.WhitelistTests.csproj -c Release --no-restore`，0 errors / 0 warnings。
- **回归测试**：`SteamP2PFriends.WhitelistTests.exe`，58/58 PASS。
- **产物身份**：`SteamP2PFriends.dll` SHA-256 `900C337296FB02D5477B64F3E21E6A834E09F29E1504FEEE2A14FF2CAF08FE1F`；MVID `00de234e-7c8d-4a5e-a5b6-ea6e1ec388d7`。

### 四、子智能体审核记录

| 审核项 | 判定 | 说明/证明位置 |
| :--- | :--- | :--- |
| 需求符合性 | 通过 | 仅对待审核远端输入做中和；不再拦截原生输入接收链 |
| U3-SDK API 真实性 | 通过 | `PlayerInput.ReceiveInputs(in ServerInvocationContext)`、`PlayerCaller.player` 已由 SDK 源码核验 |
| 空值与运行时安全 | 通过 | `ReferenceEquals` 覆盖 `SteamPlayerID` 重载陷阱及输入链对象层级 |
| 回归覆盖 | 通过 | B11 真实 Context& 定位，58/58 PASS |
| 独立子智能体结论 | **PASS** | 无阻断项 |

### 五、最终结论

- 修复已完成，编译与独立审核通过，可交付双机验证。
- 旧版本删除的 9000 多行代码不应整体恢复；本轮仅复用了其中已验证的 `ReferenceEquals` 防护思想。旧的全局 InvokeMethod、队列等待和 FixedUpdate 跳过路径会重新造成排队或全员失能，因此明确不恢复。
- **运行时门禁**：上述静态结果不等于新双机 PASS。必须使用本报告中的新 DLL，在 Host/Client 两端重新采集日志，确认 `ReceiveInputs` NRE 消失，房主及已批准客机的背包、指令、攻击、交互和僵尸仇恨恢复，并验证撤销后可再次申请加入。
