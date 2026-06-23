using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect.Roads
{
    public sealed class RoadHeightSampler
    {
        readonly Terrain _terrain;
        readonly float _surfaceOffset;

        public RoadHeightSampler(Terrain terrain, float surfaceOffset = 0.05f)
        {
            _terrain = terrain;
            _surfaceOffset = surfaceOffset;
        }

        public float Sample(Vector3 world)
        {
            if (_terrain == null)
                return world.y + _surfaceOffset;

            return _terrain.SampleHeight(world) + _terrain.transform.position.y + _surfaceOffset;
        }

        public void SmoothRing(List<Vector3> points, int iterations, float maxSlope)
        {
            if (points == null || points.Count < 3 || iterations <= 0)
                return;

            for (int iter = 0; iter < iterations; iter++)
            {
                var copy = new List<Vector3>(points);
                for (int i = 0; i < points.Count; i++)
                {
                    Vector3 prev = copy[(i - 1 + copy.Count) % copy.Count];
                    Vector3 current = copy[i];
                    Vector3 next = copy[(i + 1) % copy.Count];
                    current.y = prev.y * 0.25f + current.y * 0.5f + next.y * 0.25f;
                    points[i] = current;
                }
            }

            if (maxSlope > 0f)
                ClampSlope(points, maxSlope);
        }

        static void ClampSlope(List<Vector3> points, float maxSlope)
        {
            for (int pass = 0; pass < 4; pass++)
            {
                for (int i = 0; i < points.Count; i++)
                {
                    int j = (i + 1) % points.Count;
                    Vector3 a = points[i];
                    Vector3 b = points[j];
                    float horizontal = Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
                    if (horizontal <= 0.001f)
                        continue;

                    float maxDelta = horizontal * maxSlope;
                    float dy = b.y - a.y;
                    if (Mathf.Abs(dy) <= maxDelta)
                        continue;

                    float mid = (a.y + b.y) * 0.5f;
                    float sign = Mathf.Sign(dy);
                    a.y = mid - maxDelta * 0.5f * sign;
                    b.y = mid + maxDelta * 0.5f * sign;
                    points[i] = a;
                    points[j] = b;
                }
            }
        }
    }
}
