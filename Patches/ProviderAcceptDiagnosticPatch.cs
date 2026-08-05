using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using Steamworks;
using UnityEngine;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.2 P0-D D-1：Provider.accept Finalizer 诊断 patch。
    ///
    /// v0.2.3.2 P0-7 修复（Codex 第三次审计）：
    ///   - 移除 Prefix 中的 EnsureHostJoinSession 调用（单次 Ensure 由 addPlayer 负责）。
    ///   - 所有日志改用 FormatPrefixFor 输出精确 sid。
    ///
    /// v0.2.3.1 P0-1 修复：
    ///   - Provider.accept 有 3 个重载，精确指定 internal overload 完整参数类型数组。
    ///   - 仅诊断观察：Prefix/Postfix/Finalizer 全 void，不修改行为，不吞异常。
    ///
    /// Provider.accept 顺序（Provider.cs:4766-4988）：
    ///   1. pending.Remove
    ///   2. addPlayer(...) -> 加入 Provider.clients
    ///   3. 向新客机发送 ReplicateConfig
    ///   4. 向新客机发送既有 PlayerConnected
    ///   5. 向新客机发送 Accepted
    ///   6. AddClientToThirdpartyAntiCheat
    ///   7. 向既有玩家发送新 PlayerConnected
    ///   8. SendInitialGlobalState(newClient)
    ///   9. newClient.player.InitializePlayer()
    ///   10. SendInitialPlayerState
    /// </summary>
    [HarmonyPatch(typeof(Provider), "accept", new System.Type[] {
        typeof(SteamPlayerID), typeof(bool), typeof(bool), typeof(byte), typeof(byte), typeof(byte),
        typeof(Color), typeof(Color), typeof(Color), typeof(Color), typeof(bool),
        typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int),
        typeof(int[]), typeof(string[]), typeof(string[]),
        typeof(EPlayerSkillset), typeof(string), typeof(CSteamID), typeof(EClientPlatform)
    })]
    public static class ProviderAcceptDiagnosticPatch
    {
        [HarmonyPrefix]
        public static void Prefix(SteamPlayerID playerID)
        {
            try
            {
                ulong remoteSteamId = playerID.steamID.m_SteamID;
                // P0-7 修复：不在 accept Prefix 调用 EnsureHostJoinSession。
                // session 由 addPlayer 唯一创建（addPlayer 是 accept 内部第一个建立 client 的调用）。

                RoleLogger.Info("[Host]",
                    $"{DiagnosticContext.FormatPrefixFor(remoteSteamId, "Provider.accept ENTER")} " +
                    $"remoteSteamId={remoteSteamId} name=\"{playerID.playerName}\" " +
                    $"clients_before={Provider.clients?.Count ?? -1} pending_before={Provider.pending?.Count ?? -1}");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Host]", $"[Diag] Provider.accept Prefix 异常（不阻断）: {ex.Message}");
            }
        }

        [HarmonyPostfix]
        public static void Postfix(SteamPlayerID playerID)
        {
            try
            {
                ulong remoteSteamId = playerID.steamID.m_SteamID;
                RoleLogger.Info("[Host]",
                    $"{DiagnosticContext.FormatPrefixFor(remoteSteamId, "Provider.accept RETURNED")} " +
                    $"remoteSteamId={remoteSteamId} " +
                    $"clients_after={Provider.clients?.Count ?? -1} pending_after={Provider.pending?.Count ?? -1}");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Host]", $"[Diag] Provider.accept Postfix 异常（不阻断）: {ex.Message}");
            }
        }

        /// <summary>
        /// Finalizer：void 签名，不吞异常。
        /// 审计员要求：所有观察型 Finalizer 必须保留原异常。
        /// HarmonyLib void Finalizer 不会吞 __exception，原异常会继续传播。
        /// </summary>
        [HarmonyFinalizer]
        public static void Finalizer(SteamPlayerID playerID, System.Exception __exception)
        {
            try
            {
                ulong remoteSteamId = playerID.steamID.m_SteamID;
                if (__exception != null)
                {
                    RoleLogger.Error("[Host]",
                        $"{DiagnosticContext.FormatPrefixFor(remoteSteamId, "Provider.accept THREW")} " +
                        $"remoteSteamId={remoteSteamId} " +
                        $"exceptionType={__exception.GetType().Name} message={__exception.Message}");
                    RoleLogger.Error("[Host]",
                        $"[Diag] Provider.accept stack:\n{__exception.StackTrace}");
                }
                else
                {
                    RoleLogger.Info("[Host]",
                        $"{DiagnosticContext.FormatPrefixFor(remoteSteamId, "Provider.accept OK (no exception)")} " +
                        $"remoteSteamId={remoteSteamId}");
                }
            }
            catch
            {
                // Finalizer 内部异常不得影响原异常传播
            }
            // 不返回 __exception，void 签名保留原异常
        }
    }
}
