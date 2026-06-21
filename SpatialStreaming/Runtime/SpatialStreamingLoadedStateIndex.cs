using System;
using System.Collections.Generic;

namespace ZGConnect.SpatialStreaming
{
    /// <summary>
    /// Incremental loaded-state indexes: per-tile detail/proxy bitmasks, in-flight counts, and HLOD block records.
    /// Updated on load/unload instead of scanning all loaded records.
    /// </summary>
    public sealed class SpatialStreamingLoadedStateIndex
    {
        public sealed class TileRuntimeState
        {
            public ushort RequiredDetailMask;
            public ushort RequiredProxyMask;
            public ushort LoadedDetailMask;
            public ushort LoadedProxyMask;
            public bool TileProxyLoaded;
            public int DetailInFlight;
            public bool DetailComplete;
            public bool AnyDetailLoaded;
        }

        readonly Dictionary<string, TileRuntimeState> _tileStates = new(StringComparer.Ordinal);
        readonly Dictionary<long, SpatialLoadedSubcellRecord> _hlod2ByBlock = new();
        readonly Dictionary<long, SpatialLoadedSubcellRecord> _hlod4ByBlock = new();
        readonly SpatialDatasetRuntimeIndex _runtimeIndex;

        public IReadOnlyDictionary<long, SpatialLoadedSubcellRecord> Hlod2ByBlock => _hlod2ByBlock;
        public IReadOnlyDictionary<long, SpatialLoadedSubcellRecord> Hlod4ByBlock => _hlod4ByBlock;

        public SpatialStreamingLoadedStateIndex(SpatialDatasetRuntimeIndex runtimeIndex)
        {
            _runtimeIndex = runtimeIndex;
            BuildRequiredMasksFromManifest();
        }

        void BuildRequiredMasksFromManifest()
        {
            if (_runtimeIndex == null)
                return;

            foreach (SpatialTileManifestEntry tile in _runtimeIndex.EnumerateAllTiles())
            {
                if (tile?.Subcells == null)
                    continue;

                TileRuntimeState state = GetOrCreateTile(tile.TileId);
                state.RequiredDetailMask = 0;
                state.RequiredProxyMask = 0;

                foreach (SpatialSubcellManifestEntry subcell in tile.Subcells)
                {
                    if (subcell == null || string.IsNullOrEmpty(subcell.SubcellId))
                        continue;

                    int slot = SubcellSlot(subcell.GridX, subcell.GridY);
                    if (!string.IsNullOrEmpty(subcell.BundleRel))
                        state.RequiredDetailMask |= (ushort)(1 << slot);

                    if (!string.IsNullOrEmpty(subcell.ProxyBundleRel))
                        state.RequiredProxyMask |= (ushort)(1 << slot);
                }
            }
        }

        public void RebuildFromLoaded(IReadOnlyDictionary<string, SpatialLoadedSubcellRecord> loaded)
        {
            _hlod2ByBlock.Clear();
            _hlod4ByBlock.Clear();
            foreach (TileRuntimeState state in _tileStates.Values)
            {
                state.LoadedDetailMask = 0;
                state.LoadedProxyMask = 0;
                state.TileProxyLoaded = false;
                state.AnyDetailLoaded = false;
                state.DetailComplete = false;
                state.DetailInFlight = 0;
            }

            if (loaded == null)
                return;

            foreach (SpatialLoadedSubcellRecord record in loaded.Values)
                RegisterLoaded(record);
        }

        public static int SubcellSlot(int gridX, int gridY) => (gridY & 3) * 4 + (gridX & 3);

        public TileRuntimeState GetOrCreateTile(string tileId)
        {
            if (!_tileStates.TryGetValue(tileId, out TileRuntimeState state))
            {
                state = new TileRuntimeState();
                _tileStates[tileId] = state;
            }

            return state;
        }

        public bool TryGetTileState(string tileId, out TileRuntimeState state) =>
            _tileStates.TryGetValue(tileId, out state);

        public void RegisterLoaded(SpatialLoadedSubcellRecord record)
        {
            if (record == null)
                return;

            switch (record.LodLevel)
            {
                case SpatialStreamingLodLevel.Detail:
                    RegisterDetail(record.TileId, record.SubcellId);
                    break;
                case SpatialStreamingLodLevel.SubcellProxy:
                    RegisterSubcellProxy(record.TileId, record.SubcellId);
                    break;
                case SpatialStreamingLodLevel.TileProxy:
                    RegisterTileProxy(record.TileId);
                    break;
                case SpatialStreamingLodLevel.Hlod2x2:
                    _hlod2ByBlock[SpatialDatasetRuntimeIndex.PackBlockOrigin(record.BlockLeft, record.BlockBottom)] = record;
                    break;
                case SpatialStreamingLodLevel.Hlod4x4:
                    _hlod4ByBlock[SpatialDatasetRuntimeIndex.PackBlockOrigin(record.BlockLeft, record.BlockBottom)] = record;
                    break;
            }
        }

        public void UnregisterLoaded(SpatialLoadedSubcellRecord record)
        {
            if (record == null)
                return;

            switch (record.LodLevel)
            {
                case SpatialStreamingLodLevel.Detail:
                    UnregisterDetail(record.TileId, record.SubcellId);
                    break;
                case SpatialStreamingLodLevel.SubcellProxy:
                    UnregisterSubcellProxy(record.TileId, record.SubcellId);
                    break;
                case SpatialStreamingLodLevel.TileProxy:
                    UnregisterTileProxy(record.TileId);
                    break;
                case SpatialStreamingLodLevel.Hlod2x2:
                {
                    long packed = SpatialDatasetRuntimeIndex.PackBlockOrigin(record.BlockLeft, record.BlockBottom);
                    if (_hlod2ByBlock.TryGetValue(packed, out SpatialLoadedSubcellRecord existing) && existing.Key == record.Key)
                        _hlod2ByBlock.Remove(packed);
                    break;
                }
                case SpatialStreamingLodLevel.Hlod4x4:
                {
                    long packed = SpatialDatasetRuntimeIndex.PackBlockOrigin(record.BlockLeft, record.BlockBottom);
                    if (_hlod4ByBlock.TryGetValue(packed, out SpatialLoadedSubcellRecord existing) && existing.Key == record.Key)
                        _hlod4ByBlock.Remove(packed);
                    break;
                }
            }
        }

        public void RegisterDetailLoading(string tileId) =>
            GetOrCreateTile(tileId).DetailInFlight++;

        public void UnregisterDetailLoading(string tileId)
        {
            if (_tileStates.TryGetValue(tileId, out TileRuntimeState state) && state.DetailInFlight > 0)
                state.DetailInFlight--;
        }

        void RegisterDetail(string tileId, string subcellId)
        {
            if (string.IsNullOrEmpty(tileId))
                return;

            TileRuntimeState state = GetOrCreateTile(tileId);
            state.AnyDetailLoaded = true;
            int slot = ResolveSubcellSlot(tileId, subcellId);
            state.LoadedDetailMask |= (ushort)(1 << slot);
            RefreshDetailComplete(tileId, state);
        }

        void UnregisterDetail(string tileId, string subcellId)
        {
            if (!_tileStates.TryGetValue(tileId, out TileRuntimeState state))
                return;

            int slot = ResolveSubcellSlot(tileId, subcellId);
            state.LoadedDetailMask &= (ushort)~(1 << slot);
            state.AnyDetailLoaded = state.LoadedDetailMask != 0;
            RefreshDetailComplete(tileId, state);
        }

        void RegisterSubcellProxy(string tileId, string subcellId)
        {
            if (string.IsNullOrEmpty(tileId))
                return;

            TileRuntimeState state = GetOrCreateTile(tileId);
            int slot = ResolveSubcellSlot(tileId, subcellId);
            state.LoadedProxyMask |= (ushort)(1 << slot);
        }

        void UnregisterSubcellProxy(string tileId, string subcellId)
        {
            if (!_tileStates.TryGetValue(tileId, out TileRuntimeState state))
                return;

            int slot = ResolveSubcellSlot(tileId, subcellId);
            state.LoadedProxyMask &= (ushort)~(1 << slot);
        }

        void RegisterTileProxy(string tileId) =>
            GetOrCreateTile(tileId).TileProxyLoaded = true;

        void UnregisterTileProxy(string tileId)
        {
            if (_tileStates.TryGetValue(tileId, out TileRuntimeState state))
                state.TileProxyLoaded = false;
        }

        void RefreshDetailComplete(string tileId, TileRuntimeState state)
        {
            if (state.RequiredDetailMask == 0)
            {
                state.DetailComplete = false;
                return;
            }

            state.DetailComplete = (state.LoadedDetailMask & state.RequiredDetailMask) == state.RequiredDetailMask;
        }

        int ResolveSubcellSlot(string tileId, string subcellId)
        {
            if (_runtimeIndex != null &&
                _runtimeIndex.TryGetSubcell(tileId, subcellId, out SpatialSubcellManifestEntry subcell))
            {
                return SubcellSlot(subcell.GridX, subcell.GridY);
            }

            if (!string.IsNullOrEmpty(subcellId))
            {
                string[] parts = subcellId.Split('_');
                if (parts.Length == 2 &&
                    int.TryParse(parts[0], out int gx) &&
                    int.TryParse(parts[1], out int gy))
                {
                    return SubcellSlot(gx, gy);
                }
            }

            return 0;
        }

        public bool IsDetailLoaded(string tileId, string subcellId)
        {
            if (string.IsNullOrEmpty(tileId) || string.IsNullOrEmpty(subcellId))
                return false;

            if (!_tileStates.TryGetValue(tileId, out TileRuntimeState state))
                return false;

            int slot = ResolveSubcellSlot(tileId, subcellId);
            return (state.LoadedDetailMask & (1 << slot)) != 0;
        }

        public bool IsSubcellProxyLoaded(string tileId, string subcellId)
        {
            if (!_tileStates.TryGetValue(tileId, out TileRuntimeState state))
                return false;

            int slot = ResolveSubcellSlot(tileId, subcellId);
            return (state.LoadedProxyMask & (1 << slot)) != 0;
        }

        public bool IsTileProxyLoaded(string tileId) =>
            _tileStates.TryGetValue(tileId, out TileRuntimeState state) && state.TileProxyLoaded;

        public bool TileHasAnyDetailLoaded(string tileId) =>
            _tileStates.TryGetValue(tileId, out TileRuntimeState state) && state.AnyDetailLoaded;

        public bool IsTileDetailComplete(string tileId) =>
            _tileStates.TryGetValue(tileId, out TileRuntimeState state) && state.DetailComplete;

        public bool TileSubcellLayerReady(string tileId)
        {
            if (!_tileStates.TryGetValue(tileId, out TileRuntimeState state))
                return false;

            if (state.DetailComplete)
                return true;

            if (state.RequiredProxyMask == 0)
                return state.TileProxyLoaded;

            ushort satisfied = (ushort)(state.LoadedProxyMask | state.LoadedDetailMask);
            return (satisfied & state.RequiredProxyMask) == state.RequiredProxyMask;
        }

        public int CountDetailInFlight(string tileId) =>
            _tileStates.TryGetValue(tileId, out TileRuntimeState state) ? state.DetailInFlight : 0;

        public int CountLoadedDetailSubcells(string tileId)
        {
            if (!_tileStates.TryGetValue(tileId, out TileRuntimeState state))
                return 0;

            int count = 0;
            ushort mask = state.LoadedDetailMask;
            while (mask != 0)
            {
                count += mask & 1;
                mask >>= 1;
            }

            return count;
        }
    }
}
