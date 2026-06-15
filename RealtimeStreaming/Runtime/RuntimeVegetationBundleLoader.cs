using System.Collections;
using System.IO;
using UnityEngine;
using ZGConnect;

namespace ZGConnect.RealtimeStreaming
{
    public static class RuntimeVegetationBundleLoader
    {
        public sealed class LoadResult
        {
            public bool Success;
            public string Error;
            public BakedVegetationChunkAsset ChunkAsset;
            public AssetBundle Bundle;
        }

        public static IEnumerator LoadVegetationAssetAsync(string bundleFullPath, LoadResult into)
        {
            into.Success = false;
            into.Error = null;
            into.ChunkAsset = null;
            into.Bundle = null;

            if (string.IsNullOrEmpty(bundleFullPath) || !File.Exists(bundleFullPath))
            {
                into.Error = $"Vegetation bundle not found: {bundleFullPath}";
                yield break;
            }

            AssetBundleCreateRequest createRequest = AssetBundle.LoadFromFileAsync(bundleFullPath);
            yield return createRequest;

            AssetBundle bundle = createRequest.assetBundle;
            if (bundle == null)
            {
                into.Error = $"Failed to open vegetation bundle: {bundleFullPath}";
                yield break;
            }

            AssetBundleRequest assetRequest = bundle.LoadAllAssetsAsync<BakedVegetationChunkAsset>();
            yield return assetRequest;

            BakedVegetationChunkAsset chunkAsset = null;
            if (assetRequest.allAssets != null)
            {
                foreach (Object asset in assetRequest.allAssets)
                {
                    if (asset is BakedVegetationChunkAsset baked)
                    {
                        chunkAsset = baked;
                        break;
                    }
                }
            }

            if (chunkAsset == null)
            {
                bundle.Unload(true);
                into.Error = $"BakedVegetationChunkAsset not found in bundle: {bundleFullPath}";
                yield break;
            }

            into.Success = true;
            into.ChunkAsset = chunkAsset;
            into.Bundle = bundle;
        }
    }
}
