using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using ZGConnect;
using ZGConnect.RealtimeStreaming;

namespace ZGConnect.RealtimeStreaming.Editor
{
    public static class BuildingMetadataBinaryWriter
    {
        sealed class StringTable
        {
            readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
            readonly List<string> _strings = new();

            public int Add(string value)
            {
                value ??= string.Empty;
                if (_ids.TryGetValue(value, out int existing))
                    return existing;

                int id = _strings.Count;
                _strings.Add(value);
                _ids[value] = id;
                return id;
            }

            public IReadOnlyList<string> Strings => _strings;
        }

        public static bool TryConvertJsonFileToBytes(string jsonPath, string bytesPath, out string error)
        {
            error = null;

            if (string.IsNullOrEmpty(jsonPath) || !File.Exists(jsonPath))
            {
                error = $"JSON not found: {jsonPath}";
                return false;
            }

            try
            {
                string jsonText = File.ReadAllText(jsonPath);
                BuildingsMetadataJson metadata =
                    JsonConvert.DeserializeObject<BuildingsMetadataJson>(jsonText);

                if (metadata?.Buildings == null || metadata.Buildings.Count == 0)
                {
                    error = $"No buildings in metadata: {jsonPath}";
                    return false;
                }

                byte[] bytes = BuildBytes(metadata, ComputeSourceHash(jsonText));
                string directory = Path.GetDirectoryName(bytesPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllBytes(bytesPath, bytes);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static byte[] BuildBytes(BuildingsMetadataJson metadata, uint sourceJsonHash = 0)
        {
            if (metadata?.Buildings == null)
                throw new ArgumentException("Metadata has no buildings.");

            var stringTable = new StringTable();
            int tileIdId = stringTable.Add(metadata.TileId ?? string.Empty);

            foreach (BuildingEntryJson entry in metadata.Buildings)
                RegisterEntryStrings(stringTable, entry);

            var buildingBlobs = new List<byte[]>(metadata.Buildings.Count);
            foreach (BuildingEntryJson entry in metadata.Buildings)
                buildingBlobs.Add(WriteBuildingBlob(stringTable, entry));

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

            writer.Write(BuildingMetadataBinaryFormat.Magic);
            writer.Write((ushort)BuildingMetadataBinaryFormat.Version);
            writer.Write((ushort)0); // flags
            writer.Write(metadata.Buildings.Count);
            writer.Write(metadata.TileSizeMeters);
            writer.Write(metadata.TileOriginUnity?.X ?? 0f);
            writer.Write(metadata.TileOriginUnity?.Y ?? 0f);
            writer.Write(metadata.TileOriginUnity?.Z ?? 0f);
            writer.Write(tileIdId);
            writer.Write(0); // stringTableOffset placeholder
            writer.Write(0); // indexOffset placeholder
            writer.Write(0); // dataOffset placeholder
            writer.Write(sourceJsonHash);

            int stringTableOffset = (int)stream.Position;
            WriteStringTable(writer, stringTable.Strings);

            int indexOffset = (int)stream.Position;
            int dataOffset = indexOffset + metadata.Buildings.Count * BuildingMetadataBinaryFormat.IndexEntrySize;
            int runningDataOffset = 0;
            for (int i = 0; i < metadata.Buildings.Count; i++)
            {
                BuildingEntryJson entry = metadata.Buildings[i];
                int nameId = stringTable.Add(entry?.Name ?? string.Empty);
                byte[] blob = buildingBlobs[i];
                writer.Write(nameId);
                writer.Write(runningDataOffset);
                writer.Write(blob.Length);
                writer.Write(0);
                runningDataOffset += blob.Length;
            }

            int dataSectionOffset = (int)stream.Position;
            for (int i = 0; i < buildingBlobs.Count; i++)
                writer.Write(buildingBlobs[i]);

            byte[] fileBytes = stream.ToArray();
            WriteInt32(fileBytes, 32, stringTableOffset);
            WriteInt32(fileBytes, 36, indexOffset);
            WriteInt32(fileBytes, 40, dataSectionOffset);
            return fileBytes;
        }

        static void RegisterEntryStrings(StringTable table, BuildingEntryJson entry)
        {
            if (entry == null)
                return;

            table.Add(entry.Name);
            if (entry.Osm == null)
                return;

            table.Add(entry.Osm.CoordSource);
            RegisterOsmMatchStrings(table, entry.Osm.Building);
            if (entry.Osm.AtPoint == null)
                return;

            foreach (BuildingOsmAtPointJson feature in entry.Osm.AtPoint)
                RegisterAtPointStrings(table, feature);
        }

        static void RegisterOsmMatchStrings(StringTable table, BuildingOsmMatchJson match)
        {
            if (match == null)
                return;

            table.Add(match.OsmType);
            table.Add(match.Role);
            table.Add(match.Label);
            table.Add(match.MatchMethod);
            RegisterTagStrings(table, match.Tags);
        }

        static void RegisterAtPointStrings(StringTable table, BuildingOsmAtPointJson feature)
        {
            if (feature == null)
                return;

            table.Add(feature.OsmType);
            table.Add(feature.Role);
            table.Add(feature.Label);
            RegisterTagStrings(table, feature.Tags);
        }

        static void RegisterTagStrings(StringTable table, Dictionary<string, string> tags)
        {
            if (tags == null)
                return;

            foreach (KeyValuePair<string, string> pair in tags)
            {
                table.Add(pair.Key);
                table.Add(pair.Value);
            }
        }

        static byte[] WriteBuildingBlob(StringTable table, BuildingEntryJson entry)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

            WriteVec3(writer, entry?.LocalPosition);
            WriteVec3(writer, entry?.LocalRotation);
            WriteVec3(writer, entry?.LocalScale);

            bool hasGps = entry?.Gps != null;
            writer.Write((byte)(hasGps ? 1 : 0));
            writer.Write(new byte[7]);
            if (hasGps)
            {
                writer.Write(entry.Gps.Lat);
                writer.Write(entry.Gps.Lon);
            }

            bool hasOsm = entry?.Osm != null;
            writer.Write((byte)(hasOsm ? 1 : 0));
            if (hasOsm)
                WriteOsmBlock(writer, table, entry.Osm);

            return stream.ToArray();
        }

        static void WriteOsmBlock(BinaryWriter writer, StringTable table, BuildingOsmBlockJson block)
        {
            writer.Write(table.Add(block.CoordSource));
            writer.Write(block.QueryLat);
            writer.Write(block.QueryLon);

            bool hasMatch = block.Building != null;
            writer.Write((byte)(hasMatch ? 1 : 0));
            if (hasMatch)
            {
                BuildingOsmMatchJson match = block.Building;
                writer.Write(table.Add(match.OsmType));
                writer.Write(match.OsmId);
                writer.Write(table.Add(match.Role));
                writer.Write(table.Add(match.Label));
                writer.Write(table.Add(match.MatchMethod));
                writer.Write(match.MatchScore);
                writer.Write(match.DistanceM);
                WriteTagDictionary(writer, table, match.Tags);
            }

            ushort atPointCount = (ushort)(block.AtPoint?.Count ?? 0);
            writer.Write(atPointCount);
            if (atPointCount == 0)
                return;

            foreach (BuildingOsmAtPointJson feature in block.AtPoint)
            {
                writer.Write(table.Add(feature.OsmType));
                writer.Write(feature.OsmId);
                writer.Write(table.Add(feature.Role));
                writer.Write(table.Add(feature.Label));
                writer.Write(feature.AreaM2);
                WriteTagDictionary(writer, table, feature.Tags);
            }
        }

        static void WriteTagDictionary(BinaryWriter writer, StringTable table, Dictionary<string, string> tags)
        {
            if (tags == null || tags.Count == 0)
            {
                writer.Write((ushort)0);
                return;
            }

            writer.Write((ushort)tags.Count);
            foreach (KeyValuePair<string, string> pair in tags)
            {
                writer.Write(table.Add(pair.Key));
                writer.Write(table.Add(pair.Value ?? string.Empty));
            }
        }

        static void WriteVec3(BinaryWriter writer, TilePositionJson vec)
        {
            writer.Write(vec?.X ?? 0f);
            writer.Write(vec?.Y ?? 0f);
            writer.Write(vec?.Z ?? 0f);
        }

        static void WriteStringTable(BinaryWriter writer, IReadOnlyList<string> strings)
        {
            writer.Write(strings.Count);
            foreach (string value in strings)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
                writer.Write(bytes.Length);
                if (bytes.Length > 0)
                    writer.Write(bytes);
            }
        }

        static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        public static uint ComputeSourceHash(string jsonText)
        {
            if (string.IsNullOrEmpty(jsonText))
                return 0;

            const uint offsetBasis = 2166136261;
            const uint prime = 16777619;
            uint hash = offsetBasis;
            byte[] bytes = Encoding.UTF8.GetBytes(jsonText);
            foreach (byte b in bytes)
            {
                hash ^= b;
                hash *= prime;
            }

            return hash;
        }
    }
}
