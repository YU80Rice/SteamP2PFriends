using SDG.Unturned;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using UnityEngine;

namespace SteamP2PFriends.Shared
{
    internal enum EDisplayNameSource { Unknown, ConnectedCharacter, SteamPersona }

    internal static class SteamPersonaDisplay
    {
        internal static bool _testBypassThreadAssert;
        internal static Func<string> _testLocalPersonaProvider;
        internal static Func<CSteamID, string> _testRemotePersonaProvider;
        internal static CSteamID? _testLocalSteamId;
        internal static Func<CSteamID, string> _testConnectedNameProvider;
        internal static Action<CSteamID> _testRequestUserInfoCallback;
        internal static Func<float> _testTimeProvider;

        private const float PersonaRequestInterval = 5f;
        private const float PersonaExpirySeconds = 120f;
        private const int MaxPersonaCache = 16;
        private const float ConnectedNameTTL = 300f; // 断线后保留 5 分钟

        // persona 请求缓存（5s 限频、16 上限、120s 过期）
        private struct PersonaCacheEntry
        {
            public string Name;
            public float RequestedAt;
            public float ExpiresAt;
        }
        private static readonly Dictionary<ulong, PersonaCacheEntry> _personaCache = new Dictionary<ulong, PersonaCacheEntry>(16);

        private struct DisplayNameEntry
        {
            internal string Name;
            internal EDisplayNameSource Source;
            internal float ExpiresAt;
        }
        private static readonly Dictionary<ulong, DisplayNameEntry> _displayNameCache = new Dictionary<ulong, DisplayNameEntry>(16);

        // SteamPending 构造可能不在 UI 刷新调用栈中，因此只做无 Unity/Provider 访问的有界入队，
        // 再由 Plugin.Update 主线程提交到显示名缓存。名称从不参与授权或白名单判定。
        private struct CapturedCharacterName
        {
            internal ulong SteamId;
            internal string Name;
            internal int Epoch;
        }

        private const int MaxCapturedCharacterNames = 32;
        private static readonly object CaptureSync = new object();
        private static readonly Queue<CapturedCharacterName> _capturedCharacterNames = new Queue<CapturedCharacterName>();
        private static int _captureEpoch;

        // Beta-5 诊断 probe（仅记录 source/requestIssued/cacheHit/rowCount，不含名称/SteamID）
        private static int _probeRequestIssued;
        private static int _probeCacheHit;
        private static int _probeUnknownReturn;
        internal static int ProbeRequestIssuedForTest => _probeRequestIssued;
        internal static int ProbeCacheHitForTest => _probeCacheHit;
        internal static int ProbeUnknownForTest => _probeUnknownReturn;
        internal static void ResetProbeForTest() { _probeRequestIssued = 0; _probeCacheHit = 0; _probeUnknownReturn = 0; }

        private static void AssertGameThread()
        {
            if (!_testBypassThreadAssert) ThreadUtil.assertIsGameThread();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static float GetTimeFromUnity() { return Time.realtimeSinceStartup; }

        private static float GetTime()
        {
            if (_testTimeProvider != null) return _testTimeProvider();
            try { return GetTimeFromUnity(); } catch { return 0f; }
        }


        /// <summary>缓存已连接玩家的 character name（断线后仍可用）。</summary>
        internal static void RememberConnectedCharacterName(CSteamID id, string characterName)
        {
            AssertGameThread();
            string safe = Normalize(characterName, null);
            if (String.IsNullOrEmpty(safe)) return;
            float now = GetTime();
            ulong key = id.m_SteamID;
            // ConnectedCharacter 优先级高于 SteamPersona；不降级
            DisplayNameEntry existing;
            if (_displayNameCache.TryGetValue(key, out existing) && existing.Source == EDisplayNameSource.ConnectedCharacter)
            {
                existing.Name = safe; existing.ExpiresAt = now + ConnectedNameTTL;
                _displayNameCache[key] = existing;
                return;
            }
            PutDisplayNameBounded(key, safe, EDisplayNameSource.ConnectedCharacter, now + ConnectedNameTTL);
        }

        /// <summary>
        /// 可由 SteamPending 构造 patch 在任意线程调用。只复制不可变值并入有界队列；
        /// 不访问 Provider、Unity、SteamFriends 或 UI，异常不得影响原版握手。
        /// </summary>
        internal static void TryEnqueueObservedCharacterName(ulong steamId, string characterName)
        {
            TryEnqueueObservedIdentity(steamId, characterName, null, null);
        }

        /// <summary>
        /// 原版握手名称优先级：characterName -> playerName -> nickName。
        /// 三者都只是显示投影，永不进入审批/白名单逻辑。
        /// </summary>
        internal static bool TryEnqueueObservedIdentity(
            ulong steamId,
            string characterName,
            string playerName,
            string nickName)
        {
            if (steamId == 0UL) return false;
            string safe = Normalize(characterName, null);
            if (String.IsNullOrEmpty(safe)) safe = Normalize(playerName, null);
            if (String.IsNullOrEmpty(safe)) safe = Normalize(nickName, null);
            if (String.IsNullOrEmpty(safe)) return false;

            int epoch = Volatile.Read(ref _captureEpoch);
            lock (CaptureSync)
            {
                if (_capturedCharacterNames.Count >= MaxCapturedCharacterNames)
                    _capturedCharacterNames.Dequeue();

                _capturedCharacterNames.Enqueue(new CapturedCharacterName
                {
                    SteamId = steamId,
                    Name = safe,
                    Epoch = epoch
                });
            }
            return true;
        }

        /// <summary>仅由 Plugin.Update 的活动 P2P 房主主线程调用。</summary>
        internal static void DrainObservedCharacterNamesOnMainThread()
        {
            AssertGameThread();
            int epoch = Volatile.Read(ref _captureEpoch);

            while (true)
            {
                CapturedCharacterName captured;
                lock (CaptureSync)
                {
                    if (_capturedCharacterNames.Count == 0) return;
                    captured = _capturedCharacterNames.Dequeue();
                }

                if (captured.Epoch != epoch) continue;
                RememberConnectedCharacterName(new CSteamID(captured.SteamId), captured.Name);
            }
        }

        internal static void ResetForSession()
        {
            AssertGameThread();
            Interlocked.Increment(ref _captureEpoch);
            lock (CaptureSync) _capturedCharacterNames.Clear();
            _displayNameCache.Clear();
            _personaCache.Clear();
            ResetProbeForTest();
        }

        internal static void ResetAfterSession()
        {
            ResetForSession();
        }

        private static void PutDisplayNameBounded(ulong key, string name, EDisplayNameSource source, float expiresAt)
        {
            if (!_displayNameCache.ContainsKey(key) && _displayNameCache.Count >= MaxPersonaCache)
            {
                ulong oldestKey = 0; float oldestTime = float.MaxValue;
                foreach (var kv in _displayNameCache) { if (kv.Value.ExpiresAt < oldestTime) { oldestTime = kv.Value.ExpiresAt; oldestKey = kv.Key; } }
                _displayNameCache.Remove(oldestKey);
            }
            _displayNameCache[key] = new DisplayNameEntry { Name = name, Source = source, ExpiresAt = expiresAt };
        }

        private static bool TryGetCachedDisplayName(CSteamID id, out string name)
        {
            name = null;
            ulong key = id.m_SteamID;
            DisplayNameEntry entry;
            if (_displayNameCache.TryGetValue(key, out entry))
            {
                float now = GetTime();
                if (now < entry.ExpiresAt) { name = entry.Name; return true; }
                _displayNameCache.Remove(key);
            }
            return false;
        }

        internal static int DisplayNameCacheCountForTest => _displayNameCache.Count;
        internal static void ClearDisplayNameCacheForTest() => _displayNameCache.Clear();
        internal static int CapturedCharacterNameCountForTest
        {
            get { lock (CaptureSync) return _capturedCharacterNames.Count; }
        }

        // ===== 分层名称解析 =====

        internal static string ResolveDisplayName(CSteamID steamId)
        {
            AssertGameThread();
            if (steamId == CSteamID.Nil || !steamId.IsValid()) return "未知玩家";

            // [指令 A]：本地房主
            CSteamID localId = _testLocalSteamId ?? GetLocalSteamIdSafe();
            if (steamId.m_SteamID == localId.m_SteamID) return GetLocalDisplayName();

            // [指令 B]：已连接玩家 -> character name + 缓存
            string connectedName = TryGetConnectedCharacterName(steamId);
            if (!String.IsNullOrWhiteSpace(connectedName))
            {
                string normalized = Normalize(connectedName, "未知玩家");
                RememberConnectedCharacterName(steamId, connectedName);
                return normalized;
            }

            // 持久缓存（断线后仍显示最后已知名称）
            if (TryGetCachedDisplayName(steamId, out string cached))
            {
                _probeCacheHit++;
                return Normalize(cached, "未知玩家");
            }

            // best-effort Steam persona
            RequestPersonaRateLimited(steamId);
            string persona = TryGetCachedPersona(steamId);
            if (!String.IsNullOrWhiteSpace(persona))
            {
                string normalized = Normalize(persona, "未知玩家");
                PutDisplayNameBounded(steamId.m_SteamID, normalized, EDisplayNameSource.SteamPersona, GetTime() + PersonaExpirySeconds);
                return normalized;
            }

            _probeUnknownReturn++;
            return "未知玩家";
        }

        private static CSteamID GetLocalSteamIdSafe()
        {
            try { return SteamUser.GetSteamID(); } catch { return CSteamID.Nil; }
        }

        private static string TryGetConnectedCharacterName(CSteamID steamId)
        {
            if (_testConnectedNameProvider != null)
            {
                try { return _testConnectedNameProvider(steamId); } catch { return null; }
            }
            if (_testBypassThreadAssert) return null;
            try
            {
                List<SteamPlayer> clients = Provider.clients;
                if (clients == null) return null;
                for (int i = 0; i < clients.Count; i++)
                {
                    SteamPlayer sp = clients[i];
                    if (sp != null && sp.playerID != null && sp.playerID.steamID == steamId)
                        return sp.playerID.characterName;
                }
            }
            catch { }
            return null;
        }

        private static void RequestPersonaRateLimited(CSteamID steamId)
        {
            float now = GetTime();
            ulong key = steamId.m_SteamID;

            PersonaCacheEntry existing;
            if (_personaCache.TryGetValue(key, out existing))
            {
                if (now < existing.ExpiresAt)
                {
                    if (now < existing.RequestedAt + PersonaRequestInterval) return;
                }
                else _personaCache.Remove(key);
            }

            if (_personaCache.Count >= MaxPersonaCache)
            {
                ulong oldestKey = 0; float oldestTime = float.MaxValue;
                foreach (var kv in _personaCache) { if (kv.Value.RequestedAt < oldestTime) { oldestTime = kv.Value.RequestedAt; oldestKey = kv.Key; } }
                _personaCache.Remove(oldestKey);
            }

            _probeRequestIssued++;
            if (_testRequestUserInfoCallback != null) { try { _testRequestUserInfoCallback(steamId); } catch { } }
            else { try { SteamFriends.RequestUserInformation(steamId, true); } catch { } }

            string name = TryGetFriendPersonaName(steamId);
            _personaCache[key] = new PersonaCacheEntry { Name = name, RequestedAt = now, ExpiresAt = now + PersonaExpirySeconds };
        }

        private static string TryGetCachedPersona(CSteamID steamId)
        {
            ulong key = steamId.m_SteamID;
            PersonaCacheEntry entry;
            if (_personaCache.TryGetValue(key, out entry))
            {
                if (GetTime() < entry.ExpiresAt) return entry.Name;
                _personaCache.Remove(key);
            }
            return null;
        }

        private static string TryGetFriendPersonaName(CSteamID steamId)
        {
            if (_testRemotePersonaProvider != null) { try { return _testRemotePersonaProvider(steamId); } catch { return null; } }
            try { return SteamFriends.GetFriendPersonaName(steamId); } catch { return null; }
        }

        // ===== 既有 API =====

        internal static string GetLocalDisplayName()
        {
            AssertGameThread();
            try
            {
                string name = _testLocalPersonaProvider != null ? _testLocalPersonaProvider() : SteamFriends.GetPersonaName();
                return Normalize(name, "房主");
            }
            catch { return "房主"; }
        }

        internal static string GetRemoteDisplayName(CSteamID steamId)
        {
            AssertGameThread();
            if (steamId == CSteamID.Nil || !steamId.IsValid()) return "未知玩家";
            try
            {
                string name = _testRemotePersonaProvider != null ? _testRemotePersonaProvider(steamId) : SteamFriends.GetFriendPersonaName(steamId);
                return Normalize(name, "未知玩家");
            }
            catch { return "未知玩家"; }
        }

        internal static string FormatPlayer(CSteamID steamId)
        {
            return "玩家：" + GetRemoteDisplayName(steamId) + "（SteamID：" + steamId.m_SteamID + "）";
        }

        internal static string Normalize(string value, string fallback)
        {
            if (String.IsNullOrWhiteSpace(value)) return fallback;
            var sb = new StringBuilder(Math.Min(value.Length, 32));
            foreach (char c in value.Trim())
            {
                if (!Char.IsControl(c)) sb.Append(c);
                if (sb.Length == 32) break;
            }
            return sb.Length == 0 ? fallback : sb.ToString();
        }

        internal static int PersonaCacheCountForTest => _personaCache.Count;
        internal static void ClearPersonaCacheForTest() { _personaCache.Clear(); }
    }
}
