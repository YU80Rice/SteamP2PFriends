# 贡献者声明

本项目由以下人类与 AI 协同完成。所有贡献者按角色与贡献类型列出。

## 人类导演

### YU80Rice

- **角色**：项目导演 / 最终决策者 / 风险承担者
- **贡献**：
  - 项目构想与方向定义
  - 架构决策（listen server -> U3DS -> P2P 直连的演进）
  - 测试场景设计（双机联机测试、Stage 6A/6B 动态测试）
  - Codex 审计驱动与裁决执行
  - 所有发布授权的最终确认
- **GitHub**：未公开

## AI 导师

### Claude（Anthropic）

- **角色**：主导师 / 全栈工程专家 / 发布审查官
- **贡献**：
  - 全部 C# 代码实现（SteamP2PFriends 插件）
  - 方案设计文档（Stage 7-2-1 原生白名单设计等）
  - 审计清单（AUDIT_CHECKLIST.md §14.1 - §14.115+）
  - 动态测试修复报告（RuntimeFix-*.md）
  - Codex 审计裁决的执行与文档返修（v1 -> v1.1 -> v1.2）
  - BepInEx 插件封装与发布
- **运行环境**：Claude Code（CherryStudio 集成）
- **模型版本**：Claude Opus 4.x / Sonnet 4.x

### Kimi（月之暗面）

- **角色**：副导师 / 早期方案探讨
- **贡献**：
  - 早期架构探讨
  - 跨平台第二意见
  - listen server vs Dedicated Server 的方案对比
- **运行环境**：CherryStudio 集成

### Codex（OpenAI）

- **角色**：外部审计员 / 静态设计与动态测试裁决者
- **贡献**：
  - 135+ 次独立审计裁决（截至 2026-08-05）
  - 静态设计 FAIL/PASS 裁决
  - 动态测试 FAIL/PASS 裁决
  - P0/P1 阻断项识别与返修要求
  - 发布授权决策（受限封闭 α 部署等）
- **审计历史**：
  - Codex 92nd：Stage 6A 核心存档往返闭环 PASS
  - Codex 125th：Stage 6B 正式收官 PASS
  - Codex 126th：Stage 7-0/7-1 文档 FAIL（4 项 P0）
  - Codex 127th：Stage 7-0/7-1 v1.1 文档 PASS + 受限封闭 α 部署授权
  - Codex 128th：Stage 7-2-1 v1 设计 FAIL（3 项 P0）
  - Codex 129th：Stage 7-2-1 v1.1 设计 FAIL（3 项 P0）
  - Codex 130th：Stage 7-2-1 v1.2 设计 FAIL（2 项 P0 + 2 项 P1）
  - Codex 131st：Stage 7-2-1 v1.3 设计 FAIL（2 项 P0 + 1 项 P1）
  - Codex 132nd：Stage 7-2-1 接管蓝图 -> v1.5 设计回填（2 项 P0）
  - Codex 133rd：Stage 7-2-2 原生白名单最小编码授权 PASS（7/7 静态门通过）
  - Codex 134th：Stage 7-2-2 v1 实施审计 FAIL（P0-Snapshot + P0-UnitRepro + P1-LocalUser），授权 v1.1 定点返修
  - Codex 135th：Stage 7-2-2 v1.1 静态实现复审 PASS（17/17 单元测试独立复现）+ DLL 指纹更正 + Stage 7-3 路线

## 本地 Agent

### Cherry Claw

- **角色**：本地执行 Agent
- **贡献**：
  - 在本地工作目录执行 AI 导师的指令
  - 文件读写、编译执行、测试运行
  - 工作区记忆管理（FACT.md / JOURNAL.jsonl）
  - 与 Codex 审计员的交互执行
  - BepInEx 插件 zip 封装与 SHA-256 校验
- **运行环境**：CherryStudio + Claude Code SDK

## 工具与服务

### CherryStudio

- **角色**：本地 Agent 运行环境
- **贡献**：提供 Claude / Kimi / Codex 的统一集成界面；本地工作目录管理；持久记忆系统

### Anthropic Claude Code

- **角色**：Claude 的官方 CLI 工具
- **贡献**：提供 Claude 在终端的执行能力；文件读写工具；Bash/PowerShell 执行；多 Agent 协作

### Unturned U3 SDK

- **角色**：原版游戏源码参考
- **贡献**：提供 `SteamWhitelist`、`ServerMessageHandler_ReadyToConnect`、`ServerSavedata` 等原生 API 的源码参考
- **来源**：[smartlydressedgames/u3-sdk](https://github.com/smartlydressedgames/u3-sdk)

### BepInEx

- **角色**：插件框架
- **贡献**：提供 Unity 游戏的 Harmony 补丁注入能力

## 贡献声明

1. **代码贡献**：所有 C# 代码由 Claude 编写，YU80Rice 审阅确认
2. **设计贡献**：架构与方案设计由 Claude 主导，YU80Rice 与 Kimi 提供方向
3. **审计贡献**：所有阶段性结论由 Codex 独立审计裁决
4. **执行贡献**：所有本地操作由 Cherry Claw 执行
5. **决策贡献**：所有最终决策由 YU80Rice 确认

## 许可证

本项目采用 [MIT 许可证](./LICENSE) 开源。所有贡献者的贡献均在 MIT 许可证下授权。

---

**最后更新**：2026-08-05
