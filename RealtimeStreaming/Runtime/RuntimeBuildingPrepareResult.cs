using System.Threading.Tasks;

namespace ZGConnect.RealtimeStreaming
{
    public sealed class RuntimeBuildingPrepareResult
    {
        public bool Success;
        public string Error;
        public string TileId;
        public string GlbPath;
        public string JsonPath;
        public string Lod1GlbPath;
        public byte[] GlbBytes;
        public byte[] Lod1GlbBytes;
        public string JsonText;
        public byte[] MetadataBytes;
        public string MetadataBytesPath;
    }

    public static class RuntimeBuildingPreparer
    {
        public static Task<RuntimeBuildingPrepareResult> PrepareAsync(
            string tileId,
            string glbPath,
            string jsonPath,
            string lod1GlbPath,
            string metadataBytesPath = null)
        {
            return Task.Run(() => PrepareOnBackgroundThread(tileId, glbPath, jsonPath, lod1GlbPath, metadataBytesPath));
        }

        static RuntimeBuildingPrepareResult PrepareOnBackgroundThread(
            string tileId,
            string glbPath,
            string jsonPath,
            string lod1GlbPath,
            string metadataBytesPath)
        {
            var result = new RuntimeBuildingPrepareResult
            {
                TileId = tileId,
                GlbPath = glbPath,
                JsonPath = jsonPath,
                Lod1GlbPath = lod1GlbPath,
            };

            try
            {
                if (string.IsNullOrEmpty(glbPath) || !System.IO.File.Exists(glbPath))
                {
                    result.Error = $"GLB not found: {glbPath}";
                    return result;
                }

                result.GlbBytes = System.IO.File.ReadAllBytes(glbPath);

                if (!string.IsNullOrEmpty(lod1GlbPath) && System.IO.File.Exists(lod1GlbPath))
                    result.Lod1GlbBytes = System.IO.File.ReadAllBytes(lod1GlbPath);

                result.MetadataBytesPath = metadataBytesPath;

                result.Success = true;
            }
            catch (System.Exception ex)
            {
                result.Error = ex.Message;
            }

            return result;
        }
    }
}

