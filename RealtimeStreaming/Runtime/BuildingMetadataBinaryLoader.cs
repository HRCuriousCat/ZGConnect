using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using ZGConnect;

namespace ZGConnect.RealtimeStreaming
{
    public static class BuildingMetadataBinaryLoader
    {
        public static bool TryLoadFromFile(string bytesPath, out BuildingsMetadataJson metadata, out string error)
        {
            metadata = null;
            error = null;

            if (string.IsNullOrEmpty(bytesPath) || !File.Exists(bytesPath))
            {
                error = $"Binary metadata not found: {bytesPath}";
                return false;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(bytesPath);
                return TryLoadFromBytes(bytes, out metadata, out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool TryLoadFromBytes(byte[] bytes, out BuildingsMetadataJson metadata, out string error)
        {
            metadata = null;
            error = null;

            if (bytes == null || bytes.Length < BuildingMetadataBinaryFormat.HeaderSize)
            {
                error = "Binary metadata file is too small.";
                return false;
            }

            try
            {
                if (!TryOpenTileIndexFromBytes(bytes, out BuildingTileMetadataIndex index, out error))
                    return false;

                metadata = new BuildingsMetadataJson
                {
                    TileId = index.TileId,
                    BuildingCount = index.Entries.Length,
                    Buildings = new List<BuildingEntryJson>(index.Entries.Length),
                };

                for (int i = 0; i < index.Entries.Length; i++)
                {
                    string name = ResolveString(index.Strings, index.Entries[i].NameStringId);
                    if (!TryLoadBuildingEntry(index, name, out BuildingEntryJson entry, out string entryError))
                        throw new InvalidDataException(entryError);

                    metadata.Buildings.Add(entry);
                }

                return metadata.Buildings.Count > 0;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool TryOpenTileIndexFromFile(string bytesPath, out BuildingTileMetadataIndex index, out string error)
        {
            index = null;
            error = null;

            if (string.IsNullOrEmpty(bytesPath) || !File.Exists(bytesPath))
            {
                error = $"Binary metadata not found: {bytesPath}";
                return false;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(bytesPath);
                return TryOpenTileIndexFromBytes(bytes, out index, out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool TryOpenTileIndexFromBytes(byte[] bytes, out BuildingTileMetadataIndex index, out string error)
        {
            index = null;
            error = null;

            if (bytes == null || bytes.Length < BuildingMetadataBinaryFormat.HeaderSize)
            {
                error = "Binary metadata file is too small.";
                return false;
            }

            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
                index = ReadTileIndex(reader, bytes);
                return index?.Entries != null && index.Entries.Length > 0;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool TryLoadBuildingEntry(
            BuildingTileMetadataIndex index,
            string buildingName,
            out BuildingEntryJson entry,
            out string error)
        {
            entry = null;
            error = null;

            if (index?.FileBytes == null || index.Entries == null)
            {
                error = "Building metadata index is not initialized.";
                return false;
            }

            if (!index.TryGetEntryIndex(buildingName, out int entryIndex))
            {
                error = $"Building '{buildingName}' not found in metadata index.";
                return false;
            }

            BuildingMetadataIndexEntry indexed = index.Entries[entryIndex];
            int blobStart = index.DataOffset + indexed.DataRelOffset;
            if (blobStart < 0 || indexed.DataLength < 0 ||
                blobStart + indexed.DataLength > index.FileBytes.Length)
            {
                error = $"Corrupt metadata blob for building '{buildingName}'.";
                return false;
            }

            string resolvedName = ResolveString(index.Strings, indexed.NameStringId);
            byte[] blob = new byte[indexed.DataLength];
            Buffer.BlockCopy(index.FileBytes, blobStart, blob, 0, indexed.DataLength);
            entry = ReadBuildingEntry(blob, index.Strings, resolvedName);
            return entry != null;
        }

        static BuildingTileMetadataIndex ReadTileIndex(BinaryReader reader, byte[] fileBytes)
        {
            int fileLength = fileBytes.Length;
            byte[] magic = reader.ReadBytes(4);
            for (int i = 0; i < BuildingMetadataBinaryFormat.Magic.Length; i++)
            {
                if (magic[i] != BuildingMetadataBinaryFormat.Magic[i])
                    throw new InvalidDataException("Invalid building metadata binary magic.");
            }

            ushort version = reader.ReadUInt16();
            if (version != BuildingMetadataBinaryFormat.Version)
                throw new InvalidDataException($"Unsupported building metadata binary version: {version}");

            reader.ReadUInt16(); // flags
            int buildingCount = reader.ReadInt32();
            reader.ReadInt32(); // tileSizeMeters
            reader.ReadSingle();
            reader.ReadSingle();
            reader.ReadSingle();
            int tileIdStringId = reader.ReadInt32();
            int stringTableOffset = reader.ReadInt32();
            int indexOffset = reader.ReadInt32();
            int dataOffset = reader.ReadInt32();
            reader.ReadUInt32(); // sourceJsonHash

            if (buildingCount < 0 || stringTableOffset < 0 || indexOffset < 0 || dataOffset < 0)
                throw new InvalidDataException("Corrupt building metadata binary header.");

            string[] strings = ReadStringTable(reader, fileLength, stringTableOffset);
            var entries = new BuildingMetadataIndexEntry[buildingCount];
            reader.BaseStream.Position = indexOffset;
            for (int i = 0; i < buildingCount; i++)
            {
                entries[i] = new BuildingMetadataIndexEntry(
                    reader.ReadInt32(),
                    reader.ReadInt32(),
                    reader.ReadInt32());
                reader.ReadInt32(); // reserved
            }

            return new BuildingTileMetadataIndex
            {
                TileId = ResolveString(strings, tileIdStringId),
                FileBytes = fileBytes,
                Strings = strings,
                DataOffset = dataOffset,
                Entries = entries,
            };
        }

        static string[] ReadStringTable(BinaryReader reader, int fileLength, int stringTableOffset)
        {
            reader.BaseStream.Position = stringTableOffset;
            int stringCount = reader.ReadInt32();
            if (stringCount < 0)
                throw new InvalidDataException("Corrupt string table.");

            var strings = new string[stringCount];
            for (int i = 0; i < stringCount; i++)
            {
                int byteLength = reader.ReadInt32();
                if (byteLength < 0 || reader.BaseStream.Position + byteLength > fileLength)
                    throw new InvalidDataException($"Corrupt string table entry {i}.");

                strings[i] = byteLength == 0
                    ? string.Empty
                    : Encoding.UTF8.GetString(reader.ReadBytes(byteLength));
            }

            return strings;
        }

        static BuildingEntryJson ReadBuildingEntry(byte[] blob, string[] strings, string name)
        {
            using var stream = new MemoryStream(blob, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            var entry = new BuildingEntryJson
            {
                Name = name,
                LocalPosition = ReadVec3(reader),
                LocalRotation = ReadVec3(reader),
                LocalScale = ReadVec3(reader),
            };

            byte hasGps = reader.ReadByte();
            reader.ReadBytes(7);
            if (hasGps != 0)
            {
                entry.Gps = new BuildingGpsJson
                {
                    Lat = reader.ReadDouble(),
                    Lon = reader.ReadDouble(),
                };
            }

            byte hasOsm = reader.ReadByte();
            if (hasOsm != 0)
                entry.Osm = ReadOsmBlock(reader, strings);

            return entry;
        }

        static BuildingOsmBlockJson ReadOsmBlock(BinaryReader reader, string[] strings)
        {
            var block = new BuildingOsmBlockJson
            {
                CoordSource = ResolveString(reader, strings),
                QueryLat = reader.ReadDouble(),
                QueryLon = reader.ReadDouble(),
            };

            byte hasMatch = reader.ReadByte();
            if (hasMatch != 0)
            {
                block.Building = new BuildingOsmMatchJson
                {
                    OsmType = ResolveString(reader, strings),
                    OsmId = reader.ReadInt64(),
                    Role = ResolveString(reader, strings),
                    Label = ResolveString(reader, strings),
                    MatchMethod = ResolveString(reader, strings),
                    MatchScore = reader.ReadSingle(),
                    DistanceM = reader.ReadSingle(),
                    Tags = ReadTagDictionary(reader, strings),
                };
            }

            ushort atPointCount = reader.ReadUInt16();
            if (atPointCount > 0)
            {
                block.AtPoint = new List<BuildingOsmAtPointJson>(atPointCount);
                for (int i = 0; i < atPointCount; i++)
                {
                    block.AtPoint.Add(new BuildingOsmAtPointJson
                    {
                        OsmType = ResolveString(reader, strings),
                        OsmId = reader.ReadInt64(),
                        Role = ResolveString(reader, strings),
                        Label = ResolveString(reader, strings),
                        AreaM2 = reader.ReadSingle(),
                        Tags = ReadTagDictionary(reader, strings),
                    });
                }
            }

            return block;
        }

        static Dictionary<string, string> ReadTagDictionary(BinaryReader reader, string[] strings)
        {
            ushort tagCount = reader.ReadUInt16();
            if (tagCount == 0)
                return null;

            var tags = new Dictionary<string, string>(tagCount, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < tagCount; i++)
            {
                string key = ResolveString(reader, strings);
                string value = ResolveString(reader, strings);
                if (!string.IsNullOrEmpty(key) && !tags.ContainsKey(key))
                    tags[key] = value ?? string.Empty;
            }

            return tags;
        }

        static TilePositionJson ReadVec3(BinaryReader reader) =>
            new()
            {
                X = reader.ReadSingle(),
                Y = reader.ReadSingle(),
                Z = reader.ReadSingle(),
            };

        static string ResolveString(BinaryReader reader, string[] strings)
        {
            int id = reader.ReadInt32();
            return ResolveString(strings, id);
        }

        static string ResolveString(string[] strings, int id)
        {
            if (strings == null || id < 0 || id >= strings.Length)
                return string.Empty;

            return strings[id] ?? string.Empty;
        }
    }
}
