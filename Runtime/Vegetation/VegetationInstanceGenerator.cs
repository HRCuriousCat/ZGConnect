using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect
{
    public class VegetationInstanceGenerator
    {
        public VegetationChunk GenerateChunk(
            string tileId,
            Bounds worldBounds,
            Texture2D vegetationMask,
            VegetationRuleSet ruleSet,
            VegetationPrototype[] prototypes,
            Terrain terrain)
        {
            var chunk = new VegetationChunk
            {
                tileId = tileId,
                worldBounds = worldBounds
            };

            if (ruleSet == null || ruleSet.rules == null || ruleSet.rules.Length == 0)
                return chunk;

            Rect worldRect = new Rect(
                worldBounds.min.x,
                worldBounds.min.z,
                worldBounds.size.x,
                worldBounds.size.z
            );

            bool maskReadable = vegetationMask != null && vegetationMask.isReadable;

            if (vegetationMask != null && !maskReadable)
                Debug.LogWarning($"[ZGConnect] Vegetation mask '{vegetationMask.name}' is not readable. " +
                                 "Enable Read/Write in the texture's import settings.");

            var validator = new VegetationPlacementValidator(maskReadable ? vegetationMask : null, worldRect);
            var batchMap  = new Dictionary<int, VegetationBatch>();

            for (int ruleIndex = 0; ruleIndex < ruleSet.rules.Length; ruleIndex++)
            {
                ProcessRule(
                    ruleSet,
                    ruleIndex,
                    worldBounds,
                    worldRect,
                    vegetationMask,
                    maskReadable,
                    prototypes,
                    terrain,
                    validator,
                    batchMap,
                    chunk);
            }

            foreach (var batch in chunk.batches)
                batch.BuildMatrices();

            return chunk;
        }

        public IEnumerator GenerateChunkSpread(
            string tileId,
            Bounds worldBounds,
            Texture2D vegetationMask,
            VegetationRuleSet ruleSet,
            VegetationPrototype[] prototypes,
            Terrain terrain,
            int rulesPerFrame,
            float msBudget,
            System.Action<VegetationChunk> onComplete)
        {
            var chunk = new VegetationChunk
            {
                tileId = tileId,
                worldBounds = worldBounds
            };

            if (ruleSet == null || ruleSet.rules == null || ruleSet.rules.Length == 0)
            {
                onComplete?.Invoke(chunk);
                yield break;
            }

            Rect worldRect = new Rect(
                worldBounds.min.x,
                worldBounds.min.z,
                worldBounds.size.x,
                worldBounds.size.z);

            bool maskReadable = vegetationMask != null && vegetationMask.isReadable;
            if (vegetationMask != null && !maskReadable)
            {
                Debug.LogWarning($"[ZGConnect] Vegetation mask '{vegetationMask.name}' is not readable. " +
                                 "Enable Read/Write in the texture's import settings.");
            }

            var validator = new VegetationPlacementValidator(maskReadable ? vegetationMask : null, worldRect);
            var batchMap = new Dictionary<int, VegetationBatch>();

            int rulesThisFrame = 0;
            float frameStart = Time.realtimeSinceStartup;
            for (int ruleIndex = 0; ruleIndex < ruleSet.rules.Length; ruleIndex++)
            {
                ProcessRule(
                    ruleSet,
                    ruleIndex,
                    worldBounds,
                    worldRect,
                    vegetationMask,
                    maskReadable,
                    prototypes,
                    terrain,
                    validator,
                    batchMap,
                    chunk);

                rulesThisFrame++;
                if (ShouldYieldBudget(rulesThisFrame, rulesPerFrame, frameStart, msBudget))
                {
                    rulesThisFrame = 0;
                    frameStart = Time.realtimeSinceStartup;
                    yield return null;
                }
            }

            yield return null;

            foreach (VegetationBatch batch in chunk.batches)
                batch.BuildMatrices();

            yield return null;
            onComplete?.Invoke(chunk);
        }

        static void ProcessRule(
            VegetationRuleSet ruleSet,
            int ruleIndex,
            Bounds worldBounds,
            Rect worldRect,
            Texture2D vegetationMask,
            bool maskReadable,
            VegetationPrototype[] prototypes,
            Terrain terrain,
            VegetationPlacementValidator validator,
            Dictionary<int, VegetationBatch> batchMap,
            VegetationChunk chunk)
            {
                var rule = ruleSet.rules[ruleIndex];
                if (rule == null || rule.speciesSet == null)
                return;

            int maskChannel = ChannelForName(rule.sourceMaskChannel);
            float spacing = Mathf.Max(0.5f, rule.spacingMeters);
            float jitter = rule.jitterMeters;

                float startX = worldBounds.min.x;
                float startZ = worldBounds.min.z;
            float endX = worldBounds.max.x;
            float endZ = worldBounds.max.z;

                int cellX0 = Mathf.FloorToInt(startX / spacing);
                int cellZ0 = Mathf.FloorToInt(startZ / spacing);
            int cellX1 = Mathf.CeilToInt(endX / spacing);
            int cellZ1 = Mathf.CeilToInt(endZ / spacing);

                for (int cx = cellX0; cx <= cellX1; cx++)
                {
                    for (int cz = cellZ0; cz <= cellZ1; cz++)
                    {
                        int seed = ruleSet.globalSeed;

                        float jx = (DeterministicHash.Hash01(seed, cx, cz, ruleIndex * 4 + 0) * 2f - 1f) * jitter;
                        float jz = (DeterministicHash.Hash01(seed, cx, cz, ruleIndex * 4 + 1) * 2f - 1f) * jitter;

                        float wx = cx * spacing + jx;
                        float wz = cz * spacing + jz;

                        if (wx < startX || wx >= endX || wz < startZ || wz >= endZ)
                            continue;

                        float maskValue = 1f;
                        if (maskReadable)
                        {
                            float u = (wx - worldRect.x) / worldRect.width;
                            float v = (wz - worldRect.y) / worldRect.height;
                            Color c = vegetationMask.GetPixelBilinear(u, v);
                            maskValue = GetChannel(c, maskChannel);
                        }

                        if (maskValue < rule.densityThreshold)
                            continue;

                        float densityRoll = DeterministicHash.Hash01(seed, cx, cz, ruleIndex * 4 + 2);
                        if (densityRoll > maskValue * rule.density)
                            continue;

                        float wy = 0f;
                        if (terrain != null)
                            wy = terrain.SampleHeight(new Vector3(wx, 0f, wz)) + terrain.transform.position.y;

                        Vector3 position = new Vector3(wx, wy, wz);

                        if (!validator.IsValid(position, rule))
                            continue;

                        float speciesRoll = DeterministicHash.Hash01(seed, cx, cz, ruleIndex * 4 + 3);
                        VegetationPrototype proto = rule.speciesSet.PickPrototype(speciesRoll);
                        if (proto == null)
                            continue;

                        int protoIndex = FindPrototypeIndex(prototypes, proto);
                        if (protoIndex < 0)
                            continue;

                        float scaleRoll = DeterministicHash.Hash01(seed, cx + 7919, cz, ruleIndex);
                    float scale = Mathf.Lerp(proto.minMaxScale.x, proto.minMaxScale.y, scaleRoll);

                        float rotRoll = DeterministicHash.Hash01(seed, cx, cz + 7919, ruleIndex);
                    float yRot = Mathf.Lerp(proto.yRotationRange.x, proto.yRotationRange.y, rotRoll);

                        Quaternion rotation = Quaternion.Euler(0f, yRot, 0f);

                        if (proto.alignToTerrainNormal && terrain != null)
                        {
                        Vector3 local = position - terrain.transform.position;
                        float tu = local.x / terrain.terrainData.size.x;
                        float tv = local.z / terrain.terrainData.size.z;
                            Vector3 normal = terrain.terrainData.GetInterpolatedNormal(tu, tv);
                            rotation = Quaternion.FromToRotation(Vector3.up, normal) * rotation;
                        }

                        var instance = new VegetationInstance
                        {
                        position = position,
                        rotation = rotation,
                        scale = Vector3.one * scale,
                            prototypeIndex = protoIndex,
                        random = speciesRoll
                        };

                        if (!batchMap.TryGetValue(protoIndex, out VegetationBatch batch))
                        {
                            batch = new VegetationBatch
                            {
                                prototypeIndex = protoIndex,
                            prototype = proto
                            };
                            batchMap[protoIndex] = batch;
                            chunk.batches.Add(batch);
                        }

                        batch.instances.Add(instance);
                    }
                }
        }

        private static int ChannelForName(string name)
        {
            if (name == null) return 0;
            switch (name.ToLowerInvariant())
            {
                case "park":  return 1;
                case "grass": return 2;
                default:      return 0;
            }
        }

        private static float GetChannel(Color c, int channel)
        {
            switch (channel)
            {
                case 1:  return c.g;
                case 2:  return c.b;
                case 3:  return c.a;
                default: return c.r;
            }
        }

        private static int FindPrototypeIndex(VegetationPrototype[] prototypes, VegetationPrototype proto)
        {
            if (prototypes == null) return -1;
            for (int i = 0; i < prototypes.Length; i++)
                if (prototypes[i] == proto) return i;
            return -1;
        }

        static bool ShouldYieldBudget(int itemsThisFrame, int maxItemsPerFrame, float frameStartSeconds, float msBudget)
        {
            if (maxItemsPerFrame > 0 && itemsThisFrame >= maxItemsPerFrame)
                return true;

            if (msBudget > 0f && (Time.realtimeSinceStartup - frameStartSeconds) * 1000f >= msBudget)
                return true;

            return false;
        }
    }
}
