using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace ZGConnect.PhotogrammetryStreaming
{
    public sealed class TileDiskCache
    {
        readonly string _rootDir;
        readonly long _maxBytes;
        readonly TimeSpan _ttl;
        readonly LinkedList<string> _lru = new();
        readonly Dictionary<string, LinkedListNode<string>> _nodes = new();
        long _currentBytes;

        public TileDiskCache(string sessionId, long maxBytes, int ttlHours)
        {
            _maxBytes = Math.Max(64L * 1024L * 1024L, maxBytes);
            _ttl = TimeSpan.FromHours(Math.Max(1, ttlHours));
            _rootDir = Path.Combine(Application.temporaryCachePath, "ZGConnect", "photogrammetry", SanitizeSessionFolder(sessionId));
            Directory.CreateDirectory(_rootDir);
            RebuildIndex();
        }

        /// <summary>Google session tokens contain URL/path-illegal chars — always hash for folder names.</summary>
        public static string SanitizeSessionFolder(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return "default";

            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sessionId));
            string hex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            return hex.Substring(0, 16);
        }

        public string RootDirectory => _rootDir;

        public bool TryRead(string normalizedUrl, out byte[] bytes)
        {
            bytes = null;
            string path = PathForUrl(normalizedUrl);
            if (!File.Exists(path))
                return false;

            var info = new FileInfo(path);
            if (DateTime.UtcNow - info.LastWriteTimeUtc > _ttl)
            {
                TryDelete(path, normalizedUrl);
                return false;
            }

            bytes = File.ReadAllBytes(path);
            Touch(normalizedUrl);
            return true;
        }

        public void Write(string normalizedUrl, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return;

            string path = PathForUrl(normalizedUrl);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _rootDir);
            File.WriteAllBytes(path, bytes);
            AddOrUpdate(normalizedUrl, bytes.Length);
            EnforceBudget();
        }

        public static string NormalizeUrlForCache(string url)
        {
            if (string.IsNullOrEmpty(url))
                return url;
            int keyIdx = url.IndexOf("key=", StringComparison.OrdinalIgnoreCase);
            if (keyIdx < 0)
                return url;
            int amp = url.IndexOf('&', keyIdx);
            return amp < 0 ? url.Substring(0, keyIdx).TrimEnd('?') : url.Remove(keyIdx, amp - keyIdx + 1).TrimEnd('?');
        }

        string PathForUrl(string normalizedUrl)
        {
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalizedUrl));
            string hex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            string pathOnly = TileContentUri.PathWithoutQuery(normalizedUrl);
            string ext = pathOnly.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) ? ".glb" :
                pathOnly.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? ".json" : ".bin";
            return Path.Combine(_rootDir, hex.Substring(0, 2), hex + ext);
        }

        void RebuildIndex()
        {
            _lru.Clear();
            _nodes.Clear();
            _currentBytes = 0;
            if (!Directory.Exists(_rootDir))
                return;

            foreach (string file in Directory.EnumerateFiles(_rootDir, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                if (DateTime.UtcNow - info.LastWriteTimeUtc > _ttl)
                {
                    try { File.Delete(file); } catch { /* ignore */ }
                    continue;
                }
                string key = file;
                _currentBytes += info.Length;
                var node = _lru.AddLast(key);
                _nodes[key] = node;
            }
        }

        void Touch(string normalizedUrl)
        {
            string path = PathForUrl(normalizedUrl);
            if (_nodes.TryGetValue(path, out var node))
            {
                _lru.Remove(node);
                _lru.AddLast(node);
            }
        }

        void AddOrUpdate(string normalizedUrl, long bytes)
        {
            string path = PathForUrl(normalizedUrl);
            if (_nodes.TryGetValue(path, out var existing))
            {
                _lru.Remove(existing);
                _currentBytes -= new FileInfo(path).Length;
            }
            var node = _lru.AddLast(path);
            _nodes[path] = node;
            _currentBytes += bytes;
        }

        void TryDelete(string path, string normalizedUrl)
        {
            try
            {
                if (File.Exists(path))
                {
                    long len = new FileInfo(path).Length;
                    File.Delete(path);
                    _currentBytes -= len;
                }
            }
            catch { /* ignore */ }

            if (_nodes.TryGetValue(path, out var node))
            {
                _lru.Remove(node);
                _nodes.Remove(path);
            }
        }

        void EnforceBudget()
        {
            while (_currentBytes > _maxBytes && _lru.First != null)
            {
                string path = _lru.First.Value;
                TryDelete(path, path);
            }
        }
    }
}
