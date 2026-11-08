using System.Collections.Generic;
using Game.Shared.Data;
using Game.Shared.World;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Client.World
{
    /// <summary>
    /// Builds a region from its map table (data/shared/maps/*.json) with placeholder shapes:
    /// chunked terrain (same height function the server validates with), merged trees, box buildings,
    /// and a fence ring marking the start area. Everything static is merged per chunk to keep draw calls low on Iris Xe.
    /// </summary>
    public static class VillageBuilder
    {
        const int CellsPerChunk = 32;

        public struct Stats { public int Chunks, Trees, Triangles, Buildings; }

        public static GameObject Build(MapDef map, out Stats stats)
        {
            stats = new Stats();
            var terrain = new MapTerrain(map);
            var root = new GameObject("World " + map.id);

            float step = Mathf.Max(0.5f, map.terrain.gridStep);
            int cells = Mathf.CeilToInt(map.sizeMeters / step);
            float half = map.sizeMeters * 0.5f;
            int chunksPerSide = Mathf.CeilToInt(cells / (float)CellsPerChunk);

            var trees = terrain.Trees();
            var buckets = new Dictionary<int, List<MapTerrain.Tree>>();
            foreach (var t in trees)
            {
                int cx = Mathf.Clamp(Mathf.FloorToInt((t.X + half) / step / CellsPerChunk), 0, chunksPerSide - 1);
                int cz = Mathf.Clamp(Mathf.FloorToInt((t.Z + half) / step / CellsPerChunk), 0, chunksPerSide - 1);
                int key = cz * chunksPerSide + cx;
                if (!buckets.TryGetValue(key, out var l)) buckets[key] = l = new List<MapTerrain.Tree>();
                l.Add(t);
            }

            var cylMesh = SimpleMeshes.Cylinder(8);
            var sphMesh = SimpleMeshes.Sphere(10, 7);

            for (int cz = 0; cz < chunksPerSide; cz++)
            for (int cx = 0; cx < chunksPerSide; cx++)
            {
                int x0 = cx * CellsPerChunk, z0 = cz * CellsPerChunk;
                int nx = Mathf.Min(CellsPerChunk, cells - x0), nz = Mathf.Min(CellsPerChunk, cells - z0);
                if (nx <= 0 || nz <= 0) continue;

                var chunk = new GameObject($"Chunk_{cx}_{cz}");
                chunk.transform.SetParent(root.transform, false);
                chunk.isStatic = true;
                var mesh = TerrainChunk(terrain, -half + x0 * step, -half + z0 * step, nx, nz, step);
                chunk.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = chunk.AddComponent<MeshRenderer>();
                mr.sharedMaterial = Placeholders.Ground;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                chunk.AddComponent<MeshCollider>().sharedMesh = mesh;
                stats.Chunks++;
                stats.Triangles += nx * nz * 2;

                if (buckets.TryGetValue(cz * chunksPerSide + cx, out var list))
                    BuildTrees(chunk.transform, terrain, list, cylMesh, sphMesh, ref stats);
            }

            BuildBuildings(root.transform, terrain, map.buildings, ref stats);
            BuildFence(root.transform, terrain, map.startArea);
            return root;
        }

        static void BuildTrees(Transform parent, MapTerrain terrain, List<MapTerrain.Tree> list, Mesh cyl, Mesh sph, ref Stats stats)
        {
            var trunks = new List<CombineInstance>(list.Count);
            var leaves = new List<CombineInstance>(list.Count);
            foreach (var t in list)
            {
                float y = terrain.Height(t.X, t.Z);
                float h = 3.2f * t.Scale;
                trunks.Add(new CombineInstance { mesh = cyl, transform = Matrix4x4.TRS(new Vector3(t.X, y + h * 0.5f, t.Z), Quaternion.identity, new Vector3(0.35f * t.Scale, h * 0.5f, 0.35f * t.Scale)) });
                leaves.Add(new CombineInstance { mesh = sph, transform = Matrix4x4.TRS(new Vector3(t.X, y + h + 0.6f * t.Scale, t.Z), Quaternion.identity, Vector3.one * 2.6f * t.Scale) });
                var col = new GameObject("TreeCollider");
                col.transform.SetParent(parent, false);
                col.transform.position = new Vector3(t.X, y + h * 0.5f, t.Z);
                var cc = col.AddComponent<CapsuleCollider>();
                cc.radius = 0.2f * t.Scale;
                cc.height = h;
                stats.Trees++;
            }
            Merged(parent, "Trunks", trunks, Placeholders.Trunk);
            Merged(parent, "Leaves", leaves, Placeholders.Leaves);
        }

        static void BuildBuildings(Transform parent, MapTerrain terrain, BuildingDef[] defs, ref Stats stats)
        {
            if (defs == null) return;
            var root = new GameObject("Buildings");
            root.transform.SetParent(parent, false);
            var cube = SimpleMeshes.Box();
            var roofMesh = SimpleMeshes.Roof();
            foreach (var b in defs)
            {
                // Same base / roof numbers the server uses for its rooftop height check (MapTerrain.StandHeight).
                float y = terrain.BuildingBase(b);
                var go = new GameObject(b.name);
                go.transform.SetParent(root.transform, false);
                go.transform.SetPositionAndRotation(new Vector3(b.x, y, b.z), Quaternion.Euler(0, b.yaw, 0));
                go.isStatic = true;

                var wall = Part(go.transform, "Walls", cube, Placeholders.Tinted(b.wallColor));
                wall.transform.localPosition = new Vector3(0, b.height / 2, 0);
                wall.transform.localScale = new Vector3(b.width, b.height, b.depth);
                wall.AddComponent<BoxCollider>();

                float rh = MapTerrain.RoofHeight(b);
                if (rh > 0)
                {
                    var roof = Part(go.transform, "Roof", roofMesh, Placeholders.Tinted(b.roofColor));
                    roof.transform.localPosition = new Vector3(0, b.height, 0);
                    roof.transform.localScale = new Vector3(b.width + 0.6f, rh, b.depth + 0.6f);
                    roof.AddComponent<MeshCollider>(); // stand on the roof, not inside it
                    // Door on the +Z face.
                    var door = Part(go.transform, "Door", cube, Placeholders.Tinted("#4a3424"));
                    door.transform.localPosition = new Vector3(0, 0.95f, b.depth / 2 + 0.02f);
                    door.transform.localScale = new Vector3(1.1f, 1.9f, 0.06f);
                }
                stats.Buildings++;
            }
        }

        /// <summary>Low posts around the start area so the "이장이 막는" boundary is visible.</summary>
        static void BuildFence(Transform parent, MapTerrain terrain, CircleDef area)
        {
            if (area == null || area.radius <= 0) return;
            var cube = SimpleMeshes.Box();
            var parts = new List<CombineInstance>();
            int posts = Mathf.CeilToInt(2 * Mathf.PI * area.radius / 3f);
            for (int i = 0; i < posts; i++)
            {
                float a = i * Mathf.PI * 2 / posts;
                float x = area.x + Mathf.Cos(a) * area.radius, z = area.z + Mathf.Sin(a) * area.radius;
                float y = terrain.Height(x, z);
                parts.Add(new CombineInstance { mesh = cube, transform = Matrix4x4.TRS(new Vector3(x, y + 0.5f, z), Quaternion.Euler(0, -a * Mathf.Rad2Deg, 0), new Vector3(0.18f, 1.0f, 0.18f)) });
                // Rail to the next post.
                float a2 = (i + 0.5f) * Mathf.PI * 2 / posts;
                float mx = area.x + Mathf.Cos(a2) * area.radius, mz = area.z + Mathf.Sin(a2) * area.radius;
                float len = 2 * Mathf.PI * area.radius / posts;
                parts.Add(new CombineInstance { mesh = cube, transform = Matrix4x4.TRS(new Vector3(mx, terrain.Height(mx, mz) + 0.7f, mz), Quaternion.Euler(0, -a2 * Mathf.Rad2Deg, 0), new Vector3(0.08f, 0.08f, len)) });
            }
            var go = Merged(parent, "StartAreaFence", parts, Placeholders.Tinted("#7b5a3a"));
            go.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
        }

        static GameObject Part(Transform parent, string name, Mesh mesh, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.isStatic = true;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        static GameObject Merged(Transform parent, string name, List<CombineInstance> parts, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.isStatic = true;
            var mesh = new Mesh { indexFormat = IndexFormat.UInt32, name = name };
            mesh.CombineMeshes(parts.ToArray(), true, true);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        static Mesh TerrainChunk(MapTerrain terrain, float ox, float oz, int nx, int nz, float step)
        {
            var verts = new Vector3[(nx + 1) * (nz + 1)];
            var uvs = new Vector2[verts.Length];
            for (int z = 0; z <= nz; z++)
            for (int x = 0; x <= nx; x++)
            {
                float wx = ox + x * step, wz = oz + z * step;
                verts[z * (nx + 1) + x] = new Vector3(wx, terrain.Height(wx, wz), wz);
                uvs[z * (nx + 1) + x] = new Vector2(wx * 0.25f, wz * 0.25f);
            }
            var tris = new int[nx * nz * 6];
            int ti = 0;
            for (int z = 0; z < nz; z++)
            for (int x = 0; x < nx; x++)
            {
                int i = z * (nx + 1) + x;
                tris[ti++] = i; tris[ti++] = i + nx + 1; tris[ti++] = i + 1;
                tris[ti++] = i + 1; tris[ti++] = i + nx + 1; tris[ti++] = i + nx + 2;
            }
            var mesh = new Mesh { name = "TerrainChunk" };
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
