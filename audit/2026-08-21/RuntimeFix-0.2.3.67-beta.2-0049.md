# SteamP2PFriends 缺陷修复执行报告 - 0.2.3.67-beta.2

### 一、问题定位与修复策略

- **运行证据**：`issues#6/host-001238/LogOutput.log` 记录 `.66` 首次连接的 `Route B handshake permit -> Pending added -> Approve success -> Revoke success`，随后同一客机两次重连均被 `WHITELISTED` 拒绝；客机日志对应记录首次 `Accepted/Connected` 及后续两次失败。
- **根因 A**：旧 `ServerMessageHandler_InvokeMethod` 全局门在待审状态下截断全部服务端调用，连同世界初始化所需调用一起阻断，造成用户看到原版排队/加载界面而非进入世界后的软隔离。
- **根因 B**：`RejectedThisSession` 将拒绝或撤销后的 SteamID 锁死到当前房间会话，违背“踢出后可再次申请”。
- **修复策略**：删除全局 InvokeMethod 门，扫描当前 `Assembly-CSharp` 全部 `static *_Read(in ServerInvocationContext, ...)` gameplay RPC 读取器并逐一安装 pending 门禁；唯一显式放行 Steam Web API 认证响应。移除会话永久拒绝集合，让拒绝、超时和撤销只结束当前申请。待审指令在管理员/作弊判断前无条件拒绝。

### 二、源码溯源与核心变更

| 需求 | 落实位置 |
| :--- | :--- |
| 客机先完成原版握手和世界加载 | `Patches/P2PQuarantineAdmissionPatches.cs`：不再补丁全局 `ServerMessageHandler_InvokeMethod` |
| 世界内软隔离保持服务端权威 | `P2PQuarantineActionGatePatch.RegisterManual/ReaderPrefix`：发现并门控 96 个服务端 gameplay RPC 读取器 |
| 未审核玩家保持无敌 | `P2PQuarantineDamageGuardPatch.Prefix` |
| 待审输入本地抑制 | `Patches/P2PQuarantineClientInputPatch.cs` |
| 待审管理员也不能执行指令 | `Patches/P2PListenHostCommandPermissionPatch.ShouldBlock/Prefix` |
| 拒绝、超时、撤销后允许再次申请 | `Host/P2PApprovalManager.cs`：移除 `RejectedThisSession` 与 `RejectedForSession` |
| 新版本与旧运行证据隔离 | `SteamP2PFriendsPlugin.cs`、`Properties/AssemblyInfo.cs`、`README.md`、`CHANGELOG.md`：`0.2.3.67-beta.2` |
| 自动化覆盖真实 RPC 集 | `WhitelistTests/RouteBApprovalTests.cs` 的 B1/B7/B9/B11 |

核心语义变化：

```diff
- 待审时截断 ServerMessageHandler_InvokeMethod 全部调用
+ 仅截断 Assembly-CSharp 生成的服务端 gameplay RPC 读取器

- Reject/Revoke 将 SteamID 放入 RejectedThisSession，后续握手永久拒绝
+ Reject/Revoke/Timeout 结束本次申请并踢出，后续连接重新进入 Pending

- 已有管理员且启用作弊时可在 Pending 执行命令
+ Pending 优先级最高，远端 / 与 @ 指令无条件拒绝
```

### 三、编译与自测状态

- **插件 Release 构建**：`dotnet build SteamP2PFriends.csproj -c Release --no-restore`
- **测试 Release 构建**：`dotnet build WhitelistTests/SteamP2PFriends.WhitelistTests.csproj -c Release --no-restore`
- **编译状态**：两个项目均 `0 errors / 0 warnings`。
- **测试命令**：`WhitelistTests/bin/Release/SteamP2PFriends.WhitelistTests.exe`
- **自测结果**：`57/57 PASS`。B11 反射当前 `Assembly-CSharp` 发现 96 个被门控读取器，并确认 salvage、storage quick-grab、forage、farm harvest、door、input、inventory drop 等关键目标存在。
- **最终 DLL**：`bin/Release/SteamP2PFriends.dll`
- **长度**：`889344` bytes
- **SHA-256**：`A41E691D5F2E97622916E263481977A183CD1A73834B4DD9D4B8AF66D3B3D7B6`

### 四、子智能体审核记录

| 轮次 | 判定 | 阻断项与处置 |
| :--- | :--- | :--- |
| 1 | FAIL | 删除全局门后缺少服务端权威限制；pending 管理员可执行指令。新增细粒度 ActionGate 与 pending 优先命令拒绝。 |
| 2 | FAIL | 人工 RPC 清单遗漏 salvage/storage/forage/farm/interactable。改为扫描全部生成读取器并逐一验证 Harmony owner。 |
| 3 | PASS | 独立确认 96 个读取器、关键业务入口、认证例外、入口 fail-closed、57/57 测试及最终 DLL 身份，无阻断项。 |

### 五、偏离与妥协说明

- 无需求偏离。
- 未部署、未打包、未发布。`.66` 的双机结果不能证明 `.67`；本轮结论仅为源码、构建和自动化审核 PASS。

### 六、双机测试建议

1. 双端部署上述同一 SHA-256，归档双方完整 `BepInEx/LogOutput.log`。
2. 陌生客机连接后必须出现 `Verify -> Authenticate -> Accepted -> LocalPlayerCreated`，退出原版 LoadingUI 并看见世界，再进入 Pending。
3. Pending 30 秒内验证移动/攻击、拾取/丢弃、建筑回收、仓储快速拾取、采集、门与对象交互、`/` 和 `@` 指令均被阻止，且僵尸不能伤害客机。
4. 房主批准后限制立即解除，玩家继续游玩。
5. 分别验证拒绝、30 秒超时、批准后撤销；每次踢出后同一 SteamID 都应能重新 `Accepted -> Pending` 并再次申请。

### 七、最终结论

- **PASS（静态/构建/自动化/子智能体独立审核）**。
- **运行门仍待 `.67` 当前哈希的全新双机证据，不得继承 `.66` 的运行 PASS。**
