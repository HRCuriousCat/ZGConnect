using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ZGConnect.PhotogrammetryStreaming
{
    public static class PhotogrammetryAttributionCollector
    {
        public static string ExtractCopyrightFromGlb(byte[] glbBytes)
        {
            if (glbBytes == null || glbBytes.Length < 20)
                return null;

            int offset = 12;
            while (offset + 8 <= glbBytes.Length)
            {
                int chunkLength = System.BitConverter.ToInt32(glbBytes, offset);
                int chunkType = System.BitConverter.ToInt32(glbBytes, offset + 4);
                offset += 8;
                if (offset + chunkLength > glbBytes.Length)
                    break;

                if (chunkType == 0x4E4F534A) // JSON
                {
                    string json = System.Text.Encoding.UTF8.GetString(glbBytes, offset, chunkLength);
                    try
                    {
                        var asset = JObject.Parse(json)["asset"];
                        return asset?["copyright"]?.ToObject<string>();
                    }
                    catch
                    {
                        return null;
                    }
                }

                offset += chunkLength;
            }

            return null;
        }

        public static string Aggregate(IEnumerable<string> copyrights)
        {
            var counts = new Dictionary<string, int>();
            foreach (string raw in copyrights)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                foreach (string part in raw.Split(';'))
                {
                    string t = part.Trim();
                    if (t.Length == 0)
                        continue;
                    counts.TryGetValue(t, out int c);
                    counts[t] = c + 1;
                }
            }

            if (counts.Count == 0)
                return "Google Maps";

            return string.Join("; ", counts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key));
        }
    }
}
