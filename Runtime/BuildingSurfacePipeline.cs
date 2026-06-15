using System.IO;

namespace ZGConnect
{
    /// <summary>
    /// Versioning and folder layout for pre-baked building surface GLBs.
    /// </summary>
    public static class BuildingSurfacePipeline
    {
        /// <summary>v2: geometry-only GLB + materialSlots in JSON (no embedded textures).</summary>
        public const int Version = 2;

        /// <summary>Subfolder under the raw buildings export folder for baked GLBs.</summary>
        public const string ProcessedFolderName = "Processed";

        public static string GetProcessedFolder(string rawBuildingsFolder) =>
            Path.Combine(rawBuildingsFolder, ProcessedFolderName);
    }
}
