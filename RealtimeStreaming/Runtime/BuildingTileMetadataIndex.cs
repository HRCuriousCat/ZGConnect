using System;
using System.Collections.Generic;
using ZGConnect;

namespace ZGConnect.RealtimeStreaming
{
    public readonly struct BuildingMetadataIndexEntry
    {
        public readonly int NameStringId;
        public readonly int DataRelOffset;
        public readonly int DataLength;

        public BuildingMetadataIndexEntry(int nameStringId, int dataRelOffset, int dataLength)
        {
            NameStringId = nameStringId;
            DataRelOffset = dataRelOffset;
            DataLength = dataLength;
        }
    }

    /// <summary>Header, string table, and per-building index for lazy single-entry reads.</summary>
    public sealed class BuildingTileMetadataIndex
    {
        public string TileId;
        public byte[] FileBytes;
        public string[] Strings;
        public int DataOffset;
        public BuildingMetadataIndexEntry[] Entries;

        Dictionary<string, int> _entryByLookupKey;

        public bool TryGetEntryIndex(string buildingName, out int entryIndex)
        {
            entryIndex = -1;
            if (string.IsNullOrEmpty(buildingName) || Entries == null || Entries.Length == 0)
                return false;

            EnsureLookup();
            return _entryByLookupKey.TryGetValue(buildingName, out entryIndex);
        }

        void EnsureLookup()
        {
            if (_entryByLookupKey != null)
                return;

            _entryByLookupKey = new Dictionary<string, int>(Entries.Length * 2, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Entries.Length; i++)
            {
                string name = ResolveString(Entries[i].NameStringId);
                if (string.IsNullOrEmpty(name))
                    continue;

                if (!_entryByLookupKey.ContainsKey(name))
                    _entryByLookupKey[name] = i;

                string id = BuildingSurfaceUtility.ExtractBuildingIdFromObjectName(name);
                if (!string.IsNullOrEmpty(id) && !_entryByLookupKey.ContainsKey(id))
                    _entryByLookupKey[id] = i;
            }
        }

        string ResolveString(int id)
        {
            if (Strings == null || id < 0 || id >= Strings.Length)
                return string.Empty;

            return Strings[id] ?? string.Empty;
        }
    }
}
