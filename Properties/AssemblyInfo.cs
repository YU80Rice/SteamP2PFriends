using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("SteamP2PFriends")]
[assembly: AssemblyDescription("SteamP2PFriends v0.2.3.38 P2P-LIT 桥接版（Codex P0-LIT-02 R2 §3.1 授权实施）：在 OnServerHosted 完成 _server/_client 对齐 + serverTransport 非空守卫后、LoadClientHostedLevel 前调用 InitializeOptionalLITP2PFaultScope。该方法以软依赖方式反射发现 LaunchInventoryTidy v3.0.1 的 BeginScope(\"p2p\", map, slot) API，将 Stage6ASessionContext.CachedSlot 与 Provider.map 注入 LIT 作用域熔断隔离器。LIT 缺席：日志跳过、P2P 正常启动；LIT 已安装但作用域失败（Stage6A 上下文未稳定 / Listen Host 身份未对齐 / BeginScope 返回 false）：抛 InvalidOperationException，外层 OnServerHosted catch 触发 AbortHostStart 事务性回滚，房主启动 fail-closed 中止。SteamP2PFriends 不增加 LIT DLL 编译引用，仅运行时反射。")]
[assembly: AssemblyCompany("YU80Rice")]
[assembly: AssemblyProduct("SteamP2PFriends")]
[assembly: AssemblyCopyright("MIT")]
[assembly: ComVisible(false)]
[assembly: Guid("b3c4d5e6-f7a8-9012-3456-789abcdef012")]
[assembly: AssemblyVersion("0.2.3.38")]
[assembly: AssemblyFileVersion("0.2.3.38")]
// Stage 7-2-2（Codex 133rd §3）：纯单元测试项目 InternalsVisibleTo
//   蓝图：仅此一项 InternalsVisibleTo 条目授权；测试项目不启动 Unturned、不触碰 Provider/Steam API/Unity/文件系统
[assembly: InternalsVisibleTo("SteamP2PFriends.WhitelistTests")]
