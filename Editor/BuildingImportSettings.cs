using UnityEditor;

public class BuildingImportSettings : AssetPostprocessor
{
    void OnPreprocessModel()
    {
        // Only apply to building tiles
        if (!assetPath.Contains("Buildings/buildings_")) return;

        var model = assetImporter as ModelImporter;
        if (model == null) return;

        model.importAnimation       = false;  // no animations
        model.importBlendShapes     = false;  // no blend shapes
        model.importCameras         = false;
        model.importLights          = false;
        model.generateSecondaryUV   = false;  // no lightmap UVs
        model.isReadable            = true;   // required for facade/roof UV processing at import
        model.meshCompression       = ModelImporterMeshCompression.Low;
        model.materialImportMode    = ModelImporterMaterialImportMode.None;
    }
}
