using UnityEngine;

namespace ZGConnect
{
    public static class BuildingInteractionLayers
    {
        public const string HighlightLayerName = "BuildingHighlight";

        static int _highlightLayer = -1;

        public static int HighlightLayer
        {
            get
            {
                if (_highlightLayer < 0)
                    _highlightLayer = LayerMask.NameToLayer(HighlightLayerName);
                return _highlightLayer;
            }
        }

        public static int HighlightLayerMask => 1 << HighlightLayer;

        public static bool IsValid => HighlightLayer >= 0;
    }
}

