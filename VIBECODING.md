# 人机协作开发与审计原则

SteamP2PFriends 采用人类主导、AI 协作的工程流程。本文档记录该流程的责任边界，不将“AI 生成”视为免除测试与安全责任的理由。

## 角色分工

### 人类开发者

- 定义产品目标、用户体验和发布范围。
- 执行双机实机测试，提供截图、日志和人工行为结果。
- 决定是否合并、部署和公开发布。

### AI 实施与审计

- 阅读 U3-SDK 与现有源码，生成可编译的 C# 实现。
- 检查 ABI、Harmony 注册、主线程、并发、会话生命周期和 fail-closed 边界。
- 编译、执行自动化测试、记录 DLL SHA-256/MVID。
- 根据动态日志区分已证实根因和推测，禁止无证据盲修。

## 工程门禁

1. 所有发布 DLL 必须可从当前源码 Release 构建。
2. AssemblyVersion、AssemblyFileVersion、BepInPlugin、SHA-256 与 MVID 必须可追溯。
3. 关键 Harmony 补丁必须使用 owner + MethodInfo 进行运行时注册核验。
4. 背包、玩家、白名单和传输状态修改必须在游戏主线程执行。
5. IP 只能用于寻址；准入与授权必须使用 SteamID。
6. 房主、客机、单人和 U3DS 路径必须隔离，不允许全局伪造 `Dedicator.IsDedicatedServer`。
7. 自检失败必须禁用联机入口，不得在部分补丁缺失时带病运行。
8. 用户未授权时，AI 不得替用户启动游戏或进行外部双机测试。

## 证据等级

- **源码参考**：只证明原版逻辑和候选注入点。
- **编译通过**：只证明语法与引用成立。
- **单元/ABI 测试**：证明纯逻辑、签名和注册约束。
- **双端日志**：证明实际运行链路。
- **人工行为验收**：证明 UI、交互和玩家可见结果。

公开声称的功能必须标明所达到的证据层级，不得把静态可行性写成动态已通过。

## 发布规则

- 发布包使用 `publish/<version>/SteamP2PFriends-v<version>.zip`。
- ZIP 内只包含 `BepInEx/plugins/SteamP2PFriends.dll`。
- Beta 版使用 GitHub Prerelease；发布说明必须区分当前 DLL 的动态证据、未覆盖环境与 `offlineOnly` 认证限制。

**最后更新**：2026-08-18
