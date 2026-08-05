using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SteamP2PFriends.Client
{
    /// <summary>
    /// v0.2.3.19 D-Vis-6 客机端扩展（ClientRemotePlayerRenderProbe）。
    ///
    /// 背景：
    ///   v0.2.3.18 RemotePlayerRenderProbe 在 Provider.isServer=true 时运行（房主端），
    ///   客机端 Provider.isServer=false，直接 return，所以 D-Vis-6 客机端 0 次是预期行为。
    ///   审计建议新增客机端专用 RenderProbe，让客机端也能采样房主模型渲染状态。
    ///
    /// 设计：
    ///   - Tick() 守卫改为 `if (Provider.isServer) return;`（仅客机端运行）
    ///   - 采样目标：Provider.clients 中非本地 Player（即房主）
    ///   - 采样字段：与 RemotePlayerRenderProbe 一致（smr/cloth/animator/movement/stance）
    ///   - 新增字段：isHiddenWaitingForClothing（反射读取 PlayerAnimator，关联 D-Vis-10）
    ///
    /// 诊断目标：
    ///   - 采集客机端房主模型的 SMR.enabled 状态
    ///   - 对比主机端客机模型 SMR.enabled 状态
    ///   - 验证"平行宇宙"是否双向（双方都看不见对方模型）
    ///
    /// 严格禁止：
    ///   - 修改任何游戏状态
    ///   - 修改 SMR.enabled / isHiddenWaitingForClothing
    /// </summary>
    public static class ClientRemotePlayerRenderProbe
    {
        // 每 0.5s 检查一次
        private const float TickIntervalSeconds = 0.5f;

        // 固定采样时间点（秒）：首次出现 + 1s + 3s + 10s + 30s + 60s
        private static readonly float[] SampleSchedule = new float[] { 0f, 1f, 3f, 10f, 30f, 60f };
        private const float ScheduleTolerance = 0.25f;

        // 状态变化位置阈值（米）：移动超过 2 米视为状态变化
        private const float PositionChangeThreshold = 2f;
        private const float PositionChangeThresholdSq = PositionChangeThreshold * PositionChangeThreshold;

        private static float _tickAccumulator;
        private static bool _initialized;

        private static readonly Dictionary<ulong, ClientProbeState> _playerStates = new Dictionary<ulong, ClientProbeState>();

        // 反射缓存：PlayerAnimator.isHiddenWaitingForClothing（private bool，L152）
        private static FieldInfo _isHiddenWaitingForClothingField;
        private static FieldInfo _thirdRenderer0Field;
        private static FieldInfo _thirdRenderer1Field;

        public static bool IsInitialized => _initialized;

        public static void Initialize()
        {
            _initialized = true;
            CacheReflection();
            RoleLogger.Info("[Shared]",
                "[ClientRemotePlayerRenderProbe] 已初始化（客机端专用，0.5s 间隔，采样时间点 0/1/3/10/30/60s + 状态变化驱动）");
        }

        public static void Shutdown()
        {
            _initialized = false;
            _playerStates.Clear();
            _tickAccumulator = 0f;
        }

        private static void CacheReflection()
        {
            try
            {
                _isHiddenWaitingForClothingField = AccessTools.Field(typeof(PlayerAnimator), "isHiddenWaitingForClothing");
                _thirdRenderer0Field = AccessTools.Field(typeof(PlayerAnimator), "thirdRenderer_0");
                _thirdRenderer1Field = AccessTools.Field(typeof(PlayerAnimator), "thirdRenderer_1");
                if (_isHiddenWaitingForClothingField == null)
                    RoleLogger.Warn("[Shared]", "[ClientRemotePlayerRenderProbe] isHiddenWaitingForClothing 反射失败");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[ClientRemotePlayerRenderProbe] 反射缓存异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 每帧调用，内部按 TickIntervalSeconds 节流。
        /// 客机端专用：Provider.isServer=true 时直接 return。
        /// </summary>
        public static void Tick()
        {
            if (!_initialized) return;
            // 关键守卫：仅客机端运行（Provider.isServer=false）
            if (Provider.isServer) return;
            if (!Level.isLoaded) return;

            _tickAccumulator += Time.deltaTime;
            if (_tickAccumulator < TickIntervalSeconds) return;
            _tickAccumulator = 0f;

            try
            {
                SampleAllRemotePlayers();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[ClientRemotePlayerRenderProbe] Tick 异常: {ex}");
            }
        }

        private static void SampleAllRemotePlayers()
        {
            if (Provider.clients == null) return;

            var seenSteamIds = new Dictionary<ulong, int>();
            int totalClients = Provider.clients.Count;

            foreach (SteamPlayer sp in Provider.clients)
            {
                if (sp == null) continue;
                ulong steamId = sp.playerID?.steamID.m_SteamID ?? 0UL;
                if (steamId == 0UL) continue;
                if (!seenSteamIds.ContainsKey(steamId)) seenSteamIds[steamId] = 1;
                else seenSteamIds[steamId]++;
            }

            // 惰性清理断线玩家
            var staleKeys = new List<ulong>();
            foreach (var key in _playerStates.Keys)
            {
                if (!seenSteamIds.ContainsKey(key)) staleKeys.Add(key);
            }
            foreach (var key in staleKeys)
            {
                _playerStates.Remove(key);
                RoleLogger.Info("[Client]",
                    $"[ClientRemotePlayerRenderProbe] 惰性清理已断线玩家状态 steamId={DiagnosticMaskUtil.MaskSteamId(key)}");
            }

            // 采样每个远程玩家（客机端：非本地 Player = 房主或其他客机）
            foreach (SteamPlayer sp in Provider.clients)
            {
                if (sp == null) continue;
                if (sp.player == null) continue;
                if (sp.player.channel == null || sp.player.channel.IsLocalPlayer) continue;

                ulong steamId = sp.playerID?.steamID.m_SteamID ?? 0UL;
                if (steamId == 0UL) continue;

                SampleOrDetect(sp, steamId, totalClients);
            }
        }

        private static void SampleOrDetect(SteamPlayer sp, ulong steamId, int totalClients)
        {
            Player player = sp.player;
            if (player == null) return;

            ClientProbeState state;
            bool isFirstSighting = !_playerStates.TryGetValue(steamId, out state);
            if (isFirstSighting)
            {
                state = new ClientProbeState
                {
                    ConnectTime = Time.realtimeSinceStartup,
                    NextScheduleIndex = 0,
                    SampleCount = 0
                };
                _playerStates[steamId] = state;
            }

            float elapsed = Time.realtimeSinceStartup - state.ConnectTime;

            bool shouldSample = false;
            string reason = null;

            if (isFirstSighting)
            {
                shouldSample = true;
                reason = "first-sighting";
                state.NextScheduleIndex = 1;
            }
            else
            {
                while (state.NextScheduleIndex < SampleSchedule.Length)
                {
                    float nextTime = SampleSchedule[state.NextScheduleIndex];
                    if (elapsed >= nextTime - ScheduleTolerance)
                    {
                        shouldSample = true;
                        reason = $"schedule[{state.NextScheduleIndex}]={nextTime}s";
                        state.NextScheduleIndex++;
                        break;
                    }
                    else break;
                }
            }

            // 位置变化检测
            Vector3 currentPos = player.transform.position;
            if (state.HasLastPosition)
            {
                Vector3 delta = currentPos - state.LastPosition;
                if (delta.sqrMagnitude > PositionChangeThresholdSq)
                {
                    if (!shouldSample) { shouldSample = true; reason = "state-changed"; }
                    else reason = reason + "+state-changed";
                }
            }
            state.LastPosition = currentPos;
            state.HasLastPosition = true;

            if (!shouldSample) return;

            state.SampleCount++;
            LogSample(steamId, state.SampleCount, totalClients, reason, elapsed, currentPos, player);
        }

        private static void LogSample(ulong steamId, int sampleCount, int totalClients, string reason,
            float elapsed, Vector3 pos, Player player)
        {
            try
            {
                GameObject go = player.gameObject;
                bool activeSelf = go != null && go.activeSelf;
                bool activeInHierarchy = go != null && go.activeInHierarchy;
                int layer = go != null ? go.layer : -1;

                // Renderer 统计
                int rendererTotal = 0, rendererEnabled = 0;
                if (go != null)
                {
                    Renderer[] renderers = go.GetComponentsInChildren<Renderer>(includeInactive: true);
                    rendererTotal = renderers != null ? renderers.Length : 0;
                    if (renderers != null)
                    {
                        foreach (Renderer r in renderers)
                        {
                            if (r != null && r.enabled) rendererEnabled++;
                        }
                    }
                }

                // SkinnedMeshRenderer 统计
                int smrTotal = 0, smrEnabled = 0, smrMatNull = 0;
                if (go != null)
                {
                    SkinnedMeshRenderer[] smrs = go.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true);
                    if (smrs != null)
                    {
                        smrTotal = smrs.Length;
                        foreach (SkinnedMeshRenderer smr in smrs)
                        {
                            if (smr == null) continue;
                            if (smr.enabled) smrEnabled++;
                            if (smr.sharedMaterial == null) smrMatNull++;
                        }
                    }
                }

                // isHiddenWaitingForClothing（v0.2.3.19 新增，关联 D-Vis-10）
                string isHiddenStr = "n/a";
                string thirdRendererState = "n/a";
                try
                {
                    PlayerAnimator animator = player.animator;
                    if (animator != null)
                    {
                        if (_isHiddenWaitingForClothingField != null)
                        {
                            bool isHidden = (bool)_isHiddenWaitingForClothingField.GetValue(animator);
                            isHiddenStr = isHidden.ToString();
                        }

                        int tr0Enabled = -1, tr1Enabled = -1;
                        if (_thirdRenderer0Field != null)
                        {
                            SkinnedMeshRenderer smr0 = _thirdRenderer0Field.GetValue(animator) as SkinnedMeshRenderer;
                            tr0Enabled = smr0 != null ? (smr0.enabled ? 1 : 0) : -1;
                        }
                        if (_thirdRenderer1Field != null)
                        {
                            SkinnedMeshRenderer smr1 = _thirdRenderer1Field.GetValue(animator) as SkinnedMeshRenderer;
                            tr1Enabled = smr1 != null ? (smr1.enabled ? 1 : 0) : -1;
                        }
                        thirdRendererState = $"(tr0={tr0Enabled},tr1={tr1Enabled})";
                    }
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Client]", $"[ClientRemotePlayerRenderProbe] isHiddenWaitingForClothing 读取异常: {ex.Message}");
                }

                // PlayerClothing 槽位
                ushort clothShirt = 0, clothPants = 0, clothHat = 0;
                byte clothShirtQ = 0, clothPantsQ = 0, clothHatQ = 0;
                bool clothPresent = false;
                try
                {
                    PlayerClothing cloth = player.clothing;
                    clothPresent = cloth != null;
                    if (cloth != null)
                    {
                        clothShirt = cloth.shirt;
                        clothPants = cloth.pants;
                        clothHat = cloth.hat;
                        clothShirtQ = cloth.shirtQuality;
                        clothPantsQ = cloth.pantsQuality;
                        clothHatQ = cloth.hatQuality;
                    }
                }
                catch { /* ignore */ }

                string posStr = $"({pos.x:F2},{pos.y:F2},{pos.z:F2})";

                RoleLogger.Info("[Client]",
                    $"[ClientRenderProbe] sample={sampleCount} reason={reason} elapsed={elapsed:F2}s " +
                    $"steamId={DiagnosticMaskUtil.MaskSteamId(steamId)} totalClients={totalClients} " +
                    $"activeSelf={activeSelf} activeInHierarchy={activeInHierarchy} layer={layer} pos={posStr} " +
                    $"renderers(enabled={rendererEnabled},total={rendererTotal}) " +
                    $"smr(enabled={smrEnabled},matNull={smrMatNull},total={smrTotal}) " +
                    $"isHiddenWaitingForClothing={isHiddenStr} thirdRenderer={thirdRendererState} " +
                    $"cloth(present={clothPresent},shirt={clothShirt}/q{clothShirtQ},pants={clothPants}/q{clothPantsQ},hat={clothHat}/q{clothHatQ})");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Client]", $"[ClientRemotePlayerRenderProbe] LogSample 异常: {ex.Message}");
            }
        }

        public static void OnClientDisconnected()
        {
            try
            {
                if (Provider.clients == null) return;
                var activeSteamIds = new HashSet<ulong>();
                foreach (SteamPlayer sp in Provider.clients)
                {
                    if (sp == null) continue;
                    ulong steamId = sp.playerID?.steamID.m_SteamID ?? 0UL;
                    if (steamId != 0UL) activeSteamIds.Add(steamId);
                }
                var keysToRemove = new List<ulong>();
                foreach (var key in _playerStates.Keys)
                {
                    if (!activeSteamIds.Contains(key)) keysToRemove.Add(key);
                }
                foreach (var key in keysToRemove) _playerStates.Remove(key);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[ClientRemotePlayerRenderProbe] OnClientDisconnected 异常: {ex}");
            }
        }

        public static void ResetAll()
        {
            int cleared = _playerStates.Count;
            _playerStates.Clear();
            RoleLogger.Info("[Client]",
                $"[ClientRemotePlayerRenderProbe] ResetAll 清空所有状态 ({cleared} 个 steamId)");
        }

        private class ClientProbeState
        {
            public float ConnectTime;
            public int NextScheduleIndex;
            public int SampleCount;
            public Vector3 LastPosition;
            public bool HasLastPosition;
        }
    }
}
