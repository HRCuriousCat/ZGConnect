using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using GLTFast;
using UnityEditor;
using UnityEngine;

namespace ZGConnect.SpatialStreaming.Editor
{
    public static class SpatialGlbEditorLoader
    {
        public sealed class LoadState
        {
            public GameObject Instance;
            public GltfImport Import;
            public string Error;
        }

        public static IEnumerator LoadGlbCoroutine(string glbFullPath, string tileId, LoadState state)
        {
            state.Instance = null;
            state.Import = null;
            state.Error = null;

            if (!File.Exists(glbFullPath))
            {
                state.Error = $"GLB not found: {glbFullPath}";
                yield break;
            }

            byte[] bytes = File.ReadAllBytes(glbFullPath);
            Task<LoadState> task = LoadGlbAsync(bytes, glbFullPath, tileId);
            while (!task.IsCompleted)
                yield return null;

            if (task.IsFaulted)
            {
                state.Error = task.Exception?.GetBaseException().Message ?? "GLB load failed.";
                yield break;
            }

            LoadState result = task.Result;
            state.Instance = result.Instance;
            state.Import = result.Import;
            state.Error = result.Error;
        }

        static async Task<LoadState> LoadGlbAsync(byte[] bytes, string glbFullPath, string tileId)
        {
            var state = new LoadState();
            var import = new GltfImport(deferAgent: new UninterruptedDeferAgent());
            bool ok = await import.Load(bytes, new Uri(Path.GetFullPath(glbFullPath)));
            if (!ok)
            {
                import.Dispose();
                state.Error = "glTFast Load() failed.";
                return state;
            }

            var root = new GameObject($"SpatialBake_{tileId}");
            await import.InstantiateMainSceneAsync(root.transform);
            state.Instance = root;
            state.Import = import;
            return state;
        }

        public static void Dispose(LoadState state)
        {
            state?.Import?.Dispose();
            if (state?.Instance != null)
                UnityEngine.Object.DestroyImmediate(state.Instance);
        }
    }
}
