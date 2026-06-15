using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect.PhotogrammetryStreaming
{
    public sealed class TileMemoryCache
    {
        sealed class Entry
        {
            public string Key;
            public GameObject Root;
            public PhotogrammetryTileHolder Holder;
            public long EstimatedBytes;
        }

        readonly long _maxBytes;
        readonly LinkedList<Entry> _lru = new();
        readonly Dictionary<string, LinkedListNode<Entry>> _map = new();
        long _currentBytes;

        public TileMemoryCache(long maxBytes) => _maxBytes = maxBytes;

        public long CurrentBytes => _currentBytes;
        public int Count => _map.Count;

        public bool TryGet(string key, out GameObject root)
        {
            root = null;
            if (!_map.TryGetValue(key, out var node))
                return false;
            _lru.Remove(node);
            _lru.AddLast(node);
            root = node.Value.Root;
            return root != null;
        }

        public void Add(string key, GameObject root, PhotogrammetryTileHolder holder, long estimatedBytes)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _currentBytes -= existing.Value.EstimatedBytes;
                _lru.Remove(existing);
                _map.Remove(key);
            }

            var entry = new Entry { Key = key, Root = root, Holder = holder, EstimatedBytes = estimatedBytes };
            var node = _lru.AddLast(entry);
            _map[key] = node;
            _currentBytes += estimatedBytes;
            EnforceBudget();
        }

        public void Remove(string key)
        {
            if (!_map.TryGetValue(key, out var node))
                return;
            EvictEntry(node);
        }

        void EnforceBudget()
        {
            while (_currentBytes > _maxBytes && _lru.First != null)
                EvictEntry(_lru.First);
        }

        void EvictEntry(LinkedListNode<Entry> node)
        {
            var e = node.Value;
            _currentBytes -= e.EstimatedBytes;
            _lru.Remove(node);
            _map.Remove(e.Key);
            if (e.Holder != null)
                e.Holder.DisposeImport();
            if (e.Root != null)
                Object.Destroy(e.Root);
        }

        public void Clear()
        {
            while (_lru.First != null)
                EvictEntry(_lru.First);
        }
    }
}
