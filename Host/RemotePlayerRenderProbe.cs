using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Collections.Generic;
using UnityEngine;

namespace SteamP2PFriends.Host
{
    /// <summary>
    /// 房主看不到客机模型问题的独立 High 诊断。
    ///
    ///   - 区分 Renderer.enabled 与 renderer.gameObject.activeInHierarchy，输出"真正可渲染的 active+enabled 数"。
    ///   - 保留 activeSelf/activeInHierarchy/layer/position/scale/animator/movement/stance。
    ///
    ///   - 新增 SkinnedMeshRenderer 数量 + enabled 数量 + sharedMaterial 是否为 null
    ///   - 新增 PlayerClothing 三槽状态（shirt/pants/hat public 属性 + shirtQuality/pantsQuality/hatQuality public 字段）
    ///   - 目的：定位 H3 假设（模型加载分支跳过）+ H1 假设（Clothing 状态同步失效）
    ///   - 注：PlayerClothing.shirt/pants/hat 是 public 属性（背后 thirdClothes.shirt/pants/hat 字段）
    ///   - 注：PlayerClothing.shirtQuality/pantsQuality/hatQuality 是 public 字段（L117 等）
    ///
    /// 背景：
    ///   主机日志确定发生 NetMessages.SendMessageToClient(PlayerConnected)
    ///   transport=TransportConnection_Loopback NotSupportedException
    ///   该消息正是 vanilla 用来"告诉既有玩家新玩家已连接"的路径，
    ///   与房主无法看到客机模型在时间上吻合，应视为 High，不能静默跳过。
    ///
    ///   - 不静默吞掉 PlayerConnected loopback NotSupportedException
    ///   - 不直接调用 ClientMessageHandler_PlayerConnected.ReadMessage（会重复 Provider.addPlayer）
    ///   - 不宣称该异常与模型不可见无关
    ///
    ///   只读 RemotePlayerRenderProbe 周期性采样（每 0.5 秒检查一次）所有远程 SteamPlayer 的渲染状态。
    ///   每个远程玩家首次出现时立即采样，然后在 1s/3s/10s/30s/60s 时间点各采样一次（共 6 次固定采样）。
    ///   若检测到 active/renderer/position 等状态变化，额外输出一条 state-changed 采样日志。
    ///   玩家断线时清除其状态，重连后重新开始采样。
    ///   不修改任何游戏状态。
    ///
    /// 采样内容：
    ///   - GameObject activeSelf/activeInHierarchy
    ///   - Renderer 总数 / enabled 数 / gameObject.activeInHierarchy 数 / 真正可渲染（enabled + activeInHierarchy）数
    ///   - layer、position、scale
    ///   - PlayerAnimator/PlayerMovement/PlayerStance 初始化状态
    ///   - SteamPlayer 是否仅存在一份（按 steamId 去重）
    ///
    /// 该 probe 不解决模型不可见问题，仅收集证据供下一轮审计决策。
    /// </summary>
    public static class RemotePlayerRenderProbe
    {
        // 每 0.5s 检查一次（提高响应速度，不等于每 0.5s 输出日志）
        private const float TickIntervalSeconds = 0.5f;

        // 固定采样时间点（秒）：首次出现 + 1s + 3s + 10s + 30s + 60s
        private static readonly float[] SampleSchedule = new float[] { 0f, 1f, 3f, 10f, 30f, 60f };
        private const float ScheduleTolerance = 0.25f;

        // 状态变化位置阈值（米）：移动超过 2 米视为状态变化
        private const float PositionChangeThreshold = 2f;
        private const float PositionChangeThresholdSq = PositionChangeThreshold * PositionChangeThreshold;

        private static float _tickAccumulator;
        private static bool _initialized;

        private static readonly Dictionary<ulong, PlayerProbeState> _playerStates = new Dictionary<ulong, PlayerProbeState>();

        public static bool IsInitialized => _initialized;

        public static void Initialize()
        {
            if (!PluginLogPolicy.IsVerboseDiagnosticsEnabled)
            {
                _initialized = false;
                return;
            }

            _initialized = true;
            RoleLogger.Info("[Shared]",
                "[RemotePlayerRenderProbe] 已初始化（0.5s 检查间隔，采样时间点 0/1/3/10/30/60s + 状态变化驱动）");
        }

        public static void Shutdown()
        {
            _initialized = false;
            _playerStates.Clear();
            _tickAccumulator = 0f;
        }

        /// <summary>
        /// 清除已不在 Provider.clients 中的玩家状态，重连后重新开始采样。
        ///
        ///   - SteamPlayerID 重载 ==/!= 运算符但未判空，`sp.playerID != null` 会 NRE。
        ///     根因：operator ==(a, b) 实现为 `a.steamID == b.steamID`，b=null 时 `null.steamID` NRE。
        ///   - 改用 `?.` + `?? 0UL` 模式，绕开运算符陷阱。
        ///   - 增加 Provider.clients null 检查（shutdown 期间可能为 null）。
        ///   - 异常日志改用 ex.ToString() 输出完整堆栈，便于后续定位。
        /// </summary>
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
                    if (steamId != 0UL)
                    {
                        activeSteamIds.Add(steamId);
                    }
                }

                var keysToRemove = new List<ulong>();
                foreach (var key in _playerStates.Keys)
                {
                    if (!activeSteamIds.Contains(key))
                    {
                        keysToRemove.Add(key);
                    }
                }

                foreach (var key in keysToRemove)
                {
                    _playerStates.Remove(key);
                }

                if (keysToRemove.Count > 0)
                {
                    RoleLogger.Info("[Shared]",
                        $"[RemotePlayerRenderProbe] OnClientDisconnected 清除断线玩家状态 ({keysToRemove.Count} 个 steamId)");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[RemotePlayerRenderProbe] OnClientDisconnected 异常: {ex}");
            }
        }

        /// <summary>
        /// </summary>
        public static void ResetAll()
        {
            int cleared = _playerStates.Count;
            _playerStates.Clear();
            RoleLogger.Info("[Shared]",
                $"[RemotePlayerRenderProbe] ResetAll 清空所有状态 ({cleared} 个 steamId)");
        }

        /// <summary>
        /// 每帧调用，内部按 TickIntervalSeconds 节流。
        /// </summary>
        public static void Tick()
        {
            if (!PluginLogPolicy.IsVerboseDiagnosticsEnabled || !_initialized) return;
            if (!Provider.isServer || !Level.isLoaded) return;

            _tickAccumulator += Time.deltaTime;
            if (_tickAccumulator < TickIntervalSeconds) return;
            _tickAccumulator = 0f;

            try
            {
                SampleAllRemotePlayers();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[RemotePlayerRenderProbe] Tick 异常: {ex}");
            }
        }

        private static void SampleAllRemotePlayers()
        {
            if (Provider.clients == null) return;

            // 收集所有远程 SteamPlayer（非本地玩家）
            // 同时检测是否有重复 steamId
            var seenSteamIds = new Dictionary<ulong, int>();
            int totalClients = Provider.clients.Count;

            foreach (SteamPlayer sp in Provider.clients)
            {
                if (sp == null) continue;

                ulong steamId = sp.playerID?.steamID.m_SteamID ?? 0UL;
                if (steamId == 0UL) continue;

                if (!seenSteamIds.ContainsKey(steamId))
                {
                    seenSteamIds[steamId] = 1;
                }
                else
                {
                    seenSteamIds[steamId]++;
                }
            }

            // 输出重复检测
            foreach (var kv in seenSteamIds)
            {
                if (kv.Value > 1)
                {
                    RoleLogger.Warn("[Host]",
                        $"[RemotePlayerRenderProbe] !!! 重复 SteamPlayer 检测 steamId={kv.Key} count={kv.Value} " +
                        $"(可能与 PlayerConnected loopback NotSupportedException 有关，注意 addPlayer 是否被重复调用)");
                }
            }

            // 清理已断线玩家的状态（惰性清理：补丁 OnClientDisconnected 漏触发的兜底）
            var staleKeys = new List<ulong>();
            foreach (var key in _playerStates.Keys)
            {
                if (!seenSteamIds.ContainsKey(key))
                {
                    staleKeys.Add(key);
                }
            }
            foreach (var key in staleKeys)
            {
                _playerStates.Remove(key);
                RoleLogger.Info("[Host]",
                    $"[RemotePlayerRenderProbe] 惰性清理已断线玩家状态 steamId={key}");
            }

            // 采样每个远程玩家的渲染状态
            foreach (SteamPlayer sp in Provider.clients)
            {
                if (sp == null) continue;
                if (sp.player == null) continue;

                // 仅采样远程玩家（非房主自连）
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

            // 计算当前快照
            PlayerSnapshot current = ComputeSnapshot(player);
            if (current == null) return;

            PlayerProbeState state;
            bool isFirstSighting = !_playerStates.TryGetValue(steamId, out state);
            if (isFirstSighting)
            {
                state = new PlayerProbeState
                {
                    ConnectTime = Time.realtimeSinceStartup,
                    NextScheduleIndex = 0,
                    LastSnapshot = null,
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
                state.NextScheduleIndex = 1; // 已完成 schedule[0]=0s，下一个是 schedule[1]=1s
            }
            else
            {
                // 检查是否到达下一个采样时间点
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
                    else
                    {
                        break;
                    }
                }
            }

            // 状态变化检测（即使已经达到 schedule 采样，也额外标记 state-changed）
            if (state.LastSnapshot != null && HasStateChanged(state.LastSnapshot, current))
            {
                if (!shouldSample)
                {
                    shouldSample = true;
                    reason = "state-changed";
                }
                else
                {
                    reason = reason + "+state-changed";
                }
            }

            if (!shouldSample) return;

            state.SampleCount++;
            state.LastSnapshot = current;

            LogSample(steamId, state.SampleCount, totalClients, reason, elapsed, current, player);
        }

        private static PlayerSnapshot ComputeSnapshot(Player player)
        {
            if (player == null) return null;

            GameObject go = player.gameObject;
            if (go == null)
            {
                return null;
            }

            PlayerSnapshot snap = new PlayerSnapshot();
            snap.ActiveSelf = go.activeSelf;
            snap.ActiveInHierarchy = go.activeInHierarchy;
            snap.Layer = go.layer;

            try
            {
                snap.Position = player.transform.position;
                snap.Scale = player.transform.localScale;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Host]", $"[RemotePlayerRenderProbe] transform 访问异常 steamId=? err={ex.Message}");
                snap.Position = Vector3.zero;
                snap.Scale = Vector3.one;
            }

            Renderer[] renderers = go.GetComponentsInChildren<Renderer>(includeInactive: true);
            snap.RendererTotal = renderers != null ? renderers.Length : 0;
            snap.RendererEnabled = 0;
            snap.RendererActiveInHierarchy = 0;
            snap.RendererBothActive = 0; // 真正可渲染：enabled + gameObject.activeInHierarchy

            if (renderers != null)
            {
                foreach (Renderer r in renderers)
                {
                    if (r == null) continue;

                    bool rEnabled = r.enabled;
                    bool rActiveInHierarchy = r.gameObject != null && r.gameObject.activeInHierarchy;

                    if (rEnabled) snap.RendererEnabled++;
                    if (rActiveInHierarchy) snap.RendererActiveInHierarchy++;
                    if (rEnabled && rActiveInHierarchy) snap.RendererBothActive++;
                }
            }

            snap.SkinnedMeshRendererTotal = 0;
            snap.SkinnedMeshRendererEnabled = 0;
            snap.SkinnedMeshRendererMaterialNull = 0;
            try
            {
                SkinnedMeshRenderer[] smrs = go.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true);
                if (smrs != null)
                {
                    snap.SkinnedMeshRendererTotal = smrs.Length;
                    foreach (SkinnedMeshRenderer smr in smrs)
                    {
                        if (smr == null) continue;
                        if (smr.enabled) snap.SkinnedMeshRendererEnabled++;
                        if (smr.sharedMaterial == null) snap.SkinnedMeshRendererMaterialNull++;
                    }
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Host]", $"[RemotePlayerRenderProbe] SkinnedMeshRenderer 采样异常: {ex.Message}");
            }

            // shirt/pants/hat 是 public 属性，shirtQuality/pantsQuality/hatQuality 是 public 字段
            snap.ClothShirt = 0;
            snap.ClothPants = 0;
            snap.ClothHat = 0;
            snap.ClothShirtQuality = 0;
            snap.ClothPantsQuality = 0;
            snap.ClothHatQuality = 0;
            snap.ClothPresent = false;
            try
            {
                PlayerClothing cloth = player.clothing;
                snap.ClothPresent = cloth != null;
                if (cloth != null)
                {
                    snap.ClothShirt = cloth.shirt;
                    snap.ClothPants = cloth.pants;
                    snap.ClothHat = cloth.hat;
                    snap.ClothShirtQuality = cloth.shirtQuality;
                    snap.ClothPantsQuality = cloth.pantsQuality;
                    snap.ClothHatQuality = cloth.hatQuality;
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Host]", $"[RemotePlayerRenderProbe] PlayerClothing 采样异常: {ex.Message}");
            }

            try { snap.AnimatorPresent = player.animator != null; }
            catch (System.Exception) { snap.AnimatorPresent = false; }

            try { snap.MovementPresent = player.movement != null; }
            catch (System.Exception) { snap.MovementPresent = false; }

            try { snap.StancePresent = player.stance != null; }
            catch (System.Exception) { snap.StancePresent = false; }

            // movement 状态
            snap.MovementState = "n/a";
            if (snap.MovementPresent)
            {
                try
                {
                    bool inVehicle = player.movement.getVehicle() != null;
                    snap.MovementState = inVehicle ? "inVehicle" : "onFoot";
                }
                catch
                {
                    snap.MovementState = "getVehicle_threw";
                }
            }

            return snap;
        }

        private static bool HasStateChanged(PlayerSnapshot prev, PlayerSnapshot curr)
        {
            if (prev.ActiveSelf != curr.ActiveSelf) return true;
            if (prev.ActiveInHierarchy != curr.ActiveInHierarchy) return true;
            if (prev.RendererBothActive != curr.RendererBothActive) return true;
            if (prev.RendererEnabled != curr.RendererEnabled) return true;
            if (prev.RendererTotal != curr.RendererTotal) return true;
            if (prev.AnimatorPresent != curr.AnimatorPresent) return true;
            if (prev.MovementPresent != curr.MovementPresent) return true;
            if (prev.StancePresent != curr.StancePresent) return true;

            if (prev.SkinnedMeshRendererTotal != curr.SkinnedMeshRendererTotal) return true;
            if (prev.SkinnedMeshRendererEnabled != curr.SkinnedMeshRendererEnabled) return true;
            if (prev.SkinnedMeshRendererMaterialNull != curr.SkinnedMeshRendererMaterialNull) return true;

            if (prev.ClothShirt != curr.ClothShirt) return true;
            if (prev.ClothPants != curr.ClothPants) return true;
            if (prev.ClothHat != curr.ClothHat) return true;
            if (prev.ClothShirtQuality != curr.ClothShirtQuality) return true;
            if (prev.ClothPantsQuality != curr.ClothPantsQuality) return true;
            if (prev.ClothHatQuality != curr.ClothHatQuality) return true;

            // 位置变化阈值
            float dx = prev.Position.x - curr.Position.x;
            float dy = prev.Position.y - curr.Position.y;
            float dz = prev.Position.z - curr.Position.z;
            if (dx * dx + dy * dy + dz * dz > PositionChangeThresholdSq) return true;

            return false;
        }

        private static void LogSample(ulong steamId, int sampleCount, int totalClients,
            string reason, float elapsed, PlayerSnapshot snap, Player player)
        {
            Vector3 pos = snap.Position;
            Vector3 scale = snap.Scale;

            RoleLogger.Info("[Host]",
                $"[RemotePlayerRenderProbe] steamId={steamId} sample={sampleCount} reason={reason} " +
                $"elapsed={elapsed:F2}s totalClients={totalClients} " +
                $"activeSelf={snap.ActiveSelf} activeInHierarchy={snap.ActiveInHierarchy} layer={snap.Layer} " +
                $"pos=({pos.x:F2},{pos.y:F2},{pos.z:F2}) scale=({scale.x:F2},{scale.y:F2},{scale.z:F2}) " +
                $"renderers(enabled={snap.RendererEnabled},activeInHierarchy={snap.RendererActiveInHierarchy},both={snap.RendererBothActive},total={snap.RendererTotal}) " +
                $"smr(enabled={snap.SkinnedMeshRendererEnabled},matNull={snap.SkinnedMeshRendererMaterialNull},total={snap.SkinnedMeshRendererTotal}) " +
                $"cloth(present={snap.ClothPresent},shirt={snap.ClothShirt}/q{snap.ClothShirtQuality}," +
                $"pants={snap.ClothPants}/q{snap.ClothPantsQuality},hat={snap.ClothHat}/q{snap.ClothHatQuality}) " +
                $"animator={snap.AnimatorPresent} movement={snap.MovementPresent}({snap.MovementState}) stance={snap.StancePresent}");
        }

        private class PlayerProbeState
        {
            public float ConnectTime;
            public int NextScheduleIndex;
            public PlayerSnapshot LastSnapshot;
            public int SampleCount;
        }

        private class PlayerSnapshot
        {
            public bool ActiveSelf;
            public bool ActiveInHierarchy;
            public int Layer;
            public Vector3 Position;
            public Vector3 Scale;
            public int RendererTotal;
            public int RendererEnabled;
            public int RendererActiveInHierarchy;
            public int RendererBothActive;
            public bool AnimatorPresent;
            public bool MovementPresent;
            public string MovementState;
            public bool StancePresent;

            public int SkinnedMeshRendererTotal;
            public int SkinnedMeshRendererEnabled;
            public int SkinnedMeshRendererMaterialNull;

            // shirt/pants/hat 是 public 属性（背后 thirdClothes.shirt/pants/hat 字段）
            // shirtQuality/pantsQuality/hatQuality 是 public 字段
            public bool ClothPresent;
            public ushort ClothShirt;
            public ushort ClothPants;
            public ushort ClothHat;
            public byte ClothShirtQuality;
            public byte ClothPantsQuality;
            public byte ClothHatQuality;
        }
    }
}
