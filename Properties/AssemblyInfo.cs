using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("SteamP2PFriends")]
[assembly: AssemblyDescription("SteamP2PFriends v0.2.3.37 P0-B-6+P0-D-ESC-2 版（Codex 第二十五次双机测试外部审计 §4.1+§4.2 授权实施）：P0-B-6 onLevelLoaded Postfix 触发全地图 generateItems：25th 测试证明 v0.2.3.36 P0-B-5 在 OnServerHosted 时机过早，LevelItems.spawns=null 导致跳过预生成（主机日志 L596-L597）。修复：在 ItemManagerP0B3PreGeneratePatch.OnLevelLoaded_Postfix 中调用 P0-B-6 入口，检测 level > Level.BUILD_INDEX_SETUP + spawns 就绪 + IsP2PServerActive + 未执行过时触发 generateItems 全地图循环。静态标志位 _p0B6RegenerationDone 在 ResetHostSession/AbortHostStart 中重置。P0-D-ESC-2 Prefix 运行时诊断日志：25th 测试 Prefix 自检通过但 timeScale=0.00 持续 5-15s（主机日志 L2402-L2515），根因不明。增加状态变化即时日志 + 每 5s 心跳日志，26th 测试后根据日志确定具体修复方向（Prefix 未调用 / 条件未满足 / 返回值被覆盖）。不全局伪造 Dedicator.IsDedicatedServer。")]
[assembly: AssemblyCompany("YU80Rice")]
[assembly: AssemblyProduct("SteamP2PFriends")]
[assembly: AssemblyCopyright("MIT")]
[assembly: ComVisible(false)]
[assembly: Guid("b3c4d5e6-f7a8-9012-3456-789abcdef012")]
[assembly: AssemblyVersion("0.2.3.37")]
[assembly: AssemblyFileVersion("0.2.3.37")]
