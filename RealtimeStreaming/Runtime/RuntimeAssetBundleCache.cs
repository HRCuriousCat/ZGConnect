using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ZGConnect.RealtimeStreaming
{
    /// <summary>
    /// Reference-counted AssetBundle cache keyed by absolute file path.
    /// Prevents "another AssetBundle with the same files is already loaded" when the same
    /// bundle file is requested again before the previous owner released it.
    /// </summary>
    public static class RuntimeAssetBundleCache
    {
        sealed class Entry
        {
            public AssetBundle Bundle;
            public int RefCount;
            public bool IsLoading;
            public readonly List<Action<AssetBundle>> ReadyCallbacks = new();
            public readonly List<Action<string>> ErrorCallbacks = new();
        }

        static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

        public static string NormalizePath(string fullPath) =>
            string.IsNullOrEmpty(fullPath)
                ? string.Empty
                : Path.GetFullPath(fullPath.Replace('/', Path.DirectorySeparatorChar));

        public static IEnumerator AcquireAsync(
            string fullPath,
            Action<AssetBundle> onReady,
            Action<string> onError)
        {
            string key = NormalizePath(fullPath);
            if (string.IsNullOrEmpty(key))
            {
                onError?.Invoke("Bundle path is empty.");
                yield break;
            }

            if (!File.Exists(key))
            {
                onError?.Invoke($"Bundle not found: {key}");
                yield break;
            }

            if (!Entries.TryGetValue(key, out Entry entry))
            {
                entry = new Entry();
                Entries[key] = entry;
            }

            if (entry.Bundle != null)
            {
                entry.RefCount++;
                onReady?.Invoke(entry.Bundle);
                yield break;
            }

            if (entry.IsLoading)
            {
                if (onReady != null)
                    entry.ReadyCallbacks.Add(onReady);
                if (onError != null)
                    entry.ErrorCallbacks.Add(onError);

                while (entry.IsLoading)
                    yield return null;

                yield break;
            }

            entry.IsLoading = true;
            entry.ReadyCallbacks.Clear();
            entry.ErrorCallbacks.Clear();
            if (onReady != null)
                entry.ReadyCallbacks.Add(onReady);
            if (onError != null)
                entry.ErrorCallbacks.Add(onError);

            AssetBundleCreateRequest createRequest = AssetBundle.LoadFromFileAsync(key);
            yield return createRequest;

            entry.IsLoading = false;
            AssetBundle bundle = createRequest.assetBundle;
            if (bundle == null)
            {
                string error = $"Failed to open AssetBundle: {key}";
                InvokeErrors(entry, error);
                if (entry.ReadyCallbacks.Count == 0 && entry.ErrorCallbacks.Count == 0)
                    Entries.Remove(key);
                yield break;
            }

            entry.Bundle = bundle;
            entry.RefCount = entry.ReadyCallbacks.Count;
            InvokeReady(entry, bundle);
        }

        public static void Release(string fullPath, bool unloadAllLoadedObjects)
        {
            string key = NormalizePath(fullPath);
            if (string.IsNullOrEmpty(key) || !Entries.TryGetValue(key, out Entry entry))
                return;

            entry.RefCount--;
            if (entry.RefCount > 0)
                return;

            if (entry.Bundle != null)
            {
                entry.Bundle.Unload(unloadAllLoadedObjects);
                entry.Bundle = null;
            }

            Entries.Remove(key);
        }

        static void InvokeReady(Entry entry, AssetBundle bundle)
        {
            var callbacks = entry.ReadyCallbacks.ToArray();
            entry.ReadyCallbacks.Clear();
            entry.ErrorCallbacks.Clear();
            foreach (Action<AssetBundle> callback in callbacks)
                callback?.Invoke(bundle);
        }

        static void InvokeErrors(Entry entry, string error)
        {
            var callbacks = entry.ErrorCallbacks.ToArray();
            entry.ReadyCallbacks.Clear();
            entry.ErrorCallbacks.Clear();
            foreach (Action<string> callback in callbacks)
                callback?.Invoke(error);
        }
    }
}
