using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.22 P0-S3 修复（外部审计报告-Codex §5 + v0.2.3.21 单机冒烟前静态外部审计返修 High-1）。
    ///
    /// 补齐房主本地视图的远程玩家初始可见信号。
    ///
    /// 原版流程：
    ///   - PlayerAnimator.InitializePlayer 设 isHiddenWaitingForClothing=true（PlayerAnimator.cs:1595）
    ///   - 只有 NotifyClothingIsVisible() 会清除该标志并启用远程模型（PlayerAnimator.cs:641-662）
    ///   - vanilla 服务端接受新玩家后调用 SendInitialPlayerState（Player.cs:1650-1671）
    ///   - 但 GatherRemoteClientConnectionsMatchingPredicate 排除 IsLocalServerHost（Provider.cs:589-605）
    ///   - listen server 模式下房主共享进程，远程客机 Player 的 isHiddenWaitingForClothing 永未清除
    ///
    /// 修复：在远程 Player.InitializePlayer() 成功返回后登记重试任务，由 Plugin.Update 驱动
    ///       在严格门控下调用 player.animator.NotifyClothingIsVisible()
    ///
    /// v0.2.3.22 High-1 强化门控（审计返修）：
    ///   - player.clothing != null
    ///   - 第三人称 SMR thirdRenderer_0/1 反射非空（精确验证两个目标 renderer）
    ///   - 两个目标 SMR sharedMaterial != null
    ///   - 前置不全时有界延迟重试：0/1/3 秒，成功后停止；不得每帧强开
    ///   - 失败超重试上限只报警，不直接写字段
    ///   - 成功日志记录 hidden 前后、SMR enabled/total、material-null、shirt/pants/hat
    ///
    /// 严格门控（审计 §5 P0-S3 + v0.2.3.22 High-1）：
    ///   - HostManager.IsP2PHostMode
    ///   - Provider.isServer && !Dedicator.IsDedicatedServer
    ///   - !player.channel.IsLocalPlayer
    ///   - owner transport 为真正远端连接（!IsLocalServerHost）
    ///   - animator 已存在
    ///   - 玩家存活（player.life.IsAlive）
    ///   - clothing != null（v0.2.3.22 新增）
    ///   - thirdRenderer_0/1 非空 + sharedMaterial 非空（v0.2.3.22 新增）
    ///
    /// 严格禁止（审计 §8 + v0.2.3.22 返修）：
    ///   - 直接反射写 isHiddenWaitingForClothing
    ///   - 每帧强开 SMR
    ///   - 在前置状态不完整时强行显示
    ///   - 前置不全时永久放弃（改为有界重试）
    /// </summary>
    public static class RemotePlayerClothingVisibleBridgePatch
    {
        private const string HarmonyId = SteamP2PFriendsPlugin.HARMONY_ID;

        // 重试时机：0s（即时）、1s、3s，共 3 次尝试
        private static readonly float[] RetryDelays = { 0f, 1f, 3f };

        public static bool P0S3_Registered { get; private set; }

        // v0.2.3.22 自检：owner 精确元数据（允许其他 Postfix 共存，只检查自家 owner 精确 1）
        public static bool P0S3_PostfixOwnerVerified { get; private set; }
        public static string P0S3_PostfixOwnerSummary { get; private set; } = "<unverified>";

        // v0.2.3.22 自检：反射字段完整性
        public static bool P0S3_ReflectionComplete { get; private set; }

        public static bool AllRegistrationsSucceeded =>
            P0S3_Registered && P0S3_PostfixOwnerVerified && P0S3_ReflectionComplete;

        // v0.2.3.38 4B 编码（Codex 第三十四次审计 E-4/E-5）：暴露 RetryStates 计数为只读出口，
        // 供 Plugin.OnEnemyDisconnectedHandler 入口观察与 WorldSyncDiagnosticCore.ResetAll 会话重置观察使用。
        // 不暴露字典本身，不提供写入接口。
        public static int RetryStatesCount
        {
            get
            {
                try { return _retryStates?.Count ?? 0; }
                catch { return 0; }
            }
        }

        /// <summary>
        /// v0.2.3.38 4B 编码 R2：只读查询指定 SteamID 是否仍存在 RetryState。
        /// 用于 E-4 OnEnemyDisconnectedHandler 入口观察，证明断开的 SteamID 是否仍在字典中。
        /// 不暴露字典，不提供写入接口。
        /// </summary>
        public static bool ContainsRetryState(ulong steamId)
        {
            try
            {
                return steamId != 0UL && _retryStates != null && _retryStates.ContainsKey(steamId);
            }
            catch { return false; }
        }

        static RemotePlayerClothingVisibleBridgePatch()
        {
            // v0.2.3.38 4B 编码 E-5：注册 WorldSyncDiagnosticCore.ResetAll 会话重置回调。
            // 仅观察会话重置事件，不清空 _retryStates（审计明确禁止修改 _retryStates 与 Tick 逻辑）。
            try
            {
                WorldSyncDiagnosticCore.RegisterSessionResetCallback(OnSessionReset);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Shared]", $"[P0-S3] 注册 RegisterSessionResetCallback 异常（不阻断）: {ex.Message}");
            }
        }

        /// <summary>
        /// v0.2.3.39 5B-P0-S3（Codex 第四十二次审计 §4.1 授权）：
        ///   WorldSyncDiagnosticCore.ResetAll 会话重置回调。
        ///   修复双重生命周期缺陷之 1 -- Plugin 转发处理器遗漏调用 P0-S3 清理。
        ///   现在由 OnSessionReset 全量清空 _retryStates + _completedToRemove，
        ///   防止跨会话 Completed=true 项残留导致第二会话同 SteamID 命中旧项。
        ///   线程边界：与 Tick 同主线程访问，无需锁。
        /// </summary>
        private static void OnSessionReset()
        {
            try
            {
                int countBefore = _retryStates.Count;
                _retryStates.Clear();
                _completedToRemove.Clear();
                int countAfter = _retryStates.Count;
                RoleLogger.Info("[Shared]",
                    $"[P0-S3] SessionReset 全量清理 countBefore={countBefore} countAfter={countAfter}");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P0-S3] SessionReset 异常: {ex.Message}");
            }
        }

        // 反射缓存
        private static FieldInfo _isHiddenWaitingForClothingField;
        private static FieldInfo _thirdRenderer0Field;
        private static FieldInfo _thirdRenderer1Field;
        private static bool _reflectionCached;

        // v0.2.3.22 High-1: 每玩家重试状态
        private class RetryState
        {
            public ulong SteamId;
            public Player Player;
            public int AttemptIndex;
            public float NextRetryTime;
            public bool Completed;
            public string LastFailReason;
        }

        private static readonly Dictionary<ulong, RetryState> _retryStates = new Dictionary<ulong, RetryState>();
        private static readonly List<ulong> _completedToRemove = new List<ulong>();

        public static bool RegisterManual(Harmony harmony)
        {
            CacheReflection();

            P0S3_Registered = RegisterP0S3(harmony);
            VerifyPatchOwner(harmony);

            RoleLogger.Info("[Shared]",
                $"[P0-S3] RemotePlayerClothingVisibleBridgePatch 汇总: " +
                $"P0-S3={P0S3_Registered} " +
                $"owner={P0S3_PostfixOwnerSummary} " +
                $"ownerVerified={P0S3_PostfixOwnerVerified} " +
                $"reflectionComplete={P0S3_ReflectionComplete} " +
                $"reflectionHiddenField={(_isHiddenWaitingForClothingField != null)} " +
                $"reflectionThirdRenderer0={(_thirdRenderer0Field != null)} " +
                $"reflectionThirdRenderer1={(_thirdRenderer1Field != null)} " +
                $"allOk={AllRegistrationsSucceeded}");
            return AllRegistrationsSucceeded;
        }

        /// <summary>
        /// v0.2.3.23 P0-C1：在所有手工 patch 登记完成后由 Plugin.VerifyCriticalPatches 调用，
        /// 重新实时读取 Harmony 元数据进行精确 MethodInfo 自检。
        /// </summary>
        public static void ReverifyOwnersAfterAllRegistrations(Harmony harmony)
        {
            VerifyPatchOwner(harmony);

            RoleLogger.Info("[Shared]",
                $"[P0-S3] ReverifyOwnersAfterAllRegistrations: " +
                $"owner={P0S3_PostfixOwnerVerified} ({P0S3_PostfixOwnerSummary}) " +
                $"allOk={AllRegistrationsSucceeded}");
        }

        private static void CacheReflection()
        {
            if (_reflectionCached) return;
            _reflectionCached = true;
            try
            {
                _isHiddenWaitingForClothingField = AccessTools.Field(typeof(PlayerAnimator), "isHiddenWaitingForClothing");
                _thirdRenderer0Field = AccessTools.Field(typeof(PlayerAnimator), "thirdRenderer_0");
                _thirdRenderer1Field = AccessTools.Field(typeof(PlayerAnimator), "thirdRenderer_1");

                // v0.2.3.22 High-1: thirdRenderer_0/1 反射必须成功（isHiddenWaitingForClothing 仅诊断用可降级）
                P0S3_ReflectionComplete = (_thirdRenderer0Field != null) && (_thirdRenderer1Field != null);

                if (_isHiddenWaitingForClothingField == null)
                {
                    RoleLogger.Warn("[Shared]",
                        "[P0-S3] PlayerAnimator.isHiddenWaitingForClothing 反射失败（仅诊断用，不阻断修复）");
                }
                if (_thirdRenderer0Field == null)
                {
                    RoleLogger.Error("[Shared]",
                        "[P0-S3] !!! PlayerAnimator.thirdRenderer_0 反射失败，P0-S3 自检失败（High-1）");
                }
                if (_thirdRenderer1Field == null)
                {
                    RoleLogger.Error("[Shared]",
                        "[P0-S3] !!! PlayerAnimator.thirdRenderer_1 反射失败，P0-S3 自检失败（High-1）");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P0-S3] 反射缓存异常: {ex.Message}");
                P0S3_ReflectionComplete = false;
            }
        }

        private static bool RegisterP0S3(Harmony harmony)
        {
            const string Label = "P0-S3 Player.InitializePlayer Postfix (NotifyClothingIsVisible bridge)";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(Player), "InitializePlayer");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[P0-S3] !!! {Label} 反射失败");
                    return false;
                }
                MethodInfo postfix = typeof(Hooks).GetMethod(nameof(Hooks.InitializePlayerPostfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, postfix: new HarmonyMethod(postfix));

                HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
                int postfixCount = info?.Postfixes?.Count ?? 0;
                RoleLogger.Info("[Shared]",
                    $"[P0-S3] OK {Label} 已登记 (Postfix)。当前 InitializePlayer postfixes={postfixCount}");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P0-S3] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// v0.2.3.23 P0-C1：精确 MethodInfo owner 自检（审计报告-Codex §3 P0-C1 修订）。
        /// 使用精确 MethodInfo 比较，允许同一 owner 的其他合法 Postfix 共存。
        /// 日志输出 exact/same-owner-other/foreign/total 四项元数据。
        /// </summary>
        private static void VerifyPatchOwner(Harmony harmony)
        {
            try
            {
                MethodInfo original = AccessTools.Method(typeof(Player), "InitializePlayer");
                MethodInfo expectedPostfix = AccessTools.Method(typeof(Hooks), nameof(Hooks.InitializePlayerPostfix));
                P0S3_PostfixOwnerVerified = VerifyPatchOwnerExact(original,
                    expectedMethod: expectedPostfix,
                    summaryOut: out string summary);
                P0S3_PostfixOwnerSummary = summary;
                if (!P0S3_PostfixOwnerVerified)
                {
                    RoleLogger.Error("[Shared]", $"[P0-S3] !!! Owner 自检失败: {summary}");
                }
            }
            catch (System.Exception ex)
            {
                P0S3_PostfixOwnerVerified = false;
                P0S3_PostfixOwnerSummary = $"exception: {ex.Message}";
                RoleLogger.Error("[Shared]", $"[P0-S3] Owner 自检异常: {ex.Message}");
            }
        }

        private static bool VerifyPatchOwnerExact(MethodInfo original, MethodInfo expectedMethod, out string summaryOut)
        {
            if (original == null)
            {
                summaryOut = "original=null";
                return false;
            }
            if (expectedMethod == null)
            {
                summaryOut = "expectedMethod=null";
                return false;
            }

            HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
            System.Collections.ICollection patches = info?.Postfixes;
            if (patches == null || patches.Count == 0)
            {
                summaryOut = "postfixes=0";
                return false;
            }

            int exactExpectedCount = 0;
            int sameOwnerOtherCount = 0;
            int foreignOwnerCount = 0;
            string firstForeignOwner = null;

            foreach (Patch p in patches)
            {
                bool isOurOwner = (p.owner == HarmonyId);
                bool isExactMethod = IsSameMethodInfo(p.PatchMethod, expectedMethod);

                if (isOurOwner && isExactMethod)
                {
                    exactExpectedCount++;
                }
                else if (isOurOwner)
                {
                    sameOwnerOtherCount++;
                }
                else
                {
                    foreignOwnerCount++;
                    if (firstForeignOwner == null)
                    {
                        firstForeignOwner = $"{p.owner}/{p.PatchMethod?.DeclaringType?.Name}.{p.PatchMethod?.Name}";
                    }
                }
            }

            int total = patches.Count;
            summaryOut = $"exact={exactExpectedCount} sameOwnerOther={sameOwnerOtherCount} foreign={foreignOwnerCount} total={total} foreignOwner={firstForeignOwner ?? "<none>"}";
            return exactExpectedCount == 1;
        }

        private static bool IsSameMethodInfo(MethodInfo a, MethodInfo b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a.Name != b.Name) return false;
            if (!ReferenceEquals(a.DeclaringType, b.DeclaringType)) return false;
            if (!ReferenceEquals(a.Module, b.Module)) return false;
            try
            {
                if (a.MetadataToken != 0 && b.MetadataToken != 0
                    && a.MetadataToken == b.MetadataToken)
                {
                    return true;
                }
            }
            catch
            {
            }
            return false;
        }

        /// <summary>
        /// v0.2.3.22 High-1: 每帧驱动重试。由 Plugin.Update 调用。
        ///
        /// v0.2.3.39 5B-P0-S3（Codex 第四十二次审计 §4 授权）：
        ///   修复双重生命周期缺陷之 2 —— Tick 中 `if (rs.Completed) continue;` 使成功项
        ///   永久跳过 `_completedToRemove` 加入，导致字典中成功项从不删除。
        ///   修复：成功项立即加入 `_completedToRemove`，下一帧循环结束统一 Remove。
        ///   线程边界：`_retryStates` 仅由 Plugin.Update（游戏主线程）访问，
        ///   `RemoveRetryState` 也由 OnEnemyDisconnectedHandler（游戏主线程）调用，
        ///   `OnSessionReset` 由 ResetAll（游戏主线程）调用，全主线程访问，无需锁。
        /// </summary>
        public static void Tick()
        {
            if (_retryStates.Count == 0) return;

            float now = Time.realtimeSinceStartup;
            _completedToRemove.Clear();

            foreach (var kv in _retryStates)
            {
                RetryState rs = kv.Value;
                // v0.2.3.39 5B-P0-S3 修复：成功项立即加入移除列表，避免永久 continue 导致字典膨胀
                if (rs.Completed)
                {
                    _completedToRemove.Add(kv.Key);
                    continue;
                }
                if (now < rs.NextRetryTime) continue;

                // 玩家已断开或对象已销毁
                if (rs.Player == null)
                {
                    rs.Completed = true;
                    _completedToRemove.Add(kv.Key);
                    continue;
                }

                AttemptNotifyClothingIsVisible(rs, now);
            }

            foreach (ulong sid in _completedToRemove)
            {
                _retryStates.Remove(sid);
            }
        }

        /// <summary>
        /// v0.2.3.39 5B-P0-S3（Codex 第四十二次审计 §4.1 授权）：
        ///   单 SteamID 事件驱动清理入口。由 Plugin.OnEnemyDisconnectedHandler 调用。
        ///   仅删除指定 SteamID 的 RetryState，不影响其他在线玩家。
        ///   线程边界：与 Tick 同主线程访问，无需锁。
        /// </summary>
        /// <param name="steamId">断线玩家的 SteamID</param>
        /// <returns>是否实际删除（true=已删除，false=未找到/steamId=0）</returns>
        public static bool RemoveRetryState(ulong steamId)
        {
            if (steamId == 0UL) return false;
            try
            {
                int countBefore = _retryStates.Count;
                bool removed = _retryStates.Remove(steamId);
                int countAfter = _retryStates.Count;
                RoleLogger.Info("[Shared]",
                    $"[P0-S3] RemoveRetryState steamId={DiagnosticMaskUtil.MaskSteamId(steamId)} "
                    + $"removed={removed} countBefore={countBefore} countAfter={countAfter}");
                return removed;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P0-S3] RemoveRetryState 异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 客机断开时清理重试状态。
        /// v0.2.3.39 5B-P0-S3：保留向后兼容入口，但 Plugin.OnEnemyDisconnectedHandler
        ///   现在直接调用 RemoveRetryState(steamId) 做单 ID 清理。
        /// </summary>
        public static void OnClientDisconnected()
        {
            _retryStates.Clear();
        }

        /// <summary>
        /// 尝试一次 NotifyClothingIsVisible 调用。成功标记 Completed，失败推进下一次重试或放弃。
        /// </summary>
        private static void AttemptNotifyClothingIsVisible(RetryState rs, float now)
        {
            try
            {
                Player player = rs.Player;
                if (player == null)
                {
                    rs.Completed = true;
                    return;
                }

                // 再次校验基础门控（状态可能在重试间隔内变化）
                if (!HostManager.IsP2PHostMode || !Provider.isServer || Dedicator.IsDedicatedServer)
                {
                    rs.Completed = true;
                    return;
                }
                if (player.channel == null || player.channel.IsLocalPlayer) { rs.Completed = true; return; }
                SteamPlayer owner = player.channel.owner;
                if (owner == null || owner.IsLocalServerHost) { rs.Completed = true; return; }

                PlayerAnimator animator = player.animator;
                if (animator == null)
                {
                    AdvanceRetry(rs, now, "animator=null");
                    return;
                }

                PlayerLife life = player.life;
                if (life == null || !life.IsAlive)
                {
                    AdvanceRetry(rs, now, "life=null 或 !IsAlive");
                    return;
                }

                // v0.2.3.22 High-1 新增门控：clothing != null
                PlayerClothing clothing = player.clothing;
                if (clothing == null)
                {
                    AdvanceRetry(rs, now, "clothing=null");
                    return;
                }

                // v0.2.3.22 High-1 新增门控：thirdRenderer_0/1 非空 + sharedMaterial 非空
                SkinnedMeshRenderer smr0 = _thirdRenderer0Field?.GetValue(animator) as SkinnedMeshRenderer;
                SkinnedMeshRenderer smr1 = _thirdRenderer1Field?.GetValue(animator) as SkinnedMeshRenderer;
                if (smr0 == null || smr1 == null)
                {
                    AdvanceRetry(rs, now, $"thirdRenderer_0={smr0 != null} thirdRenderer_1={smr1 != null}");
                    return;
                }
                if (smr0.sharedMaterial == null || smr1.sharedMaterial == null)
                {
                    AdvanceRetry(rs, now, $"sharedMaterial null: smr0={smr0.sharedMaterial != null} smr1={smr1.sharedMaterial != null}");
                    return;
                }

                // 全部门控通过，执行调用
                bool hiddenBefore = ReadIsHiddenWaitingForClothing(animator);
                int smrEnabledBefore = CountSmrEnabled(player);
                int smrTotalBefore = CountSmrTotal(player);
                int materialNullBefore = CountMaterialNull(player);

                animator.NotifyClothingIsVisible();

                bool hiddenAfter = ReadIsHiddenWaitingForClothing(animator);
                int smrEnabledAfter = CountSmrEnabled(player);
                int smrTotalAfter = CountSmrTotal(player);
                int materialNullAfter = CountMaterialNull(player);

                // 读取服装 ID（public 属性，无需反射）
                ushort shirtId = 0, pantsId = 0, hatId = 0;
                try
                {
                    shirtId = clothing.shirt;
                    pantsId = clothing.pants;
                    hatId = clothing.hat;
                }
                catch { }

                RoleLogger.Info("[Host]",
                    $"[P0-S3] NotifyClothingIsVisible bridge SUCCESS attempt={rs.AttemptIndex + 1}/{RetryDelays.Length} " +
                    $"steamId={DiagnosticMaskUtil.MaskSteamId(rs.SteamId)} " +
                    $"hiddenBefore={hiddenBefore} hiddenAfter={hiddenAfter} " +
                    $"smrEnabledBefore={smrEnabledBefore} smrEnabledAfter={smrEnabledAfter} " +
                    $"smrTotalBefore={smrTotalBefore} smrTotalAfter={smrTotalAfter} " +
                    $"materialNullBefore={materialNullBefore} materialNullAfter={materialNullAfter} " +
                    $"shirt={shirtId} pants={pantsId} hat={hatId}");

                rs.Completed = true;
            }
            catch (System.Exception ex)
            {
                AdvanceRetry(rs, now, $"exception: {ex.Message}");
            }
        }

        private static void AdvanceRetry(RetryState rs, float now, string reason)
        {
            rs.LastFailReason = reason;
            rs.AttemptIndex++;
            if (rs.AttemptIndex >= RetryDelays.Length)
            {
                // 超过重试上限，只报警不写字段
                RoleLogger.Warn("[Host]",
                    $"[P0-S3] 重试上限已到 attempt={rs.AttemptIndex}/{RetryDelays.Length} " +
                    $"steamId={DiagnosticMaskUtil.MaskSteamId(rs.SteamId)} " +
                    $"lastFailReason={reason}，放弃（不写字段）");
                rs.Completed = true;
            }
            else
            {
                rs.NextRetryTime = now + RetryDelays[rs.AttemptIndex];
                RoleLogger.Info("[Host]",
                    $"[P0-S3] 延迟重试 schedule attempt={rs.AttemptIndex + 1}/{RetryDelays.Length} " +
                    $"delay={RetryDelays[rs.AttemptIndex]}s steamId={DiagnosticMaskUtil.MaskSteamId(rs.SteamId)} " +
                    $"reason={reason}");
            }
        }

        private static bool ReadIsHiddenWaitingForClothing(PlayerAnimator animator)
        {
            try
            {
                if (_isHiddenWaitingForClothingField == null) return false;
                return (bool)_isHiddenWaitingForClothingField.GetValue(animator);
            }
            catch
            {
                return false;
            }
        }

        private static int CountSmrEnabled(Player player)
        {
            try
            {
                SkinnedMeshRenderer[] smrs = player.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                if (smrs == null || smrs.Length == 0) return 0;
                int enabled = 0;
                foreach (var smr in smrs)
                {
                    if (smr != null && smr.enabled) enabled++;
                }
                return enabled;
            }
            catch
            {
                return 0;
            }
        }

        private static int CountSmrTotal(Player player)
        {
            try
            {
                SkinnedMeshRenderer[] smrs = player.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                return smrs?.Length ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private static int CountMaterialNull(Player player)
        {
            try
            {
                SkinnedMeshRenderer[] smrs = player.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                if (smrs == null || smrs.Length == 0) return 0;
                int nullCount = 0;
                foreach (var smr in smrs)
                {
                    if (smr != null && smr.sharedMaterial == null) nullCount++;
                }
                return nullCount;
            }
            catch
            {
                return 0;
            }
        }

        private static class Hooks
        {
            /// <summary>
            /// P0-S3: Player.InitializePlayer Postfix。
            /// 在远程玩家 InitializePlayer 成功返回后，登记重试任务。
            /// v0.2.3.22 High-1: 前置不全时有界延迟重试（0/1/3 秒），成功后停止；不得每帧强开。
            /// v0.2.3.38 4B 编码 E-1/E-2：内嵌诊断日志，不修改 _retryStates 逻辑。
            /// </summary>
            internal static void InitializePlayerPostfix(Player __instance)
            {
                try
                {
                    if (!HostManager.IsP2PHostMode) return;
                    if (!Provider.isServer) return;
                    if (Dedicator.IsDedicatedServer) return;
                    if (__instance == null) return;
                    if (!P0S3_ReflectionComplete) return; // 反射未就绪，fail-safe

                    if (__instance.channel == null || __instance.channel.IsLocalPlayer) return;

                    SteamPlayer owner = __instance.channel.owner;
                    if (owner == null) return;
                    if (owner.IsLocalServerHost) return;

                    ulong steamId = 0;
                    try
                    {
                        steamId = owner.playerID?.steamID.m_SteamID ?? 0UL;
                    }
                    catch { }
                    if (steamId == 0UL) return;

                    // v0.2.3.38 4B 编码 E-1：有效门控全部通过，即将进入 ContainsKey 检查。
                    // 此日志证明 Postfix 已通过所有 fail-safe 门控，到达"登记重试任务"分支入口。
                    RoleLogger.Info("[Host]",
                        $"[P0-S3] E-1 InitializePlayerPostfix 通过全部门控，即将检查 ContainsKey " +
                        $"steamId={DiagnosticMaskUtil.MaskSteamId(steamId)} retryStatesCount={RetryStatesCount}");

                    // 登记重试任务（首次尝试在下一帧 Tick 时执行，等价 0s 延迟）
                    if (_retryStates.TryGetValue(steamId, out RetryState existing))
                    {
                        // v0.2.3.38 4B 编码 R1：旧 RetryState 提前返回记录关键字段。
                        // 仅观察，不修改旧项，不覆盖字典。记录 attempt/completed/playerIsNull/lastFailReason，
                        // 4C 可证明命中项是第一会话遗留的 Completed=true 项或验证 Player 引用状态。
                        RoleLogger.Info("[Host]",
                            $"[P0-S3] E-2 TryGetValue=true 旧 RetryState 已存在，提前返回不覆盖 " +
                            $"steamId={DiagnosticMaskUtil.MaskSteamId(steamId)} retryStatesCount={RetryStatesCount} " +
                            $"attempt={existing?.AttemptIndex ?? -1} " +
                            $"completed={existing?.Completed ?? false} " +
                            $"playerIsNull={existing?.Player == null} " +
                            $"lastFailReason={existing?.LastFailReason ?? "<null>"}");
                        return; // 已在队列
                    }
                    var rs = new RetryState
                    {
                        SteamId = steamId,
                        Player = __instance,
                        AttemptIndex = 0,
                        NextRetryTime = Time.realtimeSinceStartup + RetryDelays[0],
                        Completed = false,
                        LastFailReason = null,
                    };
                    _retryStates[steamId] = rs;

                    RoleLogger.Info("[Host]",
                        $"[P0-S3] InitializePlayer Postfix 登记重试任务 steamId={DiagnosticMaskUtil.MaskSteamId(steamId)} " +
                        $"（将在 0/{RetryDelays[1]}/{RetryDelays[2]}s 重试，High-1 有界延迟）");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Host]", $"[P0-S3] InitializePlayerPostfix 异常（不阻断）: {ex.Message}");
                }
            }
        }
    }
}
