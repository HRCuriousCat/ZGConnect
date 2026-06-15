namespace ZGConnect.PhotogrammetryStreaming
{
    public sealed class ZagrebGeographicClipper
    {
        readonly double _south;
        readonly double _west;
        readonly double _north;
        readonly double _east;
        readonly bool _enabled;

        public ZagrebGeographicClipper(PhotogrammetryRegionPreset preset, bool enabled)
        {
            _enabled = enabled;
            preset.GetWgs84Bounds(out _south, out _west, out _north, out _east);
        }

        public bool IsEnabled => _enabled;

        public bool IntersectsRegion(Tile3DNode node)
        {
            if (!_enabled || node?.Bounds == null)
                return true;

            if (node.Bounds is RegionBounds)
                return node.Bounds.IntersectsWgs84Rect(_south, _west, _north, _east);

            var center = Tile3DNodeTransform.GetWorldCenterEcef(node);
            var sphere = new SphereBounds
            {
                Center = center,
                Radius = Tile3DNodeTransform.GetWorldBoundingRadius(node),
            };
            return sphere.IntersectsWgs84Rect(_south, _west, _north, _east);
        }

        public void GetBounds(out double south, out double west, out double north, out double east)
        {
            south = _south;
            west = _west;
            north = _north;
            east = _east;
        }
    }
}
