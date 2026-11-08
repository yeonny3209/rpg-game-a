using System.Collections.Generic;
using UnityEngine;

namespace Game.Client.World
{
    /// <summary>Procedural low-poly meshes in the same unit space as Unity primitives (height 2 cylinder, diameter 1 sphere).</summary>
    public static class SimpleMeshes
    {
        public static Mesh Cylinder(int sides)
        {
            var v = new List<Vector3>();
            var t = new List<int>();
            for (int i = 0; i <= sides; i++)
            {
                float a = i / (float)sides * Mathf.PI * 2f;
                float x = Mathf.Cos(a) * 0.5f, z = Mathf.Sin(a) * 0.5f;
                v.Add(new Vector3(x, -1f, z));
                v.Add(new Vector3(x, 1f, z));
            }
            for (int i = 0; i < sides; i++)
            {
                int b = i * 2;
                t.Add(b); t.Add(b + 1); t.Add(b + 2);
                t.Add(b + 2); t.Add(b + 1); t.Add(b + 3);
            }
            // top cap
            int c = v.Count;
            v.Add(new Vector3(0, 1f, 0));
            for (int i = 0; i < sides; i++)
            {
                t.Add(c); t.Add(i * 2 + 3); t.Add(i * 2 + 1);
            }
            var m = new Mesh { name = "LowCylinder" };
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        static Mesh _box, _roof;

        /// <summary>Unit cube centered on the origin, flat-shaded (24 vertices).</summary>
        public static Mesh Box()
        {
            if (_box) return _box;
            var v = new List<Vector3>();
            var t = new List<int>();
            Vector3[] n = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
            foreach (var f in n)
            {
                Vector3 u = Mathf.Abs(f.y) > 0.5f ? Vector3.right : Vector3.up;
                Vector3 r = Vector3.Cross(f, u); // clockwise winding seen from outside
                int b = v.Count;
                v.Add((f - r - u) * 0.5f); v.Add((f - r + u) * 0.5f); v.Add((f + r + u) * 0.5f); v.Add((f + r - u) * 0.5f);
                t.Add(b); t.Add(b + 1); t.Add(b + 2); t.Add(b); t.Add(b + 2); t.Add(b + 3);
            }
            _box = Finish("LowBox", v, t);
            return _box;
        }

        /// <summary>Gable roof: base 1x1 at y=0, ridge along Z at y=1.</summary>
        public static Mesh Roof()
        {
            if (_roof) return _roof;
            var v = new List<Vector3>();
            var t = new List<int>();
            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { int i = v.Count; v.Add(a); v.Add(b); v.Add(c); v.Add(d); t.Add(i); t.Add(i + 1); t.Add(i + 2); t.Add(i); t.Add(i + 2); t.Add(i + 3); }
            void Tri(Vector3 a, Vector3 b, Vector3 c) { int i = v.Count; v.Add(a); v.Add(b); v.Add(c); t.Add(i); t.Add(i + 1); t.Add(i + 2); }
            var l0 = new Vector3(-0.5f, 0, -0.5f); var l1 = new Vector3(-0.5f, 0, 0.5f);
            var r0 = new Vector3(0.5f, 0, -0.5f); var r1 = new Vector3(0.5f, 0, 0.5f);
            var k0 = new Vector3(0, 1, -0.5f); var k1 = new Vector3(0, 1, 0.5f);
            Quad(l0, l1, k1, k0);
            Quad(r1, r0, k0, k1);
            Tri(l0, k0, r0);
            Tri(r1, k1, l1);
            _roof = Finish("LowRoof", v, t);
            return _roof;
        }

        static Mesh Finish(string name, List<Vector3> v, List<int> t)
        {
            var m = new Mesh { name = name };
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        public static Mesh Sphere(int lon, int lat)
        {
            var v = new List<Vector3>();
            var t = new List<int>();
            for (int y = 0; y <= lat; y++)
            {
                float py = y / (float)lat * Mathf.PI;
                for (int x = 0; x <= lon; x++)
                {
                    float px = x / (float)lon * Mathf.PI * 2f;
                    v.Add(new Vector3(Mathf.Sin(py) * Mathf.Cos(px), Mathf.Cos(py), Mathf.Sin(py) * Mathf.Sin(px)) * 0.5f);
                }
            }
            for (int y = 0; y < lat; y++)
            for (int x = 0; x < lon; x++)
            {
                int i = y * (lon + 1) + x;
                t.Add(i); t.Add(i + 1); t.Add(i + lon + 1);
                t.Add(i + 1); t.Add(i + lon + 2); t.Add(i + lon + 1);
            }
            var m = new Mesh { name = "LowSphere" };
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }
}
