using System;
using System.Text;
using Newtonsoft.Json.Linq;

namespace ZGConnect.PhotogrammetryStreaming
{
    static class GltfGlbParser
    {
        const double MinEcefMagnitude = 1_000_000.0;
        const int GlbMagic = 0x46546C67;
        const int JsonChunkType = 0x4E4F534A;
        const int BinChunkType = 0x004E4942;

        public static bool TryReadJsonChunk(byte[] glbBytes, out string json)
        {
            json = null;
            if (glbBytes == null || glbBytes.Length < 20)
                return false;

            int offset = 12;
            while (offset + 8 <= glbBytes.Length)
            {
                int chunkLength = System.BitConverter.ToInt32(glbBytes, offset);
                int chunkType = System.BitConverter.ToInt32(glbBytes, offset + 4);
                offset += 8;
                if (offset + chunkLength > glbBytes.Length)
                    break;

                if (chunkType == 0x4E4F534A)
                {
                    json = System.Text.Encoding.UTF8.GetString(glbBytes, offset, chunkLength);
                    return true;
                }

                offset += chunkLength;
            }

            return false;
        }

        public static Vector3d ParseRtcCenterEcef(string json)
        {
            if (string.IsNullOrEmpty(json))
                return Vector3d.Zero;

            try
            {
                var root = JObject.Parse(json);
                var extras = root["extras"] as JObject;
                var rtc = extras?["rtcCenter"] as JArray
                          ?? extras?["RTC_CENTER"] as JArray;
                if (rtc != null && rtc.Count >= 3)
                {
                    return new Vector3d(
                        JsonDouble(rtc[0]),
                        JsonDouble(rtc[1]),
                        JsonDouble(rtc[2]));
                }
            }
            catch { /* ignore */ }

            return Vector3d.Zero;
        }

        public static Matrix4d ParseSceneRootNodeMatrix(string json)
        {
            if (string.IsNullOrEmpty(json))
                return Matrix4d.Identity;

            try
            {
                var root = JObject.Parse(json);
                var scenes = root["scenes"] as JArray;
                if (scenes == null || scenes.Count == 0)
                    return Matrix4d.Identity;

                int sceneIndex = root.Value<int?>("scene") ?? 0;
                if (sceneIndex < 0 || sceneIndex >= scenes.Count)
                    sceneIndex = 0;

                var scene = scenes[sceneIndex] as JObject;
                var sceneNodes = scene?["nodes"] as JArray;
                if (sceneNodes == null || sceneNodes.Count == 0)
                    return Matrix4d.Identity;

                var nodes = root["nodes"] as JArray;
                if (nodes == null)
                    return Matrix4d.Identity;

                int rootNodeIndex = sceneNodes[0].Value<int>();
                if (rootNodeIndex < 0 || rootNodeIndex >= nodes.Count)
                    return Matrix4d.Identity;

                return ParseNodeMatrix(nodes[rootNodeIndex] as JObject);
            }
            catch
            {
                return Matrix4d.Identity;
            }
        }

        public static bool HasLargeEcefTranslation(Matrix4d matrix)
        {
            var t = GetTranslation(matrix);
            return t.Magnitude >= MinEcefMagnitude;
        }

        public static Vector3d GetTranslation(Matrix4d matrix) =>
            new(matrix.At(0, 3), matrix.At(1, 3), matrix.At(2, 3));

        static Matrix4d ParseNodeMatrix(JObject node)
        {
            if (node == null)
                return Matrix4d.Identity;

            if (node["matrix"] is JArray matrix && matrix.Count >= 16)
            {
                var m = new double[16];
                for (int i = 0; i < 16; i++)
                    m[i] = JsonDouble(matrix[i]);
                return new Matrix4d(m);
            }

            var translation = ReadVec3(node["translation"]);
            var rotation = ReadQuat(node["rotation"]);
            var scale = ReadVec3(node["scale"], 1.0, 1.0, 1.0);
            return ComposeTrs(translation, rotation, scale);
        }

        static Matrix4d ComposeTrs(Vector3d t, (double x, double y, double z, double w) q, Vector3d s)
        {
            double x = q.x, y = q.y, z = q.z, w = q.w;
            double xx = x * x, yy = y * y, zz = z * z;
            double xy = x * y, xz = x * z, yz = y * z;
            double wx = w * x, wy = w * y, wz = w * z;

            return new Matrix4d(new double[]
            {
                (1 - 2 * (yy + zz)) * s.x, (2 * (xy + wz)) * s.x, (2 * (xz - wy)) * s.x, 0,
                (2 * (xy - wz)) * s.y, (1 - 2 * (xx + zz)) * s.y, (2 * (yz + wx)) * s.y, 0,
                (2 * (xz + wy)) * s.z, (2 * (yz - wx)) * s.z, (1 - 2 * (xx + yy)) * s.z, 0,
                t.x, t.y, t.z, 1
            });
        }

        static Vector3d ReadVec3(JToken token, double dx = 0, double dy = 0, double dz = 0)
        {
            if (token is not JArray arr || arr.Count < 3)
                return new Vector3d(dx, dy, dz);
            return new Vector3d(JsonDouble(arr[0]), JsonDouble(arr[1]), JsonDouble(arr[2]));
        }

        static (double x, double y, double z, double w) ReadQuat(JToken token)
        {
            if (token is not JArray arr || arr.Count < 4)
                return (0, 0, 0, 1);
            return (JsonDouble(arr[0]), JsonDouble(arr[1]), JsonDouble(arr[2]), JsonDouble(arr[3]));
        }

        static double JsonDouble(JToken token) =>
            token == null ? 0.0 : token.ToObject<double>();

        public static bool IsNearIdentity(Matrix4d matrix, double epsilon = 1e-6)
        {
            for (int col = 0; col < 4; col++)
            {
                for (int row = 0; row < 4; row++)
                {
                    double expected = row == col ? 1.0 : 0.0;
                    if (Math.Abs(matrix.At(row, col) - expected) > epsilon)
                        return false;
                }
            }
            return true;
        }

        /// <summary>Rotation + scale from a 4x4, with translation column zeroed (ECEF translation belongs on Content pivot).</summary>
        public static Matrix4d RotationScaleOnly(Matrix4d matrix) => WithoutTranslation(matrix);

        /// <summary>
        /// Clears scene root node TRS so glTFast does not re-apply transforms folded into the Content pivot.
        /// </summary>
        public static bool TryNeutralizeSceneRootNode(byte[] glbBytes, out byte[] patchedGlb)
        {
            patchedGlb = null;
            if (!TryReadJsonChunk(glbBytes, out string json))
                return false;

            try
            {
                var gltf = JObject.Parse(json);
                if (!TryGetSceneRootNode(gltf, out JObject rootNode))
                    return false;

                if (IsNearIdentity(ParseNodeMatrix(rootNode)))
                    return false;

                rootNode.Remove("matrix");
                rootNode.Remove("translation");
                rootNode.Remove("rotation");
                rootNode.Remove("scale");

                string newJson = gltf.ToString(Newtonsoft.Json.Formatting.None);
                patchedGlb = RebuildGlbWithJson(glbBytes, newJson);
                return patchedGlb != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Removes absolute ECEF translation from the scene root node. RTC placement stays on the Content pivot;
        /// the root keeps rotation/scale so mesh orientation is unchanged.
        /// </summary>
        public static bool TryStripSceneRootEcefTranslation(byte[] glbBytes, out byte[] patchedGlb)
        {
            patchedGlb = null;
            if (!TryReadJsonChunk(glbBytes, out string json))
                return false;

            try
            {
                var gltf = JObject.Parse(json);
                if (!TryGetSceneRootNode(gltf, out JObject rootNode))
                    return false;

                if (!TryStripNodeEcefTranslation(rootNode))
                    return false;

                string newJson = gltf.ToString(Newtonsoft.Json.Formatting.None);
                patchedGlb = RebuildGlbWithJson(glbBytes, newJson);
                return patchedGlb != null;
            }
            catch
            {
                return false;
            }
        }

        static bool TryGetSceneRootNode(JObject gltf, out JObject rootNode)
        {
            rootNode = null;
            var scenes = gltf["scenes"] as JArray;
            var nodes = gltf["nodes"] as JArray;
            if (scenes == null || scenes.Count == 0 || nodes == null || nodes.Count == 0)
                return false;

            int sceneIndex = gltf.Value<int?>("scene") ?? 0;
            if (sceneIndex < 0 || sceneIndex >= scenes.Count)
                sceneIndex = 0;

            var sceneNodes = (scenes[sceneIndex] as JObject)?["nodes"] as JArray;
            if (sceneNodes == null || sceneNodes.Count == 0)
                return false;

            int rootNodeIndex = sceneNodes[0].Value<int>();
            if (rootNodeIndex < 0 || rootNodeIndex >= nodes.Count)
                return false;

            rootNode = nodes[rootNodeIndex] as JObject;
            return rootNode != null;
        }

        static bool TryStripNodeEcefTranslation(JObject node)
        {
            if (node["matrix"] is JArray matrix && matrix.Count >= 16)
            {
                var m = new double[16];
                for (int i = 0; i < 16; i++)
                    m[i] = JsonDouble(matrix[i]);

                var parsed = new Matrix4d(m);
                if (!HasLargeEcefTranslation(parsed))
                    return false;

                Matrix4d stripped = WithoutTranslation(parsed);
                node["matrix"] = new JArray(MatrixToGltfColumnMajor(stripped));
                return true;
            }

            if (node["translation"] is JArray translation && translation.Count >= 3)
            {
                var t = new Vector3d(
                    JsonDouble(translation[0]),
                    JsonDouble(translation[1]),
                    JsonDouble(translation[2]));
                if (t.Magnitude < MinEcefMagnitude)
                    return false;

                node.Remove("translation");
                return true;
            }

            return false;
        }

        static Matrix4d WithoutTranslation(Matrix4d matrix) => new(new double[]
        {
            matrix.At(0, 0), matrix.At(0, 1), matrix.At(0, 2), 0,
            matrix.At(1, 0), matrix.At(1, 1), matrix.At(1, 2), 0,
            matrix.At(2, 0), matrix.At(2, 1), matrix.At(2, 2), 0,
            0, 0, 0, 1
        });

        static double[] MatrixToGltfColumnMajor(Matrix4d matrix)
        {
            var values = new double[16];
            for (int i = 0; i < 16; i++)
                values[i] = matrix.At(i % 4, i / 4);
            return values;
        }

        static byte[] RebuildGlbWithJson(byte[] original, string newJson)
        {
            byte[] binChunk = null;
            int offset = 12;
            while (offset + 8 <= original.Length)
            {
                int chunkLength = BitConverter.ToInt32(original, offset);
                int chunkType = BitConverter.ToInt32(original, offset + 4);
                offset += 8;
                if (offset + chunkLength > original.Length)
                    break;

                if (chunkType == BinChunkType)
                {
                    binChunk = new byte[chunkLength];
                    Buffer.BlockCopy(original, offset, binChunk, 0, chunkLength);
                }

                offset += chunkLength;
            }

            int jsonPad = (4 - (newJson.Length % 4)) % 4;
            byte[] jsonBytes = Encoding.UTF8.GetBytes(newJson + new string(' ', jsonPad));

            int totalLength = 12 + 8 + jsonBytes.Length;
            if (binChunk != null)
                totalLength += 8 + binChunk.Length;

            var result = new byte[totalLength];
            int write = 0;

            WriteInt32(result, ref write, GlbMagic);
            WriteInt32(result, ref write, 2);
            WriteInt32(result, ref write, totalLength);

            WriteInt32(result, ref write, jsonBytes.Length);
            WriteInt32(result, ref write, JsonChunkType);
            Buffer.BlockCopy(jsonBytes, 0, result, write, jsonBytes.Length);
            write += jsonBytes.Length;

            if (binChunk != null)
            {
                WriteInt32(result, ref write, binChunk.Length);
                WriteInt32(result, ref write, BinChunkType);
                Buffer.BlockCopy(binChunk, 0, result, write, binChunk.Length);
            }

            return result;
        }

        static void WriteInt32(byte[] buffer, ref int offset, int value)
        {
            buffer[offset++] = (byte)value;
            buffer[offset++] = (byte)(value >> 8);
            buffer[offset++] = (byte)(value >> 16);
            buffer[offset++] = (byte)(value >> 24);
        }
    }
}
