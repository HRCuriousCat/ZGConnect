using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ZGConnect.PhotogrammetryStreaming
{
    static class JsonTokenParse
    {
        public static double Double(JToken token) => token == null ? 0.0 : token.ToObject<double>();
        public static string String(JToken token) => token?.ToObject<string>();
    }

    public sealed class Tile3DNode
    {
        public string Id;
        public double GeometricError;
        public BoundingVolume Bounds;
        public string ContentUri;
        public List<Tile3DNode> Children = new();
        public int Depth;
        public Tile3DNode Parent;
        public Matrix4d LocalTransform = Matrix4d.Identity;
        public Matrix4d TransformToEcef = Matrix4d.Identity;
    }

    public abstract class BoundingVolume
    {
        public abstract bool IntersectsWgs84Rect(double south, double west, double north, double east);
        public abstract Vector3d GetCenterEcef();
        public abstract double GetBoundingRadius();
    }

    public sealed class RegionBounds : BoundingVolume
    {
        public double WestRad;
        public double SouthRad;
        public double EastRad;
        public double NorthRad;
        public double MinHeight;
        public double MaxHeight;

        public static RegionBounds Parse(JArray arr)
        {
            if (arr == null || arr.Count < 6)
                return null;
            return new RegionBounds
            {
                WestRad = JsonTokenParse.Double(arr[0]),
                SouthRad = JsonTokenParse.Double(arr[1]),
                EastRad = JsonTokenParse.Double(arr[2]),
                NorthRad = JsonTokenParse.Double(arr[3]),
                MinHeight = JsonTokenParse.Double(arr[4]),
                MaxHeight = JsonTokenParse.Double(arr[5]),
            };
        }

        public override bool IntersectsWgs84Rect(double south, double west, double north, double east)
        {
            double s = GeodeticMath.Rad2Deg(SouthRad);
            double w = GeodeticMath.Rad2Deg(WestRad);
            double n = GeodeticMath.Rad2Deg(NorthRad);
            double e = GeodeticMath.Rad2Deg(EastRad);
            return GeodeticMath.Wgs84RectsOverlap(s, w, n, e, south, west, north, east);
        }

        public override Vector3d GetCenterEcef()
        {
            double lat = (SouthRad + NorthRad) * 0.5;
            double lon = (WestRad + EastRad) * 0.5;
            double h = (MinHeight + MaxHeight) * 0.5;
            return GeodeticMath.Wgs84ToEcef(GeodeticMath.Rad2Deg(lat), GeodeticMath.Rad2Deg(lon), h);
        }

        public override double GetBoundingRadius()
        {
            var c = GetCenterEcef();
            var corner = GeodeticMath.Wgs84ToEcef(GeodeticMath.Rad2Deg(NorthRad), GeodeticMath.Rad2Deg(EastRad), MaxHeight);
            return (corner - c).Magnitude;
        }
    }

    public sealed class SphereBounds : BoundingVolume
    {
        public Vector3d Center;
        public double Radius;

        public static SphereBounds Parse(JArray arr)
        {
            if (arr == null || arr.Count < 4)
                return null;
            return new SphereBounds
            {
                Center = new Vector3d(JsonTokenParse.Double(arr[0]), JsonTokenParse.Double(arr[1]), JsonTokenParse.Double(arr[2])),
                Radius = JsonTokenParse.Double(arr[3]),
            };
        }

        public override bool IntersectsWgs84Rect(double south, double west, double north, double east)
        {
            GeodeticMath.EcefToWgs84(Center, out double centerLat, out double centerLon, out _);
            if (centerLat >= south && centerLat <= north && centerLon >= west && centerLon <= east)
                return true;

            double latC = (south + north) * 0.5;
            double lonC = (west + east) * 0.5;
            var rectCenter = GeodeticMath.Wgs84ToEcef(latC, lonC, 0);
            if ((rectCenter - Center).Magnitude <= Radius + 2500.0)
                return true;

            for (int i = 0; i <= 4; i++)
            {
                for (int j = 0; j <= 4; j++)
                {
                    double lat = south + (north - south) * i / 4.0;
                    double lon = west + (east - west) * j / 4.0;
                    var p = GeodeticMath.Wgs84ToEcef(lat, lon, 0);
                    if ((p - Center).Magnitude <= Radius + 2500.0)
                        return true;
                }
            }
            return false;
        }

        public override Vector3d GetCenterEcef() => Center;
        public override double GetBoundingRadius() => Radius;
    }

    public sealed class BoxBounds : BoundingVolume
    {
        public Vector3d Center;
        public Vector3d HalfAxis0;
        public Vector3d HalfAxis1;
        public Vector3d HalfAxis2;

        public static BoxBounds Parse(JArray arr)
        {
            if (arr == null || arr.Count < 12)
                return null;
            return new BoxBounds
            {
                Center = new Vector3d(JsonTokenParse.Double(arr[0]), JsonTokenParse.Double(arr[1]), JsonTokenParse.Double(arr[2])),
                HalfAxis0 = new Vector3d(JsonTokenParse.Double(arr[3]), JsonTokenParse.Double(arr[4]), JsonTokenParse.Double(arr[5])),
                HalfAxis1 = new Vector3d(JsonTokenParse.Double(arr[6]), JsonTokenParse.Double(arr[7]), JsonTokenParse.Double(arr[8])),
                HalfAxis2 = new Vector3d(JsonTokenParse.Double(arr[9]), JsonTokenParse.Double(arr[10]), JsonTokenParse.Double(arr[11])),
            };
        }

        public override bool IntersectsWgs84Rect(double south, double west, double north, double east)
        {
            double r = Math.Sqrt(
                HalfAxis0.Magnitude * HalfAxis0.Magnitude +
                HalfAxis1.Magnitude * HalfAxis1.Magnitude +
                HalfAxis2.Magnitude * HalfAxis2.Magnitude);
            var sb = new SphereBounds { Center = Center, Radius = r };
            return sb.IntersectsWgs84Rect(south, west, north, east);
        }

        public override Vector3d GetCenterEcef() => Center;
        public override double GetBoundingRadius()
        {
            return Math.Sqrt(
                HalfAxis0.Magnitude * HalfAxis0.Magnitude +
                HalfAxis1.Magnitude * HalfAxis1.Magnitude +
                HalfAxis2.Magnitude * HalfAxis2.Magnitude);
        }
    }

    public static class Tile3DParser
    {
        public static Tile3DNode ParseTileset(string json, string baseUrl = null) =>
            ParseTileset(json, baseUrl, out _);

        public static Tile3DNode ParseTileset(string json, string baseUrl, out Matrix4d tilesetRootTransform)
        {
            tilesetRootTransform = Matrix4d.Identity;
            var root = JObject.Parse(json);
            double rootError = root.Value<double?>("geometricError") ?? 0;
            var rootTile = root["root"] as JObject;
            if (rootTile == null)
                return null;
            var rootNode = ParseNode(rootTile, rootError, baseUrl, null, 0, "root");
            tilesetRootTransform = ParseTransform(root["transform"]);
            Tile3DNodeTransform.RecomputeTransformChain(rootNode, tilesetRootTransform);
            return rootNode;
        }

        public static void RecomputeTransforms(Tile3DNode root, Matrix4d tilesetRootTransform) =>
            Tile3DNodeTransform.RecomputeTransformChain(root, tilesetRootTransform);

        static Tile3DNode ParseNode(JObject obj, double inheritedError, string baseUrl, Tile3DNode parent, int depth, string id)
        {
            var node = new Tile3DNode
            {
                Id = id,
                Depth = depth,
                Parent = parent,
                GeometricError = obj.Value<double?>("geometricError") ?? inheritedError,
                LocalTransform = ParseTransform(obj["transform"]),
            };

            if (obj["boundingVolume"] is JObject bv)
                node.Bounds = ParseBoundingVolume(bv);

            if (obj["content"] is JObject content)
                node.ContentUri = ResolveUri(JsonTokenParse.String(content["uri"]), baseUrl);
            else if (obj["content"] is JValue cv)
                node.ContentUri = ResolveUri(JsonTokenParse.String(cv), baseUrl);

            if (obj["children"] is JArray children)
            {
                for (int i = 0; i < children.Count; i++)
                {
                    if (children[i] is JObject childObj)
                    {
                        var child = ParseNode(childObj, node.GeometricError, baseUrl, node, depth + 1, $"{id}/{i}");
                        node.Children.Add(child);
                    }
                }
            }

            return node;
        }

        static Matrix4d ParseTransform(JToken token)
        {
            if (token is not JArray arr || arr.Count < 16)
                return Matrix4d.Identity;

            var m = new double[16];
            for (int i = 0; i < 16; i++)
                m[i] = JsonTokenParse.Double(arr[i]);
            return new Matrix4d(m);
        }

        static BoundingVolume ParseBoundingVolume(JObject bv)
        {
            if (bv["region"] is JArray region)
                return RegionBounds.Parse(region);
            if (bv["sphere"] is JArray sphere)
                return SphereBounds.Parse(sphere);
            if (bv["box"] is JArray box)
                return BoxBounds.Parse(box);
            return null;
        }

        static string ResolveUri(string uri, string baseUrl)
        {
            if (string.IsNullOrEmpty(uri))
                return uri;
            if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return uri;
            if (uri.StartsWith("/"))
                return "https://tile.googleapis.com" + uri;
            if (!string.IsNullOrEmpty(baseUrl))
            {
                string cleanBase = StripQueryAndFragment(baseUrl);
                int slash = cleanBase.LastIndexOf('/');
                string prefix = slash >= 0 ? cleanBase.Substring(0, slash + 1) : cleanBase;
                return prefix + uri;
            }
            return uri;
        }

        static string StripQueryAndFragment(string url)
        {
            if (string.IsNullOrEmpty(url))
                return url;
            int q = url.IndexOf('?', StringComparison.Ordinal);
            if (q >= 0)
                url = url.Substring(0, q);
            int hash = url.IndexOf('#');
            if (hash >= 0)
                url = url.Substring(0, hash);
            return url;
        }
    }
}
