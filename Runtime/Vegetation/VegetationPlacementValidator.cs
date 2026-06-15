using UnityEngine;

namespace ZGConnect
{
    public class VegetationPlacementValidator
    {
        private readonly Texture2D mask;
        private readonly Rect tileWorldRect;

        public VegetationPlacementValidator(Texture2D vegetationMask, Rect worldRect)
        {
            mask = vegetationMask;
            tileWorldRect = worldRect;
        }

        public bool IsValid(Vector3 worldPosition, VegetationRule rule)
        {
            if (mask == null)
                return true;

                float u = (worldPosition.x - tileWorldRect.x) / tileWorldRect.width;
                float v = (worldPosition.z - tileWorldRect.y) / tileWorldRect.height;
                Color sample = mask.GetPixelBilinear(u, v);
            return sample.a <= rule.exclusionThreshold;
        }
    }
}
