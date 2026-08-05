using SDG.Unturned;
using SteamP2PFriends.Host;
using Steamworks;
using System;
using System.Collections.Generic;

namespace SteamP2PFriends.WhitelistTests.Fakes
{
    /// <summary>
    /// Stage 7-2-2 单元测试 fake：IWhitelistStore。
    /// 蓝图 §3：不启动 Unturned、不触碰 Provider/Steam API/Unity/文件系统。
    /// </summary>
    internal sealed class FakeWhitelistStore : IWhitelistStore
    {
        private readonly List<SteamWhitelistID> _list = new List<SteamWhitelistID>();

        // 调用计数
        public int LoadCount;
        public int SaveCount;
        public int ContainsCount;
        public int AddOrUpdateCount;
        public int RemoveCount;
        public int SnapshotCount;
        public int RestoreCount;

        // 失败模式：设置后下一次调用抛异常
        public Exception ThrowOnLoad;
        public Exception ThrowOnSave;
        public Exception ThrowOnContains;
        public Exception ThrowOnAddOrUpdate;
        public Exception ThrowOnRemove;
        public Exception ThrowOnSnapshot;
        public Exception ThrowOnRestore;

        // 可控结果
        public bool ContainsResult = true;
        public bool RemoveResult = true;

        // 记录最近 AddOrUpdate/Remove 的目标
        public CSteamID? LastAddTarget;
        public string LastAddTag;
        public CSteamID? LastAddJudge;
        public CSteamID? LastRemoveTarget;

        public void Load()
        {
            LoadCount++;
            if (ThrowOnLoad != null) { var e = ThrowOnLoad; ThrowOnLoad = null; throw e; }
        }

        public void Save()
        {
            SaveCount++;
            if (ThrowOnSave != null) { var e = ThrowOnSave; ThrowOnSave = null; throw e; }
        }

        public bool Contains(CSteamID steamId)
        {
            ContainsCount++;
            if (ThrowOnContains != null) { var e = ThrowOnContains; ThrowOnContains = null; throw e; }
            return ContainsResult;
        }

        public void AddOrUpdate(CSteamID steamId, string tag, CSteamID judgeId)
        {
            AddOrUpdateCount++;
            LastAddTarget = steamId;
            LastAddTag = tag;
            LastAddJudge = judgeId;
            if (ThrowOnAddOrUpdate != null) { var e = ThrowOnAddOrUpdate; ThrowOnAddOrUpdate = null; throw e; }

            // 维护内存 list 模拟原生按 SteamID 更新或新增
            for (int i = 0; i < _list.Count; i++)
            {
                if (_list[i].steamID == steamId)
                {
                    _list[i] = new SteamWhitelistID(steamId, tag, judgeId);
                    return;
                }
            }
            _list.Add(new SteamWhitelistID(steamId, tag, judgeId));
        }

        public bool Remove(CSteamID steamId)
        {
            RemoveCount++;
            LastRemoveTarget = steamId;
            if (ThrowOnRemove != null) { var e = ThrowOnRemove; ThrowOnRemove = null; throw e; }

            // RemoveResult 直接控制返回值；true 时从内存 list 移除（若存在）
            if (!RemoveResult) return false;

            for (int i = 0; i < _list.Count; i++)
            {
                if (_list[i].steamID == steamId)
                {
                    _list.RemoveAt(i);
                    return true;
                }
            }
            // RemoveResult=true 但 list 中无此条目：仍返回 true（测试场景控制）
            // 这样测试可以专注验证 Save 失败路径，无需预先注入成员
            return true;
        }

        public List<SteamWhitelistID> Snapshot()
        {
            SnapshotCount++;
            if (ThrowOnSnapshot != null) { var e = ThrowOnSnapshot; ThrowOnSnapshot = null; throw e; }
            // 深拷贝
            var copy = new List<SteamWhitelistID>(_list.Count);
            foreach (var x in _list)
                copy.Add(new SteamWhitelistID(x.steamID, x.tag, x.judgeID));
            return copy;
        }

        public void Restore(List<SteamWhitelistID> snapshot)
        {
            RestoreCount++;
            if (ThrowOnRestore != null) { var e = ThrowOnRestore; ThrowOnRestore = null; throw e; }
            _list.Clear();
            if (snapshot != null)
            {
                foreach (var x in snapshot)
                    _list.Add(new SteamWhitelistID(x.steamID, x.tag, x.judgeID));
            }
        }

        // 测试辅助：直接注入成员
        public void InjectMember(CSteamID steamId, string tag, CSteamID judgeId)
        {
            _list.Add(new SteamWhitelistID(steamId, tag, judgeId));
        }

        public int InternalCount => _list.Count;
    }
}
