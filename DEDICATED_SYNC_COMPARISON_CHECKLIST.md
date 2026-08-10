# Dedicated Server 原版同步行为与 P2P Listen Host 对照清单

> **2026-08-10 状态说明**：本文档保留为 U3DS 权威实现与 listen-host 适配的历史对照资料。SteamP2PFriends 当前产品路线不启动 U3DS，而是在严格 P2P Host 门控下复用原版服务端权威分支。本文中的旧阻断项不得直接视为 `0.2.3.56` 的当前发布状态；请以 [README.md](./README.md)、[CHANGELOG.md](./CHANGELOG.md) 和 [AUDIT_CHECKLIST.md](./AUDIT_CHECKLIST.md) 页首为准。

---

**文档性质**：状态同步阶段的强制审计基准（Living Checklist）  
**适用项目**：SteamP2PFriends  
**建立日期**：2026-07-24  
**当前基线**：v0.2.3.39 stage 5B-1B v2.5.1 完成；第 33 次综合回归 7/7 场景通过，Codex 第七十次审计裁决 🟢 有条件采信 + 免除 v2 重写 + 放行认证路径只读验证，Codex 第七十一次审计裁决 🟢 九项返修通过 + Stage 5B 合格收官，Codex 第七十二次审计裁决 🔴 v1 阻断 + 🟡 放行 v2 返修 + ⚪ DLL 五维降级为 SHA-256 一致，Codex 第七十三次审计裁决 🔴 v2 阻断 + 🟡 放行 v3 设计返修；DLL SHA-256（主机基线）：`C5483DF751D540092EBC2CB2E3636D42F0BF4624D75079BCE8567B596DE13225`；**Codex 72nd §5.2 修订**：客机 DLL 证据门降级为 SHA-256 only（不再要求 MVID/PE 时间戳/文件大小/写入时间作为独立阻断条件）；客机实际部署路径唯一 + SHA-256 一致即可
**历史构建基线**：
- v0.2.3.39 stage 5B v6.6（Zombie 专项）+ Barricade v2.2（静态审计前）— 第 32C 次 Zombie 专项双机测试完整通过 7/7 项，Codex 第五十五次审计裁决 🟢 32C 完整通过；Zombie 权威生命周期主缺陷（根因 B）和完整快照刷新缺陷（根因 C）已闭环 E5
- v0.2.3.39 stage 5B-1B v2.5.1 — 32B Barricade 双机专项测试通过 + 33rd 综合回归通过 + Codex 70th 有条件采信 + Codex 71st 九项返修通过
**Codex 71st §2 P1-DOC-2 修订**：顶部基线已更新为 v2.5.1 / DLL `C5483DF...3225`，旧 v6.6/Barricade v2.2/DLL `483344...` 保留为历史构建记录
**权威源码**：`D:/Agent-工作目录/U3-SDK/Assets/Runtime/Assembly-CSharp/Unturned/`  

---

## 0. 强制审计规则

从本清单建立之日起，在“状态同步问题”阶段进行的每一次代码审计、实施审计、单机冒烟审计和双机回归审计，都必须：

1. 在报告开头声明已对照本文件，并记录本文件的日期或 Git 版本。
2. 明确本轮影响的同步条目 ID，禁止只写笼统的“世界同步已修复”。
3. 为每个受影响条目分别核验：原版行为、插件实现、主机发送、客机接收、人工行为。
4. 报告结束前更新本清单对应条目的证据等级、最新证据和剩余缺口。
5. 新发现的同步子系统必须先添加新条目，再开展修复；禁止在清单外静默扩大补丁范围。
6. 若报告没有逐项对照本清单，审计结论最高只能是“证据不完整”，不得放行稳定版。

本清单只把 Dedicated Server 当作**原版权威同步行为参考实现**，不授权把 Listen Server 全局伪装成 Dedicated Server。

---

## 1. 不可违反的架构边界

### 1.1 允许的做法

- 读取 U3-SDK，定位 Dedicated Server 的生成、权威 tick、初始快照、增量广播和接收入口。
- 优先复用 Listen Host 已自然满足的 `Provider.isServer == true` 路径。
- 仅对被证明缺失的、精确的 `Dedicator.IsDedicatedServer` 调用点进行条件扩展。
- 条件扩展必须同时受 P2P Host 模式和远端非 Loopback 接收者约束（若该调用点涉及接收者）。
- 保留原版 Dedicated Server 行为不变，并对普通单机、客机进程、LAN/其他模式 fail-closed。

### 1.2 禁止的做法

- 全局修改、伪造或 Hook `Dedicator.IsDedicatedServer` 返回值。
- 将全部 `Dedicator.IsDedicatedServer` 分支机械替换为 `Provider.isServer`。
- 因为某个分支在 Dedicated Server 中运行，就默认它属于网络同步。
- 把无头渲染、GSLT、公服广告、专服命令行、进程生命周期、专服衰减或性能优化逻辑带入 Listen Host。
- 仅凭 Harmony Patch 登记成功、主机方法命中或网络包出现，就宣布人工功能通过。

---

## 2. 统一证据等级

| 等级 | 名称 | 必须具备的证据 |
|---|---|---|
| **E0** | 未溯源 | 尚未确认原版 Dedicated/Server 行为 |
| **E1** | 原版已溯源 | 有 U3-SDK 文件、方法、条件和发送/接收入口证据 |
| **E2** | 插件已覆盖 | 已证明 Listen Host 自然执行，或已有精确补丁覆盖缺失分支 |
| **E3** | 主机侧运行 | 双机日志证明权威生成、tick、ask/Send/广播入口实际命中 |
| **E4** | 双端链路闭环 | 主机 Send/ask 与客机对应 `Receive*` 可按同一事件或状态关联 |
| **E5** | 人工行为通过 | 双方最终状态一致，并完成正向、反向、重连/跨会话等适用场景 |

状态标记：

- ✅ 已通过：达到该条目要求的 E5。
- 🟢 链路通过：达到 E4，但仍需补充人工或边界回归。
- 🟡 部分通过：E1-E3 或人工场景不完整。
- 🔴 失败/阻断：已出现可复现的状态不一致。
- ⚪ 未审计：E0。

---

## 3. 每条同步链的固定审计模板

每个条目必须按以下链路记录，不得省略中间层：

```text
原版权威条件
→ 原版生成/更新入口
→ 原版发送入口及接收目标
→ Listen Host 是否自然执行
→ 插件是否只补齐精确缺口
→ 主机运行时 Send/ask 证据
→ 客机 Receive 证据
→ 双方人工观察结果
→ 断线重连/跨会话/复活后的状态
```

---

## 4. 核心同步能力对照矩阵

> “当前证据”以 v0.2.3.37 第 26/27 次双机测试和现有源码为基线。后续每次审计必须更新该列。
>
> **v0.2.3.38 阶段 2 诊断补丁登记**（2026-07-25 返修版 v4，P0-R9 修复）：3 个 P0EDiagnostic patch（UseableBarricadeDiagnosticPatch 8 DP / ZombieEntityMappingDiagnosticPatch 7 DP / PlayerManagerCullingDiagnosticPatch 3 DP，共 18 DP）已编译登记，**不改变任何控制流或返回值**，仅取证。返修版 v2 修复 Codex 阶段 2 外部审计 P0-R1~R7（单一 struct __state、修正 Alive 签名、isBusy 直读 player.equipment.isBusy、isUseable 属性反射、identity-based 登记、会话 Reset、per-DP+bound 节流、新增 build/dropBarricade 权威创建点）。返修版 v3 修复 Codex 阶段 2 v2 审计 P0-R8（DP-8 Prefix 中 `group` 字段未脱敏）。返修版 v4 修复阶段 3A 实机冒烟发现的 P0-R9（`ZombieRegion.PlayerCountInRegion` 是 property 非 field，原 `AccessTools.Field` 返回 null 导致 Zombie patch fail-closed，7 DP 全部未登记 -> DiagnosticBuildValid=false -> P2P 入口阻断）。SHA-256：`4554CC104295A57BC6BAE6B48EE1828746AE30F7C2CF608E046238141AAB454F`。按 Finding 7 不提升 E 等级。
>
> **v0.2.3.38 stage 4C 第 29 次双机诊断基线**（2026-07-26，Codex 第三十八次审计裁决后）：4C-0/4C-1 通过；4C-2 ⚪ 取消后续实机补测（v1.3 调整，576 米为原版规则非当前缺陷）；4C-3 🔴 缺陷复现且根因证据闭环（P0-S3 旧状态跨会话残留，独立根因 A）；4C-4 🔴 失败（请求已到主机，首次权威接收未正常完成，第二次因 wasAsked=true 提前返回）；4C-5 🔴 失败且生命周期缺陷闭环（Listen Host 离区销毁仍有远端客机占用的权威 Zombie Region，独立根因 B，源码+实机证据 E5）；4C-6 🔴 失败（完整重建快照缺失，独立根因 C，强候选 E4）。DP-8.7 已触发 4 次（host 2803/7319/8097 + client 6618），R5/R6 证据闭环。Codex §6 授权 Stage 5A 四项只读审计；§7 禁止编码 DP-9/10/11、禁止新增 DamageZombieRequest/Zombie.applyClothing 等 U3-SDK 中不存在的 Hook、第 30 次双机测试冻结。
>
> **v0.2.3.39 stage 5B v6.6 修复完成基线**（2026-07-27，Codex 第五十四次 + 第五十五次审计裁决后）：
> - **根因 B 修复（SYNC-ZOMBIE-02）**：v6.6 介入点 `TryProtectOldBound`（主机本地离区且远端客机仍占用 oldBound 时，跳过权威 Region 销毁）+ `TryProcessNewBound`（主机进入 newBound 且 newRegion.isNetworked=true && newLoadedBound.isZombiesLoaded=false 时，跳过 generateZombies 整体重建，保留原实体集合连续性）。Harmony Prefix 优先级 VeryLow，与 P0-D 互斥（P0-D 处理 IsLocalPlayer=false 远端客机，v6.6 处理 IsLocalPlayer=true 房主本地）。
> - **根因 C 修复（SYNC-ZOMBIE-01）**：因 v6.6 TryProcessNewBound 跳过 generateZombies 整体重建，原实体集合（含服装字段）保留，客机无需重新接收完整快照。补测 L1588 TryProcessNewBound 触发 + L1590 POST newRegion(bound=0) after:count=22（无 generateZombies(0) 重建）+ 客机 ReceiveZombies 仅 1 次（无二次刷新）。
> - **第 32C 次双机测试 7/7 项完整通过**：① TryProtectOldBound 触发（host L1710）② 客机攻击有真实伤害并可击杀（host L1982 sendZombieDead id=11 主机权威广播，独立于 P0-C1 的死亡事件链路）③ 僵尸 AI 追逐位置变化 ④ P0-C1 周期状态链 20 次周期接收（仅负责位置/朝向，不负责死亡事件）⑤ 主客机状态完全一致 ⑥ 服装同步（DP-4 主客机两端一致）⑦ 无异常。
> - **DLL SHA-256**：`483344BAB6E1494FE853494E5636F61EF9C7A9C1F4C1E49BBCDCE0EC0B99140E`（673,280 bytes）。
> - **Codex 55th 纠正 Agent 报告两处错误**：① 不得用 `id=0` 证明房主离区后击杀（host L1709 id=0 已经死亡=True，真正证据是 id=11：host L1710 房主离区 -> client L1708 ReceiveZombieDead id=11 -> host L1982 sendZombieDead id=11）；② 不得写"P0-C1 把客机击杀上传给主机"（正确链路：客机攻击请求 -> 主机权威计算伤害与死亡 -> 主机广播死亡事件 sendZombieDead；P0-C1 只负责周期位置/朝向状态广播）。
> - **Codex 55th 授权**：修正 32C 原报告/补测报告/本清单；开始 Barricade v2.2 静态审计；规划第 33 次综合回归。
> - **Codex 55th 暂不放行**：Barricade v2.2 未经静态审计直接编码；32B Barricade 双机测试；第 33 次综合回归实际执行；认证路径改造。

| ID | 同步能力 | 原版权威入口/参考 | 当前等级 | 当前状态与证据 | 下一验收门槛 |
|---|---|---|---|---|---|
| **SYNC-PLAYER-01** | 玩家加入、实体创建、远端模型生成 | `Provider.accept`、`SteamPlayer`、`Player.InitializePlayer` | E4 | 🟡 v0.2.3.38 stage 4C 第 29 次双机诊断（2026-07-26，Codex 第三十八次审计裁决后）：4C-1 近距离模型可见通过（ClientRenderProbe 10 个样本全显示主机模型+服装）；4C-3 第二会话主机不可见客机模型经 P0-S3 直接证据闭环（host 4135 contained=True retryStatesCount=1 + host 5203-5204 retryStatesCount=1 TryGetValue=true attempt=0 completed=True playerIsNull=True + host 5230 both=0 smr enabled=0 持续至 5714，复活后 host 5822 both=3 smr enabled=2 恢复），属**独立根因 A：P0-S3 旧状态跨会话残留**，非 ZombieRegion 簇，非 576 米 culling。Codex §6 授权 Stage 5A-1 P0-S3 跨会话清理审计（建议文件：`P0-S3-second-session-runtime-evidence-audit.md`） | Stage 5A-1 只读审计通过审计门后，阶段 5B 修复实施；阶段 5C 第 30 次回归测试验证 |
| **SYNC-PLAYER-02** | 玩家位置、旋转、姿态和远端渲染 | `PlayerManager.sendPlayerStates`/`ReceivePlayerStates`、Movement/Stance/Look | E4 | 🟢 U3-SDK 可见距离 576 米（原版规则，非缺陷）；第 27 次第二会话双方出生约相距 1121 米，服务端按原版规则发送 `CulledPosition`，之后解除裁剪才发送真实位置。v0.2.3.38 阶段 2 返修版 v3 已登记 PlayerManagerCullingDiagnosticPatch（DP-1 SendPlayerStates_Write Prefix 读 forClient.culledPlayers/playersToSend；DP-2 ReceivePlayerStates Postfix；DP-3 tellState Prefix 读 isSentinel/before transform.position），**不改变控制流**，仅取证。**v1.3 调整**：4C-2 576 米 culling 边界补测正式关闭（576 米为原版正常裁剪距离，非当前缺陷；PEI 地形不适合肉眼边界验证；第二会话近距离不可见已证明属 P0-S3 跨会话残留根因 A，非距离裁剪） | 轻量检查：<100 米双方正常可见；玩家返回近距离后模型恢复；日志未显示近距离错误裁剪。重新开启条件：无遮挡 <576 米仍不可见，或返回近距离后仍因裁剪不恢复 |
| **SYNC-PLAYER-03** | Life/Inventory/Clothing/Equipment 等组件初始化 | `Player.InitializePlayer` 及各组件初始化 RPC | E3 | 🟡 第 27 次 clothing 可见链成功，不能再用“PlayerVisibility 未生效”解释远距离模型不可见 | 建立组件级人工验收；与距离 culling 分开审计 |
| **SYNC-PLAYER-04** | 伤害、死亡、复活状态 | `PlayerLife` 伤害/死亡/`SendRespawn` | E3 | 🟡 客机能被幽灵僵尸伤害并死亡，复活后状态恢复；恢复本身反证首次状态未闭环 | 正常僵尸伤害、双方死亡/复活、复活后实体唯一且状态一致 |
| **SYNC-ITEM-01** | 区域物品初始生成与快照 | `ItemManager.generateItems`、`askItems` -> `ReceiveItems` | E4/🔴（Codex 阶段 3B 审计返修后降级） | ❌ v0.2.3.38 v4 阶段 3B 双机诊断（2026-07-25）：3B-C2 实证物品刷新不一致（沙发位置主机看曲棍球棒、客机看望远镜），双方可各自拾取自己所见物品，最终状态不一致，原 E5 证据失真。P0-B-6 仍能保证物品存在性（success=4096），但不能保证内容一致性 | 区分"区域初始物品内容/随机生成一致性"与"掉落物增量同步"，独立审计 ItemManager 同步路径 |
| **SYNC-ITEM-02** | 地面物品掉落、拾取和移除增量 | `ItemManager.dropItem`、`ReceiveItem` 及移除 RPC | E4 | 🟢 历史测试已有收发和可拾取证据；第 27 次未完整覆盖全部双向行为 | 主客机分别丢弃、拾取、移除并跨区域复核 |
| **SYNC-BUILD-01** | 客机发起可建造物放置请求 | ID366 实际入口：`UseableBarricade.startPrimary`、`SendBarricadeNone/Vehicle` -> `ReceiveBarricadeNone/Vehicle` | E4（Codex 第三十八次审计裁决后维持 E4，仅“请求到达主机”已证实，根因未闭环） | 🟡 v0.2.3.38 stage 4C 第 29 次双机诊断（2026-07-26，Codex 第三十八次审计裁决后）：客机请求到达主机已闭环（host 6532/7380-7381 DP-5 PRE wasAsked(before=False)）；首次主机权威调用在 wasAsked=true 后、正常返回前发生异常退出或诊断链缺口（DP-5 POST 缺席，主机同事件 DP-4 checkClaims 日志缺席）；第二次请求因第一次残留 wasAsked=true 直接提前返回，pendingBuildHandle 未创建。**仅“请求到达主机”已证实，根因未闭环，功能未实现**。Codex §6 授权 Stage 5A-2 P0-E-2 Barricade 首次接收异常审计（建议文件：`P0-E-2-ReceiveBarricadeNone-first-call-audit.md`） | Stage 5A-2 只读审计通过审计门后，阶段 5B 修复实施；阶段 5C 第 30 次回归测试验证 |
| **SYNC-BARRICADE-01** | Barricade 区域初始同步 | `BarricadeManager.onRegionUpdated`、`SendRegion` | E4 | 🟢 已有精确远端区域同步补丁；主机放置后客机可见并可打开 | 跨区域、重连和跨会话初始快照 E5 |
| **SYNC-BARRICADE-02** | Barricade 放置、状态变化、拆除 | `UseableBarricade.startPrimary`、Send/ReceiveBarricade、`BarricadeManager.dropBarricade` 与状态/销毁广播 | E3/🔴（Codex 第三十八次审计裁决后维持 E3，仅“请求到达主机”已证实，禁止升级） | ❌ 主机->客机成立；客机->主机失败。v0.2.3.38 stage 4C 第 29 次双机诊断（2026-07-26，Codex 第三十八次审计裁决后）：客机请求到达主机已闭环（host 6532/7380-7381）；首次主机权威调用异常退出点未闭环（DP-5 POST 缺席 + 主机同事件 DP-4 checkClaims 日志缺席）；第二次请求因 wasAsked=true 提前返回。**仅“请求到达主机”已证实，根因未闭环，功能未实现**，禁止把“请求到达”写成“功能链路闭环”。Codex §6 授权 Stage 5A-2 P0-E-2 Barricade 首次接收异常审计 | Stage 5A-2 只读审计通过审计门后，阶段 5B 修复实施；阶段 5C 第 30 次回归测试验证 |
| **SYNC-STRUCTURE-01** | Structure 区域初始同步与增量 | `StructureManager.onRegionUpdated` 及区域发送 | E3 | 🟡 已有区域同步实现，当前缺少第 27 次完整人工证据 | 双向放置/拆除、跨区域、重连和跨会话验证 |
| **SYNC-RESOURCE-01** | 树木/资源初始状态与破坏、重生 | `ResourceManager.SendResources` → `ReceiveResources` | E3 | 🟡 已有精确区域同步实现；第 27 次资源行为未完整执行 | 主客机分别砍树，双方看到血量/倒下/重生一致 |
| **SYNC-OBJECT-01** | 地图 Object 初始状态与交互状态 | `ObjectManager.askObjects` → `ReceiveObjects` | E4 | 🟢 区域 ask/Receive 链已实现并有运行证据；复杂交互未全面人工验收 | 门、开关、可破坏对象等代表性类型双向验收 |
| **SYNC-VEHICLE-01** | 载具初始全量快照 | `ReceiveMultipleVehicles` | E4 | 🟢 客机已收到载具初始大包 | 跨会话和重连后无重复、无旧实例 |
| **SYNC-VEHICLE-02** | 载具位置、物理和周期状态 | `sendVehicleStates` → `ReceiveVehicleStates` | E4 | 🟢 第 26/27 次均有持续双端收发；严格双向人工驾驶未完成 | 主机/客机分别驾驶、碰撞、停车，双方位置一致 |
| **SYNC-VEHICLE-03** | 上车、座位、换座和下车 | enter/exit vehicle 请求与广播 | E3 | 🟡 Patch 和状态包存在；第 27 次人工行为验收不完整 | 双向驾驶、乘客、换座、下车全部 E5 |
| **SYNC-ANIMAL-01** | 动物初始快照 | `ReceiveMultipleAnimals` | E4 | 🟢 已有初始收包证据 | 跨区域、重连、跨会话状态无残留 |
| **SYNC-ANIMAL-02** | 动物 AI tick 与周期状态 | `AnimalManager.Update`、`sendAnimalStates` → `ReceiveAnimalStates` | E4 | 🟢 精确替换周期广播 dedicated-only 调用点，双端状态包已闭环 | 移动、受伤、死亡、掉落物的双向人工验收 |
| **SYNC-ZOMBIE-01** | 僵尸区域初始快照（完整快照含服装） | `SendZombiesToPlayer` -> `ReceiveZombies`（`ZombieManager.cs:674-694` 完整包字段 type/speciality/shirt/pants/hat/gear/position/dead；`:618-663` ReceiveZombies 用这些字段创建实体；`:623-626` isNetworked=true 时提前返回） | **E5** | ✅ **v0.2.3.39 stage 5B v6.6 修复完成**（2026-07-27，Codex 第五十五次审计裁决后）：根因 C 已闭环。v6.6 `TryProcessNewBound` 介入点在主机进入 newBound 且 `newRegion.isNetworked=true && newLoadedBound.isZombiesLoaded=false` 时跳过 `generateZombies(newBound)` 整体重建，保留原实体集合（含服装字段）连续性。第 32C 次补测 host L1588 `TryProcessNewBound newBound=0` 触发 + L1590 POST `newRegion(bound=0) after:count=22`（无 generateZombies(0) 重建）+ 客机 ReceiveZombies 仅 1 次（无二次刷新）。原实体集合（含已死亡僵尸 [0] dead=True）保留，客机无需重新接收完整快照。**注**：本修复通过"避免重建"路径绕开"补发完整快照"需求，而非直接实施 SendZombiesToPlayer 重发；若未来 v6.6 介入条件不满足（newLoadedBound.isZombiesLoaded=true），仍可能复现完整快照缺失。回归保护：第 33 次综合回归必须包含 Zombie 跨区域往返场景 | Stage 5C 第 33 次综合回归验证（含跨区域往返、跨会话、重连场景） |
| **SYNC-ZOMBIE-02** | 僵尸 AI tick、位置与周期状态 + Listen Host 权威 Region 生命周期 | `ReceiveZombieStates`（`ZombieManager.cs:280-296` 只更新位置）、ZombieManager server tick、`onBoundUpdated`（`:1450-1457` 离区销毁 / `:1460-1492` 服务器 PlayerCountInRegion 更新） | **E5** | ✅ **v0.2.3.39 stage 5B v6.6 修复完成**（2026-07-27，Codex 第五十五次审计裁决后）：根因 B 已闭环。v6.6 `TryProtectOldBound` 介入点在主机本地离区且 `remoteOccupantsInOldBound > 0`（oldBound 仍有远端客机占用）时跳过 `ZombieRegion.destroy()` 权威 Region 销毁；`TryProcessNewBound` 见 SYNC-ZOMBIE-01。第 32C 次双机测试 host L1710 `TryProtectOldBound oldBound=0 remoteOccupantsInOldBound=1` 触发，POST `oldRegion count=22 playerCount=2 isNet=True`（保留权威实体）。客机 L1708 ReceiveZombieDead id=11 + host L1982 sendZombieDead id=11 主机权威广播死亡事件链路完整。两条独立同步链已区分：① 死亡事件链（sendZombieDead/ReceiveZombieDead，主机权威）② 周期状态链（P0-C1 SendZombieStates_Write/ReceiveZombieStates，仅位置/朝向）。**注**：v6.6 Prefix 优先级 VeryLow，与 P0-D 互斥（P0-D IsLocalPlayer=false 远端客机，v6.6 IsLocalPlayer=true 房主本地）。客机断线/死亡/传送/世界重置时计数不泄漏由原版 PlayerCountInRegion 更新逻辑保障 | Stage 5C 第 33 次综合回归验证（含房主多次跨区域往返、客机断线重连、跨会话场景） |
| **SYNC-ZOMBIE-03** | 僵尸伤害、死亡、掉落与刷新 | Zombie damage/death、掉落、区域刷新 | E3 | 🟡 客机能受伤/死亡，但幽灵僵尸使当前证据失真 | 主客机分别击杀、掉落一致、刷新一致且无幽灵实体 |
| **SYNC-SESSION-01** | 主机退出菜单后重新开服的世界状态清理 | 各 Manager 的 level unload/load、静态集合和事件解绑 | E3 | 🟡 P0-B-6/SessionReuse 正常；v0.2.3.38 stage 4C 第 29 次双机诊断（2026-07-26）：4C-3 第二会话主机不可见客机模型经 P0-S3 直接证据闭环（host 4135/5203-5204/5230-5714），属**独立根因 A：P0-S3 旧状态跨会话残留**。`_retryStates` 中同一 SteamID 的完成状态未清除；第二会话初始化命中旧记录后提前返回，P0-S3 不再执行可见性补偿。复活触发新的原版状态刷新后模型恢复。Codex §6 授权 Stage 5A-1 P0-S3 跨会话清理审计（建议文件：`P0-S3-second-session-runtime-evidence-audit.md`） | Stage 5A-1 只读审计通过审计门后，阶段 5B 修复实施；阶段 5C 第 30 次回归测试验证 |
| **SYNC-SESSION-02** | 客机断开后重连 | 连接清理、区域订阅重建、实体重新生成 | E3 | 🟡 断线计数清理通过；第 27 次未完整执行重连 | 同一房主内断开重连后所有 E4 链重新建立且无重复 |
| **SYNC-PAUSE-01** | Listen Host ESC 时权威世界继续运行 | `Time.timeScale`、Provider/Manager Update | E5 | ✅ 远端客机在线时 `shouldIntervene=True`、`timeScale=1.00`，状态包持续 | 保持回归，不再扩大补丁范围 |
| **SYNC-WORKSHOP-01** | 双方已安装相同 Workshop 地图/物品后的状态同步 | MasterBundle、地图 Asset、各 Manager 通用同步链 | E0 | ⚪ 第 27 次未执行 | 第 28 次或独立测试完成手动同订阅兼容性验收 |
| **SYNC-WORKSHOP-02** | 必需内容清单、缺失提示和下载 | 专服 Workshop/UGC 清单逻辑，仅作参考 | E0 | ⚪ 尚未实现，规划为 v0.2.4 工作包 | 只读清单采集→差异诊断→UI 提示→订阅下载/重连 |
| **SYNC-WORLD-01** | 时间、昼夜和天气状态 | Provider/Lighting/Weather 服务器状态 RPC | E0 | ⚪ 尚未形成专项证据矩阵 | U3-SDK 溯源并增加至少一次长会话双端一致性测试 |
| **SYNC-WORLD-02** | 全局事件、空投等世界事件 | Event/Level 管理器服务器广播 | E0 | ⚪ 尚未专项审计 | 枚举原版事件发送入口，按是否适用于好友 P2P 决定范围 |

---

## 5. 当前阻断项与审计优先级

### P0：必须先闭环

**当前阶段**（v0.2.3.39 Codex 70th 采信后，进入认证路径只读验证阶段）：

**Codex 70th §1.1 采信可关闭的五组目标**（不得扩展为"所有状态同步 P0 全部解决"，Codex 70th §3 P1-8）：

- ✅ **根因 A 已闭环**（SYNC-PLAYER-01 + SYNC-SESSION-01 **子范围：P0-S3 跨会话旧状态残留**，P0-S3 跨会话清理第 30 次双机测试通过 + 第 33 次综合回归 S6 SessionReuse 验证通过。**子范围限定**（Codex 71st P1-DOC-7 + Codex 72nd §1.2-4 修订）：仅 P0-S3 子根因达 E5，**SYNC-PLAYER-01 条目整体仍维持 E4，SYNC-SESSION-01 条目整体仍维持 E3**（各自保持原等级，不一并升级为 E4）；玩家加入、实体创建、远端模型生成、会话世界状态的其他子项未在 33rd 关闭）
- ✅ **根因 B 已闭环**（SYNC-ZOMBIE-02 **子范围：Listen Host 离区销毁权威 Region**，v6.6 TryProtectOldBound + TryProcessNewBound，第 32C 次双机测试 7/7 通过 + 第 33 次综合回归 S2 通过。**子范围限定**（Codex 71st P1-DOC-7）：仅"主机本地离区且远端客机仍占用 oldBound"子根因达 E5，Zombie 周期状态、伤害广播、掉落等其他子项未关闭）
- ✅ **根因 C 已闭环**（SYNC-ZOMBIE-01 **子范围：完整快照刷新缺失**，v6.6 TryProcessNewBound 跳过 generateZombies 重建，原实体集合保留。**主证据来源**（Codex 71st P1-DOC-3 修订）：第 32C 次双机测试同一会话 L1588 TryProcessNewBound 触发 + L1590 POST newRegion(bound=0) after:count=22（无 generateZombies(0) 重建）+ 客机 ReceiveZombies 仅 1 次（无二次刷新）+ 实体 ID/服装/死亡状态对照（DP-4 主客机两端一致）。**第 33 次综合回归 S3 仅作为综合行为回归补充**，证明跨会话 Zombie 数量稳定（22 个），**不构成实体级一致证据**（Codex 70th §3 P1-4）。**子范围限定**（Codex 71st P1-DOC-7）：仅"主机进入 newBound 时跳过完整重建"子根因达 E5，Zombie 初始内容、跨会话实体级一致、死亡掉落等其他子项未关闭）
- ✅ **P0-E-2 Barricade 已闭环**（SYNC-BUILD-01 + SYNC-BARRICADE-02 **子范围：客机发起放置请求链路**，Stage 5B-1B v2.5.1 编码完成 + 第 32B 次双机专项测试通过 + 第 33 次综合回归 S4 客机放置链路完整通过 transform=366。**子范围限定**（Codex 71st P1-DOC-7）：仅"客机->主机放置请求"子根因达 E5，Barricade 区域初始同步、跨区域、跨会话快照、拆除链路等其他子项未关闭，SYNC-BARRICADE-01 仍维持 E4）
- ✅ **P0-D-ESC-2 暂停干预已闭环**（第 33 次综合回归 S5 通过；Codex 70th §3 P1-5 修订：实际表现为"检测暂停并恢复到 1.00"，非"全过程强制保持 1.00"；L2731 瞬间 timeScale=0.00 后被 patch 恢复到 1.00）

**576 米 culling 不参与裁决**（Codex 70th §3 P1-3 修订）：
- 第 33 次综合回归 S1 仅验证"近距离玩家模型可见性与第二会话恢复"
- `culledCount=0` 只说明 culling 未进入触发条件，**不构成对 576 米 culling 边界功能的强验证**
- 576 米 culling 实机任务已被 Codex 69th §6 R-4 删除，不参与本次裁决
- 不得写成"culling 未触发即通过"

**Codex 70th 授权的下一步**：
1. **认证路径只读验证**（Codex 70th §5 授权）：研究 transport SteamID、加入请求 SteamID、SteamPlayer.playerID.steamID、好友关系、fail-closed 边界
2. **并行补齐 IMP-1/4/5**（Codex 71st P1-DOC-4/P1-DOC-6 统一口径）：
   - **IMP-1 客机 DLL SHA-256**（Codex 72nd §5.2 修订）：不阻塞只读验证；**阻塞下一轮动态测试**（认证改造后 Auth-R1 测试前必须取回客机实际部署 DLL，与主机基线 `C5483DF...3225` SHA-256 比对一致；并确认插件目录中无第二份同名 DLL）。**不再要求** MVID / PE 时间戳 / 文件大小 / 写入时间作为独立阻断条件
   - **IMP-5 双机系统校时**：不阻塞只读验证；**阻塞下一轮动态测试**（双机时钟偏差必须 ≤ ±2 秒）
   - **IMP-4 录像归档**：不阻塞只读验证；**默认阻塞正式发布归档**；若下一轮测试计划仍将录像列为必需证据，则也在对应测试计划中显式列为前置条件
   - **只读报告提交不依赖 IMP 完成进度**：`auth-path-readonly-verification.md` 撰写完成后可立即提交下一次审计，IMP-1/4/5 单独并行补齐
3. **提交 Codex 71st/72nd/73rd 审计**：只读验证报告可独立提交（**Codex 72nd §1.2-3 修订**：移除"必须与 IMP-1/4/5 一起提交"口径，改为"只读报告可独立提交，IMP-1/4/5 不阻塞报告提交"）

**Codex 70th 继续禁止**：
- 修改认证代码或网络控制流
- `offlineOnly` 移除
- 新增 Harmony Patch、Tick 或反射
- 编译、部署或执行认证动态测试
- 新增诊断 patch 或重新编译（Codex 69th 明确禁止）
- 宣布正式版可发布

### 历史 P0 项状态

1. ~~`SYNC-ZOMBIE-02` -> Stage 5A-3 P0-E-1 Zombie 权威生命周期审计~~ ✅ **已闭环**（v6.6 TryProtectOldBound，32C 7/7 通过 + 33rd S2 通过）
2. ~~`SYNC-ZOMBIE-01` -> Stage 5A-4 P0-E-1 Zombie 完整快照刷新审计~~ ✅ **已闭环**（v6.6 TryProcessNewBound 跳过重建，32C 7/7 通过 + 33rd S3 通过；Codex 70th §3 P1-4 修订：仅证明数量稳定 22 个，非实体级一致）
3. ~~`SYNC-PLAYER-01` + `SYNC-SESSION-01` -> Stage 5A-1 P0-S3 跨会话清理审计~~ ✅ **已闭环**（第 30 次双机测试 P0-S3 第二会话回归通过 + 33rd S6 SessionReuse 通过）
4. ~~`SYNC-BUILD-01` + `SYNC-BARRICADE-02` -> Stage 5A-2 P0-E-2 Barricade 首次接收异常审计~~ ✅ **已闭环**（Stage 5B-1B v2.5.1 编码完成 + 32B 通过 + 33rd S4 客机放置链路完整通过 transform=366）

### 仍存在的 E0-E4 同步条目（Codex 70th §3 P1-8，不得宣称全部解决）

以下同步条目仍处于 E0-E4 证据等级，未在 33rd 综合回归中关闭：

- Item 初始内容不一致（待专项审计）
- 玩家组件/死亡（待专项审计）
- Zombie 掉落（`SYNC-ZOMBIE-03` 待专项审计）
- 载具（`SYNC-VEHICLE-02/03` 待专项审计）
- 资源（`SYNC-RESOURCE-01` 待专项审计）
- Structure（`SYNC-STRUCTURE-01` 待专项审计）
- Animal（`SYNC-ANIMAL-02` 待专项审计）
- Workshop 兼容（`SYNC-WORKSHOP-01/02` 待 P2 阶段）
- 全局世界事件（`SYNC-WORLD-01/02` 待远期）

### P1：P0 修复后补齐人工闭环

1. `SYNC-SESSION-02`：断开重连。✅ 33rd 客机 3 次连接（含 1 次会话内重连）均通过
2. `SYNC-VEHICLE-02/03`：严格双向驾驶、上下车。⏸️ 33rd 未包含载具专项（待后续场景补齐）
3. `SYNC-RESOURCE-01`、`SYNC-STRUCTURE-01`、`SYNC-OBJECT-01`：双向交互。✅ 33rd S7 物品存储可见性通过（用户人工观察）
4. `SYNC-ANIMAL-02`、`SYNC-ZOMBIE-03`：受伤、死亡、掉落。⏸️ 33rd 未包含僵尸死亡掉落专项（用户现场观察客机可触发僵尸追逐/攻击已部分覆盖）
5. **Zombie v6.6 回归保护**：✅ 33rd S2 + S3 通过（TryProtectOldBound/TryProcessNewBound 修复生效 + bound=0 zombieCount=22 跨会话数量稳定；Codex 70th §3 P1-4：仅证明数量稳定，非实体级一致）

### 后续工作包

1. `SYNC-WORKSHOP-01/02`：Workshop 兼容与必需内容工作包。⏸️ P2 可选，33rd 未测
2. `SYNC-WORLD-01/02`：全局时间、天气和事件覆盖盘点。⏸️ 远期

---

## 6. 每次审计报告必须包含的对照表

复制下表到每次状态同步审计报告中：

| 条目 ID | 审计前等级 | 本轮新增证据 | 审计后等级 | 是否回归 | 剩余缺口 |
|---|---:|---|---:|---|---|
| SYNC-... | E? | U3-SDK/代码/日志/人工证据 | E? | 是/否 | ... |

报告还必须回答：

1. 本轮是否新增或修改任何 `Dedicator.IsDedicatedServer` 调用点？
2. 若是，是否证明该调用点属于网络同步，而非 Dedicated 专属运行环境逻辑？
3. 是否仅在 P2P Host 模式生效？
4. 是否区分远端玩家与房主 Loopback 玩家？
5. 普通单机、客机进程、Dedicated Server 原行为是否保持不变？
6. 是否同时具备主机 Send/ask 与客机 Receive 证据？
7. 是否完成对应人工行为、重连和跨会话验收？

---

## 7. 维护规则

- 本文件位于仓库根目录，是跨版本持续维护文件，不随单次 `.audit` 归档冻结。
- 单次测试报告保存当时证据；本文件保存最新裁决。
- 新证据推翻旧结论时，必须降低等级并写明失败测试编号，禁止只追加乐观结论。
- “Patch 已登记”最高只能支持 E2；“主客机有包”最高只能支持 E4；只有人工最终状态一致才能达到 E5。
- 在所有 P0 条目达到 E5、P1 核心条目完成回归前，不得宣布“状态同步问题全部解决”。
