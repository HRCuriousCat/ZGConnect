using System.Collections.Generic;

namespace ZGConnect.SpatialStreaming
{
    static class SpatialStreamingHudFormatting
    {
        const int LabelWidth = 11;
        const int ColumnWidth = 5;

        public static void AppendLodTable(List<string> lines, in SpatialStreamingStats stats)
        {
            lines.Add(string.Empty);
            lines.Add(FormatHeader());
            AppendRow(lines, "detail", stats.Detail);
            AppendRow(lines, "subcell", stats.SubcellProxy);
            AppendRow(lines, "tile", stats.TileProxy);
            AppendRow(lines, "hlod2x2", stats.Hlod2x2);
            AppendRow(lines, "hlod4x4", stats.Hlod4x4);
            AppendRow(lines, "total", new SpatialStreamingLodBandStats
            {
                Pending = stats.Pending,
                Loading = stats.Loading,
                Loaded = stats.LoadedTotal,
                Shown = stats.VisibleBlocks,
            });
        }

        static string FormatHeader()
        {
            string label = PadLabel(string.Empty);
            return $"{label}{PadColumn("pend")}{PadColumn("load")}{PadColumn("done")}{PadColumn("show")}";
        }

        static void AppendRow(List<string> lines, string label, in SpatialStreamingLodBandStats band)
        {
            lines.Add(
                $"{PadLabel(label)}{PadNumber(band.Pending)}{PadNumber(band.Loading)}" +
                $"{PadNumber(band.Loaded)}{PadNumber(band.Shown)}");
        }

        static string PadLabel(string label) =>
            label.Length >= LabelWidth ? label[..LabelWidth] : label.PadRight(LabelWidth);

        static string PadColumn(string text) =>
            text.Length >= ColumnWidth ? text[..ColumnWidth] : text.PadLeft(ColumnWidth);

        static string PadNumber(int value) => value.ToString().PadLeft(ColumnWidth);
    }
}
