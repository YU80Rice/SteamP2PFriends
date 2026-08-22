# 缺陷诊断构建执行报告 - 0.2.3.68-beta.2

### 一、问题定位与诊断策略

- **现象**：Elver 权限门在客机单独位于区域时可能卡住并拉回；房主进入同一区域后现象消失。
- **当前结论**：历史诊断包不足以区分 Binary State RPC 丢失、远区 LevelObject 激活/碰撞漂移和权威位置纠偏。本轮交付专项 Debug 诊断版，不宣称 issues#7 已修复。
- **U3-SDK 溯源**：
  - `ObjectManager.ReceiveToggleObjectBinaryStateRequest`：权威请求入口，`ObjectManager.cs:593`。
  - `ObjectManager.ReceiveObjectBinaryState`：客机状态应用入口，`ObjectManager.cs:545`。
  - `ObjectManager.GatherRemoteClientConnections`：按对象区域选择广播目标，`ObjectManager.cs:1305`。
  - `PlayerInput.ReceiveSimulateMispredictedInputs`：真实预测误差校正入口，`PlayerInput.cs:1353`。
  - `Player.ReceiveTeleport`：显式传送入口，`Player.cs:980`。
  - `PlayerMovement.OnControllerColliderHit`：角色控制器碰撞入口，`PlayerMovement.cs:2054`。
- **策略**：对以上真实入口及对象激活链增加 Debug-only、只读、异常隔离的 Harmony 观测；Release 编译为 no-op；任一关键 Hook 缺失时纳入 `DiagnosticBuildValid` fail-closed 门。

### 二、源码溯源与核心变更

| 需求点 | 落实位置 |
| :--- | :--- |
| 记录客机请求、主机权威判定与提交结果 | `Patches/Issue7ObjectBinaryStateDiagnosticPatch.cs` 的 `ToggleObjectBinaryState_Prefix`、`Authority_*` |
| 记录广播对象与实际接收应用 | 同文件 `GatherRecipients_Postfix`、`Receive_*` |
| 记录 GUID、region/index、激活与 Collider 状态 | 同文件 `Describe`、`DescribeActivation`、`Activation_Postfix` |
| 捕捉卡住/拉回链 | 同文件 `ControllerColliderHit_Prefix`、`Misprediction_Prefix`、`ReceiveTeleport_Prefix` |
| 防止日志改变游戏行为 | 所有 Hook 均为观察式 void；Finalizer 原样返回异常；`Log` 自身吞并诊断异常 |
| 防止碰撞刷满配额 | 同一目标门与 `PlayerMovement` 实例每 1 秒最多记录一次 |
| Debug/Release 边界与自检 | `Properties/AssemblyInfo.cs`、`SteamP2PFriendsPlugin.cs`、`SteamP2PFriends.csproj` |

核心差异：

```diff
+ [assembly: AssemblyConfiguration("Issue7-Debug")] // DEBUG only
+ Issue7ObjectBinaryStateDiagnosticPatch.RegisterManual(_harmony)
+ if (!Issue7ObjectBinaryStateDiagnosticPatch.VerifyRegistration()) allOk = false
+ 12 个 Debug-only Hook：request / authority / recipients / receive / activation / collision / correction
+ 每会话独立 400 条配额，Collider 按门与玩家实例 1 秒节流
  Release: RegisterManual => release-noop，VerifyRegistration => true
```

### 三、编译、自测与二进制核验

| 项目 | 结果 |
| :--- | :--- |
| Release Rebuild | 0 errors / 0 warnings |
| 测试项目 Rebuild | 0 errors / 0 warnings |
| Route B 回归测试 | 58/58 PASS |
| Debug Rebuild | 0 errors / 0 warnings |
| Debug AssemblyVersion | `0.2.3.68` |
| Debug AssemblyConfiguration | `Issue7-Debug` |
| Debug MVID | `5a5b7d79-4da4-4c2a-bfb1-f235ff71e780` |
| Debug DLL SHA-256 | `340E6D1A5AA6B4AAA350E9DEA3EFD0B3658E4588D06D1EA3FE81CE636873068B` |
| Debug PDB SHA-256 | `A7568E59478A9D600A83C938CB5AA28E16CF1C90B4FC7CB72783114C55234852` |
| Release DLL SHA-256 | `A72B03DCA725F8A88C8B3C356B6F9F46A892B14696ADB79DA7E791BD7C6719B9` |

程序集检查证明 Debug 类型含完整 46 个方法及 12 个登记 Hook；Release 类型仅含属性、no-op 登记/验证与静态构造器，不含专项业务 Hook。

构建和测试日志：`Issue7-Release-final-build.log`、`Issue7-tests-final-build.log`、`Issue7-tests-final-run.log`、`Issue7-Debug-final-build.log`。

### 四、子智能体独立审核记录

| 轮次 | 判定 | 结果 |
| :--- | :--- | :--- |
| 1 | FAIL | 阻断：误用 `tellState` 作为拉回链；authority 默认 false 可能误报提交 |
| 2 | PASS | 改用真实 `ReceiveSimulateMispredictedInputs`，补充 Teleport/Collider；提交结果严格分类 |
| 3 | PASS | 最终碰撞节流增量线程安全、只读且不抑制关键纠偏事件；无阻断项 |

专项审核覆盖需求符合性、真实 U3 方法签名、线程安全、状态分类、异常隔离、隐私脱敏、Debug/Release 条件边界和最终二进制哈希。

### 五、偏离、残余风险与 QA

- **版本**：保持 `0.2.3.68-beta.2`，未提升正式版本号；仅 Debug 增加 `Issue7-Debug` 配置标记。
- **无业务修复**：本轮只构建诊断证据链，不强制替换资产、碰撞或网络状态。
- **环境门槛**：两端必须移除旧包 `2867869391`，保留 `2136497468` 与 `3759194844`，并部署完全相同的 Debug DLL 哈希。
- **运行证据缺口**：尚无当前哈希的主客机 UMM 包，因此 issues#7 根因与修复结论仍为待验证。
- **有界残余项**：过期 Collider 关联条目可能保留至会话 reset；单地图内有界，不影响原生路径或本轮关键证据。

详细双机步骤：`issues#7/DEBUG-TEST-INSTRUCTIONS.md`。

### 六、最终结论

- **构建与静态门禁：PASS**。
- **子智能体最终审核：PASS，无阻断项**。
- **issues#7 运行根因/修复：尚未关闭，等待当前 Debug 哈希的双机诊断包**。
