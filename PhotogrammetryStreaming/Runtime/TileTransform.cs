using System;

namespace ZGConnect.PhotogrammetryStreaming
{
    public readonly struct Matrix4d
    {
        readonly double[] _m;

        public Matrix4d(double[] columnMajor16)
        {
            _m = columnMajor16 ?? throw new ArgumentNullException(nameof(columnMajor16));
            if (_m.Length < 16)
                throw new ArgumentException("Expected 16 column-major matrix elements.", nameof(columnMajor16));
        }

        public static Matrix4d Identity => new(new double[]
        {
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1
        });

        public double At(int row, int col) => _m[(col * 4) + row];

        public Vector3d TransformPoint(Vector3d p) => new(
            At(0, 0) * p.x + At(0, 1) * p.y + At(0, 2) * p.z + At(0, 3),
            At(1, 0) * p.x + At(1, 1) * p.y + At(1, 2) * p.z + At(1, 3),
            At(2, 0) * p.x + At(2, 1) * p.y + At(2, 2) * p.z + At(2, 3));

        public Vector3d TransformVector(Vector3d v) => new(
            At(0, 0) * v.x + At(0, 1) * v.y + At(0, 2) * v.z,
            At(1, 0) * v.x + At(1, 1) * v.y + At(1, 2) * v.z,
            At(2, 0) * v.x + At(2, 1) * v.y + At(2, 2) * v.z);

        public double MaxScale()
        {
            double s0 = Math.Sqrt(At(0, 0) * At(0, 0) + At(1, 0) * At(1, 0) + At(2, 0) * At(2, 0));
            double s1 = Math.Sqrt(At(0, 1) * At(0, 1) + At(1, 1) * At(1, 1) + At(2, 1) * At(2, 1));
            double s2 = Math.Sqrt(At(0, 2) * At(0, 2) + At(1, 2) * At(1, 2) + At(2, 2) * At(2, 2));
            return Math.Max(s0, Math.Max(s1, s2));
        }

        public static Matrix4d Multiply(Matrix4d a, Matrix4d b)
        {
            var r = new double[16];
            for (int col = 0; col < 4; col++)
            {
                for (int row = 0; row < 4; row++)
                {
                    double sum = 0;
                    for (int k = 0; k < 4; k++)
                        sum += a.At(row, k) * b.At(k, col);
                    r[(col * 4) + row] = sum;
                }
            }
            return new Matrix4d(r);
        }

        public static Matrix4d Translation(Vector3d t) => new(new double[]
        {
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            t.x, t.y, t.z, 1
        });

        /// <summary>3D Tiles glTF Y-up to Z-up (+90° about X), matching cesium-native Transforms::Y_UP_TO_Z_UP.</summary>
        public static Matrix4d YUpToZUp => new(new double[]
        {
            1, 0, 0, 0,
            0, 0, 1, 0,
            0, -1, 0, 0,
            0, 0, 0, 1
        });
    }

    public static class Tile3DNodeTransform
    {
        public static Vector3d GetWorldCenterEcef(Tile3DNode node)
        {
            if (node?.Bounds == null)
                return Vector3d.Zero;
            if (node.Bounds is RegionBounds)
                return node.Bounds.GetCenterEcef();
            return node.TransformToEcef.TransformPoint(node.Bounds.GetCenterEcef());
        }

        public static double GetWorldBoundingRadius(Tile3DNode node)
        {
            if (node?.Bounds == null)
                return 0;

            if (node.Bounds is RegionBounds)
                return node.Bounds.GetBoundingRadius();

            if (node.Bounds is BoxBounds box)
            {
                var h0 = node.TransformToEcef.TransformVector(box.HalfAxis0);
                var h1 = node.TransformToEcef.TransformVector(box.HalfAxis1);
                var h2 = node.TransformToEcef.TransformVector(box.HalfAxis2);
                return Math.Sqrt(
                    h0.Magnitude * h0.Magnitude +
                    h1.Magnitude * h1.Magnitude +
                    h2.Magnitude * h2.Magnitude);
            }

            return node.Bounds.GetBoundingRadius() * Math.Max(node.TransformToEcef.MaxScale(), 1.0);
        }

        public static void RecomputeTransformChain(Tile3DNode node, Matrix4d parentToEcef)
        {
            if (node == null)
                return;

            node.TransformToEcef = Matrix4d.Multiply(parentToEcef, node.LocalTransform);
            foreach (var child in node.Children)
                RecomputeTransformChain(child, node.TransformToEcef);
        }

        public static bool EnclosesWorldEcef(Tile3DNode node, Vector3d ecef, double toleranceM = 2000.0)
        {
            if (node?.Bounds == null)
                return true;

            if (node.Bounds is RegionBounds region)
            {
                GeodeticMath.EcefToWgs84(ecef, out double lat, out double lon, out _);
                double south = GeodeticMath.Rad2Deg(region.SouthRad);
                double west = GeodeticMath.Rad2Deg(region.WestRad);
                double north = GeodeticMath.Rad2Deg(region.NorthRad);
                double east = GeodeticMath.Rad2Deg(region.EastRad);
                return lat >= south && lat <= north && lon >= west && lon <= east;
            }

            var center = GetWorldCenterEcef(node);
            double radius = GetWorldBoundingRadius(node);
            return (center - ecef).Magnitude <= radius + toleranceM;
        }
    }
}
