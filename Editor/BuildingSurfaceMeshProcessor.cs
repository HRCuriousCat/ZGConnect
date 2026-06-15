using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ZGConnect.Editor
{
    internal enum SurfaceTriangleType
    {
        Skip = 0,
        Wall = 1,
        FlatRoof = 2,
        SlopedRoof = 3,
    }

    /// <summary>
    /// Classifies building mesh triangles, remaps UVs, splits submeshes, assigns materials.
    /// </summary>
    public static class BuildingSurfaceMeshProcessor
    {
        // Placeholder facade texture currently uses 8 horizontal cells.
        // Keep this explicit so U mapping can be scaled from measured wall width.
        private const int kFacadeColumnsInTexture = 8;

        /// <summary>Roof UV scale: multiply profile meters-per-tile so texture repeats 2× less often.</summary>
        private const float kRoofTilingReductionFactor = 2f;

        private const bool kDebugCategory = true;
        private const int kDebugCategoryLogLimit = 200;
        private static int s_debugCategoryLogs;
        private const int kDebugUvLogLimit = 300;
        private static int s_debugUvLogs;

        private struct EmitVertex
        {
            public Vector3 position;
            public Vector3 normal;
            public Vector2 uv;
        }

        private struct WallTri
        {
            public int i0, i1, i2;
            public Vector3 v0, v1, v2;      // local-space positions for mesh output
            public Vector3 mv0, mv1, mv2;   // meter-space positions for UV projection
            public Vector3 normalLocal;     // local-space normal for mesh output
            public Vector3 normalWorld;     // world-space normal for classification/UV basis
        }

        private class WallBucket
        {
            public readonly List<WallTri> triangles = new List<WallTri>();
            public int axis; // 0=+X 1=-X 2=+Z 3=-Z
        }

        public static void ProcessBuilding(
            GameObject building,
            BuildingSurfaceSettings settings,
            string buildingId)
        {
            if (settings == null) return;

            var renderers = building.GetComponentsInChildren<MeshRenderer>(true);
            foreach (MeshRenderer renderer in renderers)
            {
                MeshFilter mf = renderer.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                if (!ProcessMeshFilter(building, mf, renderer, settings, buildingId))
                {
                    Debug.LogWarning(
                        $"[ZGConnect] Building surface: could not process mesh '{mf.sharedMesh.name}' on '{building.name}'. " +
                        "Ensure building GLBs import with Read/Write enabled.");
                }
            }
        }

        /// <summary>
        /// Assigns facade/roof materials for a baked GLB (UVs already in mesh; no remesh).
        /// </summary>
        public static void AssignBuildingMaterials(
            GameObject building,
            BuildingSurfaceSettings settings,
            string buildingId)
        {
            if (settings == null || building == null) return;

            MeshRenderer renderer = building.GetComponentInChildren<MeshRenderer>(true);
            if (renderer == null) return;

            MeshFilter mf = renderer.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return;

            float scaleComp = GetScaleCompensation(mf.transform);
            float height = meshHeightMeters(mf, scaleComp);
            BuildingData bd = building.GetComponent<BuildingData>();
            int enrichedFloors = bd != null ? bd.floors : 0;

            BuildingCategoryProfile houseRef = settings.houseProfile;
            float metersPerFloor = houseRef.metersPerFloor;
            BuildingCategory category = BuildingSurfaceUtility.ResolveCategory(
                height, metersPerFloor, enrichedFloors);
            BuildingCategoryProfile profile = settings.GetProfile(category);
            string variantSeed = BuildingSurfaceUtility.ComposeVariantSeedForBuilding(
                building, buildingId);
            int catSalt = BuildingSurfaceUtility.CategoryVariantSalt(category);

            int facadeVar = BuildingSurfaceUtility.VariantIndex(
                variantSeed, profile.facadeVariants?.Length ?? 0,
                BuildingSurfaceUtility.SaltFacade ^ catSalt);
            int flatVar = BuildingSurfaceUtility.VariantIndex(
                variantSeed, profile.flatRoofVariants?.Length ?? 0,
                BuildingSurfaceUtility.SaltFlatRoof ^ catSalt);
            int slopedVar = BuildingSurfaceUtility.VariantIndex(
                variantSeed, profile.slopedRoofVariants?.Length ?? 0,
                BuildingSurfaceUtility.SaltSlopedRoof ^ catSalt);

            int subCount = mf.sharedMesh.subMeshCount;
            bool hasFlat = subCount >= 2;
            bool hasSloped = subCount >= 3;

            BuildingSharedMaterialApplier.AssignFacadeRoofMaterialSlots(
                renderer, settings, category, facadeVar, flatVar, slopedVar, hasFlat, hasSloped);
        }

        public static void ResetDebugSession()
        {
            s_debugCategoryLogs = 0;
            s_debugUvLogs = 0;
        }

        private static bool ProcessMeshFilter(
            GameObject building,
            MeshFilter mf,
            MeshRenderer renderer,
            BuildingSurfaceSettings settings,
            string buildingId)
        {
            Mesh src = mf.sharedMesh;
            if (!src.isReadable)
                return false;

            float scaleComp = GetScaleCompensation(mf.transform);
            float height = meshHeightMeters(mf, scaleComp);
            BuildingData bd = building.GetComponent<BuildingData>();
            int enrichedFloors = bd != null ? bd.floors : 0;

            BuildingCategoryProfile houseRef = settings.houseProfile;
            float metersPerFloor = houseRef.metersPerFloor;
            int inferredFloors = BuildingSurfaceUtility.FloorCountFromHeight(height, metersPerFloor);
            int floorsForFacade = enrichedFloors > 0 ? enrichedFloors : inferredFloors;
            BuildingCategory category = BuildingSurfaceUtility.ResolveCategory(
                height, metersPerFloor, enrichedFloors);
            BuildingCategoryProfile profile = settings.GetProfile(category);

            LogCategoryDebug(building, mf, src, scaleComp, height, metersPerFloor,
                enrichedFloors, inferredFloors, category);

            string variantSeed = BuildingSurfaceUtility.ComposeVariantSeedForBuilding(
                building, buildingId);
            int catSalt = BuildingSurfaceUtility.CategoryVariantSalt(category);

            int facadeVar = BuildingSurfaceUtility.VariantIndex(
                variantSeed, profile.facadeVariants?.Length ?? 0,
                BuildingSurfaceUtility.SaltFacade ^ catSalt);
            int flatVar = BuildingSurfaceUtility.VariantIndex(
                variantSeed, profile.flatRoofVariants?.Length ?? 0,
                BuildingSurfaceUtility.SaltFlatRoof ^ catSalt);
            int slopedVar = BuildingSurfaceUtility.VariantIndex(
                variantSeed, profile.slopedRoofVariants?.Length ?? 0,
                BuildingSurfaceUtility.SaltSlopedRoof ^ catSalt);

            var wallBuckets = new Dictionary<int, WallBucket>();
            var flatTris = new List<WallTri>();
            var slopedTris = new List<WallTri>();

            // Keep original welded vertices: output mesh reuses src.vertices and
            // indices reference them directly. We only overwrite UVs per vertex.
            Vector2[] uvs0 = GetOrInitUv0(src);

            int subCount = src.subMeshCount;
            for (int s = 0; s < subCount; s++)
            {
                int[] indices = src.GetTriangles(s);
                Vector3[] verts = src.vertices;
                Vector3[] norms = src.normals;
                if (norms == null || norms.Length != verts.Length)
                    norms = RecalculateNormals(verts, indices);

                for (int i = 0; i < indices.Length; i += 3)
                {
                    int i0 = indices[i];
                    int i1 = indices[i + 1];
                    int i2 = indices[i + 2];

                    Vector3 v0 = verts[i0];
                    Vector3 v1 = verts[i1];
                    Vector3 v2 = verts[i2];

                    // Meter-space for UV projection only (keeps mesh geometry untouched).
                    // Use world-space positions (same metric basis as height), but anchor
                    // them to building-local origin to avoid large world-coordinate phase drift.
                    Vector3 metricOrigin = building.transform.position;
                    Vector3 mv0 = mf.transform.TransformPoint(v0) - metricOrigin;
                    Vector3 mv1 = mf.transform.TransformPoint(v1) - metricOrigin;
                    Vector3 mv2 = mf.transform.TransformPoint(v2) - metricOrigin;

                    Vector3 nLocal = (norms[i0] + norms[i1] + norms[i2]).normalized;
                    Vector3 nWorld = mf.transform.TransformDirection(nLocal).normalized;

                    SurfaceTriangleType type = Classify(nWorld, settings);
                    var tri = new WallTri
                    {
                        i0 = i0,
                        i1 = i1,
                        i2 = i2,
                        v0 = v0,
                        v1 = v1,
                        v2 = v2,
                        mv0 = mv0,
                        mv1 = mv1,
                        mv2 = mv2,
                        normalLocal = nLocal,
                        normalWorld = nWorld
                    };

                    switch (type)
                    {
                        case SurfaceTriangleType.Wall:
                            int key = WallKey(nWorld);
                            if (!wallBuckets.TryGetValue(key, out WallBucket bucket))
                            {
                                bucket = new WallBucket { axis = key };
                                wallBuckets[key] = bucket;
                            }
                            bucket.triangles.Add(tri);
                            break;
                        case SurfaceTriangleType.FlatRoof:
                            flatTris.Add(tri);
                            break;
                        case SurfaceTriangleType.SlopedRoof:
                            slopedTris.Add(tri);
                            break;
                    }
                }
            }

            LogUvDebug(building, mf, src, settings, category, mf.transform.lossyScale, wallBuckets, flatTris, slopedTris);

            // Render path: split vertices per surface type so UV projections can differ
            // between wall/flat/sloped without artifacts on shared edges.
            var wallVerts = new List<EmitVertex>();
            var wallIndices = new List<int>();
            foreach (WallBucket bucket in wallBuckets.Values)
                AppendWallBucket(bucket, profile, floorsForFacade, wallVerts, wallIndices);

            Vector3 roofUAxisWorld = ComputeRoofUAxisFromWalls(wallBuckets);

            var flatVerts = new List<EmitVertex>();
            var flatIndices = new List<int>();
            float flatRoofMetersPerTile = profile.flatRoofMetersPerTile * kRoofTilingReductionFactor;
            float slopedRoofMetersPerTile = profile.slopedRoofMetersPerTile * kRoofTilingReductionFactor;

            AppendRoofTris(flatTris, flatRoofMetersPerTile, roofUAxisWorld, wallBuckets, flatVerts, flatIndices, sloped: false);

            var slopedVerts = new List<EmitVertex>();
            var slopedIndices = new List<int>();
            AppendRoofTris(slopedTris, slopedRoofMetersPerTile, roofUAxisWorld, wallBuckets, slopedVerts, slopedIndices, sloped: true);

            if (wallVerts.Count == 0 && flatVerts.Count == 0 && slopedVerts.Count == 0)
                return false;

            bool hasWall = wallVerts.Count > 0;
            bool hasFlat = flatVerts.Count > 0;
            bool hasSloped = slopedVerts.Count > 0;

            Mesh dst = BuildCombinedMesh(wallVerts, wallIndices, flatVerts, flatIndices, slopedVerts, slopedIndices);
            if (dst == null) return false;

            // Unique name — multiple buildings share one tile prefab asset.
            dst.name = $"{building.name}_{src.name}_Surfaces";

            // Keep welded/original mesh for collider workflows.
            EnsureColliderUsesSourceMesh(building, src);
            mf.sharedMesh = dst;

            BuildingSharedMaterialApplier.AssignFacadeRoofMaterialSlots(
                renderer, settings, category, facadeVar, flatVar, slopedVar, hasFlat, hasSloped);
            return true;
        }

        private static SurfaceTriangleType Classify(Vector3 n, BuildingSurfaceSettings settings)
        {
            float ay = Mathf.Abs(n.y);
            if (ay <= settings.wallMaxNormalY)
                return SurfaceTriangleType.Wall;
            // Keep underside triangles too (important for colliders). Classify by |Y|.
            if (ay >= settings.flatRoofMinNormalY)
                return SurfaceTriangleType.FlatRoof;
            return SurfaceTriangleType.SlopedRoof;
        }

        private static Vector2[] GetOrInitUv0(Mesh src)
        {
            var uv = src.uv;
            if (uv != null && uv.Length == src.vertexCount)
                return uv;
            return new Vector2[src.vertexCount];
        }

        private static void AppendWallBucketWelded(
            WallBucket bucket,
            BuildingCategoryProfile profile,
            Vector2[] uvs,
            List<int> indices)
        {
            if (bucket.triangles.Count == 0) return;

            float yMin = float.MaxValue, yMax = float.MinValue;
            float uMin = float.MaxValue, uMax = float.MinValue;

            foreach (WallTri t in bucket.triangles)
            {
                foreach (Vector3 v in new[] { t.mv0, t.mv1, t.mv2 })
                {
                    if (v.y < yMin) yMin = v.y;
                    if (v.y > yMax) yMax = v.y;
                    float u = WallU(bucket.axis, v);
                    if (u < uMin) uMin = u;
                    if (u > uMax) uMax = u;
                }
            }

            float facadeH = Mathf.Max(0.01f, yMax - yMin);
            float floorCount = facadeH / profile.metersPerFloor;
            int nFloors = Mathf.Min(Mathf.CeilToInt(floorCount), profile.maxFloorsInTexture);
            nFloors = Mathf.Max(1, nFloors);

            float vScale = nFloors / (float)profile.maxFloorsInTexture;
            float uScale = profile.horizontalMetersPerRepeat;

            foreach (WallTri t in bucket.triangles)
            {
                // Preserve welded geometry by referencing original vertex indices.
                indices.Add(t.i0);
                indices.Add(t.i1);
                indices.Add(t.i2);

                SetWallUv(uvs, t.i0, t.v0, bucket.axis, yMin, facadeH, uMin, uScale, vScale);
                SetWallUv(uvs, t.i1, t.v1, bucket.axis, yMin, facadeH, uMin, uScale, vScale);
                SetWallUv(uvs, t.i2, t.v2, bucket.axis, yMin, facadeH, uMin, uScale, vScale);
            }
        }

        private static void SetWallUv(
            Vector2[] uvs,
            int vi,
            Vector3 pos,
            int axis,
            float yMin,
            float facadeH,
            float uMin,
            float uScale,
            float vScale)
        {
            float u = (WallU(axis, pos) - uMin) / uScale;
            float v = (pos.y - yMin) / facadeH * vScale;
            uvs[vi] = new Vector2(u, v);
        }

        private static void AppendRoofTrisWelded(
            List<WallTri> tris,
            float metersPerTile,
            Vector2[] uvs,
            List<int> indices,
            bool sloped)
        {
            foreach (WallTri t in tris)
            {
                Vector3 n = t.normalWorld.normalized;
                Vector3 tangent, bitangent;

                if (!sloped)
                {
                    tangent = Vector3.right;
                    bitangent = Vector3.forward;
                }
                else
                {
                    Vector3 up = Vector3.up;
                    tangent = Vector3.ProjectOnPlane(Vector3.right, n).normalized;
                    if (tangent.sqrMagnitude < 1e-6f)
                        tangent = Vector3.ProjectOnPlane(Vector3.forward, n).normalized;
                    bitangent = Vector3.Cross(n, tangent).normalized;
                }

                indices.Add(t.i0);
                indices.Add(t.i1);
                indices.Add(t.i2);

                SetRoofUv(uvs, t.i0, t.v0, tangent, bitangent, metersPerTile);
                SetRoofUv(uvs, t.i1, t.v1, tangent, bitangent, metersPerTile);
                SetRoofUv(uvs, t.i2, t.v2, tangent, bitangent, metersPerTile);
            }
        }

        private static void SetRoofUv(
            Vector2[] uvs,
            int vi,
            Vector3 p,
            Vector3 tangent,
            Vector3 bitangent,
            float metersPerTile)
        {
            float u = Vector3.Dot(p, tangent) / metersPerTile;
            float v = Vector3.Dot(p, bitangent) / metersPerTile;
            uvs[vi] = new Vector2(u, v);
        }

        

        private static int WallKey(Vector3 n)
        {
            if (Mathf.Abs(n.x) >= Mathf.Abs(n.z))
                return n.x >= 0f ? 0 : 1;
            return n.z >= 0f ? 2 : 3;
        }

        private static void AppendWallBucket(
            WallBucket bucket,
            BuildingCategoryProfile profile,
            int floorsForFacade,
            List<EmitVertex> verts,
            List<int> indices)
        {
            if (bucket.triangles.Count == 0) return;

            float yMin = float.MaxValue, yMax = float.MinValue;
            float uMin = float.MaxValue, uMax = float.MinValue;

            // Stable per-bucket wall frame in world orientation:
            // - V axis: world up (metric Y)
            // - U axis: horizontal tangent along the wall
            Vector3 avgN = Vector3.zero;
            foreach (WallTri tri in bucket.triangles)
                avgN += tri.normalWorld;
            avgN.Normalize();

            Vector3 horizN = Vector3.ProjectOnPlane(avgN, Vector3.up).normalized;
            if (horizN.sqrMagnitude < 1e-6f)
                horizN = bucket.axis <= 1 ? Vector3.right : Vector3.forward;

            // Force wall U axis to the global horizontal plane cardinal directions.
            // This avoids diagonal/rotated facade mapping on noisy vertical surfaces.
            Vector3 wallTangent =
                Mathf.Abs(horizN.x) >= Mathf.Abs(horizN.z)
                    ? Vector3.forward   // walls facing +/-X map along +Z
                    : Vector3.right;    // walls facing +/-Z map along +X

            foreach (WallTri t in bucket.triangles)
            {
                foreach (Vector3 v in new[] { t.mv0, t.mv1, t.mv2 })
                {
                    if (v.y < yMin) yMin = v.y;
                    if (v.y > yMax) yMax = v.y;
                    float u = Vector3.Dot(v, wallTangent);
                    if (u < uMin) uMin = u;
                    if (u > uMax) uMax = u;
                }
            }

            float facadeH = Mathf.Max(0.01f, yMax - yMin);
            int nFloors = Mathf.Clamp(
                floorsForFacade,
                1,
                Mathf.Max(1, profile.maxFloorsInTexture));
            float vScale = nFloors / (float)profile.maxFloorsInTexture;
            float facadeW = Mathf.Max(0.01f, uMax - uMin);

            // Width scaling analogous to floors-on-height:
            // 1) infer how many horizontal "cells" this wall should occupy from width in meters
            // 2) map that count into texture U domain [0..1] using known facade column count
            int nCols = Mathf.CeilToInt(facadeW / Mathf.Max(0.25f, profile.horizontalMetersPerRepeat));
            nCols = Mathf.Max(1, nCols);
            float uScale = nCols / (float)kFacadeColumnsInTexture;

            foreach (WallTri t in bucket.triangles)
            {
                indices.Add(verts.Count);
                verts.Add(MakeWallVertex(t.v0, t.mv0, t.normalLocal, wallTangent, yMin, facadeH, uMin, facadeW, uScale, vScale));
                indices.Add(verts.Count);
                verts.Add(MakeWallVertex(t.v1, t.mv1, t.normalLocal, wallTangent, yMin, facadeH, uMin, facadeW, uScale, vScale));
                indices.Add(verts.Count);
                verts.Add(MakeWallVertex(t.v2, t.mv2, t.normalLocal, wallTangent, yMin, facadeH, uMin, facadeW, uScale, vScale));
            }
        }

        private static EmitVertex MakeWallVertex(
            Vector3 localPos, Vector3 metricPos, Vector3 normalLocal, Vector3 wallTangent,
            float yMin, float facadeH, float uMin, float facadeW, float uScale, float vScale)
        {
            float u = (Vector3.Dot(metricPos, wallTangent) - uMin) / facadeW * uScale;
            float v = (metricPos.y - yMin) / facadeH * vScale;
            return new EmitVertex { position = localPos, normal = normalLocal, uv = new Vector2(u, v) };
        }

        private static Vector3 ComputeRoofUAxisFromWalls(Dictionary<int, WallBucket> wallBuckets)
        {
            // Derive a dominant horizontal tangent from actual wall normals.
            // This follows the building's real orientation better than coarse X/Z buckets.
            Vector3 sum = Vector3.zero;
            Vector3 refTangent = Vector3.zero;
            bool hasRef = false;

            foreach (var kv in wallBuckets)
            {
                var tris = kv.Value.triangles;
                if (tris == null || tris.Count == 0) continue;

                Vector3 avgN = Vector3.zero;
                for (int i = 0; i < tris.Count; i++)
                    avgN += tris[i].normalWorld;
                avgN.Normalize();

                Vector3 horizN = Vector3.ProjectOnPlane(avgN, Vector3.up).normalized;
                if (horizN.sqrMagnitude < 1e-6f) continue;

                Vector3 tangent = Vector3.Cross(Vector3.up, horizN).normalized;
                if (tangent.sqrMagnitude < 1e-6f) continue;

                if (!hasRef)
                {
                    refTangent = tangent;
                    hasRef = true;
                }
                else if (Vector3.Dot(tangent, refTangent) < 0f)
                {
                    tangent = -tangent; // keep hemisphere consistent for averaging
                }

                float weight = tris.Count;
                sum += tangent * weight;
            }

            Vector3 dominant = Vector3.ProjectOnPlane(sum, Vector3.up);
            if (dominant.sqrMagnitude < 1e-6f)
                return Vector3.right;
            return dominant.normalized;
        }

        private static float WallU(int axis, Vector3 p)
        {
            switch (axis)
            {
                case 0:
                case 1: return p.z;
                default: return p.x;
            }
        }

        private static void AppendRoofTris(
            List<WallTri> tris,
            float metersPerTile,
            Vector3 roofUAxisWorld,
            Dictionary<int, WallBucket> wallBuckets,
            List<EmitVertex> verts,
            List<int> indices,
            bool sloped)
        {
            // Fallback axis from wall orientation in case a roof edge degenerates.
            Vector3 fallback = Vector3.ProjectOnPlane(roofUAxisWorld, Vector3.up).normalized;
            if (fallback.sqrMagnitude < 1e-6f)
                fallback = Vector3.right;

            // Dominant wall axis from building-wide wall geometry (PCA in horizontal plane).
            // Roof axis is constrained to wall axis or 90deg alternative.
            Vector3 wallAxis = ComputeDominantWallAxisPca(wallBuckets, fallback);
            Vector3 wallAxis90 = Vector3.Cross(Vector3.up, wallAxis).normalized;
            Vector3 roofEdgeAxis = ComputeDominantRoofEdgeAxis(tris, wallAxis);
            Vector3 tangent =
                Mathf.Abs(Vector3.Dot(roofEdgeAxis, wallAxis)) >= Mathf.Abs(Vector3.Dot(roofEdgeAxis, wallAxis90))
                    ? wallAxis
                    : wallAxis90;
            Vector3 bitangent = Vector3.Cross(Vector3.up, tangent).normalized;

            // Anchor UV phase to this roof set's local bounds.
            float minU = float.MaxValue, minV = float.MaxValue;
            foreach (WallTri t in tris)
            {
                minU = Mathf.Min(minU, Vector3.Dot(t.mv0, tangent), Vector3.Dot(t.mv1, tangent), Vector3.Dot(t.mv2, tangent));
                minV = Mathf.Min(minV, Vector3.Dot(t.mv0, bitangent), Vector3.Dot(t.mv1, bitangent), Vector3.Dot(t.mv2, bitangent));
            }
            if (minU == float.MaxValue) minU = 0f;
            if (minV == float.MaxValue) minV = 0f;

            foreach (WallTri t in tris)
            {
                indices.Add(verts.Count);
                verts.Add(RoofVertex(t.v0, t.mv0, t.normalLocal, tangent, bitangent, minU, minV, metersPerTile));
                indices.Add(verts.Count);
                verts.Add(RoofVertex(t.v1, t.mv1, t.normalLocal, tangent, bitangent, minU, minV, metersPerTile));
                indices.Add(verts.Count);
                verts.Add(RoofVertex(t.v2, t.mv2, t.normalLocal, tangent, bitangent, minU, minV, metersPerTile));
            }
        }

        private static Vector3 ComputeDominantWallAxisPca(
            Dictionary<int, WallBucket> wallBuckets,
            Vector3 fallback)
        {
            double meanX = 0.0;
            double meanZ = 0.0;
            int count = 0;

            foreach (var kv in wallBuckets)
            {
                var tris = kv.Value.triangles;
                for (int i = 0; i < tris.Count; i++)
                {
                    meanX += tris[i].mv0.x; meanZ += tris[i].mv0.z; count++;
                    meanX += tris[i].mv1.x; meanZ += tris[i].mv1.z; count++;
                    meanX += tris[i].mv2.x; meanZ += tris[i].mv2.z; count++;
                }
            }
            if (count <= 2) return fallback.normalized;
            meanX /= count;
            meanZ /= count;

            double cxx = 0.0, cxz = 0.0, czz = 0.0;
            foreach (var kv in wallBuckets)
            {
                var tris = kv.Value.triangles;
                for (int i = 0; i < tris.Count; i++)
                {
                    AccumulateCov(tris[i].mv0, meanX, meanZ, ref cxx, ref cxz, ref czz);
                    AccumulateCov(tris[i].mv1, meanX, meanZ, ref cxx, ref cxz, ref czz);
                    AccumulateCov(tris[i].mv2, meanX, meanZ, ref cxx, ref cxz, ref czz);
                }
            }

            // Principal direction of 2x2 covariance matrix.
            double theta = 0.5 * System.Math.Atan2(2.0 * cxz, cxx - czz);
            Vector3 axis = new Vector3((float)System.Math.Cos(theta), 0f, (float)System.Math.Sin(theta)).normalized;
            if (axis.sqrMagnitude < 1e-6f)
                axis = fallback.normalized;

            // Resolve sign ambiguity by matching fallback wall hint.
            if (Vector3.Dot(axis, fallback) < 0f)
                axis = -axis;
            return axis;
        }

        private static void AccumulateCov(
            Vector3 p,
            double meanX,
            double meanZ,
            ref double cxx,
            ref double cxz,
            ref double czz)
        {
            double dx = p.x - meanX;
            double dz = p.z - meanZ;
            cxx += dx * dx;
            cxz += dx * dz;
            czz += dz * dz;
        }

        private static Vector3 ComputeDominantRoofEdgeAxis(List<WallTri> tris, Vector3 fallbackAxis)
        {
            Vector3 sum = Vector3.zero;
            Vector3 refDir = Vector3.zero;
            bool hasRef = false;

            for (int i = 0; i < tris.Count; i++)
            {
                AddEdgeDir(tris[i].mv0, tris[i].mv1, ref hasRef, ref refDir, ref sum);
                AddEdgeDir(tris[i].mv1, tris[i].mv2, ref hasRef, ref refDir, ref sum);
                AddEdgeDir(tris[i].mv2, tris[i].mv0, ref hasRef, ref refDir, ref sum);
            }

            Vector3 axis = Vector3.ProjectOnPlane(sum, Vector3.up);
            if (axis.sqrMagnitude < 1e-6f)
                return fallbackAxis.normalized;
            return axis.normalized;
        }

        private static void AddEdgeDir(
            Vector3 a,
            Vector3 b,
            ref bool hasRef,
            ref Vector3 refDir,
            ref Vector3 sum)
        {
            Vector3 d = Vector3.ProjectOnPlane(b - a, Vector3.up);
            float len = d.magnitude;
            if (len < 1e-4f) return;
            d /= len;

            if (!hasRef)
            {
                refDir = d;
                hasRef = true;
            }
            else if (Vector3.Dot(d, refDir) < 0f)
            {
                d = -d; // hemisphere alignment for robust averaging
            }

            // Weight by edge length so long roof edges dominate orientation.
            sum += d * len;
        }

        private static EmitVertex RoofVertex(
            Vector3 localPos, Vector3 metricPos, Vector3 normalLocal,
            Vector3 tangent, Vector3 bitangent, float minU, float minV, float metersPerTile)
        {
            float u = (Vector3.Dot(metricPos, tangent) - minU) / metersPerTile;
            float v = (Vector3.Dot(metricPos, bitangent) - minV) / metersPerTile;
            return new EmitVertex
            {
                position = localPos,
                normal = normalLocal,
                uv = new Vector2(u, v),
            };
        }

        private static Mesh BuildCombinedMesh(
            List<EmitVertex> wallVerts, List<int> wallIdx,
            List<EmitVertex> flatVerts, List<int> flatIdx,
            List<EmitVertex> slopedVerts, List<int> slopedIdx)
        {
            bool hasWall = wallVerts.Count > 0;
            bool hasFlat = flatVerts.Count > 0;
            bool hasSloped = slopedVerts.Count > 0;
            int subMeshCount = (hasWall ? 1 : 0) + (hasFlat ? 1 : 0) + (hasSloped ? 1 : 0);
            if (subMeshCount == 0) return null;

            var allVerts = new List<EmitVertex>();
            if (hasWall) allVerts.AddRange(wallVerts);

            int flatOffset = allVerts.Count;
            if (hasFlat)
            {
                allVerts.AddRange(flatVerts);
                for (int i = 0; i < flatIdx.Count; i++)
                    flatIdx[i] += flatOffset;
            }

            int slopedOffset = allVerts.Count;
            if (hasSloped)
            {
                allVerts.AddRange(slopedVerts);
                for (int i = 0; i < slopedIdx.Count; i++)
                    slopedIdx[i] += slopedOffset;
            }

            var positions = new Vector3[allVerts.Count];
            var normals = new Vector3[allVerts.Count];
            var uvs = new Vector2[allVerts.Count];
            for (int i = 0; i < allVerts.Count; i++)
            {
                positions[i] = allVerts[i].position;
                normals[i] = allVerts[i].normal;
                uvs[i] = allVerts[i].uv;
            }

            var mesh = new Mesh { name = "BuildingSurfaces" };
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = subMeshCount;

            int sm = 0;
            if (hasWall)
                mesh.SetIndices(wallIdx, MeshTopology.Triangles, sm++);
            if (hasFlat)
                mesh.SetIndices(flatIdx, MeshTopology.Triangles, sm++);
            if (hasSloped)
                mesh.SetIndices(slopedIdx, MeshTopology.Triangles, sm);

            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Embeds procedurally built meshes into an existing prefab asset file.
        /// Returns true if any mesh was embedded.
        /// </summary>
        public static bool PersistEphemeralMeshes(GameObject root, string prefabAssetPath)
        {
            if (string.IsNullOrEmpty(prefabAssetPath) || root == null)
                return false;

            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPath) == null)
            {
                Debug.LogWarning(
                    $"[ZGConnect] Cannot embed meshes — prefab asset not loaded yet: {prefabAssetPath}");
                return false;
            }

            bool any = false;
            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = mf.sharedMesh;
                if (mesh == null)
                    continue;

                if (!string.IsNullOrEmpty(AssetDatabase.GetAssetPath(mesh)))
                    continue;

                Mesh owned = Object.Instantiate(mesh);
                owned.name = mesh.name;
                mf.sharedMesh = owned;

                owned.hideFlags = HideFlags.None;
                AssetDatabase.AddObjectToAsset(owned, prefabAssetPath);
                EditorUtility.SetDirty(owned);
                any = true;
            }

            if (any)
                EditorUtility.SetDirty(AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPath));

            return any;
        }

        private static float meshHeightMeters(MeshFilter mf, float scaleComp)
        {
            if (mf == null || mf.sharedMesh == null) return 3f;

            Bounds b = TransformBounds(mf.transform.localToWorldMatrix, mf.sharedMesh.bounds);
            // Height categorisation should use actual world-space metres.
            // Do NOT divide by import scale here (worldBoundsY already represents scene units).
            return Mathf.Max(0.01f, b.size.y);
        }

        private static void LogCategoryDebug(
            GameObject building,
            MeshFilter mf,
            Mesh src,
            float scaleComp,
            float heightMeters,
            float metersPerFloor,
            int enrichedFloors,
            int inferredFloors,
            BuildingCategory category)
        {
            if (!kDebugCategory || s_debugCategoryLogs >= kDebugCategoryLogLimit) return;
            s_debugCategoryLogs++;

            Bounds localBounds = src != null ? src.bounds : default;
            Bounds worldBounds = TransformBounds(mf.transform.localToWorldMatrix, localBounds);
            Vector3 lossy = mf.transform.lossyScale;
            int usedFloors = enrichedFloors > 0 ? enrichedFloors : inferredFloors;

            Debug.Log(
                $"[ZGConnect][CategoryDebug] #{s_debugCategoryLogs} " +
                $"building='{building?.name}' mesh='{src?.name}' " +
                $"lossyScale=({lossy.x:F3},{lossy.y:F3},{lossy.z:F3}) scaleComp={scaleComp:F3} " +
                $"localBoundsY={localBounds.size.y:F3} worldBoundsY={worldBounds.size.y:F3} " +
                $"heightMeters={heightMeters:F3} metersPerFloor={metersPerFloor:F3} " +
                $"enrichedFloors={enrichedFloors} inferredFloors={inferredFloors} usedFloors={usedFloors} " +
                $"category={category}");
        }

        private static void LogUvDebug(
            GameObject building,
            MeshFilter mf,
            Mesh src,
            BuildingSurfaceSettings settings,
            BuildingCategory category,
            Vector3 metricScale,
            Dictionary<int, WallBucket> wallBuckets,
            List<WallTri> flatTris,
            List<WallTri> slopedTris)
        {
            if (settings == null || !settings.debugSurfaceUv || s_debugUvLogs >= kDebugUvLogLimit)
                return;

            s_debugUvLogs++;

            int wallTriCount = 0;
            foreach (var kv in wallBuckets)
                wallTriCount += kv.Value.triangles.Count;

            int flatTriCount = flatTris.Count;
            int slopedTriCount = slopedTris.Count;

            ComputeMetricBounds(wallBuckets, flatTris, slopedTris,
                out float minX, out float maxX, out float minY, out float maxY, out float minZ, out float maxZ);

            Debug.Log(
                $"[ZGConnect][UvDebug] #{s_debugUvLogs} " +
                $"building='{building?.name}' mesh='{src?.name}' category={category} " +
                $"metricScale=({metricScale.x:F3},{metricScale.y:F3},{metricScale.z:F3}) " +
                $"wallTris={wallTriCount} flatTris={flatTriCount} slopedTris={slopedTriCount} " +
                $"metricBBox=({minX:F3},{minY:F3},{minZ:F3})..({maxX:F3},{maxY:F3},{maxZ:F3}) " +
                $"wallMaxNormalY={settings.wallMaxNormalY:F3} flatRoofMinNormalY={settings.flatRoofMinNormalY:F3}");
        }

        private static void ComputeMetricBounds(
            Dictionary<int, WallBucket> wallBuckets,
            List<WallTri> flatTris,
            List<WallTri> slopedTris,
            out float minX, out float maxX,
            out float minY, out float maxY,
            out float minZ, out float maxZ)
        {
            minX = minY = minZ = float.MaxValue;
            maxX = maxY = maxZ = float.MinValue;

            foreach (var kv in wallBuckets)
            {
                foreach (var t in kv.Value.triangles)
                {
                    ExpandBounds(t.mv0, ref minX, ref maxX, ref minY, ref maxY, ref minZ, ref maxZ);
                    ExpandBounds(t.mv1, ref minX, ref maxX, ref minY, ref maxY, ref minZ, ref maxZ);
                    ExpandBounds(t.mv2, ref minX, ref maxX, ref minY, ref maxY, ref minZ, ref maxZ);
                }
            }

            foreach (var t in flatTris)
            {
                ExpandBounds(t.mv0, ref minX, ref maxX, ref minY, ref maxY, ref minZ, ref maxZ);
                ExpandBounds(t.mv1, ref minX, ref maxX, ref minY, ref maxY, ref minZ, ref maxZ);
                ExpandBounds(t.mv2, ref minX, ref maxX, ref minY, ref maxY, ref minZ, ref maxZ);
            }

            foreach (var t in slopedTris)
            {
                ExpandBounds(t.mv0, ref minX, ref maxX, ref minY, ref maxY, ref minZ, ref maxZ);
                ExpandBounds(t.mv1, ref minX, ref maxX, ref minY, ref maxY, ref minZ, ref maxZ);
                ExpandBounds(t.mv2, ref minX, ref maxX, ref minY, ref maxY, ref minZ, ref maxZ);
            }

            if (minX == float.MaxValue)
            {
                minX = minY = minZ = 0f;
                maxX = maxY = maxZ = 0f;
            }
        }

        private static void ExpandBounds(
            Vector3 p,
            ref float minX, ref float maxX,
            ref float minY, ref float maxY,
            ref float minZ, ref float maxZ)
        {
            if (p.x < minX) minX = p.x;
            if (p.x > maxX) maxX = p.x;
            if (p.y < minY) minY = p.y;
            if (p.y > maxY) maxY = p.y;
            if (p.z < minZ) minZ = p.z;
            if (p.z > maxZ) maxZ = p.z;
        }

        private static float GetScaleCompensation(Transform t)
        {
            Vector3 s = t.lossyScale;
            float sx = Mathf.Abs(s.x);
            float sy = Mathf.Abs(s.y);
            float sz = Mathf.Abs(s.z);
            float avg = (sx + sy + sz) / 3f;
            return Mathf.Max(0.0001f, avg);
        }

        private static Vector3 GetMetricScale(Transform t)
        {
            Vector3 s = t.lossyScale;
            return new Vector3(
                Mathf.Max(0.0001f, Mathf.Abs(s.x)),
                Mathf.Max(0.0001f, Mathf.Abs(s.y)),
                Mathf.Max(0.0001f, Mathf.Abs(s.z)));
        }

        private static Bounds TransformBounds(Matrix4x4 m, Bounds b)
        {
            Vector3 c = m.MultiplyPoint3x4(b.center);
            Vector3 e = b.extents;
            Vector3 ax = m.MultiplyVector(new Vector3(e.x, 0, 0));
            Vector3 ay = m.MultiplyVector(new Vector3(0, e.y, 0));
            Vector3 az = m.MultiplyVector(new Vector3(0, 0, e.z));
            Vector3 we = new Vector3(
                Mathf.Abs(ax.x) + Mathf.Abs(ay.x) + Mathf.Abs(az.x),
                Mathf.Abs(ax.y) + Mathf.Abs(ay.y) + Mathf.Abs(az.y),
                Mathf.Abs(ax.z) + Mathf.Abs(ay.z) + Mathf.Abs(az.z));
            return new Bounds(c, we * 2f);
        }

        private static void EnsureColliderUsesSourceMesh(GameObject building, Mesh sourceMesh)
        {
            if (building == null || sourceMesh == null) return;
            var col = building.GetComponent<MeshCollider>();
            if (col == null)
                col = building.AddComponent<MeshCollider>();
            col.sharedMesh = sourceMesh;
        }

        private static Vector3[] RecalculateNormals(Vector3[] verts, int[] indices)
        {
            var norms = new Vector3[verts.Length];
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
}
