using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using Steamworks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace SteamP2PFriends.Host
{
    internal enum EStage6BWorkshopState
    {
        Empty,
        Cleared,
        Validated,
        Committed,
        Mapped,
        CleanupFaulted
    }

    internal sealed class Stage6BWorkshopRequirement
    {
        internal readonly ulong FileId;
        internal readonly uint Timestamp;
        internal readonly bool IsMapRoot;

        internal Stage6BWorkshopRequirement(ulong fileId, uint timestamp, bool isMapRoot)
        {
            FileId = fileId;
            Timestamp = timestamp;
            IsMapRoot = isMapRoot;
        }
    }

    internal static class Stage6BWorkshopSession
    {
        private static readonly List<Stage6BWorkshopRequirement> _requirements =
            new List<Stage6BWorkshopRequirement>();
        private static EStage6BWorkshopState _state = EStage6BWorkshopState.Empty;
        private static Guid _token = Guid.Empty;

        internal static EStage6BWorkshopState CurrentState { get { return _state; } }
        internal static bool HasNoRequirementPlan
        {
            get { return _requirements.Count == 0 && _token == Guid.Empty; }
        }
        internal static bool HasActiveP2PSession
        {
            get
            {
                return _token != Guid.Empty &&
                    (_state == EStage6BWorkshopState.Committed ||
                     _state == EStage6BWorkshopState.Mapped);
            }
        }

        internal static bool TryBuildValidatedPlan(LevelInfo selectedLevel, out string failure)
        {
            ThreadUtil.assertIsGameThread();
            failure = null;

            // This is deliberately a pure rejection. Recovery from CleanupFaulted is
            // allowed only through HostManager's P2P pre-build cleanup gateway.
            if (_state != EStage6BWorkshopState.Cleared)
            {
                failure = "Stage6B Build requires Cleared; actual=" + _state;
                return false;
            }

            try
            {
                if (selectedLevel == null || selectedLevel.configData == null)
                    return FailAfterStrictCleanup("Stage6B selected level/config is null", out failure);

                if (!Assets.hasLoadedMaps || !Assets.hasLoadedUgc || Assets.isLoading || Provider.isLoadingUGC)
                    return FailAfterStrictCleanup("Stage6B assets/workshop are not ready", out failure);

                PropertyInfo waitProperty = AccessTools.Property(typeof(Assets), "ShouldWaitForNewAssetsToFinishLoading");
                if (waitProperty == null || waitProperty.PropertyType != typeof(bool))
                    return FailAfterStrictCleanup("Stage6B readiness property is unavailable", out failure);
                if ((bool)waitProperty.GetValue(null, null))
                    return FailAfterStrictCleanup("Stage6B assets worker is still loading", out failure);

                if (Level.getLevel(Provider.map) != selectedLevel || string.IsNullOrEmpty(selectedLevel.path))
                    return FailAfterStrictCleanup("Stage6B selected level does not match Provider.map", out failure);
                if (selectedLevel.IsMissingAnyDependencies())
                    return FailAfterStrictCleanup("Stage6B map declares a missing dependency", out failure);

                List<ulong> orderedIds = new List<ulong>();
                HashSet<ulong> seen = new HashSet<ulong>();
                if (selectedLevel.publishedFileId != 0)
                {
                    seen.Add(selectedLevel.publishedFileId);
                    orderedIds.Add(selectedLevel.publishedFileId);
                }

                ulong[] declared = selectedLevel.configData.RequiredWorkshopFileIds;
                if (declared != null)
                {
                    foreach (ulong id in declared)
                    {
                        if (id == 0)
                            return FailAfterStrictCleanup("Stage6B map declares workshop ID 0", out failure);
                        if (seen.Add(id))
                            orderedIds.Add(id);
                    }
                }

                List<SteamContent> ugc = Provider.provider != null && Provider.provider.workshopService != null
                    ? Provider.provider.workshopService.ugc : null;
                if (ugc == null)
                    return FailAfterStrictCleanup("Stage6B local workshop content list is unavailable", out failure);

                int hostEnabledWorldCount = AppendEnabledWorldWorkshopIds(ugc, seen, orderedIds);

                if (orderedIds.Count > 255)
                    return FailAfterStrictCleanup("Stage6B requirement count exceeds native 255 limit", out failure);

                TryLogEvidence(
                    "build-input map=" + Provider.map +
                    " mapRoot=" + selectedLevel.publishedFileId +
                    " declaredCount=" + (declared == null ? 0 : declared.Length) +
                    " hostEnabledWorldCount=" + hostEnabledWorldCount +
                    " candidateCount=" + orderedIds.Count +
                    " candidateIds=" + FormatIds(orderedIds));

                Dictionary<ulong, SteamContent> contentById = new Dictionary<ulong, SteamContent>();
                foreach (SteamContent content in ugc)
                {
                    if (content != null)
                        contentById[content.publishedFileID.m_PublishedFileId] = content;
                }

                bool mapHasBundles = selectedLevel.publishedFileId != 0 &&
                    Directory.Exists(Path.Combine(selectedLevel.path, "Bundles"));
                foreach (ulong id in orderedIds)
                {
                    SteamContent content;
                    if (!contentById.TryGetValue(id, out content))
                        return FailAfterStrictCleanup("Stage6B required workshop content is not locally registered", out failure);

                    ulong sizeOnDisk;
                    string installPath;
                    uint timestamp;
                    if (!SteamUGC.GetItemInstallInfo(content.publishedFileID, out sizeOnDisk, out installPath, 1024, out timestamp) ||
                        timestamp == 0 || string.IsNullOrEmpty(installPath) || !Directory.Exists(installPath))
                        return FailAfterStrictCleanup("Stage6B required workshop content is not installed with a timestamp", out failure);

                    if (!SameDirectory(content.path, installPath))
                        return FailAfterStrictCleanup("Stage6B workshop service path differs from Steam install path", out failure);

                    bool isMapRoot = id == selectedLevel.publishedFileId;
                    if (isMapRoot)
                    {
                        if (content.type != ESteamUGCType.MAP)
                            return FailAfterStrictCleanup("Stage6B map root is not MAP content", out failure);
                        if (mapHasBundles && !HasNonEmptyWorkshopOrigin(id))
                            return FailAfterStrictCleanup("Stage6B map Bundles origin is missing or empty", out failure);
                    }
                    else if (!HasNonEmptyWorkshopOrigin(id))
                    {
                        return FailAfterStrictCleanup("Stage6B declared dependency origin is missing or empty", out failure);
                    }

                    _requirements.Add(new Stage6BWorkshopRequirement(id, timestamp, isMapRoot));
                    TryLogEvidence(
                        "validated-item id=" + id +
                        " timestamp=" + timestamp +
                        " mapRoot=" + isMapRoot +
                        " type=" + content.type +
                        " origin=nonempty");
                }

                _token = Guid.NewGuid();
                _state = EStage6BWorkshopState.Validated;
                TryLogEvidence("validated requirementCount=" + _requirements.Count);
                return true;
            }
            catch (Exception ex)
            {
                return FailAfterStrictCleanup("Stage6B Build exception: " + ex.GetType().Name, out failure);
            }
        }

        internal static bool TryCommitBeforeHost(out string failure)
        {
            ThreadUtil.assertIsGameThread();
            failure = null;
            if (_state != EStage6BWorkshopState.Validated || _token == Guid.Empty)
            {
                failure = "Stage6B Commit requires Validated state with token; actual=" + _state;
                return false;
            }

            try
            {
                IList ids = RequireStaticList(typeof(Provider), "_serverWorkshopFileIDs");
                IList advertised = RequireStaticList(typeof(Provider), "serverRequiredWorkshopFiles");
                if (ids.Count != 0 || advertised.Count != 0)
                    return FailAfterStrictCleanup("Stage6B Commit requires empty native Workshop lists", out failure);

                MethodInfo register = AccessTools.Method(typeof(Provider), "registerServerUsingWorkshopFileId",
                    new Type[] { typeof(ulong), typeof(uint) });
                if (register == null)
                    return FailAfterStrictCleanup("Stage6B timestamp registration overload is unavailable", out failure);

                foreach (Stage6BWorkshopRequirement requirement in _requirements)
                    register.Invoke(null, new object[] { requirement.FileId, requirement.Timestamp });

                if (ids.Count != _requirements.Count || advertised.Count != _requirements.Count)
                    return FailAfterStrictCleanup("Stage6B Commit post-count mismatch", out failure);

                for (int index = 0; index < _requirements.Count; ++index)
                {
                    if ((ulong)ids[index] != _requirements[index].FileId)
                        return FailAfterStrictCleanup("Stage6B Commit ID order mismatch", out failure);
                }

                _state = EStage6BWorkshopState.Committed;
                TryLogEvidence(
                    "committed requirementCount=" + _requirements.Count +
                    " serverIdCount=" + ids.Count +
                    " serverRequiredCount=" + advertised.Count);
                return true;
            }
            catch (Exception ex)
            {
                return FailAfterStrictCleanup("Stage6B Commit exception: " + ex.GetType().Name, out failure);
            }
        }

        internal static Guid GetCommittedTokenOrThrow()
        {
            if (_state != EStage6BWorkshopState.Committed || _token == Guid.Empty)
                throw new InvalidOperationException("Stage6B token requested before Commit");
            return _token;
        }

        internal static bool TryApplyServerMapping(LevelInfo selectedLevel, Guid hostToken, out string failure)
        {
            ThreadUtil.assertIsGameThread();
            failure = null;
            if (_state != EStage6BWorkshopState.Committed || _token == Guid.Empty || hostToken != _token)
            {
                failure = "Stage6B mapping requires matching Committed token";
                return false;
            }

            try
            {
                bool mappingWasCalled = false;
                if (_requirements.Count > 0)
                {
                    MethodInfo apply = AccessTools.Method(typeof(Assets), "ApplyServerAssetMapping",
                        new Type[] { typeof(LevelInfo), typeof(List<PublishedFileId_t>) });
                    if (apply == null)
                        return FailAfterStrictCleanup("Stage6B server mapping method is unavailable", out failure);

                    List<PublishedFileId_t> ids = new List<PublishedFileId_t>(_requirements.Count);
                    foreach (Stage6BWorkshopRequirement requirement in _requirements)
                        ids.Add(new PublishedFileId_t(requirement.FileId));
                    apply.Invoke(null, new object[] { selectedLevel, ids });
                    mappingWasCalled = true;
                }

                _state = EStage6BWorkshopState.Mapped;
                TryLogEvidence(
                    "mapped requirementCount=" + _requirements.Count +
                    " apply=" + (mappingWasCalled ? "called" : "skipped-empty-plan"));
                return true;
            }
            catch (Exception ex)
            {
                return FailAfterStrictCleanup("Stage6B mapping exception: " + ex.GetType().Name, out failure);
            }
        }

        internal static bool TryStrictWorkshopCleanup(out string failure)
        {
            ThreadUtil.assertIsGameThread();
            failure = null;
            try
            {
                IList ids = RequireStaticList(typeof(Provider), "_serverWorkshopFileIDs");
                IList advertised = RequireStaticList(typeof(Provider), "serverRequiredWorkshopFiles");
                ids.Clear();
                advertised.Clear();
                if (ids.Count != 0 || advertised.Count != 0)
                    return FailCleanup("Stage6B native Workshop list clear verification failed", out failure);

                MethodInfo clearMapping = AccessTools.Method(typeof(Assets), "ClearServerAssetMapping", Type.EmptyTypes);
                FieldInfo currentMapping = AccessTools.Field(typeof(Assets), "currentAssetMapping");
                FieldInfo defaultMapping = AccessTools.Field(typeof(Assets), "defaultAssetMapping");
                if (clearMapping == null || currentMapping == null || defaultMapping == null)
                    return FailCleanup("Stage6B mapping cleanup members are unavailable", out failure);

                clearMapping.Invoke(null, null);
                if (!Object.ReferenceEquals(currentMapping.GetValue(null), defaultMapping.GetValue(null)))
                    return FailCleanup("Stage6B mapping cleanup verification failed", out failure);

                _requirements.Clear();
                _token = Guid.Empty;
                _state = EStage6BWorkshopState.Cleared;
                return true;
            }
            catch (Exception ex)
            {
                return FailCleanup("Stage6B cleanup exception: " + ex.GetType().Name, out failure);
            }
        }

        internal static void MarkCleanupFaulted()
        {
            _state = EStage6BWorkshopState.CleanupFaulted;
        }

        private static bool FailAfterStrictCleanup(string primaryFailure, out string failure)
        {
            string cleanupFailure;
            if (TryStrictWorkshopCleanup(out cleanupFailure))
            {
                failure = primaryFailure;
                return false;
            }
            failure = primaryFailure + "; strict cleanup failed: " + cleanupFailure;
            return false;
        }

        private static bool FailCleanup(string reason, out string failure)
        {
            _state = EStage6BWorkshopState.CleanupFaulted;
            failure = reason;
            return false;
        }

        private static IList RequireStaticList(Type type, string fieldName)
        {
            FieldInfo field = AccessTools.Field(type, fieldName);
            IList list = field != null ? field.GetValue(null) as IList : null;
            if (list == null)
                throw new InvalidOperationException("Stage6B required list is unavailable: " + fieldName);
            return list;
        }

        private static bool HasNonEmptyWorkshopOrigin(ulong fileId)
        {
            MethodInfo findOrigin = AccessTools.Method(typeof(Assets), "FindWorkshopFileOrigin",
                new Type[] { typeof(ulong) });
            AssetOrigin origin = findOrigin != null
                ? findOrigin.Invoke(null, new object[] { fileId }) as AssetOrigin : null;
            return origin != null && origin.GetAssets() != null && origin.GetAssets().Count > 0;
        }

        private static int AppendEnabledWorldWorkshopIds(
            List<SteamContent> ugc,
            HashSet<ulong> seen,
            List<ulong> orderedIds)
        {
            HashSet<ulong> ambientSet = new HashSet<ulong>();

            foreach (SteamContent content in ugc)
            {
                if (content == null)
                    continue;

                ulong id = content.publishedFileID.m_PublishedFileId;
                if (id == 0 || seen.Contains(id))
                    continue;

                if (LocalWorkshopSettings.get().getEnabled(content.publishedFileID) == false)
                    continue;

                if (content.type != ESteamUGCType.OBJECT &&
                    content.type != ESteamUGCType.ITEM &&
                    content.type != ESteamUGCType.VEHICLE)
                    continue;

                ambientSet.Add(id);
            }

            List<ulong> ambientIds = new List<ulong>(ambientSet);
            ambientIds.Sort();

            int appendedCount = 0;
            foreach (ulong id in ambientIds)
            {
                if (seen.Add(id))
                {
                    orderedIds.Add(id);
                    ++appendedCount;
                }
            }

            return appendedCount;
        }

        private static bool SameDirectory(string first, string second)
        {
            if (String.IsNullOrEmpty(first) || String.IsNullOrEmpty(second))
                return false;
            string normalizedFirst = Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedSecond = Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return String.Equals(normalizedFirst, normalizedSecond, StringComparison.OrdinalIgnoreCase);
        }

        // Diagnostics only. It intentionally owns no state and never changes control flow.
        private static void TryLogEvidence(string message)
        {
            try
            {
                RoleLogger.Info("[Stage6B]", message);
            }
            catch
            {
                // Observability must never alter Build/Commit/Apply behavior.
            }
        }

        private static string FormatIds(IList<ulong> ids)
        {
            if (ids == null || ids.Count == 0)
                return "[]";

            return "[" + string.Join(",", ids) + "]";
        }
    }
}
