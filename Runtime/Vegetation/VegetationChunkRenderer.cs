using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ZGConnect
{
    public class VegetationChunkRenderer : MonoBehaviour
    {
        private const int MaxInstancesPerDraw = 1023;

        private struct DrawCall
        {
            public VegetationBatch batch;
            public Mesh            mesh;
            public int             submeshIndex;
            public int             trianglesPerInstance;
            public RenderParams    renderParams;
        }

        private VegetationChunk chunk;
        private readonly List<DrawCall> drawCalls = new List<DrawCall>();

        private VegetationSpawnAnimationSettings animationSettings;
        private VegetationDrawDistanceSettings   drawDistanceSettings;
        private Transform drawDistanceCamera;
        private Camera    drawDistanceCameraComponent;
        private bool      chunkFrustumCullEnabled = true;
        private bool      instanceFrustumCullEnabled = true;

        private bool  spawnAnimationActive;
        private float spawnTime;
        private bool  animationComplete = true;

        public int TotalInstances => chunk?.TotalInstances ?? 0;
        public int DrawCallCount  => drawCalls.Count;
        public bool IsAnimating   => spawnAnimationActive && !animationComplete;

        public void SetChunk(
            VegetationChunk newChunk,
            VegetationPrototype[] prototypes,
            VegetationSpawnAnimationSettings animation,
            bool playSpawnAnimation,
            VegetationDrawDistanceSettings drawDistance,
            Transform camera,
            bool frustumCullChunks = true,
            bool frustumCullInstances = true)
        {
            chunk                = newChunk;
            animationSettings    = animation;
            drawDistanceSettings = drawDistance;
            drawDistanceCamera   = camera;
            drawDistanceCameraComponent = ResolveCameraComponent(camera);
            chunkFrustumCullEnabled     = frustumCullChunks;
            instanceFrustumCullEnabled    = frustumCullInstances;
            spawnAnimationActive = playSpawnAnimation && animation.IsActive;
            spawnTime            = Time.time;
            animationComplete    = !spawnAnimationActive;

            if (spawnAnimationActive)
                RefreshAnimatedMatrices(0f);
            else
                BuildFinalMatrices();

            RebuildDrawCalls(prototypes);
        }

        /// <summary>Backward-compatible overload — no spawn animation or distance culling.</summary>
        public void SetChunk(
            VegetationChunk newChunk,
            VegetationPrototype[] prototypes,
            VegetationSpawnAnimationSettings animation,
            bool playSpawnAnimation) =>
            SetChunk(newChunk, prototypes, animation, playSpawnAnimation,
                VegetationDrawDistanceSettings.Disabled, null);

        /// <summary>Backward-compatible overload — no spawn animation.</summary>
        public void SetChunk(VegetationChunk newChunk, VegetationPrototype[] prototypes) =>
            SetChunk(newChunk, prototypes, default, playSpawnAnimation: false);

        public void Clear()
        {
            chunk = null;
            drawCalls.Clear();
            spawnAnimationActive = false;
            animationComplete    = true;
        }

        private void BuildFinalMatrices()
        {
            if (chunk == null) return;
            foreach (VegetationBatch batch in chunk.batches)
                batch.BuildMatrices();
        }

        private void RefreshAnimatedMatrices(float elapsed)
        {
            if (chunk == null) return;

            float chunkGrowth = animationSettings.mode == VegetationSpawnAnimationMode.ChunkPopIn
                ? VegetationGrowthAnimation.ChunkGrowth(elapsed, animationSettings.chunkPopInDuration)
                : 1f;

            foreach (VegetationBatch batch in chunk.batches)
            {
                batch.BuildMatricesWithGrowth(
                    chunkGrowth,
                    animationSettings.mode,
                    elapsed,
                    animationSettings.perTreeGrowDuration,
                    animationSettings.perTreeMaxStagger);
            }
        }

        private void RebuildDrawCalls(VegetationPrototype[] prototypes)
        {
            drawCalls.Clear();
            if (chunk == null) return;

            foreach (VegetationBatch batch in chunk.batches)
            {
                if (batch.prototype == null || batch.prototype.mesh == null)
                    continue;
                if (batch.prototype.materials == null || batch.prototype.materials.Length == 0)
                    continue;
                if (batch.instances.Count == 0)
                    continue;

                for (int s = 0; s < batch.prototype.materials.Length; s++)
                {
                    Material mat = VegetationInstancingMaterialCache.Get(batch.prototype.materials[s]);
                    if (mat == null) continue;

                    drawCalls.Add(new DrawCall
                    {
                        batch        = batch,
                        mesh         = batch.prototype.mesh,
                        submeshIndex = s,
                        trianglesPerInstance = CountMeshTriangles(batch.prototype.mesh),
                        renderParams = new RenderParams(mat)
                        {
                            worldBounds       = batch.bounds,
                            shadowCastingMode = ShadowCastingMode.On,
                            receiveShadows    = true
                        }
                    });
                }
            }
        }

        private void Update()
        {
            if (chunk == null || drawCalls.Count == 0)
                return;

            VegetationRenderStats.RegisterChunk(TotalInstances);

            Transform camTransform = drawDistanceCamera != null ? drawDistanceCamera : Camera.main?.transform;
            Camera camera = drawDistanceCameraComponent != null ? drawDistanceCameraComponent : Camera.main;

            if (drawDistanceSettings.IsEnabled && camTransform != null)
            {
                float closest = HorizontalDistanceToBoundsXZ(camTransform.position, chunk.worldBounds);
                if (closest >= drawDistanceSettings.maxDrawDistance)
                    return;
            }

            bool animating = spawnAnimationActive && !animationComplete;
            float elapsed  = animating ? Time.time - spawnTime : 0f;

            if (animating && VegetationGrowthAnimation.IsChunkComplete(elapsed, animationSettings))
            {
                animationComplete = true;
                BuildFinalMatrices();
                animating = false;
            }

            float chunkGrowth = animationSettings.mode == VegetationSpawnAnimationMode.ChunkPopIn && animating
                ? VegetationGrowthAnimation.ChunkGrowth(elapsed, animationSettings.chunkPopInDuration)
                : 1f;

            VegetationSpawnAnimationMode growthMode = animating ? animationSettings.mode : VegetationSpawnAnimationMode.None;

            Plane[] frustumPlanes = (chunkFrustumCullEnabled || instanceFrustumCullEnabled)
                ? VegetationFrustumUtility.GetFrustumPlanes(camera)
                : null;

            if (chunkFrustumCullEnabled &&
                !VegetationFrustumUtility.TestBounds(frustumPlanes, chunk.worldBounds))
            {
                VegetationRenderStats.ChunkFrustumCulled(TotalInstances);
                return;
            }

            var cull = new VegetationCullContext
            {
                cameraPosition = camTransform != null ? camTransform.position : Vector3.zero,
                hasCamera      = camTransform != null,
                drawDistance   = drawDistanceSettings,
                frustumPlanes  = instanceFrustumCullEnabled ? frustumPlanes : null
            };

            for (int d = 0; d < drawCalls.Count; d++)
            {
                DrawCall dc = drawCalls[d];
                VegetationBatch batch = dc.batch;

                batch.BuildDrawMatrices(
                    cull,
                    chunkGrowth,
                    growthMode,
                    elapsed,
                    animationSettings.perTreeGrowDuration,
                    animationSettings.perTreeMaxStagger);

                if (batch.drawCount <= 0)
                    continue;

                int trisPerInstance = dc.trianglesPerInstance;
                VegetationRenderStats.RecordDraw(batch.drawCount, trisPerInstance);

                int offset = 0;
                while (offset < batch.drawCount)
                {
                    int count = Mathf.Min(MaxInstancesPerDraw, batch.drawCount - offset);
                    Graphics.RenderMeshInstanced(
                        dc.renderParams,
                        dc.mesh,
                        dc.submeshIndex,
                        batch.drawMatrices,
                        count,
                        offset);
                    offset += count;
                }
            }
        }

        static Camera ResolveCameraComponent(Transform cameraTransform)
        {
            if (cameraTransform == null)
                return null;

            Camera camera = cameraTransform.GetComponent<Camera>();
            return camera != null ? camera : cameraTransform.GetComponentInChildren<Camera>();
        }

        static int CountMeshTriangles(Mesh mesh)
        {
            if (mesh == null)
                return 0;

            long count = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
                count += mesh.GetIndexCount(s);
            return (int)(count / 3);
        }

        internal static float HorizontalDistanceToBoundsXZ(Vector3 worldPosition, Bounds bounds)
        {
            float cx = Mathf.Clamp(worldPosition.x, bounds.min.x, bounds.max.x);
            float cz = Mathf.Clamp(worldPosition.z, bounds.min.z, bounds.max.z);
            float dx = worldPosition.x - cx;
            float dz = worldPosition.z - cz;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private void OnDrawGizmosSelected()
        {
            if (chunk == null) return;
            Gizmos.color = new Color(0.2f, 0.9f, 0.2f, 0.3f);
            Gizmos.DrawWireCube(chunk.worldBounds.center, chunk.worldBounds.size);
        }
    }
}
