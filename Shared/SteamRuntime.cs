using System;
using System.Reflection;
using HarmonyLib;
using SteamP2PFriends.Shared;

namespace SteamP2PFriends.Shared
{
    /// <summary>
    /// Steamworks.NET 运行时反射绑定（迁移自 LaunchP2PHostManager v2.11.0 SteamRuntime.cs）。
    ///
    /// Libs/ 目录中没有 Steamworks.NET DLL（它由游戏运行时由 BepInEx 加载链中
    /// Assembly-CSharp-firstpass.dll 或独立的 Steamworks.dll / steam_api64.dll 提供）。
    /// 因此本模组不直接在编译期引用 Steamworks.NET，而是在运行时通过反射调用
    /// SteamFriends.SetRichPresence / SteamGameServer.GetSteamID 等方法。
    /// </summary>
    internal static class SteamRuntime
    {
        private static Type _steamFriendsType;
        private static Type _steamGameServerType;
        private static Type _csteamIdType;
        private static Type _steamUserType;
        private static Type _gameServerType;
        private static bool _initialized;

        public static Type CSteamIDType => _csteamIdType;

        public static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;

            _steamFriendsType = AccessTools.TypeByName("Steamworks.SteamFriends");
            _steamGameServerType = AccessTools.TypeByName("Steamworks.SteamGameServer");
            _csteamIdType = AccessTools.TypeByName("Steamworks.CSteamID");
            _steamUserType = AccessTools.TypeByName("Steamworks.SteamUser");
            _gameServerType = AccessTools.TypeByName("Steamworks.GameServer");

            if (_steamFriendsType == null)
                RoleLogger.Warn("[Shared]", "Steamworks.SteamFriends 类型未找到，Steam 集成可能受限。");
            if (_steamGameServerType == null)
                RoleLogger.Warn("[Shared]", "Steamworks.SteamGameServer 类型未找到，server SteamID 取不到。");
            if (_csteamIdType == null)
                RoleLogger.Warn("[Shared]", "Steamworks.CSteamID 类型未找到。");
            if (_steamUserType == null)
                RoleLogger.Warn("[Shared]", "Steamworks.SteamUser 类型未找到，本地 SteamID 获取将不可用。");
            if (_gameServerType == null)
                RoleLogger.Warn("[Shared]", "Steamworks.GameServer 类型未找到，会话复用检测将不可用。");
        }

        // ---- SteamFriends ----

        public static bool SetRichPresence(string key, string value)
        {
            EnsureInitialized();
            if (_steamFriendsType == null) return false;
            try
            {
                MethodInfo mi = AccessTools.Method(_steamFriendsType, "SetRichPresence",
                    new[] { typeof(string), typeof(string) });
                if (mi == null) { RoleLogger.Warn("[Shared]", "SteamFriends.SetRichPresence 未找到"); return false; }
                return (bool)mi.Invoke(null, new object[] { key, value });
            }
            catch (Exception ex) { RoleLogger.Warn("[Shared]", $"SetRichPresence({key}) 失败: {ex.Message}"); return false; }
        }

        // ---- SteamGameServer ----

        public static object GetGameServerSteamID()
        {
            EnsureInitialized();
            if (_steamGameServerType == null) return null;
            try
            {
                MethodInfo mi = AccessTools.Method(_steamGameServerType, "GetSteamID", Type.EmptyTypes);
                if (mi == null) { RoleLogger.Warn("[Shared]", "SteamGameServer.GetSteamID 未找到"); return null; }
                return mi.Invoke(null, null);
            }
            catch (Exception ex) { RoleLogger.Warn("[Shared]", $"GetGameServerSteamID 失败: {ex.Message}"); return null; }
        }

        public static string GetGameServerSteamIDString()
        {
            object id = GetGameServerSteamID();
            if (id == null) return null;
            try
            {
                FieldInfo fi = AccessTools.Field(id.GetType(), "m_SteamID");
                if (fi != null && fi.FieldType == typeof(ulong))
                {
                    return ((ulong)fi.GetValue(id)).ToString();
                }
                return id.ToString();
            }
            catch (Exception ex) { RoleLogger.Warn("[Shared]", $"GetGameServerSteamIDString 失败: {ex.Message}"); return null; }
        }

        public static object GetLocalSteamID()
        {
            EnsureInitialized();
            if (_steamUserType == null) return null;
            try
            {
                MethodInfo mi = AccessTools.Method(_steamUserType, "GetSteamID", Type.EmptyTypes);
                if (mi == null) { RoleLogger.Warn("[Shared]", "SteamUser.GetSteamID 未找到"); return null; }
                return mi.Invoke(null, null);
            }
            catch (Exception ex) { RoleLogger.Warn("[Shared]", $"GetLocalSteamID 失败: {ex.Message}"); return null; }
        }

        public static object CreateCSteamID(ulong value)
        {
            EnsureInitialized();
            if (_csteamIdType == null) return null;
            try
            {
                ConstructorInfo ci = AccessTools.Constructor(_csteamIdType, new[] { typeof(ulong) });
                if (ci == null) { RoleLogger.Warn("[Shared]", "CSteamID(ulong) 构造函数未找到"); return null; }
                return ci.Invoke(new object[] { value });
            }
            catch (Exception ex) { RoleLogger.Warn("[Shared]", $"CreateCSteamID({value}) 失败: {ex.Message}"); return null; }
        }

        // ---- GameServer 会话复用探测 ----

        public static bool IsGameServerAlive()
        {
            return GetHSteamPipeValue() != 0;
        }

        public static int GetHSteamPipeValue()
        {
            EnsureInitialized();
            if (_gameServerType == null) return -1;
            try
            {
                MethodInfo mi = AccessTools.Method(_gameServerType, "GetHSteamPipe", Type.EmptyTypes);
                if (mi == null) { RoleLogger.Warn("[Shared]", "GameServer.GetHSteamPipe 未找到"); return -2; }

                object pipe = mi.Invoke(null, null);
                if (pipe == null) return 0;

                FieldInfo fi = AccessTools.Field(pipe.GetType(), "m_HSteamPipe");
                if (fi != null && fi.FieldType == typeof(int))
                    return (int)fi.GetValue(pipe);

                int result;
                return int.TryParse(pipe.ToString(), out result) ? result : -3;
            }
            catch (Exception ex) { RoleLogger.Warn("[Shared]", $"GetHSteamPipeValue 失败: {ex.Message}"); return -4; }
        }

        public static bool SetAdvertiseServerActive(bool active)
        {
            EnsureInitialized();
            if (_steamGameServerType == null) return false;
            try
            {
                MethodInfo mi = AccessTools.Method(_steamGameServerType, "SetAdvertiseServerActive", new[] { typeof(bool) });
                if (mi == null) { RoleLogger.Warn("[Shared]", "SteamGameServer.SetAdvertiseServerActive 未找到"); return false; }
                mi.Invoke(null, new object[] { active });
                return true;
            }
            catch (Exception ex) { RoleLogger.Warn("[Shared]", $"SetAdvertiseServerActive({active}) 失败: {ex.Message}"); return false; }
        }

        // ---- 全量清理 ----

        public static void ClearAllRichPresence()
        {
            SetRichPresence("connect", "");
            SetRichPresence("steam_player_group", "");
            SetRichPresence("steam_display", "");
            SetRichPresence("level_name", "");
        }
    }
}
