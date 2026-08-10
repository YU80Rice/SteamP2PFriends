# 贡献者与工具声明

SteamP2PFriends 由人类开发者主导，并借助多种 AI 模型与本地工具完成架构讨论、代码实现、静态审计和动态取证。

## YU80Rice

- 项目所有者与最终决策者。
- 定义“无 U3DS 便捷联机”产品目标。
- 设计房主/客机交互流程、审批策略和测试场景。
- 手动部署插件并执行双机实机测试。
- 承担发布、用户安全与项目方向的最终责任。

GitHub: [YU80Rice](https://github.com/YU80Rice)

## AI 协作

### OpenAI Codex

- 技术主管、全栈实施与独立审计。
- 完成 Stage 7 审批 HUD、房间规则、Direct-IP、物品权威修复等后续实施与发布工作。
- 基于 U3-SDK、代码、日志、DLL 指纹和双机实验作出 PASS/FAIL 裁决。

### Anthropic Claude

- 参与早期与中期 C# 实现、架构文档和审计报告整理。
- 参与 Stage 6/7 多轮返修和测试资料生成。

### Kimi

- 参与早期 listen-host、Dedicated Server 与 P2P 方案比较和架构讨论。

## 主要工具与参考

- [Unturned U3 SDK](https://github.com/smartlydressedgames/u3-sdk)：原版游戏逻辑、UI、传输和权威实现参考。
- [BepInEx](https://github.com/BepInEx/BepInEx)：Unity/.NET 插件加载框架。
- [Harmony](https://github.com/pardeike/Harmony)：运行时补丁机制。
- Steamworks.NET / Steam Networking Sockets：Steam 身份和网络传输。
- Radmin LAN：IPv4 虚拟局域网测试环境。

## 声明

- 人类开发者对最终并入、部署、测试和发布负责。
- AI 生成内容不代替编译、静态检查、运行时日志和人工实机验收。
- 本项目在 [MIT License](./LICENSE) 下开源。

**最后更新**：2026-08-10
