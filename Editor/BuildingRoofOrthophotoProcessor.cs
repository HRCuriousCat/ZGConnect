using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ZGConnect.Editor
{
    /// <summary>
    /// Roof-only surface pass: maps roof UVs to the full tile orthophoto (0–1) and assigns an ortho material.
    /// Facade triangles keep their original mesh UVs and geometry.
    /// </summary>
    public static class BuildingRoofOrthophotoProcessor
    {
        private enum RoofTriangleType
        {
            Skip = 0,
            Wall = 1,
            FlatRoof = 2,
            SlopedRoof = 3,
        }

        private struct RoofTri
        {
            public int i0, i1, i2;
            public Vector3 v0, v1, v2;
            public Vector3 normalLocal;
        }

        private struct EmitVertex
        {
            public Vector3 position;
            public Vector3 normal;
            public Vector2 uv;
        }

        public static int ProcessTile(
            GameObject tileRoot,
            BuildingSurfaceSettings settings,
            Material roofMaterial,
            Vector3 tileOriginWorld,
            float tileSizeMeters)
        {
            if (tileRoot == null || settings == null || roofMaterial == null || tileSizeMeters <= 0f)
                return 0;

            int processed = 0;
            int childCount = tileRoot.transform.childCount;
            for (int c = 0; c < childCount; c++)
            {
                Transform child = tileRoot.transform.GetChild(c);
                processed += ProcessBuilding(
                    child.gameObject, settings, roofMaterial, tileOriginWorld, tileSizeMeters);
            }

            if (processed == 0 && childCount > 0)
            {
                Debug.LogWarning(
                    $"[ZGConnect] Roof orthophoto: tile '{tileRoot.name}' â€” 0 / {childCount} building mesh(es) processed.");
            }

            return processed;
        }

        /// <summary>Tile origin and size from <see cref="CityTileRecord"/> (matches terrain ortho placement).</summary>
        public static int ProcessTile(
            GameObject tileRoot,
            BuildingSurfaceSettings settings,
            Material roofMaterial,
            CityTileRecord rec,
            CityDataset dataset,
            BuildingsMetadataJson meta = null)
        {
            if (rec == null)
                return 0;

            float tileSize = dataset != null && dataset.tileSizeMeters > 0
                ? dataset.tileSizeMeters
                : Mathf.Max(1f, rec.right - rec.left);

            Vector3 tileOrigin = ResolveTileOriginWorld(rec, meta);
            return ProcessTile(tileRoot, settings, roofMaterial, tileOrigin, tileSize);
        }

        /// <summary>
        /// South-west tile corner in Unity world space — same reference as terrain ortho tiles.
        /// Building transforms use absolute Unity coordinates from companion JSON, not tile-root-local.
        /// </summary>
        public static Vector3 ResolveTileOriginWorld(CityTileRecord rec, BuildingsMetadataJson meta)
        {
            if (meta?.TileOriginUnity != null)
            {
                return new Vector3(
                    meta.TileOriginUnity.X,
                    meta.TileOriginUnity.Y,
                    meta.TileOriginUnity.Z);
            }

            return rec != null ? rec.unityPosition : Vector3.zero;
        }

        public static int ProcessBuilding(
            GameObject building,
            BuildingSurfaceSettings settings,
            Material roofMaterial,
            Vector3 tileOriginWorld,
            float tileSizeMeters)
        {
            if (building == null || settings == null || roofMaterial == null)
                return 0;

            int processed = 0;
            foreach (MeshRenderer renderer in building.GetComponentsInChildren<MeshRenderer>(true))
            {
                MeshFilter mf = renderer.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null)
                    continue;

                if (ProcessMeshFilter(building, mf, renderer, settings, roofMaterial, tileOriginWorld, tileSizeMeters))
                    processed++;
            }

            return processed;
        }

        private static bool ProcessMeshFilter(
            GameObject building,
            MeshFilter mf,
            MeshRenderer renderer,
            BuildingSurfaceSettings settings,
            Material roofMaterial,
            Vector3 tileOriginWorld,
            float tileSizeMeters)
        {
            Mesh srcMesh = mf.sharedMesh;
            if (srcMesh == null)
                return false;

            Mesh src = srcMesh;
            Mesh readableClone = null;
            if (!src.isReadable)
            {
                readableClone = BuildingMeshProcessingUtility.CloneMeshGeometry(srcMesh);
                if (readableClone == null)
                    return false;
                readableClone.name = srcMesh.name + "_Readable";
                src = readableClone;
            }

            Vector3[] allVerts = src.vertices;
            int vertCount = allVerts.Length;
            if (vertCount == 0)
            {
                if (readableClone != null)
                    Object.DestroyImmediate(readableClone);
                return false;
            }

            Vector3[] allNorms = src.normals;
            bool normsReady = allNorms != null && allNorms.Length == vertCount;

            Vector2[] srcUvs = src.uv != null && src.uv.Length == vertCount
                ? src.uv
                : new Vector2[vertCount];

            var wallIndices = new List<int>();
            var flatTris = new List<RoofTri>();
            var slopedTris = new List<RoofTri>();

            int subCount = src.subMeshCount;
            for (int s = 0; s < subCount; s++)
            {
                int[] indices = src.GetTriangles(s);
                Vector3[] norms = normsReady
                    ? allNorms
                    : RecalculateNormals(allVerts, indices);

                for (int i = 0; i < indices.Length; i += 3)
                {
                    int i0 = indices[i];
                    int i1 = indices[i + 1];
                    int i2 = indices[i + 2];

                    Vector3 nLocal = (norms[i0] + norms[i1] + norms[i2]).normalized;
                    Vector3 nWorld = mf.transform.TransformDirection(nLocal).normalized;

                    RoofTriangleType type = Classify(nWorld, settings);
                    if (type == RoofTriangleType.Wall)
                    {
                        wallIndices.Add(i0);
                        wallIndices.Add(i1);
                        wallIndices.Add(i2);
                    }
                    else if (type == RoofTriangleType.FlatRoof)
                    {
                        flatTris.Add(new RoofTri
                        {
                            i0 = i0, i1 = i1, i2 = i2,
                            v0 = allVerts[i0], v1 = allVerts[i1], v2 = allVerts[i2],
                            normalLocal = nLocal,
                        });
                    }
                    else if (type == RoofTriangleType.SlopedRoof)
                    {
                        slopedTris.Add(new RoofTri
                        {
                            i0 = i0, i1 = i1, i2 = i2,
                            v0 = allVerts[i0], v1 = allVerts[i1], v2 = allVerts[i2],
                            normalLocal = nLocal,
                        });
                    }
                }
            }

            if (flatTris.Count == 0 && slopedTris.Count == 0)
            {
                if (readableClone != null)
                    Object.DestroyImmediate(readableClone);
                return false;
            }

            Vector3[] srcNorms = normsReady
                ? allNorms
                : RecalculateNormals(
                    allVerts,
                    wallIndices.Count > 0 ? wallIndices.ToArray() : null);

            var wallVerts = new List<Vector3>(vertCount);
            var wallNorms = new List<Vector3>(vertCount);
            var wallUvs = new List<Vector2>(vertCount);
            for (int i = 0; i < vertCount; i++)
            {
                wallVerts.Add(allVerts[i]);
                wallNorms.Add(i < srcNorms.Length ? srcNorms[i] : Vector3.up);
                wallUvs.Add(i < srcUvs.Length ? srcUvs[i] : Vector2.zero);
            }

            var flatVerts = new List<EmitVertex>();
            var flatIndices = new List<int>();
            var slopedVerts = new List<EmitVertex>();
            var slopedIndices = new List<int>();

            AppendRoofTris(flatTris, mf, tileOriginWorld, tileSizeMeters, flatVerts, flatIndices);
            AppendRoofTris(slopedTris, mf, tileOriginWorld, tileSizeMeters, slopedVerts, slopedIndices);

            Mesh dst = BuildMesh(wallVerts, wallNorms, wallUvs, wallIndices,
                flatVerts, flatIndices, slopedVerts, slopedIndices);
            if (dst == null)
            {
                if (readableClone != null)
                    Object.DestroyImmediate(readableClone);
                return false;
            }

            dst.name = $"{building.name}_{src.name}_RoofOrtho";

            EnsureColliderUsesSourceMesh(building, srcMesh);
            mf.sharedMesh = dst;

            if (readableClone != null)
                Object.DestroyImmediate(readableClone);

            bool hasWall = wallIndices.Count > 0;
            bool hasFlat = flatVerts.Count > 0;
            bool hasSloped = slopedVerts.Count > 0;

            var mats = new List<Material>();
            if (hasWall)
            {
                Material wallMat = renderer.sharedMaterial;
                if (wallMat == null)
                {
                    wallMat = settings.GetFacadeMaterial(BuildingCategory.House, 0);
                    if (wallMat == null)
                        wallMat = settings.GetFlatRoofMaterial(BuildingCategory.House, 0);
                }
                mats.Add(wallMat != null ? wallMat : roofMaterial);
            }
            if (hasFlat)
                mats.Add(roofMaterial);
            if (hasSloped)
                mats.Add(roofMaterial);
            renderer.sharedMaterials = mats.ToArray();

            return true;
        }

        private static void AppendRoofTris(
            List<RoofTri> tris,
            MeshFilter mf,
            Vector3 tileOriginWorld,
            float tileSizeMeters,
            List<EmitVertex> verts,
            List<int> indices)
        {
            float invSize = 1f / tileSizeMeters;
            int baseIndex = verts.Count;

            foreach (RoofTri t in tris)
            {
                Vector3 w0 = mf.transform.TransformPoint(t.v0);
                Vector3 w1 = mf.transform.TransformPoint(t.v1);
                Vector3 w2 = mf.transform.TransformPoint(t.v2);

                Vector2 uv0 = TileUv(w0, tileOriginWorld, invSize);
                Vector2 uv1 = TileUv(w1, tileOriginWorld, invSize);
                Vector2 uv2 = TileUv(w2, tileOriginWorld, invSize);

                indices.Add(baseIndex);
                indices.Add(baseIndex + 1);
                indices.Add(baseIndex + 2);

                verts.Add(new EmitVertex { position = t.v0, normal = t.normalLocal, uv = uv0 });
                verts.Add(new EmitVertex { position = t.v1, normal = t.normalLocal, uv = uv1 });
                verts.Add(new EmitVertex { position = t.v2, normal = t.normalLocal, uv = uv2 });
                baseIndex += 3;
            }
        }

        /// <summary>
        /// Maps world XZ into tile orthophoto UV 0–1.
        /// Origin is <see cref="CityTileRecord.unityPosition"/> (south-west corner), same as terrain tiles.
        /// </summary>
        private static Vector2 TileUv(Vector3 worldPos, Vector3 tileOriginWorld, float invTileSize)
        {
            float u = (worldPos.x - tileOriginWorld.x) * invTileSize;
            float v = (worldPos.z - tileOriginWorld.z) * invTileSize;
            return new Vector2(u, v);
        }

        private static Mesh BuildMesh(
            List<Vector3> wallVerts,
            List<Vector3> wallNorms,
            List<Vector2> wallUvs,
            List<int> wallIndices,
            List<EmitVertex> flatVerts,
            List<int> flatIndices,
            List<EmitVertex> slopedVerts,
            List<int> slopedIndices)
        {
            bool hasWall = wallIndices.Count > 0;
            bool hasFlat = flatVerts.Count > 0;
            bool hasSloped = slopedVerts.Count > 0;
            int subMeshCount = (hasWall ? 1 : 0) + (hasFlat ? 1 : 0) + (hasSloped ? 1 : 0);
            if (subMeshCount == 0)
                return null;

            var positions = new List<Vector3>(wallVerts);
            var normals = new List<Vector3>(wallNorms);
            var uvs = new List<Vector2>(wallUvs);

            int flatOffset = positions.Count;
            if (hasFlat)
            {
                foreach (EmitVertex ev in flatVerts)
                {
                    positions.Add(ev.position);
                    normals.Add(ev.normal);
                    uvs.Add(ev.uv);
                }
                for (int i = 0; i < flatIndices.Count; i++)
                    flatIndices[i] += flatOffset;
            }

            int slopedOffset = positions.Count;
            if (hasSloped)
            {
                foreach (EmitVertex ev in slopedVerts)
                {
                    positions.Add(ev.position);
                    normals.Add(ev.normal);
                    uvs.Add(ev.uv);
                }
                for (int i = 0; i < slopedIndices.Count; i++)
                    slopedIndices[i] += slopedOffset;
            }

            var mesh = new Mesh { name = "BuildingRoofOrtho" };
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = subMeshCount;

            int sm = 0;
            if (hasWall)
                mesh.SetIndices(wallIndices, MeshTopology.Triangles, sm++);
            if (hasFlat)
                mesh.SetIndices(flatIndices, MeshTopology.Triangles, sm++);
            if (hasSloped)
                mesh.SetIndices(slopedIndices, MeshTopology.Triangles, sm);

            mesh.RecalculateBounds();
            return mesh;
        }

        private static RoofTriangleType Classify(Vector3 nWorld, BuildingSurfaceSettings settings)
        {
            float ay = Mathf.Abs(nWorld.y);
            if (ay <= settings.wallMaxNormalY)
                return RoofTriangleType.Wall;
            if (ay >= settings.flatRoofMinNormalY)
                return RoofTriangleType.FlatRoof;
            return RoofTriangleType.SlopedRoof;
        }

        private static void EnsureColliderUsesSourceMesh(GameObject building, Mesh sourceMesh)
        {
            if (building == null || sourceMesh == null)
                return;
            MeshCollider col = building.GetComponent<MeshCollider>();
            if (col == null)
                col = building.AddComponent<MeshCollider>();
            col.sharedMesh = sourceMesh;
        }

        private static Vector3[] RecalculateNormals(Vector3[] verts, int[] indices)
        {
            var norms = new Vector3[verts.Length];
            if (indices == null || indices.Length < 3)
                return norms;

            for (int i = 0; i < indices.Length; i += 3)
            {
                int i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
                Vector3 n = Vector3.Cross(verts[i1] - verts[i0], verts[i2] - verts[i0]).normalized;
                norms[i0] += n;
                norms[i1] += n;
                norms[i2] += n;
            }
            for (int i = 0; i < norms.Length; i++)
                norms[i] = norms[i].normalized;
            return norms;
        }
    }

    /// <summary>
    /// Resolves or imports per-tile orthophoto textures and shared roof materials for building import.
    /// </summary>
    public static class BuildingRoofOrthophotoResolver
    {
        private const string UrpLitShader = "Universal Render Pipeline/Lit";

        public static bool TryEnsureTileRoofMaterial(
            CityTileRecord rec,
            CityDataset dataset,
            BuildingsImportSettings settings,
            out Material roofMaterial)
        {
            return TryEnsureTileRoofMaterial(rec, dataset, settings, null, out roofMaterial);
        }

        public static bool TryEnsureTileRoofMaterial(
            CityTileRecord rec,
            CityDataset dataset,
            BuildingsImportSettings settings,
            string basemapIdOverride,
            out Material roofMaterial)
        {
            roofMaterial = null;
            if (rec == null || settings == null)
                return false;

            string basemapId = !string.IsNullOrEmpty(basemapIdOverride)
                ? basemapIdOverride
                : settings.RoofOrthophotoBasemapId;

            if (!TryResolveTextureAsset(
                    rec, dataset, settings, basemapId, out Texture2D texture, out string textureAssetPath))
            {
                Debug.LogWarning(
                    $"[ZGConnect] Roof orthophoto: no texture for tile '{rec.tileId}' (basemap '{basemapId}'). " +
                    "Import terrain ortho first or assign an ortho basemap source in Buildings import.");
                return false;
            }

            string matFolder = $"{settings.OutputFolder}/Materials/Buildings/RoofOrtho";
            ZGConnectPathUtils.EnsureAssetFolder(matFolder);
            string matPath = $"{matFolder}/RoofOrtho_{rec.tileId}_{basemapId}.mat";

            roofMaterial = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (roofMaterial == null)
            {
                Shader shader = Shader.Find(UrpLitShader);
                if (shader == null)
                    shader = Shader.Find("Standard");

                roofMaterial = new Material(shader);
                AssignTexture(roofMaterial, texture);
                roofMaterial.name = Path.GetFileNameWithoutExtension(matPath);
                AssetDatabase.CreateAsset(roofMaterial, matPath);
            }
            else
            {
                AssignTexture(roofMaterial, texture);
                EditorUtility.SetDirty(roofMaterial);
            }

            return true;
        }

        private static bool TryResolveTextureAsset(
            CityTileRecord rec,
            CityDataset dataset,
            BuildingsImportSettings settings,
            string basemapId,
            out Texture2D texture,
            out string textureAssetPath)
        {
            texture = null;
            textureAssetPath = null;
            basemapId = string.IsNullOrEmpty(basemapId)
                ? (string.IsNullOrEmpty(settings.RoofOrthophotoBasemapId) ? "ortho" : settings.RoofOrthophotoBasemapId)
                : basemapId;

            if (rec.basemapLayers != null)
            {
                foreach (BasemapLayerEntry entry in rec.basemapLayers)
                {
                    if (entry.basemapId == basemapId && entry.texture != null)
                    {
                        texture = entry.texture;
                        textureAssetPath = AssetDatabase.GetAssetPath(texture);
                        return true;
                    }
                }
            }

            if (rec.primaryBasemapTexture != null &&
                (rec.basemapLayers == null || rec.basemapLayers.Count == 0))
            {
                texture = rec.primaryBasemapTexture;
                textureAssetPath = AssetDatabase.GetAssetPath(texture);
                return true;
            }

            if (TryFindImportedTexturePath(rec.tileId, basemapId, settings.OutputFolder, out textureAssetPath))
            {
                texture = AssetDatabase.LoadAssetAtPath<Texture2D>(textureAssetPath);
                if (texture != null)
                    return true;
            }

            if (settings.RoofOrthophotoSource != null &&
                settings.RoofOrthophotoSource.Type == BasemapType.Ortho &&
                settings.RoofOrthophotoSource.Metadata?.Tiles != null)
            {
                var lookup = ZGConnectPathUtils.BuildOrthoLookup(settings.RoofOrthophotoSource.Metadata.Tiles);
                if (lookup.TryGetValue(rec.tileId, out OrthoTileJson orthoTile))
                {
                    string srcTex = Path.Combine(settings.RoofOrthophotoSource.FolderPath, orthoTile.TextureFile);
                    if (!File.Exists(srcTex))
                        return false;

                    int res = settings.RoofOrthophotoSource.Metadata.Settings.TextureResolution;
                    string bmSubfolder = $"{basemapId}_{res}";
                    string texFolder = $"{settings.OutputFolder}/Textures/{bmSubfolder}";
                    ZGConnectPathUtils.EnsureAssetFolder(texFolder);

                    string ext = Path.GetExtension(orthoTile.TextureFile);
                    textureAssetPath = $"{texFolder}/{rec.tileId}_{basemapId}_{res}{ext}";
                    string dstFull = ZGConnectPathUtils.AssetPathToFullPath(textureAssetPath);

                    if (!File.Exists(dstFull))
                        File.Copy(srcTex, dstFull);

                    AssetDatabase.ImportAsset(textureAssetPath);
                    ZGConnectImporter.ConfigureOrthoBasemapTexture(
                        textureAssetPath, settings.BasemapMaxTextureSize);

                    texture = AssetDatabase.LoadAssetAtPath<Texture2D>(textureAssetPath);
                    return texture != null;
                }
            }

            return false;
        }

        private static bool TryFindImportedTexturePath(
            string tileId,
            string basemapId,
            string outputFolder,
            out string assetPath)
        {
            assetPath = null;
            string texturesRoot = $"{outputFolder}/Textures";
            if (!AssetDatabase.IsValidFolder(texturesRoot.Replace('\\', '/')))
                return false;

            string[] subfolders = AssetDatabase.GetSubFolders(texturesRoot);
            foreach (string sub in subfolders)
            {
                if (!sub.Contains(basemapId))
                    continue;

                string[] guids = AssetDatabase.FindAssets(
                    $"{tileId}_{basemapId}",
                    new[] { sub });
                foreach (string guid in guids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (path.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase) ||
                        path.EndsWith(".jpg", System.StringComparison.OrdinalIgnoreCase) ||
                        path.EndsWith(".jpeg", System.StringComparison.OrdinalIgnoreCase))
                    {
                        assetPath = path;
                        return true;
                    }
                }
            }

            return false;
        }

        private static void AssignTexture(Material mat, Texture2D texture)
        {
            if (mat == null || texture == null)
                return;
            if (mat.HasProperty("_BaseMap"))
                mat.SetTexture("_BaseMap", texture);
            if (mat.HasProperty("_MainTex"))
                mat.SetTexture("_MainTex", texture);
        }
    }
}
