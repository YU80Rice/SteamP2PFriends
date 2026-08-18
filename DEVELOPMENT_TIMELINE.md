# SteamP2PFriends 项目发展时间线

> 本文把当前工作区可核验的实现报告、测试报告、归档目录、源代码目录与 Git 提交串成一条时间线。日期以报告中明确写出的日期为准；只有文件时间而没有正文日期的内容，标为“目录时间参考”，不把推测写成事实。

## 项目定位

SteamP2PFriends 的目标是类似 Minecraft 的本地联机：房主在自己的 Unturned 客户端中同时承担 listen-host 服务端与本地客户端，好友通过 Steam P2P 加入，房主审批后进入同一世界；房主退出时会话结束。产品目标不是启动独立 U3DS。U3DS 相关材料属于早期探索或兼容性隔离背景，不能当作当前产品功能承诺。

## 1. 原型与立项：先证明“能联机”

### 2026-07-10：原版 SteamP2PFriends 原型

`SteamP2PFriends-原版` 已包含 `P2PHostManager`、`P2PJoinManager`、`P2PLobbyManager`、菜单 UI、Harmony 补丁和模块行为等组件，并已有 DLL/PDB/ZIP 产物。它是最早的可运行形态和接口试验场。证据：`D:/Agent-工作目录/DevelopMyUNMultiplayerModAndModloader/SteamP2PFriends-原版/`。

### 2026-07-09—07-16：LaunchP2PHostManager 路线

该项目集中实现 P2PHostManager、listen server、Lobby 与 Steam 运行时适配，并形成移植规格和插件手册。它暴露了身份与生命周期问题：早期路线曾使用 `SteamGameServer`/AnonID，后续证据要求改用 `SteamUser.GetSteamID()`；因此它成为技术来源，而不是当前代码的直接依赖。证据：`LaunchP2PHostManager/STEAMP2PFRIENDS_移植规格.md`、`P2PHostManager.cs`、`SteamRuntime.cs`。

### 2026-07-14—07-16：LaunchP2PU3dsProcessManager 分叉

另一条路线尝试管理独立 U3DS 进程、配置、存档注入和加载 UI。它解决的是专用服务器/进程编排问题，与本地 listen-host 产品目标不同。后续检查确认当前 SteamP2PFriends 的 `.csproj`、源码和发布 ZIP 没有引用或打包该项目。证据：`LaunchP2PU3dsProcessManager/`；当前仓库 `SteamP2PFriends.csproj` 与 `publish/0.2.3.60/`。

## 2. 涅槃重生：统一到 SteamUser + listen-host 权威模型

### 2026-07-16：v0.2 合并设计与 SteamUser 可行性确认

`v0.2` 合并方案明确提出：以原版 listen-server 骨架为基础，吸收 LaunchP2PHostManager 的适配层；同时将身份、重试状态机和主机权威纳入统一设计。同期的 SteamUser P2P 可行性报告与 v0.2 审计报告把“无需 U3DS、使用房主个人 SteamID 完成握手”确立为验证方向。证据：`往期归档日志/SteamP2Friends-v0.2-合并方案.md`、`SteamP2PFriends-SteamUser-P2P可行性验证报告.md`、`SteamP2PFriends-v0.2-审计报告.md`。

### 2026-07-16—07-17：v0.2.1、v0.2.2 早期双机验证

v0.2.1 完成合并后的第一次静态审计；v0.2.2 建立双机测试准备清单，明确先做房主单机门禁、再做双机连接，并以 SteamUser identity P2P 验证非 U3DS listen-host。v0.2.2.1 的第一次双机测试和 v0.2.2.2 的第二次测试、修复审计与放行审计，形成了后续“先单机、后双机、证据归档”的测试纪律。证据：对应 `往期归档日志/SteamP2PFriends-v0.2.1-*`、`v0.2.2-*` 文件。

## 3. 挫折与连续返修：v0.2.3 的诊断化阶段

### 2026-07-17—07-18：v0.2.3.0—v0.2.3.2

v0.2.3 的实现审计明确“不放行第三次双机测试”，原因集中在握手、原生加载/队列和状态边界。v0.2.3.1、v0.2.3.2 转为 `diag-only`，增加启动自检、日志和 P0/Medium 修复闭环；第三、第四次双机测试经历失败分析、外部复核、修复实施和最终放行记录。这是项目从“功能堆叠”转向“可观测、可复现、可审计”的转折点。证据：`v0.2.3-实现审计报告.md`、`v0.2.3.1-diag-only审计报告.md`、全部 `v0.2.3.2-*` 报告。

### 2026-07-18—07-20：v0.2.3.3—v0.2.3.9

第五至第七次双机测试反复出现连接后卡队列、原生资源/资产校验、客户端黑模、移动与动画不同步等问题。v0.2.3.4 失败后先出根因审计，再由 v0.2.3.5—.7 逐项返修；.8 做静态验收；.9 完成 smoke2 放行。报告显示，问题不是单一网络故障，而是握手门禁、资产完整性和世界状态权威链条的耦合缺陷。证据：`v0.2.3.3-*` 至 `v0.2.3.9-*` 报告。

### 2026-07-20—07-23：v0.2.3.10—v0.2.3.26

第八至第十六次测试把审计扩展到世界同步、原生前缀/ABI、资源哈希、黑模、转动同步和“平行宇宙”状态漂移。v0.2.3.13 曾出现“成功越过排队后仍被资源完整性校验踢出”；v0.2.3.20 对跨端状态分叉进行专项审计；v0.2.3.22—.25 继续单机冒烟、失败复盘和双机放行；v0.2.3.26 的世界同步失败报告保留为重要未通过证据。证据：`往期归档日志/SteamP2PFriends-v0.2.3.10-*`、`.12-*`、`.13-*`、`.14-*`、`.16-*`、`.20-*`、`.21-*`、`.22-*`、`.23-*`、`.25-*`、`.26-*`。

## 4. 审计制度化与功能收敛

### 2026-07-23—08-05：从问题驱动到阶段门禁

项目逐步固定 AUDIT_CHECKLIST、单机冒烟/双机回归、日志与指纹归档、ABI/Harmony 注册自检和 fail-closed 门禁。当前仓库的 Git 历史从 `v0.2.3.37 stage6A-1` 开始记录阶段化开发；Stage 7-2-1 连续返修三轮后由 Codex 接管蓝图，Stage 7-2-2 完成原生白名单最小编码并登记 Stage 7-3。证据：仓库 Git 提交 `d7c8f03`、`47fcb87`、`551b64d`、`4e7ac78`、`8c5491c`、`822e01c`、`0e08679`。

### 2026-08-10—08-11：v0.2.3.56—.59 功能候选期

`.56` 加入入场 HUD、房间规则、审批隔离、SteamID P2P 与 IPv4 Direct-IP；`.57`—`.58` 收敛单端口与显式域名解析；`.59` 加入世界状态广播，但报告明确标为诊断候选，动态行为尚未独立验收。证据：仓库 `CHANGELOG.md`、提交 `6fcb7b6`、`a4dd451`、`8bd5c1a`。

## 5. Beta：修复幽灵图标并完成总回归

### 2026-08-13：v0.2.3.60-beta.1

最终 Beta 回归 Case `Final-Beta-Test-20260813-1300`（归档于 `.audit/beta-test-development/Beta-Stage1-AdmissionHUD-20260809/artifacts/Final-Beta-Test-20260813-1300`）验证了 Host/Client 双端同 DLL、P2P listen-host 主流程、审批/房间规则/Direct-IP 等既定范围，以及背包幽灵图标专项。记录为 `261/261 PASS`、`AllOK=true`；双端 DLL SHA-256 为 `03565C7E99804FF4AC5E980E766C52DFEB00E41F10BE34B388F8EBC9A417CE11`。版本发布标识为 `v0.2.3.60-beta.1`，内部 AssemblyVersion 保持 `0.2.3.60`。

## 6. 当前结论与未完成事项

- 已完成：本地 P2P listen-host 的 Beta 总回归与关键功能证据闭环。
- 仍保留：开发期 Diagnostic/Probe/Trace 类会产生较多日志；这属于可观测性遗留，不等同于 LaunchP2PHostManager/U3DSProcessManager 被打包进插件。正式版前应做日志分级/默认降噪，并重新回归。
- 未承诺：独立 U3DS 进程管理、SakuraFRP 公网 UDP 穿透、所有地图/模组/未来 Unturned 版本兼容。它们必须有独立实现和证据后才能从“候选/限制”升级为发布承诺。

### 2026-08-18：v0.2.3.61-beta.2 静态收敛候选

在首个公开 Beta 的动态证据之后，源码移除了废弃 `ESteamPacket` 发送探针、同账号 LAN SteamID 去重绕过和可关闭完整补丁集的 A/B 开关；同时将 SteamPlayer 构造器注入收紧为精确 ABI 签名，默认关闭高频诊断日志。该节点是静态收敛，不是新的双机验收：新构建只能在当前运行时引用、自动化测试和新的双端回归均完成后升级为发布证据。`offlineOnly` 认证绕过仍是正式版阻断项，未被本次清理伪装为已修复。

## 证据边界

本文引用的早期节点来自工作区归档报告和项目目录；当前版本节点来自仓库 Git、CHANGELOG、发布包与 Beta artifact。归档目录本身不随本仓库提交，路径保留用于审计追溯；删除或移动归档不会改变本文已记录的事实，但会降低复核能力。
