using System;
using System.Collections.Generic;
using Game.Shared.Data;

namespace Game.Shared.World
{
    /// <summary>
    /// Ground height of a region map, generated from the numbers in data/shared/maps/*.json.
    /// The client builds its mesh and the server validates movement / places monsters from the SAME function,
    /// so both sides agree on the ground without exporting a heightmap.
    /// When hand-made terrain replaces this (art stage), swap the implementation for a baked heightmap lookup.
    /// </summary>
    public sealed class MapTerrain
    {
        readonly MapDef _map;

        public MapTerrain(MapDef map) { _map = map; }

        public MapDef Map => _map;
        public float HalfSize => _map.sizeMeters * 0.5f;

        public bool InBounds(float x, float z)
        {
            float h = HalfSize;
            return x >= -h && x <= h && z >= -h && z <= h;
        }

        public float Height(float x, float z)
        {
            double hills =
                Math.Sin(x * 0.031) * Math.Cos(z * 0.027) * 0.6 +
                Math.Sin((x + z) * 0.012) * 0.3 +
                Math.Cos((x - z) * 0.045) * 0.1;
            double h = hills * _map.terrain.hillHeight;

            // Press flat areas (village, fields). Each blends back to hills over its own radius.
            double keep = 1.0;
            var flats = _map.terrain.flats;
            if (flats != null)
            {
                for (int i = 0; i < flats.Length; i++)
                {
                    var f = flats[i];
                    double dx = x - f.x, dz = z - f.z;
                    double d = Math.Sqrt(dx * dx + dz * dz);
                    double r = f.radius;
                    if (r <= 0) continue;
                    double t = d <= r ? 0 : Math.Min(1.0, (d - r) / r);
                    t = t * t * (3 - 2 * t); // smoothstep
                    if (t < keep) keep = t;
                }
            }
            return (float)(h * keep);
        }

        /// <summary>Buildings sit on the lowest of these ground samples so no wall floats (client mesh uses the same rule).</summary>
        public float BuildingBase(BuildingDef b)
        {
            return Math.Min(Math.Min(Height(b.x - b.width / 2, b.z - b.depth / 2), Height(b.x + b.width / 2, b.z + b.depth / 2)), Height(b.x, b.z));
        }

        /// <summary>Gable roof height above the walls (0 for low structures without a roof).</summary>
        public static float RoofHeight(BuildingDef b)
        {
            return b.height >= 1.5f ? Math.Min(b.width, b.depth) * 0.45f : 0f;
        }

        /// <summary>
        /// Highest surface a player can stand on at (x, z): the ground or the top of a building (roof peak, lenient).
        /// Used by the server's jump-height check so climbing onto a house is not treated as flying.
        /// </summary>
        public float StandHeight(float x, float z)
        {
            float g = Height(x, z);
            var bs = _map.buildings;
            if (bs == null) return g;
            for (int i = 0; i < bs.Length; i++)
            {
                var b = bs[i];
                double r = b.yaw * Math.PI / 180.0;
                double dx = x - b.x, dz = z - b.z;
                double lx = dx * Math.Cos(r) - dz * Math.Sin(r);
                double lz = dx * Math.Sin(r) + dz * Math.Cos(r);
                const double overhang = 0.4;
                if (Math.Abs(lx) > b.width / 2 + overhang || Math.Abs(lz) > b.depth / 2 + overhang) continue;
                float top = BuildingBase(b) + b.height + RoofHeight(b);
                if (top > g) g = top;
            }
            return g;
        }

        public struct Tree
        {
            public float X, Z, Scale;
        }

        /// <summary>Deterministic tree placement (same on every client).</summary>
        public List<Tree> Trees()
        {
            var cfg = _map.trees;
            var list = new List<Tree>(cfg.count);
            uint s = (uint)cfg.seed;
            if (s == 0) s = 1;
            float h = HalfSize - 4f;
            int guard = 0;
            while (list.Count < cfg.count && guard++ < cfg.count * 20)
            {
                float x = (Next(ref s) * 2f - 1f) * h;
                float z = (Next(ref s) * 2f - 1f) * h;
                float scale = 0.8f + Next(ref s) * 0.7f;
                if (Avoided(x, z)) continue;
                list.Add(new Tree { X = x, Z = z, Scale = scale });
            }
            return list;
        }

        bool Avoided(float x, float z)
        {
            var avoid = _map.trees.avoid;
            if (avoid == null) return false;
            for (int i = 0; i < avoid.Length; i++)
            {
                float dx = x - avoid[i].x, dz = z - avoid[i].z;
                if (dx * dx + dz * dz < avoid[i].radius * avoid[i].radius) return true;
            }
            return false;
        }

        static float Next(ref uint s)
        {
            // xorshift32
            s ^= s << 13;
            s ^= s >> 17;
            s ^= s << 5;
            return (s & 0xFFFFFF) / (float)0x1000000;
        }
    }

    /// <summary>Yaw convention shared by client and server: Unity degrees, 0 = +Z, 90 = +X.</summary>
    public static class Yaw
    {
        public static float FromDirection(float dx, float dz)
        {
            return (float)(Math.Atan2(dx, dz) * 180.0 / Math.PI);
        }

        public static void ToDirection(float yawDeg, out float dx, out float dz)
        {
            double r = yawDeg * Math.PI / 180.0;
            dx = (float)Math.Sin(r);
            dz = (float)Math.Cos(r);
        }

        /// <summary>Smallest absolute difference between two yaws, 0..180.</summary>
        public static float Delta(float a, float b)
        {
            float d = (a - b) % 360f;
            if (d < 0) d += 360f;
            return d > 180f ? 360f - d : d;
        }
    }
}
