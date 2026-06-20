namespace ZGConnect.SpatialStreaming
{
    public static class SpatialTileIdUtility
    {
        public static bool TryParse(string tileId, out int left, out int bottom)
        {
            left = 0;
            bottom = 0;
            if (string.IsNullOrEmpty(tileId))
                return false;

            string[] parts = tileId.Split('_');
            return parts.Length == 2
                   && int.TryParse(parts[0], out left)
                   && int.TryParse(parts[1], out bottom);
        }

        public static string Format(int left, int bottom) => $"{left}_{bottom}";

        public static void ToGridIndices(int left, int bottom, int tileSizeMeters, out int gridX, out int gridZ)
        {
            int size = tileSizeMeters > 0 ? tileSizeMeters : 1000;
            gridX = left / size;
            gridZ = bottom / size;
        }

        public static int AlignDown(int value, int alignCells) =>
            alignCells <= 1 ? value : value - (value % alignCells);

        public static int AlignDownMeters(int epsgMeters, int alignMeters)
        {
            if (alignMeters <= 1)
                return epsgMeters;
            return epsgMeters - (epsgMeters % alignMeters);
        }

        public static int AlignUp(int value, int alignCells)
        {
            if (alignCells <= 1)
                return value;

            int rem = value % alignCells;
            return rem == 0 ? value : value + (alignCells - rem);
        }
    }
}
