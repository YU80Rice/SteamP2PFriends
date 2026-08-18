# Changelog

## 项目发展史

从原版 P2P 原型、LaunchP2PHostManager 与独立 U3DS 路线探索，到 SteamUser 身份重构、连续双机失败复盘、审计制度化和 `v0.2.3.60-beta.1` 总回归，完整叙事与证据索引见 [DEVELOPMENT_TIMELINE.md](./DEVELOPMENT_TIMELINE.md)。本文只把归档报告、源代码目录、Git 提交和测试 artifact 能够支持的内容写成事实。

本项目从 `0.2.3.56` 开始在此记录面向用户的发布变更。更早的实验、审计与双机测试历史保留在 `AUDIT_CHECKLIST.md` 与本地 `.audit` 归档中。

## v0.2.3.62-beta.2 - 2026-08-18

> Beta 2 低频连接取证构建：不修改 SteamID P2P listen-host 的连接、认证、准入、白名单或重传行为，只补齐可归档的连接事件证据。

### Changed

- 插件、Assembly 与文件版本更新为 `0.2.3.62`；发布标识为 `v0.2.3.62-beta.2`。
- 默认输出低频 `[P2P-Connection]` 事件，不受 `VerboseDiagnostics=false` 与 `RouteDiagnostics=false` 影响：客机连接调用和状态迁移、收到 `Verify`、`Authenticate` 发送调用、主机收到/处理 `Authenticate`、主机接受或拒绝、客机收到 `Accepted`、本地断开请求。
- 认证日志只记录事件、SteamID、传输类型、状态与异常类型；不读取或输出认证票据内容，不创建重传或修改任何原版返回值/异常。

### 验证边界

- 本构建已完成静态目标解析和自动化回归；旧 `0.2.3.61` 双机归档不能替代新 DLL 的双机运行时验证。

## v0.2.3.61-beta.2 - 2026-08-18

> ✅ **Beta 2 公开预发布版**：面向 SteamUser P2P listen-host 的本地多人联机。双端手动测试归档 `Beta2-P2P-AHost-20260818-1300` 的 `evidence-summary.json` 为 `AllOK=true`。

### Changed

- 插件、Assembly 与文件版本更新为 `0.2.3.61`；发布标识为 `v0.2.3.61-beta.2`。
- 生产默认关闭详细日志和 Steam Networking Sockets 路由诊断；详细日志改用新的 `VerboseDiagnostics` 配置键，旧开发期 `VerboseLog=true` 不会在升级后重新启用探针。诊断探针不可用时仅降级日志，不再阻断可独立验证的 P2P 功能路径。
- 移除历史 A/B `FullFixBuild` 开关。公开 Beta 始终登记和验证完整兼容性补丁集。
- SteamPlayer 构造器补丁改为精确参数签名解析；游戏 ABI 不匹配时 fail-closed，不再按反射返回顺序选择构造器。
- 编译工程的 Release 配置将警告视为错误，并以当前本机 Unturned/BepInEx 运行时引用进行构建审计。
- Harmony 兼容性判定改为分级：本插件的关键 hook 缺失或重复仍 fail-closed；普通第三方观察型补丁仅记录告警；同目标 Transpiler，以及 P2P transport/auth/route 关键目标上的第三方 Prefix、Finalizer 或 Transpiler 会明确阻断。
- 每次启动自检会扫描本插件实际注册的全部 Harmony 目标，并写出 `BepInEx/config/SteamP2PFriends/p2p-harmony-compatibility.json`，记录第三方 owner、目标、patch 类型、优先级和阻断决策。
- 当前发布 DLL SHA-256：`3031C999138E850AED61636032B1580FAFBC6DC35B2F1F3D673262C43C67FC89`；自动化测试 `268/268 PASS`。
- `TestLogs` 增加可选 CFG 快照/哈希辅助记录：保存双方 `com.yu80rice.steamp2pfriends.cfg`，并要求 `VerboseDiagnostics=false`、`RouteDiagnostics=false`。该工具不替代手动部署、手动日志归档或实际联机测试。

### Removed

- 删除依赖废弃 `ESteamPacket` 的 SteamChannel 发送诊断补丁及其断线清理路径。
- 删除同账号 LAN 重复 SteamID 绕过及其死代码；公开 Beta 不再提供身份去重绕过。

### 已知限制

- 本版本不承诺与所有第三方 BepInEx、Harmony Transpiler、Doorstop 或原生注入工具共存。启动期原生注入失败必须同时提供 BepInEx preloader 日志，不能仅凭游戏内日志归因。

## 0.2.3.60 - 2026-08-13

> ✅ **Beta 预发布版**：已完成 P2P listen-host 双机总回归（Case：`Final-Beta-Test-20260813-1300`）。

> 发布标识：`v0.2.3.60-beta.1`（首个公开 Beta 测试版本；插件内部 AssemblyVersion 保持 `0.2.3.60`）。

### Fixed

- 修复容器向背包 Ctrl+右键快速转移后，继续移动物品可能在旧格子留下不占格、但会遮挡真实物品的幽灵图标。
- 修复仅校验并必要时重建房主本地 UI 投影；不修改真实库存、物品数据、RPC 或背包权威事务。
- 新增字段类型契约和 drag/swap/drop Harmony 注册启动自检，不兼容时 fail-closed 禁用候选联机入口。

### Verification status

- Release 编译 `0 errors / 18 个既有 CS0612 / 0 新警告`；全量自动化测试 `261/261 PASS`。
- 运行时专项复测通过：Host/Client 双端使用同一 DLL，背包幽灵图标未再出现。
- 总回归证据：`AllOK=true`，双端日志封存哈希一致，启动自检 `pass=26 fail=0`。

### Beta 测试实现方法与免责声明

- 测试拓扑为 Steam P2P listen-host：房主同进程承担 server 与本地 client，另一台机器作为 P2P 客机。
- 双端部署同一 `0.2.3.60` DLL，核对 Size、SHA-256、MVID，并在测试前后封存 BepInEx/Unity 日志及 SHA-256。
- 覆盖 SteamID 加入、审批隔离与解除、房间设置、作弊权限、PVP/死亡规则、库存操作、IPv4/域名直连、世界播报、正常/异常退出等流程。
- 本版本为 Beta 预发布软件，不保证对所有 Unturned 版本、地图、模组、第三方网络节点、NAT、运营商、防火墙或未来游戏更新兼容。使用前请备份存档；因使用本插件造成的存档损坏、物品丢失、连接失败、数据不同步或服务中断，由使用者自行承担。

## 0.2.3.59 - 2026-08-11

> ⚠️ **诊断候选版**：本版实施 Stage 10 世界播报。世界播报动态行为**尚未完成独立动态验收**，不得宣称已 PASS。

### Added

- 新增**世界状态播报（全员系统公告）**：在插件 listen-host 世界中，房主服务端订阅原生权威事件，向当前世界所有玩家（房主、已批准客机、仍在 30 秒审核隔离中的客机）广播同一条系统消息。
  - 覆盖事件：已批准玩家进入世界、新玩家进入等待审核、房主批准并解除隔离、30 秒审核超时、已批准玩家离开、待审核玩家主动离开、玩家死亡（`PlayerLife.onPlayerDied` 全部 30 种 `EDeathCause`）。
  - 全员发送固定 `fromPlayer=null / toPlayer=null / useRichTextFormatting=false`；隔离玩家的 5 秒倒计时仍为定向私聊提示，不属于世界公告。
- 新增 `P2PWorldStatusBroadcaster`（生命周期/事件入口/幂等/节流/发送）与 `P2PWorldStatusTemplates`（纯函数名称清洗、死亡映射、147 条候选文案）。
  - 文案：29 种普通死因各 2 实用 + 3 趣味（随机选 1，随机源为播报器私有的可注入 `System.Random`，不使用 `UnityEngine.Random`）；`SUICIDE` 仅 2 条简短实用文案，禁止趣味回退。
  - 幂等/节流：同一玩家死亡最小间隔 2 秒；全局最多 8 条/10 秒；超限直接丢弃不排队。
  - 名称安全：清除控制字符/换行/富文本标签，最长 32 UTF-16 字符且不截断代理项；SteamID 不进入聊天文案。
- 新增 BepInEx 配置（`[World Broadcast]`）：`EnableWorldStatusBroadcast=true`、`BroadcastJoinLeave=true`、`BroadcastDeaths=true`；总开关关闭时不订阅死亡事件或发送消息。

### Changed

- `P2PQuarantineAdmissionService.PromoteConnected` 现返回显式 `QuarantinePromotionResult`（Ignored/AlreadyApproved/Activated/RejectedMissingReservation/RejectedSignalFailure），连接播报只由 AlreadyApproved/Activated 触发。
- 审批成功播报严格位于白名单持久化、隔离解除、pending 清理全部成功之后；超时播报在 Kick 前写 expected-departure 标记，断线消费标记不重复播报普通离开。

### Known limitations（未变）

- 世界播报动态行为尚未完成独立动态验收（本版仅静态候选）。
- SakuraFRP/公网 UDP 穿透与域名解析尚未完成独立动态验收（Stage 9-3 静态候选）。
- 背包物品移动/丢弃后的幽灵图标缺少可用运行时证据，当前挂起。
- Steam 付费外观/装饰资产尚未完成专项双端指纹验证。

## 0.2.3.58 - 2026-08-11

> ⚠️ **诊断候选版**：本版实施 Stage 9-3 显式域名 Direct-IP。SakuraFRP 公网 UDP 穿透与域名解析**尚未完成独立动态验收**，不得宣称已 PASS。

### Added

- 新增**显式域名直连模式**：在直连页注入“插件域名直连（FRP）”开关（默认关闭，不持久化为默认开启）。
  - 开启后，任意合法 ASCII DNS 域名 + 玩家输入端口 → 异步解析为 IPv4 → query/connection 端口均等于输入端口 → 跳过原版 U3DS/A2S 查询直接连接。
  - 不写死任何 SakuraFRP / 供应商域名、节点 IP 或远端端口；每次显式连接都重新解析玩家当前输入的域名。
- 新增 `TryBuildExplicitDnsEndpoint` / `IsValidAsciiDnsName` 域名纯函数验证（FQDN 253/63 标签规则、拒绝 IPv4/SteamID/URL/IPv6/空标签/连续点/首尾连字符/非法字符）。
- 新增异步 DNS 解析控制器：
  - 线程模型：DNS worker 只构造不可变结果并入有界队列，绝不触碰 Unity/Glazier/Provider；主线程 `Tick` 消费并 `Provider.connect`。
  - epoch 失效：编辑地址/切换模式/关闭菜单/取消后，旧结果一律丢弃。
  - 超时 5s fail-closed；在途 DNS ≤2；结果队列 ≤8；单次只连一个有效 IPv4，不扫描多端口、不试 `R±1`。
  - IPv4 安全筛选：只接受 `InterNetwork`，拒绝 `0.0.0.0`、`255.255.255.255`、multicast `224.0.0.0/4`、IPv6；取 DNS 返回顺序中第一个有效地址。
- 主线程提交前置条件完整复核（`DiagnosticBuildValid`、菜单活动、未连接、地址/端口快照一致、epoch 有效）。
- 客机日志：`[DirectIP-DNS] resolved host=<脱敏> candidateCount=N sharedPort=R queryPort=R connectionPort=R`。

### Changed

- 路由优先级：SteamID P2P（不受开关影响）> 数值 IPv4 同步 Direct-IP > 显式 DNS 模式 > 原版（DNS/URL/U3DS Server Code）。
- 原版直连提示区在显式 DNS 模式开启并输入合法域名时显示“插件域名直连（FRP）”说明。

### Fixed

- 关闭开关时 DNS 继续走原版 U3DS，不被无条件劫持。
- 显式模式输入无效域名/URL 时报错并停止，不再静默落回原版 web/A2S 路径。

### Known limitations（未变）

- SakuraFRP/公网 UDP 穿透与域名解析尚未完成独立动态验收（本版仅静态候选）。
- 背包物品移动/丢弃后的幽灵图标缺少可用运行时证据，当前挂起。
- Steam 付费外观/装饰资产尚未完成专项双端指纹验证。

## 0.2.3.57 - 2026-08-11

> ⚠️ **诊断候选版**：本版实施 Stage 9-2 SakuraFRP 单端口 Direct-IP 端口语义。SakuraFRP 公网 UDP 穿透**尚未完成独立动态验收**，不得宣称已 PASS。

### Changed

- Direct-IP 端口语义改为**单端口**：客机输入的端口既是 query 端口也是 connection 端口（`queryPort == connectionPort`）。
  - 局域网/Radmin：客机端口填房主实际监听的 `27016`（不再是 `27015`）。
  - SakuraFRP：只需一条 UDP 隧道（本地 `127.0.0.1:27016` → 远端 `R`），客机输入远端端口 `R`。
- `65535` 端口在单端口语义下不再因 `+1` 溢出而被拒绝；`0` 仍拒绝。
- 客机连接后 `TryGetQueryPort` 投影修正：单端口 Direct-IP 下不再显示 `R-1`，而是显示实际端口 `R`。

### Added

- `DirectIpSinglePortQueryPortPatch`：修正单端口 Direct-IP 的连接后 query 端口投影（仅显示层，不参与授权）。
- `UnifiedJoinAddressClassifier.IsSinglePortDirectIpParameters` 纯函数判断。
- 客机直连日志改为无歧义单端口格式 `[DirectIP-SinglePort] sharedPort=... queryPort=... connectionPort=...`。
- 数值 IPv4 识别时原版提示区显示单端口 UDP 说明（局域网/Radmin 默认 27016；SakuraFRP 填远端端口）。
- 新增 Stage 9-2 注册硬门：`TryGetQueryPort` Postfix 真实 Harmony 注册失败时 `DiagnosticBuildValid=false`，禁用联机入口。

### Known limitations（未变）

- SakuraFRP/公网 UDP 穿透尚未完成独立动态验收（本版仅静态候选）。
- 背包物品移动/丢弃后的幽灵图标缺少可用运行时证据，当前挂起。
- Steam 付费外观/装饰资产尚未完成专项双端指纹验证。

## 0.2.3.56 - 2026-08-10

### Added

- 在原版直连地址栏识别个人 SteamID，使用 Steam P2P 加入房主世界。
- IPv4 query-less 直连，支持局域网和 Radmin LAN。
- 原版玩家列表（默认按 `P` 打开，可重绑）内的“允许/撤销允许”按钮。
- 30 秒新玩家隔离、无敌保护和 5 秒间隔聊天倒计时。
- 房间设置：玩家数、难度、PVP、作弊权限、死亡保留物品/技能/经验。
- 上一次房间设置持久化。
- 房主 SteamID 复制按钮和用法提示。
- Steam 玩家名称展示层，SteamID 仍是唯一授权键。

### Fixed

- 房主审批后客机仍被持续拒绝/重连的旧流程，改为入场后隔离审批。
- listen-host 自然刷新物品的双端重复生成和幽灵地面物品。
- 远程玩家在关闭作弊时仍可执行 `/day`、`/night` 等命令。
- 审批 HUD 列表滚动范围、精确快照和重建事务一致性问题。
- Direct-IP 被 U3DS A2S 查询阻断并在 10 次尝试后报“未找到服务器”的问题。

### Verified

- `151/151` 自动化静态/单元/ABI 测试通过。
- SteamID P2P 与 Radmin LAN Direct-IP 同轮双机回归通过。
- 创意工坊缺失内容下载、审批隔离、PVP/死亡规则与命令权限经双机验证。

### Known limitations

- SakuraFRP/公网 UDP 穿透尚未完成独立验收。
- 背包物品移动/丢弃后的幽灵图标缺少可用运行时证据，当前挂起。
- Steam 付费外观/装饰资产尚未完成专项双端指纹验证。
