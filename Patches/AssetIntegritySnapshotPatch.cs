using System;
using System.Reflection;
using System.Text;
using HarmonyLib;
using SDG.NetPak;
using SDG.NetTransport;
using SDG.Unturned;
using SteamP2PFriends.Shared;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    ///
    /// 触发背景：第九次-2/3 双机测试客机均被 vanilla 资产 hash 校验踢出（CUSTOM(57)）。
    /// 客机端只能看到 serverHash（serverEffective），无法定位两端 effective hash 差异的具体环节。
    /// 本 patch 在两端同时输出完整 hash 计算快照，供外部审计定位根因。
    ///
    /// 双端诊断设计（外部审计员要求）：
    ///   - Server-side: hook ClientStaticMethod&lt;Guid,string,string,byte[],string,string&gt;.Invoke 的 Prefix
    ///     检查 __instance 是否为 Assets.SendKickForHashMismatch（ReferenceEquals）
    ///     输出: clientHash（反射 ServerMessageHandler_ValidateAssets.clientHash 静态字段）
    ///           + asset.hash + originMasterBundle.serverHashes.{windows,linux,mac}Hash
    ///           + serverEffective（Invoke arg4）+ Hash.verifyHash(clientHash, serverEffective) 结果
    ///   - Client-side: hook Assets.ReceiveKickForHashMismatch 的 Prefix
    ///     输出: asset.hash + originMasterBundle.hash + originMasterBundle.doesHashFileExist
    ///           + clientEffective（Hash.combine(asset.hash, originMasterBundle.hash) 或 asset.hash）
    ///           + serverHash（参数）+ Hash.verifyHash(clientEffective, serverHash) 结果
    ///
    ///   - 移除 [HarmonyPatch] attribute（PatchAll 在泛型类 ClientStaticMethod&lt;,...&gt;.Invoke 上静默失败）
    ///   - Prefix 改为 public static，由 Plugin.RegisterAssetIntegritySnapshotPatches() 手动登记
    ///   - 与 14 个 SteamGameServerNetworkingSockets wrapper patch 一致
    ///
    ///   - ServerInvokePrefix 参数名从语义化（guid/serverName/...）改回 vanilla arg1..arg6
    ///   - 根因：Harmony 按参数名注入，参数名不匹配时 _harmony.Patch 抛 ArgumentException
    ///     导致 Server-side 登记 FAIL（GetPatchedMethods 不包含）
    ///   - vanilla 源码（U3-SDK ClientStaticMethod.cs:713）：
    ///     public void Invoke(ENetReliability reliability, ITransportConnection transportConnection,
    ///         T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6)
    ///   - 修复方案：Prefix 参数改用 arg1..arg6，方法内部用语义化局部变量别名保持可读性
    ///
    /// 严禁清单（外部审计员共同要求）：
    ///   - ❌ 返回 false（会阻止 vanilla 踢出行为）
    ///   - ❌ 修改 __result 或任何参数
    ///   - ❌ 调用 Provider.RequestDisconnect / Provider.disconnect
    ///   - ❌ 白名单 GUID
    ///   - ❌ 修改 asset.hash 或 originMasterBundle.hash / serverHashes
    ///   - ❌ 强制 ShouldVerifyHash=false
    /// </summary>
    internal static class AssetIntegritySnapshotPatch
    {
        // ===== 反射缓存 =====

        private static FieldInfo _sendKickForHashMismatchField;
        private static object _sendKickForHashMismatchInstance;
        private static bool _sendKickForHashMismatchResolved;

        private static FieldInfo _validateAssetsClientHashField;
        private static bool _validateAssetsClientHashResolved;

        private static FieldInfo _originMasterBundleServerHashesField;
        private static FieldInfo _originMasterBundleDoesHashFileExistField;
        private static bool _originMasterBundleFieldsResolved;

        private static MethodInfo _hashCombineMethod;
        private static MethodInfo _hashVerifyHashMethod;
        private static bool _hashMethodsResolved;

        /// <summary>
        /// 由 Plugin.Awake 调用，触发运行时解析（不阻断加载，失败仅记录警告）。
        /// </summary>
        internal static void RuntimeProbe()
        {
            try
            {
                ResolveSendKickForHashMismatchInstance();
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Shared]", $"[AssetIntegritySnapshot] RuntimeProbe(SendKick) 失败: {ex.Message}");
            }

            try
            {
                ResolveValidateAssetsClientHashField();
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Shared]", $"[AssetIntegritySnapshot] RuntimeProbe(clientHash) 失败: {ex.Message}");
            }

            try
            {
                ResolveOriginMasterBundleFields();
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Shared]", $"[AssetIntegritySnapshot] RuntimeProbe(OriginMasterBundle) 失败: {ex.Message}");
            }

            try
            {
                ResolveHashMethods();
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Shared]", $"[AssetIntegritySnapshot] RuntimeProbe(Hash) 失败: {ex.Message}");
            }
        }

        // ===== Server-side Prefix: ClientStaticMethod<Guid,string,string,byte[],string,string>.Invoke =====

        /// <summary>
        /// 服务端 Prefix：拦截 Assets.SendKickForHashMismatch.Invoke 调用，输出完整 hash 计算快照。
        /// 注意：此 Prefix 会 patch 所有 ClientStaticMethod&lt;Guid,string,string,byte[],string,string&gt;.Invoke 调用，
        /// 但只有 __instance == Assets.SendKickForHashMismatch 时才输出诊断。
        ///
        /// 由 Plugin.RegisterAssetIntegritySnapshotPatches() 手动登记。
        ///
        /// Harmony 按参数名注入，参数名不匹配会导致 _harmony.Patch 抛 ArgumentException，
        /// 进而导致 Server-side 登记 FAIL（GetPatchedMethods 不包含）。
        /// vanilla 源码（U3-SDK ClientStaticMethod.cs:713）：
        ///   public void Invoke(ENetReliability reliability, ITransportConnection transportConnection,
        ///       T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6)
        /// 修复方案：Prefix 参数改用 arg1..arg6，方法内部用语义化局部变量别名。
        /// </summary>
        [HarmonyPrefix]
        public static void ServerInvokePrefix(
            object __instance,
            ENetReliability reliability,
            ITransportConnection transportConnection,
            Guid arg1,
            string arg2,
            string arg3,
            byte[] arg4,
            string arg5,
            string arg6)
        {
            Guid guid = arg1;
            string serverName = arg2;
            string serverFriendlyName = arg3;
            byte[] serverHash = arg4;
            string serverAssetBundleNameWithoutExtension = arg5;
            string serverAssetOrigin = arg6;

            try
            {
                if (!IsSendKickForHashMismatchInstance(__instance))
                {
                    return; // 不是 SendKickForHashMismatch.Invoke，跳过
                }

                RoleLogger.Info("[Host]",
                    $"[AssetIntegritySnapshot/Server] >>> SendKickForHashMismatch.Invoke 拦截 <<<");
                RoleLogger.Info("[Host]",
                    $"[AssetIntegritySnapshot/Server] guid={guid:N} reliability={reliability} transport={transportConnection?.GetType().Name}");
                RoleLogger.Info("[Host]",
                    $"[AssetIntegritySnapshot/Server] serverName=\"{serverName}\" serverFriendlyName=\"{serverFriendlyName}\"");
                RoleLogger.Info("[Host]",
                    $"[AssetIntegritySnapshot/Server] serverAssetBundleNameWithoutExtension=\"{serverAssetBundleNameWithoutExtension}\" serverAssetOrigin=\"{serverAssetOrigin}\"");

                // 输出 serverHash（serverEffective = Hash.combine(asset.hash, platformHash) 或 asset.hash）
                RoleLogger.Info("[Host]",
                    $"[AssetIntegritySnapshot/Server] serverHash(arg4, 即 serverEffective)={HashToString(serverHash)}");

                // 反射读取 ServerMessageHandler_ValidateAssets.clientHash 静态字段
                byte[] clientHash = TryReadValidateAssetsClientHash();
                RoleLogger.Info("[Host]",
                    $"[AssetIntegritySnapshot/Server] clientHash(反射 ServerMessageHandler_ValidateAssets.clientHash)={HashToString(clientHash)}");

                // Assets.find(guid) 获取 asset
                Asset asset = Assets.find(guid);
                if (asset == null)
                {
                    RoleLogger.Warn("[Host]",
                        $"[AssetIntegritySnapshot/Server] Assets.find({guid:N}) returned null");
                }
                else
                {
                    RoleLogger.Info("[Host]",
                        $"[AssetIntegritySnapshot/Server] server asset.name=\"{asset.name}\" FriendlyName=\"{asset.FriendlyName}\" type={asset.GetType().Name}");
                    RoleLogger.Info("[Host]",
                        $"[AssetIntegritySnapshot/Server] server asset.hash={HashToString(asset.hash)}");
                    RoleLogger.Info("[Host]",
                        $"[AssetIntegritySnapshot/Server] server asset.ShouldVerifyHash={ReadShouldVerifyHash(asset)}");

                    // 反射读取 originMasterBundle 信息
                    DumpOriginMasterBundleServerSide(asset);
                }

                // 计算 Hash.verifyHash(clientHash, serverHash)
                bool? verifyResult = TryHashVerifyHash(clientHash, serverHash);
                RoleLogger.Info("[Host]",
                    $"[AssetIntegritySnapshot/Server] Hash.verifyHash(clientHash, serverHash)={VerifyResultToString(verifyResult)} " +
                    $"(if false, 这就是 vanilla 踢出的直接原因)");

                RoleLogger.Info("[Host]",
                    $"[AssetIntegritySnapshot/Server] <<< 快照结束 >>>");
            }
            catch (Exception ex)
            {
                RoleLogger.Error("[Host]", $"[AssetIntegritySnapshot/Server] Prefix 异常（不阻断 vanilla 行为）: {ex}");
            }
        }

        // ===== Client-side Prefix: Assets.ReceiveKickForHashMismatch =====

        /// <summary>
        /// 客机端 Prefix：拦截 vanilla ReceiveKickForHashMismatch，输出完整 hash 计算快照。
        /// 此时客机已收到 serverHash 参数（serverEffective），可以与本地 clientEffective 对比。
        ///
        /// 由 Plugin.RegisterAssetIntegritySnapshotPatches() 手动登记。
        /// </summary>
        [HarmonyPrefix]
        public static void ClientReceiveKickPrefix(
            Guid guid,
            string serverName,
            string serverFriendlyName,
            byte[] serverHash,
            string serverAssetBundleNameWithoutExtension,
            string serverAssetOrigin)
        {
            try
            {
                RoleLogger.Info("[Client]",
                    $"[AssetIntegritySnapshot/Client] >>> ReceiveKickForHashMismatch 拦截 <<<");
                RoleLogger.Info("[Client]",
                    $"[AssetIntegritySnapshot/Client] guid={guid:N}");
                RoleLogger.Info("[Client]",
                    $"[AssetIntegritySnapshot/Client] serverName=\"{serverName}\" serverFriendlyName=\"{serverFriendlyName}\"");
                RoleLogger.Info("[Client]",
                    $"[AssetIntegritySnapshot/Client] serverAssetBundleNameWithoutExtension=\"{serverAssetBundleNameWithoutExtension}\" serverAssetOrigin=\"{serverAssetOrigin}\"");
                RoleLogger.Info("[Client]",
                    $"[AssetIntegritySnapshot/Client] serverHash(arg, 即 serverEffective)={HashToString(serverHash)}");

                Asset asset = Assets.find(guid);
                if (asset == null)
                {
                    RoleLogger.Warn("[Client]",
                        $"[AssetIntegritySnapshot/Client] Assets.find({guid:N}) returned null");
                }
                else
                {
                    RoleLogger.Info("[Client]",
                        $"[AssetIntegritySnapshot/Client] client asset.name=\"{asset.name}\" FriendlyName=\"{asset.FriendlyName}\" type={asset.GetType().Name}");
                    RoleLogger.Info("[Client]",
                        $"[AssetIntegritySnapshot/Client] client asset.hash={HashToString(asset.hash)}");
                    RoleLogger.Info("[Client]",
                        $"[AssetIntegritySnapshot/Client] client asset.ShouldVerifyHash={ReadShouldVerifyHash(asset)}");

                    // 名称对比
                    bool nameMatches = string.Equals(asset.name, serverName, StringComparison.Ordinal);
                    bool friendlyMatches = string.Equals(asset.FriendlyName, serverFriendlyName, StringComparison.Ordinal);
                    RoleLogger.Info("[Client]",
                        $"[AssetIntegritySnapshot/Client] nameMatches={nameMatches} friendlyNameMatches={friendlyMatches} " +
                        $"(两者都 true 时 vanilla 走 'disagree on asset configuration' 分支，错误码 CUSTOM(57))");

                    // 反射读取 originMasterBundle 信息
                    DumpOriginMasterBundleClientSide(asset);

                    // 计算客机端 effective hash
                    byte[] clientEffective = ComputeClientEffectiveHash(asset);
                    RoleLogger.Info("[Client]",
                        $"[AssetIntegritySnapshot/Client] clientEffective={HashToString(clientEffective)}");

                    // 计算 Hash.verifyHash(clientEffective, serverHash)
                    bool? verifyResult = TryHashVerifyHash(clientEffective, serverHash);
                    RoleLogger.Info("[Client]",
                        $"[AssetIntegritySnapshot/Client] Hash.verifyHash(clientEffective, serverHash)={VerifyResultToString(verifyResult)} " +
                        $"(if false, 两端 effective hash 不同 -> 这就是被踢出的根因)");

                    // 额外诊断：asset.hash vs serverHash（vanilla 第 2704 行的 "server asset bundle hash out of date" 分支）
                    bool? assetHashVsServerHash = TryHashVerifyHash(asset.hash, serverHash);
                    RoleLogger.Info("[Client]",
                        $"[AssetIntegritySnapshot/Client] Hash.verifyHash(asset.hash, serverHash)={VerifyResultToString(assetHashVsServerHash)} " +
                        $"(if true, vanilla 会在 2704 行输出 'Server asset bundle hash out of date'，shouldVerifyGameFiles=false)");
                }

                RoleLogger.Info("[Client]",
                    $"[AssetIntegritySnapshot/Client] <<< 快照结束 >>>");
            }
            catch (Exception ex)
            {
                RoleLogger.Error("[Client]", $"[AssetIntegritySnapshot/Client] Prefix 异常（不阻断 vanilla 行为）: {ex}");
            }
        }

        // ===== 反射辅助方法 =====

        private static bool IsSendKickForHashMismatchInstance(object instance)
        {
            if (instance == null) return false;
            if (!_sendKickForHashMismatchResolved)
            {
                ResolveSendKickForHashMismatchInstance();
            }
            if (_sendKickForHashMismatchInstance == null) return false;
            return ReferenceEquals(instance, _sendKickForHashMismatchInstance);
        }

        private static void ResolveSendKickForHashMismatchInstance()
        {
            _sendKickForHashMismatchResolved = true;
            try
            {
                _sendKickForHashMismatchField = typeof(Assets).GetField(
                    "SendKickForHashMismatch",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                if (_sendKickForHashMismatchField == null)
                {
                    RoleLogger.Warn("[Shared]",
                        "[AssetIntegritySnapshot] Assets.SendKickForHashMismatch 字段未找到（可能 vanilla 版本变化）");
                    return;
                }
                _sendKickForHashMismatchInstance = _sendKickForHashMismatchField.GetValue(null);
                RoleLogger.Info("[Shared]",
                    $"[AssetIntegritySnapshot] SendKickForHashMismatch 实例解析成功 type={_sendKickForHashMismatchInstance?.GetType().Name}");
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Shared]",
                    $"[AssetIntegritySnapshot] ResolveSendKickForHashMismatchInstance 异常: {ex.Message}");
            }
        }

        private static byte[] TryReadValidateAssetsClientHash()
        {
            if (!_validateAssetsClientHashResolved)
            {
                ResolveValidateAssetsClientHashField();
            }
            if (_validateAssetsClientHashField == null) return null;
            try
            {
                return _validateAssetsClientHashField.GetValue(null) as byte[];
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]",
                    $"[AssetIntegritySnapshot] 读取 ServerMessageHandler_ValidateAssets.clientHash 异常: {ex.Message}");
                return null;
            }
        }

        private static void ResolveValidateAssetsClientHashField()
        {
            _validateAssetsClientHashResolved = true;
            try
            {
                Type validateAssetsType = typeof(Assets).Assembly.GetType("SDG.Unturned.ServerMessageHandler_ValidateAssets");
                if (validateAssetsType == null)
                {
                    RoleLogger.Warn("[Shared]",
                        "[AssetIntegritySnapshot] ServerMessageHandler_ValidateAssets 类型未找到");
                    return;
                }
                _validateAssetsClientHashField = validateAssetsType.GetField(
                    "clientHash",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (_validateAssetsClientHashField == null)
                {
                    RoleLogger.Warn("[Shared]",
                        "[AssetIntegritySnapshot] ServerMessageHandler_ValidateAssets.clientHash 字段未找到");
                }
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Shared]",
                    $"[AssetIntegritySnapshot] ResolveValidateAssetsClientHashField 异常: {ex.Message}");
            }
        }

        private static void ResolveOriginMasterBundleFields()
        {
            _originMasterBundleFieldsResolved = true;
            try
            {
                // originMasterBundle 类型是 MasterBundleConfig（在 SDG.Unturned namespace，public class）
                // 通过 asset.originMasterBundle 获取实例后反射其字段
                Type assetBundleRefType = typeof(Assets).Assembly.GetType("SDG.Unturned.MasterBundleConfig");
                if (assetBundleRefType == null)
                {
                    RoleLogger.Warn("[Shared]",
                        "[AssetIntegritySnapshot] MasterBundleConfig 类型未找到");
                    return;
                }
                _originMasterBundleServerHashesField = assetBundleRefType.GetField(
                    "serverHashes",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                _originMasterBundleDoesHashFileExistField = assetBundleRefType.GetField(
                    "doesHashFileExist",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (_originMasterBundleServerHashesField == null)
                {
                    RoleLogger.Warn("[Shared]",
                        "[AssetIntegritySnapshot] MasterBundleConfig.serverHashes 字段未找到");
                }
                if (_originMasterBundleDoesHashFileExistField == null)
                {
                    RoleLogger.Warn("[Shared]",
                        "[AssetIntegritySnapshot] MasterBundleConfig.doesHashFileExist 字段未找到");
                }
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Shared]",
                    $"[AssetIntegritySnapshot] ResolveOriginMasterBundleFields 异常: {ex.Message}");
            }
        }

        private static void ResolveHashMethods()
        {
            _hashMethodsResolved = true;
            try
            {
                Type hashType = typeof(Hash);
                // Hash.combine 有两个重载：combine(params byte[][]) 和 combine(List<byte[]>)
                // 指定 params byte[][] 版本
                _hashCombineMethod = AccessTools.Method(hashType, "combine", new Type[] { typeof(byte[][]) });
                _hashVerifyHashMethod = AccessTools.Method(hashType, "verifyHash", new Type[] { typeof(byte[]), typeof(byte[]) });
                if (_hashCombineMethod == null)
                {
                    RoleLogger.Warn("[Shared]", "[AssetIntegritySnapshot] Hash.combine(params byte[][]) 方法未找到");
                }
                if (_hashVerifyHashMethod == null)
                {
                    RoleLogger.Warn("[Shared]", "[AssetIntegritySnapshot] Hash.verifyHash(byte[], byte[]) 方法未找到");
                }
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Shared]",
                    $"[AssetIntegritySnapshot] ResolveHashMethods 异常: {ex.Message}");
            }
        }

        private static void DumpOriginMasterBundleServerSide(Asset asset)
        {
            try
            {
                object originMasterBundle = ReadOriginMasterBundle(asset);
                if (originMasterBundle == null)
                {
                    RoleLogger.Info("[Host]", "[AssetIntegritySnapshot/Server] originMasterBundle=null");
                    return;
                }

                RoleLogger.Info("[Host]",
                    $"[AssetIntegritySnapshot/Server] originMasterBundle.type={originMasterBundle.GetType().Name}");

                // 读取 originMasterBundle.hash（public byte[]）
                byte[] ombHash = ReadPublicPropertyOrField<byte[]>(originMasterBundle, "hash");
                RoleLogger.Info("[Host]",
                    $"[AssetIntegritySnapshot/Server] originMasterBundle.hash={HashToString(ombHash)}");

                // 读取 originMasterBundle.serverHashes（internal MasterBundleHash）
                object serverHashes = ReadServerHashes(originMasterBundle);
                if (serverHashes == null)
                {
                    RoleLogger.Info("[Host]", "[AssetIntegritySnapshot/Server] originMasterBundle.serverHashes=null");
                }
                else
                {
                    byte[] winHash = ReadPublicField<byte[]>(serverHashes, "windowsHash");
                    byte[] macHash = ReadPublicField<byte[]>(serverHashes, "macHash");
                    byte[] linuxHash = ReadPublicField<byte[]>(serverHashes, "linuxHash");
                    RoleLogger.Info("[Host]",
                        $"[AssetIntegritySnapshot/Server] serverHashes.windowsHash={HashToString(winHash)}");
                    RoleLogger.Info("[Host]",
                        $"[AssetIntegritySnapshot/Server] serverHashes.macHash={HashToString(macHash)}");
                    RoleLogger.Info("[Host]",
                        $"[AssetIntegritySnapshot/Server] serverHashes.linuxHash={HashToString(linuxHash)}");
                }
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]",
                    $"[AssetIntegritySnapshot/Server] DumpOriginMasterBundle 异常: {ex.Message}");
            }
        }

        private static void DumpOriginMasterBundleClientSide(Asset asset)
        {
            try
            {
                object originMasterBundle = ReadOriginMasterBundle(asset);
                if (originMasterBundle == null)
                {
                    RoleLogger.Info("[Client]", "[AssetIntegritySnapshot/Client] originMasterBundle=null");
                    return;
                }

                RoleLogger.Info("[Client]",
                    $"[AssetIntegritySnapshot/Client] originMasterBundle.type={originMasterBundle.GetType().Name}");

                // 读取 originMasterBundle.hash（public byte[]）
                byte[] ombHash = ReadPublicPropertyOrField<byte[]>(originMasterBundle, "hash");
                RoleLogger.Info("[Client]",
                    $"[AssetIntegritySnapshot/Client] originMasterBundle.hash={HashToString(ombHash)}");

                // 读取 originMasterBundle.doesHashFileExist（internal bool）
                bool? doesHashFileExist = ReadDoesHashFileExist(originMasterBundle);
                RoleLogger.Info("[Client]",
                    $"[AssetIntegritySnapshot/Client] originMasterBundle.doesHashFileExist={doesHashFileExist?.ToString() ?? "null(反射失败)"}");

                // 读取 originMasterBundle.assetBundleNameWithoutExtension（public string）
                string bundleName = ReadPublicPropertyOrField<string>(originMasterBundle, "assetBundleNameWithoutExtension");
                RoleLogger.Info("[Client]",
                    $"[AssetIntegritySnapshot/Client] originMasterBundle.assetBundleNameWithoutExtension=\"{bundleName}\"");

                // 读取 originMasterBundle.serverHashes（客机端通常为 null，因为 MasterBundleValidation.initialize 仅在 dedicated server 调用）
                object serverHashes = ReadServerHashes(originMasterBundle);
                RoleLogger.Info("[Client]",
                    $"[AssetIntegritySnapshot/Client] originMasterBundle.serverHashes={serverHashes?.GetType().Name ?? "null"} " +
                    $"(客机端预期为 null，因为 MasterBundleValidation.initialize 仅 dedicated server 调用)");
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Client]",
                    $"[AssetIntegritySnapshot/Client] DumpOriginMasterBundle 异常: {ex.Message}");
            }
        }

        private static MasterBundleConfig ReadOriginMasterBundle(Asset asset)
        {
            try
            {
                // asset.originMasterBundle 是 public MasterBundleConfig { get; internal set; }，可直接访问
                return asset.originMasterBundle;
            }
            catch
            {
                return null;
            }
        }

        private static object ReadServerHashes(object originMasterBundle)
        {
            if (!_originMasterBundleFieldsResolved)
            {
                ResolveOriginMasterBundleFields();
            }
            if (_originMasterBundleServerHashesField == null) return null;
            try
            {
                return _originMasterBundleServerHashesField.GetValue(originMasterBundle);
            }
            catch { return null; }
        }

        private static bool? ReadDoesHashFileExist(object originMasterBundle)
        {
            if (!_originMasterBundleFieldsResolved)
            {
                ResolveOriginMasterBundleFields();
            }
            if (_originMasterBundleDoesHashFileExistField == null) return null;
            try
            {
                return (bool)_originMasterBundleDoesHashFileExistField.GetValue(originMasterBundle);
            }
            catch { return null; }
        }

        private static byte[] ComputeClientEffectiveHash(Asset asset)
        {
            try
            {
                object originMasterBundle = ReadOriginMasterBundle(asset);
                if (originMasterBundle == null)
                {
                    return asset.hash;
                }

                byte[] ombHash = ReadPublicPropertyOrField<byte[]>(originMasterBundle, "hash");
                bool? doesHashFileExist = ReadDoesHashFileExist(originMasterBundle);

                // vanilla 客机端逻辑（Assets.cs:2706-2709 注释）：
                // asset.hash is only combined with assetbundle hash if client detects the multiplatform "*.hash" file
                // used by the server, so if the hash used by the server is equal to asset.hash (not the same as hash
                // sent by client) that means the "*.hash" file was not used on the server
                if (ombHash != null && doesHashFileExist == true)
                {
                    if (!_hashMethodsResolved) ResolveHashMethods();
                    if (_hashCombineMethod != null)
                    {
                        // Hash.combine(params byte[][] hashes) - 需要包装成 byte[][]
                        byte[][] hashesArray = new byte[][] { asset.hash, ombHash };
                        return _hashCombineMethod.Invoke(null, new object[] { hashesArray }) as byte[];
                    }
                }
                return asset.hash;
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Client]",
                    $"[AssetIntegritySnapshot/Client] ComputeClientEffectiveHash 异常: {ex.Message}");
                return asset.hash;
            }
        }

        private static bool? TryHashVerifyHash(byte[] a, byte[] b)
        {
            if (!_hashMethodsResolved) ResolveHashMethods();
            if (_hashVerifyHashMethod == null) return null;
            if (a == null || b == null) return false;
            try
            {
                return (bool)_hashVerifyHashMethod.Invoke(null, new object[] { a, b });
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Shared]",
                    $"[AssetIntegritySnapshot] Hash.verifyHash 反射调用异常: {ex.Message}");
                return null;
            }
        }

        private static bool? ReadShouldVerifyHash(Asset asset)
        {
            try
            {
                PropertyInfo prop = typeof(Asset).GetProperty("ShouldVerifyHash",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                {
                    return (bool)prop.GetValue(asset, null);
                }
            }
            catch { }
            return null;
        }

        private static T ReadPublicPropertyOrField<T>(object obj, string name)
        {
            try
            {
                Type type = obj.GetType();
                PropertyInfo prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                {
                    return (T)prop.GetValue(obj, null);
                }
                FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (field != null)
                {
                    return (T)field.GetValue(obj);
                }
            }
            catch { }
            return default;
        }

        private static T ReadPublicField<T>(object obj, string name)
        {
            try
            {
                Type type = obj.GetType();
                FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (field != null)
                {
                    return (T)field.GetValue(obj);
                }
            }
            catch { }
            return default;
        }

        private static string HashToString(byte[] hash)
        {
            if (hash == null) return "null";
            if (hash.Length == 0) return "empty";
            StringBuilder sb = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
            {
                sb.Append(hash[i].ToString("X2"));
            }
            return sb.ToString();
        }

        private static string VerifyResultToString(bool? result)
        {
            return result?.ToString() ?? "null(反射失败)";
        }
    }
}
