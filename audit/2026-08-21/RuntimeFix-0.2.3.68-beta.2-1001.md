# 缺陷修复执行报告 - 0.2.3.68-beta.2

## 一、问题定位与修复策略

### 1. 运行日志事实

- 客机诊断包 `UMM-诊断包_20260821_093314/LogOutput.log` 加载的是 `0.2.3.67`，启动日志报告 `generated gameplay RPC readers gated=96`。
- 同一客机日志三次到达 `Connecting -> ServerAccepted -> LocalPlayerCreated`，其中两次继续到达插件状态 `Connected(Operational)`。因此它不是白名单握手硬拒绝；“仍显示排队/加载画面”是独立的运行问题，不能用插件状态机日志判定为已恢复。
- 房主诊断包 `UMM-诊断包_20260821_093319/LogOutput.log` 记录 `Route B handshake permit -> Pending added -> Approve success -> Revoke success -> 再次 handshake permit -> Pending added`，证明撤销后同一 SteamID 已能再次申请。
- 用户实际观察到房主和批准后客机全面失能。该现象与 `.67` 在生成 RPC reader 和 `PlayerInput.FixedUpdate` 层整体返回相符。

### 2. U3-SDK 与实际 DLL 溯源

项目引用的 `D:/Agent-工作目录/DevelopMyUNMultiplayerModAndModloader/Libs/Assembly-CSharp.dll` 与本机 Unturned `E:/Steam/steamapps/common/Unturned/Unturned_Data/Managed/Assembly-CSharp.dll` 均为 SHA-256 `E1146353E5C9BFF901EE94829640D88919C5E89F6A6B90B22C73ABF5C1608F94`，不是另一版 API。

| 事实 | U3-SDK 位置 | 结论 |
|---|---|---|
| `ServerMessageHandler_InvokeMethod.ReadMessage` 调用 `netMethod.readMethod(context)` | `Assets/Runtime/Assembly-CSharp/NetMessaging/ServerMessageHandler_InvokeMethod.cs:17-100` | 生成 reader 是参数消费层，Prefix 返回会破坏读取链。 |
| `PlayerInput.ReceiveInputs(in ServerInvocationContext)` 读取 reader、维护反作弊窗口、读取并入队输入包 | `Unturned/Player/PlayerInput.cs:1393-1503` | 必须让原版方法完整执行。 |
| `PlayerInput.FixedUpdate` 同时执行本地采样/发包与服务端队列消费、移动、装备模拟、ACK | `Unturned/Player/PlayerInput.cs:1524-1850` | 不能整体跳过，否则房主和远端玩家都会失能。 |
| U3 属性真实类型为 `SteamCall` | `Unturned/Provider/SteamChannel.cs:14-60` | 不使用虚构的 `SteamCallAttribute`。 |
| `SERVERSIDE` 与 `ONLY_FROM_OWNER` 的实际校验语义 | `Unturned/Provider/SteamChannel.cs:287-305` | 门禁按真实枚举值筛选，不再只比较属性类型名。 |

已同时从 U3-SDK 和实际 `Assembly-CSharp.dll` 元数据核验以下真实入口：

- `BarricadeDrop.ReceiveSalvageRequest(in ServerInvocationContext)`
- `StructureDrop.ReceiveSalvageRequest(in ServerInvocationContext)`
- `InteractableStorage.ReceiveInteractRequest(in ServerInvocationContext, bool)`
- `ResourceManager.ReceiveForageRequest(in ServerInvocationContext, byte, byte, ushort)`
- `InteractableFarm.ReceiveHarvestRequest(in ServerInvocationContext)`
- `InteractableDoor.ReceiveToggleRequest(in ServerInvocationContext, bool)`
- `PlayerInventory.ReceiveDragItem/ReceiveSwapItem/ReceiveDropItem`
- `PlayerEquipment.ReceiveEquipRequest/ReceiveToggleVisionRequest`

### 3. 修复策略

- 删除全局 `ServerMessageHandler_InvokeMethod` 门与生成 `*_Read` 门，不再 patch `PlayerInput.FixedUpdate`。
- 在真实 `Receive*` 业务方法上安装服务端权威 Prefix；只纳入 `SteamCall(SERVERSIDE)` 或 `SteamCall(ONLY_FROM_OWNER)`，并排除握手、输入解析和聊天读取链。
- 在 `PlayerInput.ReceiveInputs` Postfix 清洗待审远端已经解析并入队的数据包：移动、攻击、按键和射线交互归零；保留帧号、恢复量、视角、反作弊计数、队列推进和确认帧。
- 使用 `channel.IsLocalPlayer` 排除房主本地实例；批准从 Pending 原子移除后，所有隔离门自然放行。
- 白名单只负责持久授权；未知客机临时通过原版白名单检查，进入世界后登记 Pending，30 秒超时踢出，断开后清理 Pending，可再次申请。

## 二、核心代码变更

### 源码溯源矩阵

| 需求 | 落实位置 |
|---|---|
| 待审客机通过握手进入世界 | `Patches/Patch_ServerConnectValidation.cs:14-23`; `Host/P2PApprovalManager.cs:180-285` |
| 不破坏原版 RPC 参数读取 | `Patches/P2PQuarantineAdmissionPatches.cs:93-125` |
| 不破坏房主/客机输入生命周期 | `Patches/P2PQuarantineClientInputPatch.cs:12-39` |
| 待审业务动作服务端拒绝 | `Patches/P2PQuarantineAdmissionPatches.cs:22-165` |
| 待审无敌 | `Patches/P2PQuarantineAdmissionPatches.cs:173-195` |
| 待审指令拒绝、房主和批准玩家放行 | `Patches/P2PListenHostCommandPermissionPatch.cs:79-104` |
| 批准、撤销、超时与重连状态 | `Host/P2PApprovalManager.cs:288-427` |
| Harmony 登记 fail-closed 核验 | `SteamP2PFriendsPlugin.cs:787-825` |
| 精确函数/签名与输入清洗回归 | `WhitelistTests/RouteBApprovalTests.cs:196-334` |

### 核心 Diff 摘要

```diff
- Prefix generated *_Read methods and skip PlayerInput.FixedUpdate
+ Prefix real SteamCall Receive* business methods after reader parsing
+ Postfix PlayerInput.ReceiveInputs and neutralize only pending remote packets
+ Require SteamCall.validation == SERVERSIDE or ONLY_FROM_OWNER
+ Exclude channel.IsLocalPlayer and non-pending players
```

版本已更新为 `0.2.3.68` / `v0.2.3.68-beta.2`，README 与 CHANGELOG 已同步当前哈希和运行验证边界。

## 三、编译与自测状态

| 项目 | 命令 | 结果 |
|---|---|---|
| 插件 Release | `dotnet build SteamP2PFriends.csproj -c Release --no-restore` | 0 errors / 0 warnings |
| 测试 Release | `dotnet build WhitelistTests/SteamP2PFriends.WhitelistTests.csproj -c Release --no-restore` | 0 errors / 0 warnings |
| 回归测试 | `WhitelistTests/bin/Release/SteamP2PFriends.WhitelistTests.exe` | 58/58 PASS |
| Diff 格式 | `git diff --check` | 无 whitespace error；仅既有 LF/CRLF 提示 |

构建产物：

- 路径：`bin/Release/SteamP2PFriends.dll`
- 大小：`892416` bytes
- SHA-256：`0739AD2A1BF4BA9DF699B0B9607232FE247AF09E4BAA4C22E0CB00CD57039AA7`
- AssemblyVersion：`0.2.3.68`

测试 B11 逐项核验关键真实方法、参数和 `SteamCall` 语义；由于 .NET Framework 测试宿主不能加载 `InteractableStorage` 的默认接口元数据，该项使用已有 Mono.Cecil 延迟读取实际 DLL 元数据，不跳过验证。B12 核验输入清洗保留帧号、恢复量、视角和网络推进字段。

## 四、子智能体独立审核记录

| 审核项 | 判定 | 说明/证明位置 |
|---|---|---|
| 需求符合性 | 通过 | 握手放行、世界内 Pending、30 秒超时、批准/撤销/重连路径独立。 |
| U3 函数真实性 | 通过 | `ServerMessageHandler_InvokeMethod`、`PlayerInput`、`SteamChannel` 与关键 `Receive*` 签名均复核。 |
| 并发与线程安全 | 通过 | Provider 生命周期和状态变更保持游戏线程；Pending 使用并发集合且复合登记有锁。 |
| 事务与一致性 | 通过 | 批准持久化成功后解除 Pending；撤销持久化提交后才定向踢出；失败保持隔离/连接。 |
| 环境适配与 Harmony | 通过 | 当前 DLL 注册验证检查精确 Prefix/Postfix 所有权；扫描失败使 Route B 入口 fail-closed。 |
| 输入与房主隔离 | 通过 | 不 patch `FixedUpdate`/`*_Read`；Postfix 仅处理 pending remote，房主 `IsLocalPlayer` 排除。 |

独立审核最终判定：**PASS（静态 + 回归测试门禁）**，无阻断项。

非阻断残余风险：ActionGate 依赖游戏运行时反射扫描，`OwnerPrefix.ExtractOwner` 异常时选择 fail-open。双机测试必须确认启动日志出现 `parsed gameplay RPC targets gated=N context=N owner=N` 且 Route B registration valid。

## 五、最终结论与 QA 门禁

本轮源码、Release 编译、58 项回归和独立审核均通过，可移交 `0.2.3.68-beta.2` 双机验证。**这不是双机运行 PASS，不能继承 `.67` 或更早构建的任何运行结论，也不能据此发布正式版本。**

新双机 Case 必须绑定上述 DLL 哈希，并验证：

1. 未授权客机完成 `Verify -> Authenticate -> Accepted -> LocalPlayerCreated`，退出原版 LoadingUI 并真实看见/进入世界。
2. 房主本地背包拖放/交换/丢弃、指令、近战/枪械攻击均正常。
3. 待审客机不能移动、攻击、拾取/丢弃、回收/破坏建筑、打开仓储/门、使用指令，且受到僵尸攻击不死亡。
4. 批准后客机无需重连即可攻击僵尸、调整背包、拾取/丢弃、交互，并正常触发僵尸仇恨。
5. 拒绝与 30 秒超时后，同一 SteamID 可再次 `Accepted -> Pending`。
6. 批准后撤销会先移除持久授权再踢出；同一 SteamID 再连时重新进入 Pending，而不是被握手硬拒绝。
