# 缺陷修复执行报告 - 0.2.3.69-beta.2

### 一、问题定位与修复策略

- **现象**：Elver `Binary_State` 权限门在房主与客机同区时可通行；房主离开后，客机仍能看到开门动画，但主机权威 Collider 阻挡并持续纠正客机位置。
- **运行证据**：`0.2.3.68 Debug` 双端日志证明 request、authority commit、`recipients=1` 和 client receive 全部成功；房主离区后目标 `30,31:181` 为 `used=True`、`activeInRegion=False`、`colliders=2 enabled=2 activeEnabled=2`，随后主机碰撞与客机 `0.35-0.72m` misprediction correction 同窗出现。
- **根因**：U3-SDK 专用服务器在 `ServerPrefabUtil.RemoveClientComponents` 中将 Legacy `Animation.cullingType` 设为 `AlwaysAnimate`，明确用于保证门开关动画在服务器推进；图形化 listen-host 不走该 `Dedicator.IsDedicatedServer` 路径。既有远区碰撞补丁关闭渲染器后只恢复根节点，导致客机视觉门与主机权威 Collider 姿态分叉。
- **修复策略**：仅在 P2P listen-host 远端 LevelObject 覆盖、对象有 Collider 且交互类型为 `InteractableObjectBinaryState` 时，保存每个 Animation 原 culling type 并设为 `AlwaysAnimate`；策略必须先于根节点 `SetActive(true)`，以便 `OnEnable` 立即应用当前门状态。区域退出、客机断线及会话清理均恢复原值。

### 二、源码溯源与核心变更

| 需求点 | 落实位置 |
| :--- | :--- |
| 复用 U3DS 的门动画语义 | `Patches/LevelObjectRemoteCollisionPatch.cs:265` 的 `ApplyRemoteAnimationPolicy` |
| 只处理远端覆盖、IOBS、Collider 对象 | 同文件 `112-155`、`265-291` |
| culling 策略早于根激活 | 同文件 `155-159` |
| 覆盖撤销与断线恢复 | 同文件 `116-153`、`229-253`、`309-383` |
| 单一异常资产不阻断其他恢复 | 同文件 `331-382` 的逐 Animation 异常隔离 |
| 修复 Debug 配额空引用并记录 culling | `Patches/Issue7ObjectBinaryStateDiagnosticPatch.cs:550-595,655-681` |
| 锁定保存/恢复与调用顺序 | `WhitelistTests/RemoteCollisionAnimationPolicyTests.cs:11-50` |

核心差异：

```diff
 if (levelObject.interactable is InteractableObjectBinaryState)
 {
     originalCulling[animation] = animation.cullingType;
     animation.cullingType = AnimationCullingType.AlwaysAnimate;
 }
 gameObject.SetActive(true); // policy is applied first

+ coverage removed / remote disconnect / session reset:
+ restore each original culling type with per-animation exception isolation
```

版本同步至 `0.2.3.69-beta.2`；Debug AssemblyConfiguration 为 `Issue7-Fix-Debug`。新增 `UnityEngine.AnimationModule` 显式引用，不引入第三方库。

### 三、编译、自测与产物状态

| 项目 | 结果 |
| :--- | :--- |
| Release Rebuild | `0 errors / 0 warnings` |
| 测试项目 Rebuild | `0 errors / 0 warnings` |
| 自动化回归 | `60/60 PASS` |
| Debug Rebuild | `0 errors / 0 warnings` |
| Debug AssemblyVersion | `0.2.3.69` |
| Debug AssemblyConfiguration | `Issue7-Fix-Debug` |
| Debug MVID | `570ecbff-ea3d-409e-b4c7-6531097c8b7a` |
| Debug DLL SHA-256 | `9213E6DDE4C430F4DEC5F79B99961D17CD4D5F58A4494DBF17EF86DD6A37FD0E` |
| Debug PDB SHA-256 | `F14E60ED6B114CEB4AE7462266E8116A5C5F400838D4AC1898ED72E3BBF14F0A` |
| Release DLL SHA-256 | `855950FB987521B79CD7545F2EA31DCEA0DBA916589CC961DE9D6429CA9A5B5B` |

构建日志：`Issue7-0.2.3.69-Release-build.log`、`Issue7-0.2.3.69-tests-build.log`、`Issue7-0.2.3.69-tests-run.log`、`Issue7-0.2.3.69-Debug-build.log`。

交付目录：`issues#7/builds/0.2.3.69-beta.2-debug/`。复制后的 DLL/PDB 已重新计算哈希并与 `bin/Debug` 一致。

### 四、子智能体独立审核记录

| 轮次 | 判定 | 说明 |
| :--- | :--- | :--- |
| 1 | PASS | 无阻断项；确认 listen-host/远端覆盖/IOBS/Collider 范围、重叠覆盖差分、断线与会话恢复、普通家具路径保持。建议全量恢复逐项隔离异常。 |
| 2 | PASS | 采纳建议后复核；对象级失败项保留重试，全量恢复单项异常不再中断后续对象，最终释放跨会话引用。无阻断项。 |

专项审核覆盖需求符合性、并发与游戏线程前提、Unity destroyed-object 语义、覆盖重叠、恢复一致性、异常路径、性能范围、环境分支和测试真实性。

### 五、偏离与残余风险

- **无需求偏离**：没有改变 Binary State RPC、权限条件、玩家位置或网络协议；没有对全地图 Animation 强制 `AlwaysAnimate`。
- **静态门禁 PASS，不等于运行修复 PASS**：IL 测试证明策略结构、恢复调用和调用顺序，不能替代 Unity 双机物理运行。
- **环境门槛**：房主测试前仍应移除旧 Elver 汉化包 `2867869391`，保留原版 Elver `2136497468` 与纯文本汉化 `3759194844`，避免历史资产冲突干扰结论。
- **线程前提**：相关状态沿既有 `LevelObjects.Update`、Provider 断线和会话生命周期游戏线程调用；本轮未新增跨线程入口。

### 六、QA 验收用例

1. 两端部署完全相同的 Debug DLL SHA-256，并确认启动日志包含 `strategy=postfix-root-reactivation+dynamic-animation`、`debug-hooks=12/12`。
2. 同一会话、同一权限门依次测试房主同区、房主离区、房主返回；每阶段执行开门、关门和双向穿越。
3. 房主离区时确认目标门 `alwaysAnimate > 0`，客机无持续拉回；房主返回后视觉与 Collider 一致。
4. 客机断线后确认覆盖撤销无异常，再次连接重复测试。
5. 房主远离后测试普通家具/柜子，确认客机仍不能穿模。
6. 双端由 UMM 导出新诊断包并注明角色、时间、门位置和 DLL 哈希。

### 七、最终结论

- **源码、编译、自动化测试、二进制身份与独立审核：PASS**。
- **issues#7 运行验收：待当前 Debug DLL 哈希的新双机证据，不标记为已关闭**。
