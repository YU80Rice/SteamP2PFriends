using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace SteamP2PFriends.Host
{
    /// <summary>
    /// v0.2.3.17 P0-E：vanilla listen server effective hash 计算限制修复。
    ///
    /// 背景：vanilla MasterBundleValidation.initialize 开头要求 Dedicator.IsDedicatedServer=true，
    /// 否则抛 NotSupportedException。listen server 模式下 serverHashes=null，服务端 serverHash=asset.hash
    /// （未组合 platform hash），与客机端 Hash.combine(asset.hash, omb.hash) 不匹配 -> verifyHash=False -> 踢出。
    ///
    /// 修复：反射调 vanilla private static MasterBundleValidation.loadHashForBundle(MasterBundleConfig)，
    /// 遍历 Assets.allMasterBundles 填充 serverHashes 字段。填充后 vanilla 服务端自动走
    /// Hash.combine(asset.hash, platformHash) 路径，与客机端对齐。
    ///
    /// vanilla 源码依据（U3-SDK）：
    ///   - Provider/MasterBundleValidation.cs:77-80 IsDedicatedServer 守卫
    ///   - Provider/MasterBundleValidation.cs:103-164 loadHashForBundle 实现
    ///   - Bundles/Assets.cs:289 allMasterBundles 字段（private static List<MasterBundleConfig>）
    ///   - NetMessaging/ServerMessageHandler_ValidateAssets.cs:124-135 服务端 hash 计算
    ///   - Bundles/ClientAssetIntegrity.cs:173-176 客机端 hash 计算
    ///
    /// 严禁清单：
    ///   - 不修改 vanilla hash 计算逻辑（仅填充字段，不 patch 任何 vanilla 方法）
    ///   - 不伪造 hash 值（直接复用 vanilla loadHashForBundle 的返回值）
    ///   - 不跳过 DoesAnyHashMatch validity check（复刻 vanilla initialize 的检查）
    ///   - 不 patch SendKickForHashMismatch（不阻止踢出）
    /// </summary>
    internal static class MasterBundleHashInitializer
    {
        // ===== 反射缓存 =====
        private static Type _masterBundleValidationType;
        private static MethodInfo _loadHashForBundleMethod;
        private static FieldInfo _allMasterBundlesField;
        private static FieldInfo _serverHashesField;
        private static FieldInfo _doesHashFileExistField;
        private static FieldInfo _masterBundleHashWindowsField;
        private static FieldInfo _masterBundleHashMacField;
        private static FieldInfo _masterBundleHashLinuxField;
        private static MethodInfo _doesAnyHashMatchMethod;

        private static bool _reflectionResolved;
        private static bool _reflectionFailed;

        /// <summary>
        /// 反射初始化（仅一次，失败不重试）。
        /// </summary>
        public static bool TryResolveReflection()
        {
            if (_reflectionResolved) return !_reflectionFailed;
            _reflectionResolved = true;

            try
            {
                _masterBundleValidationType = typeof(Assets).Assembly.GetType("SDG.Unturned.MasterBundleValidation");
                if (_masterBundleValidationType == null)
                {
                    RoleLogger.Error("[Host]", "[MasterBundleHashInit] !!! MasterBundleValidation 类型未找到");
                    _reflectionFailed = true;
                    return false;
                }

                _loadHashForBundleMethod = AccessTools.Method(
                    _masterBundleValidationType,
                    "loadHashForBundle",
                    new Type[] { typeof(MasterBundleConfig) });
                if (_loadHashForBundleMethod == null)
                {
                    RoleLogger.Error("[Host]", "[MasterBundleHashInit] !!! loadHashForBundle 方法未找到");
                    _reflectionFailed = true;
                    return false;
                }

                _allMasterBundlesField = typeof(Assets).GetField(
                    "allMasterBundles",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (_allMasterBundlesField == null)
                {
                    RoleLogger.Error("[Host]", "[MasterBundleHashInit] !!! Assets.allMasterBundles 字段未找到");
                    _reflectionFailed = true;
                    return false;
                }

                _serverHashesField = typeof(MasterBundleConfig).GetField(
                    "serverHashes",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (_serverHashesField == null)
                {
                    RoleLogger.Error("[Host]", "[MasterBundleHashInit] !!! MasterBundleConfig.serverHashes 字段未找到");
                    _reflectionFailed = true;
                    return false;
                }

                _doesHashFileExistField = typeof(MasterBundleConfig).GetField(
                    "doesHashFileExist",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (_doesHashFileExistField == null)
                {
                    RoleLogger.Error("[Host]", "[MasterBundleHashInit] !!! MasterBundleConfig.doesHashFileExist 字段未找到");
                    _reflectionFailed = true;
                    return false;
                }

                Type masterBundleHashType = typeof(Assets).Assembly.GetType("SDG.Unturned.MasterBundleHash");
                if (masterBundleHashType == null)
                {
                    RoleLogger.Error("[Host]", "[MasterBundleHashInit] !!! MasterBundleHash 类型未找到");
                    _reflectionFailed = true;
                    return false;
                }
                _masterBundleHashWindowsField = masterBundleHashType.GetField("windowsHash");
                _masterBundleHashMacField = masterBundleHashType.GetField("macHash");
                _masterBundleHashLinuxField = masterBundleHashType.GetField("linuxHash");
                if (_masterBundleHashWindowsField == null || _masterBundleHashMacField == null || _masterBundleHashLinuxField == null)
                {
                    RoleLogger.Error("[Host]", "[MasterBundleHashInit] !!! MasterBundleHash.windowsHash/macHash/linuxHash 字段未找到");
                    _reflectionFailed = true;
                    return false;
                }

                _doesAnyHashMatchMethod = AccessTools.Method(
                    masterBundleHashType,
                    "DoesAnyHashMatch",
                    new Type[] { typeof(byte[]) });
                if (_doesAnyHashMatchMethod == null)
                {
                    RoleLogger.Warn("[Host]", "[MasterBundleHashInit] !! DoesAnyHashMatch 方法未找到（将跳过 validity check）");
                }

                RoleLogger.Info("[Host]", "[MasterBundleHashInit] OK 反射缓存完成");
                return true;
            }
            catch (Exception ex)
            {
                RoleLogger.Error("[Host]", $"[MasterBundleHashInit] !!! 反射初始化异常: {ex}");
                _reflectionFailed = true;
                return false;
            }
        }

        /// <summary>
        /// 遍历 Assets.allMasterBundles，对每个 doesHashFileExist=true 的 bundle：
        ///   1. 调 vanilla loadHashForBundle(bundle) 获取 MasterBundleHash
        ///   2. （可选）调 vanilla DoesAnyHashMatch(bundle.hash) validity check
        ///   3. 赋值 bundle.serverHashes = container
        /// </summary>
        /// <returns>成功填充的 bundle 数量；-1 表示反射失败或异常</returns>
        public static int PopulateServerHashes()
        {
            if (!TryResolveReflection()) return -1;

            try
            {
                var allMasterBundles = _allMasterBundlesField.GetValue(null) as List<MasterBundleConfig>;
                if (allMasterBundles == null)
                {
                    RoleLogger.Error("[Host]", "[MasterBundleHashInit] !!! allMasterBundles 为 null（Assets 可能未完成加载）");
                    return -1;
                }

                int total = allMasterBundles.Count;
                int populated = 0;
                int skippedNoHashFile = 0;
                int skippedValidityFail = 0;
                int failed = 0;

                RoleLogger.Info("[Host]",
                    $"[MasterBundleHashInit] === 开始填充 serverHashes total={total} ===");

                foreach (var bundle in allMasterBundles)
                {
                    if (bundle == null) { failed++; continue; }

                    string bundleName = bundle.assetBundleNameWithoutExtension;
                    byte[] bundleHash = ReadBundleHash(bundle);

                    try
                    {
                        bool doesHashFileExist = (bool)_doesHashFileExistField.GetValue(bundle);
                        if (!doesHashFileExist)
                        {
                            RoleLogger.Info("[Host]",
                                $"[MasterBundleHashInit] 跳过（无 .hash 文件）bundle=\"{bundleName}\"");
                            skippedNoHashFile++;
                            continue;
                        }

                        object serverHashesObj = _loadHashForBundleMethod.Invoke(null, new object[] { bundle });
                        if (serverHashesObj == null)
                        {
                            RoleLogger.Warn("[Host]",
                                $"[MasterBundleHashInit] loadHashForBundle 返回 null bundle=\"{bundleName}\"");
                            failed++;
                            continue;
                        }

                        if (_doesAnyHashMatchMethod != null && bundleHash != null)
                        {
                            bool isValid = (bool)_doesAnyHashMatchMethod.Invoke(serverHashesObj, new object[] { bundleHash });
                            if (!isValid)
                            {
                                RoleLogger.Warn("[Host]",
                                    $"[MasterBundleHashInit] validity check 失败（hash 文件与 bundle.hash 不匹配）bundle=\"{bundleName}\" " +
                                    $"bundleHash={HashToString(bundleHash)} - 跳过填充（与 vanilla initialize 行为一致）");
                                skippedValidityFail++;
                                continue;
                            }
                        }

                        _serverHashesField.SetValue(bundle, serverHashesObj);
                        populated++;

                        byte[] winHash = (byte[])_masterBundleHashWindowsField.GetValue(serverHashesObj);
                        byte[] macHash = (byte[])_masterBundleHashMacField.GetValue(serverHashesObj);
                        byte[] linuxHash = (byte[])_masterBundleHashLinuxField.GetValue(serverHashesObj);

                        RoleLogger.Info("[Host]",
                            $"[MasterBundleHashInit] OK bundle=\"{bundleName}\" " +
                            $"bundleHash={HashToString(bundleHash)} " +
                            $"win={HashToString(winHash)} mac={HashToString(macHash)} linux={HashToString(linuxHash)}");
                    }
                    catch (Exception ex)
                    {
                        RoleLogger.Error("[Host]",
                            $"[MasterBundleHashInit] !!! bundle=\"{bundleName}\" 异常: {ex.Message}");
                        failed++;
                    }
                }

                RoleLogger.Info("[Host]",
                    $"[MasterBundleHashInit] === 完成 total={total} populated={populated} " +
                    $"skippedNoHashFile={skippedNoHashFile} skippedValidityFail={skippedValidityFail} failed={failed} ===");

                return populated;
            }
            catch (Exception ex)
            {
                RoleLogger.Error("[Host]", $"[MasterBundleHashInit] !!! PopulateServerHashes 异常: {ex}");
                return -1;
            }
        }

        private static byte[] ReadBundleHash(MasterBundleConfig bundle)
        {
            try
            {
                PropertyInfo prop = typeof(MasterBundleConfig).GetProperty("hash",
                    BindingFlags.Public | BindingFlags.Instance);
                if (prop != null) return (byte[])prop.GetValue(bundle, null);
                FieldInfo field = typeof(MasterBundleConfig).GetField("hash",
                    BindingFlags.Public | BindingFlags.Instance);
                if (field != null) return (byte[])field.GetValue(bundle);
            }
            catch { }
            return null;
        }

        private static string HashToString(byte[] hash)
        {
            if (hash == null) return "null";
            if (hash.Length == 0) return "empty";
            StringBuilder sb = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("X2"));
            return sb.ToString();
        }
    }
}
