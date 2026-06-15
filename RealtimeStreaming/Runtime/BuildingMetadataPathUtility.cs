using System.IO;

namespace ZGConnect.RealtimeStreaming
{
    public static class BuildingMetadataPathUtility
    {
        public const string FacadeJsonFolder = "building_meshes";
        public const string OrthoJsonFolder = "building_meshes_ortho";
        public const string BinaryFolderSuffix = "_bin";
        public const string MetadataFilePrefix = "buildings_";

        public static string GetJsonFolder(bool orthoRoofStyle) =>
            orthoRoofStyle ? OrthoJsonFolder : FacadeJsonFolder;

        public static string GetBinaryFolder(bool orthoRoofStyle) =>
            GetJsonFolder(orthoRoofStyle) + BinaryFolderSuffix;

        public static string GetJsonFileName(string tileId) =>
            $"{MetadataFilePrefix}{tileId}.json";

        public static string GetBinaryFileName(string tileId) =>
            $"{MetadataFilePrefix}{tileId}.bytes";

        public static void ResolveMetadataPaths(
            string datasetRoot,
            string tileId,
            bool orthoRoofStyle,
            out string jsonPath,
            out string bytesPath)
        {
            string jsonFolder = GetJsonFolder(orthoRoofStyle);
            string binFolder = GetBinaryFolder(orthoRoofStyle);
            jsonPath = Path.Combine(datasetRoot,
                $"{jsonFolder}/{GetJsonFileName(tileId)}".Replace('/', Path.DirectorySeparatorChar));
            bytesPath = Path.Combine(datasetRoot,
                $"{binFolder}/{GetBinaryFileName(tileId)}".Replace('/', Path.DirectorySeparatorChar));
        }

        public static string GetBinaryFolderForJsonFolder(string jsonFolder)
        {
            if (string.IsNullOrEmpty(jsonFolder))
                return jsonFolder;

            if (jsonFolder.EndsWith(BinaryFolderSuffix, System.StringComparison.OrdinalIgnoreCase))
                return jsonFolder;

            return jsonFolder + BinaryFolderSuffix;
        }
    }
}
