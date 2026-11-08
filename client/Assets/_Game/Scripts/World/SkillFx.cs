using System.Collections.Generic;
using Game.Client.Net;
using Game.Shared.Data;
using Game.Shared.Protocol;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Client.World
{
    /// <summary>
    /// Placeholder skill effects built from flat meshes (fans, discs, slabs) until real VFX exist.
    /// Only the look: the server decides what a skill hits, from the same ranges and angles in skills.json.
    /// </summary>
    public static class SkillFx
    {
        static readonly Dictionary<long, Mesh> _fans = new Dictionary<long, Mesh>();

        public static void Play(SkillDef s, Vector3 pos, float yawDeg)
        {
            if (s == null) return;
            Color col = Placeholders.ParseColor(s.color, Color.white);
            switch (s.kind)
            {
                case "melee": Fan(pos, yawDeg, s.range, s.arcDeg, col, 0.22f); break;
                case "circle": Fan(pos, yawDeg, s.range, 360f, col, 0.3f); break;
                case "dash": Slab(pos, yawDeg, s.distance, s.radius * 2f, col, 0.28f); break;
                case "backstep": Slab(pos, yawDeg + 180f, s.distance, 0.7f, col, 0.28f); break;
                case "stance": Fan(pos, yawDeg, 1.6f, 360f, col, Mathf.Clamp(s.duration, 0.4f, 1.5f)); break;
                case "buff": Fan(pos, yawDeg, 1.1f, 360f, col, 0.7f); break;
                case "blink": Slab(pos, yawDeg, s.distance, 0.9f, col, 0.35f); Fan(pos, yawDeg, 1.3f, 360f, col, 0.45f); break;
                case "ground":
                {
                    var at = Ahead(pos, yawDeg, s.distance);
                    Fan(at, 0, s.radius, 360f, new Color(col.r, col.g, col.b), Mathf.Max(0.3f, s.delay));       // warning ring while it winds up
                    Later(Mathf.Max(0.01f, s.delay), () => Fan(at, 0, s.radius, 360f, col, 0.35f));
                    break;
                }
                case "zone":
                {
                    var at = Ahead(pos, yawDeg, s.distance);
                    Fan(at, 0, s.radius, 360f, col, Mathf.Max(0.5f, s.delay + s.ticks * s.tickInterval));
                    break;
                }
                case "chain": Fan(pos, yawDeg, s.range, s.arcDeg, col, 0.25f); break;
                case "shield": Fan(pos, yawDeg, 1.4f, 360f, col, Mathf.Clamp(s.duration, 0.6f, 3f)); break;
                case "taunt": Fan(pos, yawDeg, Mathf.Min(s.range, 10f), 360f, col, 0.5f); break;
                case "partyBuff": Fan(pos, yawDeg, 2.2f, 360f, col, 1.0f); break;
                case "heal": Fan(pos, yawDeg, 1.2f, 360f, col, 0.6f); Slab(pos, yawDeg, Mathf.Min(s.range, 8f), 0.5f, col, 0.35f); break;
                case "groupHeal": Fan(pos, yawDeg, 3.5f, 360f, col, 0.9f); break;
                case "revive": Fan(pos, yawDeg, 2.6f, 360f, col, 1.2f); break;
            }
            if (!string.IsNullOrEmpty(s.fx)) Extra(s, pos, yawDeg, col);
        }

        // ── 마법사 속성별 연출: the same kinds look different per element (spikes, columns, swirls ...) ──

        static void Extra(SkillDef s, Vector3 pos, float yaw, Color col)
        {
            bool ground = s.kind == "ground" || s.kind == "zone";
            Vector3 at = ground ? Ahead(pos, yaw, s.distance) : s.kind == "circle" ? pos : Ahead(pos, yaw, 1.2f);
            float r = ground ? s.radius : s.kind == "circle" ? s.range : 1f;
            float land = ground ? Mathf.Max(0.01f, s.delay) : 0.01f;
            Color bright = Color.Lerp(col, Color.white, 0.45f);
            Color deep = Color.Lerp(col, Color.black, 0.35f);
            switch (s.fx)
            {
                case "column": Later(land, () => Block(at, 0.3f + r * 0.18f, 7f, bright, 0.45f)); break;
                case "strike": Later(land, () => { Block(at + Vector3.up * 0.0f, 0.18f, 10f, bright, 0.25f); Fan(at, 0, r, 360f, col, 0.3f); }); break;
                case "spikes": Later(land, () => Spikes(at, r, col, Mathf.Clamp(Mathf.RoundToInt(r * 4f), 5, 16), 1.6f, 0.9f)); break;
                case "quake": Later(land, () => { Spikes(at, r, deep, 12, 0.7f, 0.5f); Fan(at, 0, r, 360f, bright, 0.25f); }); break;
                case "rockfall": Later(land - 0.1f, () => Block(at + Vector3.up * 0.0f, r * 0.55f, r * 0.9f, deep, 0.5f)); Later(land, () => Spikes(at, r, deep, 8, 0.8f, 0.5f)); break;
                case "burst": Later(land, () => { Fan(at, 0, r, 360f, bright, 0.3f); Fan(at, 0, r * 0.55f, 360f, col, 0.45f); }); break;
                case "pool": Fan(at, 0, r * 0.7f, 360f, deep, Mathf.Max(0.5f, land + s.ticks * s.tickInterval)); Fan(at, 0, r, 360f, col, Mathf.Max(0.5f, land + s.ticks * s.tickInterval)); break;
                case "swirl": for (int i = 0; i < 3; i++) Fan(at, i * 120f, r * (0.5f + 0.25f * i), 240f, i % 2 == 0 ? bright : col, Mathf.Max(0.5f, land + s.ticks * s.tickInterval)); break;
                case "wave": Fan(pos, 0, r, 360f, col, 0.35f); Fan(pos, 0, r * 0.6f, 360f, bright, 0.5f); break;
                case "grasp": Later(land, () => { Spikes(at, r * 0.6f, deep, 7, 2.2f, 0.45f); Block(at, r * 0.35f, 2.8f, Color.black, 0.45f); }); break;
                case "aura": Spikes(pos, 0.9f, bright, 8, 1.1f, Mathf.Clamp(s.duration > 0 ? s.duration : 1f, 0.6f, 3f), 0.12f); break;
                case "blink": Slab(pos, yaw, Mathf.Max(1f, s.distance), 0.5f, bright, 0.3f); Spikes(pos, 0.6f, col, 6, 1.4f, 0.4f, 0.1f); break;
                case "chain": Slab(pos, yaw, Mathf.Min(s.range, 9f), 0.12f, bright, 0.2f); break;
                case "beam": Slab(pos, yaw, Mathf.Min(s.range, 14f), 0.14f, bright, 0.18f); break;
                case "thorn": case "shard": case "blade": case "icicle": case "jet": case "bolt": case "rock": case "orb":
                    Spikes(Ahead(pos, yaw, 0.9f), 0.5f, bright, 4, 0.7f, 0.2f, 0.1f); break;
            }
        }

        static void Block(Vector3 at, float width, float height, Color col, float life)
        {
            Spawn("FxBlock", SimpleMeshes.Box(), Placeholders.FxMat(col), at + Vector3.up * (height * 0.5f), Quaternion.identity, new Vector3(width, height, width), life);
        }

        static void Spikes(Vector3 at, float radius, Color col, int count, float height, float life, float width = 0.2f)
        {
            for (int i = 0; i < count; i++)
            {
                float a = (i + 0.5f * (i % 2)) / count * Mathf.PI * 2f;
                float rr = radius * (0.35f + 0.6f * ((i * 37) % 10) / 10f);
                var p = at + new Vector3(Mathf.Cos(a) * rr, 0, Mathf.Sin(a) * rr);
                float h = height * (0.6f + 0.4f * ((i * 53) % 10) / 10f);
                Spawn("FxSpike", SimpleMeshes.Box(), Placeholders.FxMat(col), p + Vector3.up * (h * 0.5f), Quaternion.Euler(0, a * Mathf.Rad2Deg, ((i * 7) % 5 - 2) * 4f),
                      new Vector3(width, h, width), life);
            }
        }

        static Vector3 Ahead(Vector3 pos, float yawDeg, float dist)
        {
            float r = yawDeg * Mathf.Deg2Rad;
            return pos + new Vector3(Mathf.Sin(r), 0, Mathf.Cos(r)) * dist;
        }

        static void Later(float seconds, System.Action a)
        {
            var go = new GameObject("FxLater");
            go.AddComponent<FxTimer>().Set(seconds, a);
        }

        /// <summary>Element colours by index into elements.json (set when the data arrives); projectile Tier is that index for spells.</summary>
        public static Color[] ElementColors = new Color[0];

        public static void SetElements(SharedData d)
        {
            if (d == null || d.elements == null || d.elements.list == null) return;
            ElementColors = new Color[d.elements.list.Length];
            for (int i = 0; i < ElementColors.Length; i++) ElementColors[i] = Placeholders.ParseColor(d.elements.list[i].color, Color.white);
        }

        static Mesh FanMesh(float range, float arcDeg)
        {
            long key = ((long)Mathf.RoundToInt(range * 10f) << 20) | (long)Mathf.RoundToInt(arcDeg);
            if (_fans.TryGetValue(key, out var m) && m) return m;
            int segs = Mathf.Clamp(Mathf.CeilToInt(arcDeg / 12f), 3, 36);
            var v = new Vector3[segs + 2];
            v[0] = Vector3.zero;
            float start = -arcDeg * 0.5f;
            for (int i = 0; i <= segs; i++)
            {
                float a = (start + arcDeg * i / segs) * Mathf.Deg2Rad;
                v[i + 1] = new Vector3(Mathf.Sin(a) * range, 0, Mathf.Cos(a) * range);
            }
            var t = new List<int>();
            for (int i = 0; i < segs; i++) { t.Add(0); t.Add(i + 2); t.Add(i + 1); t.Add(0); t.Add(i + 1); t.Add(i + 2); } // both sides
            m = new Mesh { name = "SkillFan" };
            m.vertices = v;
            m.triangles = t.ToArray();
            m.RecalculateBounds();
            _fans[key] = m;
            return m;
        }

        static GameObject Spawn(string name, Mesh mesh, Material mat, Vector3 pos, Quaternion rot, Vector3 scale, float life)
        {
            var go = new GameObject(name);
            go.transform.SetPositionAndRotation(pos, rot);
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            go.AddComponent<FxLife>().Life = life;
            return go;
        }

        static void Fan(Vector3 pos, float yawDeg, float range, float arcDeg, Color col, float life)
        {
            Spawn("FxFan", FanMesh(range, arcDeg), Placeholders.FxMat(col), pos + Vector3.up * 0.12f, Quaternion.Euler(0, yawDeg, 0), Vector3.one, life);
        }

        static void Slab(Vector3 pos, float yawDeg, float length, float width, Color col, float life)
        {
            var rot = Quaternion.Euler(0, yawDeg, 0);
            var center = pos + rot * Vector3.forward * (length * 0.5f) + Vector3.up * 0.6f;
            Spawn("FxSlab", SimpleMeshes.Box(), Placeholders.FxMat(col), center, rot, new Vector3(width, 0.06f, length), life);
        }
    }

    public sealed class FxTimer : MonoBehaviour
    {
        float _t; System.Action _a;
        public void Set(float t, System.Action a) { _t = t; _a = a; }
        void Update() { _t -= Time.deltaTime; if (_t <= 0) { var a = _a; _a = null; Destroy(gameObject); a?.Invoke(); } }
    }

    /// <summary>Effect objects shrink away and delete themselves.</summary>
    public sealed class FxLife : MonoBehaviour
    {
        public float Life = 0.25f;
        float _age;
        Vector3 _scale;

        void Start() { _scale = transform.localScale; }

        void Update()
        {
            _age += Time.deltaTime;
            float k = _age / Mathf.Max(0.05f, Life);
            if (k >= 1f) { Destroy(gameObject); return; }
            // Quick flash that thins out (no transparency in the placeholder material).
            transform.localScale = new Vector3(_scale.x * (1f + 0.08f * k), Mathf.Max(0.001f, _scale.y * (1f - k * k)), _scale.z * (1f + 0.08f * k));
        }
    }

    /// <summary>Flies the arrows and sword waves the server announces. Hits are the server's business; this is only the picture.</summary>
    public sealed class ProjectileManager : MonoBehaviour
    {
        sealed class Proj
        {
            public GameObject Go;
            public Vector3 Dir;
            public float Speed, Range, Traveled;
        }

        NetClient _net;
        readonly Dictionary<uint, Proj> _live = new Dictionary<uint, Proj>();
        readonly List<uint> _done = new List<uint>();

        public void Init(NetClient net)
        {
            _net = net;
            _net.OnProjectileSpawn += HandleSpawn;
            _net.OnProjectileEnd += HandleEnd;
        }

        void OnDestroy()
        {
            if (_net != null) { _net.OnProjectileSpawn -= HandleSpawn; _net.OnProjectileEnd -= HandleEnd; }
            foreach (var p in _live.Values) if (p.Go) Destroy(p.Go);
        }

        static readonly string[] TierColors = { "#e8d8a8", "#ffd060", "#ff7a50" };

        void HandleSpawn(ProjectileSpawn s)
        {
            Yaw(s.Yaw, out var dir);
            var go = new GameObject("Projectile " + s.Kind);
            go.transform.SetPositionAndRotation(new Vector3(s.X, s.Y, s.Z), Quaternion.Euler(0, s.Yaw, 0));
            if (s.Kind == ProjectileKind.Arrow)
            {
                var prefab = Resources.Load<GameObject>("Weapons/arrow");
                if (prefab)
                {
                    var arrow = Instantiate(prefab, go.transform);
                    arrow.transform.localPosition = Vector3.zero;
                    arrow.transform.localRotation = Quaternion.Euler(90f, 0, 0);     // model +Y → flight direction
                    var amat = Resources.Load<Material>("Weapons/Mat_arrow");
                    foreach (var ar in arrow.GetComponentsInChildren<Renderer>(true)) { if (amat) ar.sharedMaterial = amat; ar.shadowCastingMode = ShadowCastingMode.Off; }
                    _live[s.ProjectileId] = new Proj { Go = go, Dir = dir, Speed = s.Speed, Range = s.Range };
                    return;
                }
            }
            Vector3 size; Color col;
            if (s.Kind == ProjectileKind.SwordWave) { size = new Vector3(1.8f, 0.05f, 0.5f); col = new Color(0.5f, 0.88f, 1f); }
            else if (s.Kind == ProjectileKind.Magic)
            {
                size = new Vector3(0.38f, 0.38f, 0.38f);
                col = s.Tier == 250 ? new Color(1f, 0.88f, 0.4f) : s.Tier < SkillFx.ElementColors.Length ? SkillFx.ElementColors[s.Tier] : new Color(0.7f, 0.6f, 1f);
            }
            else if (s.Kind == ProjectileKind.Dagger) { size = new Vector3(0.06f, 0.03f, 0.45f); col = new Color(0.82f, 0.85f, 0.95f); }
            else { size = new Vector3(0.07f, 0.07f, 0.9f); col = Placeholders.ParseColor(TierColors[Mathf.Clamp(s.Tier, 0, 2)], Color.white); }
            go.AddComponent<MeshFilter>().sharedMesh = s.Kind == ProjectileKind.Magic ? SimpleMeshes.Sphere(8, 6) : SimpleMeshes.Box();
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = Placeholders.FxMat(col);
            r.shadowCastingMode = ShadowCastingMode.Off;
            go.transform.localScale = size;
            _live[s.ProjectileId] = new Proj { Go = go, Dir = dir, Speed = s.Speed, Range = s.Range };
        }

        void HandleEnd(ProjectileEnd e)
        {
            if (_live.TryGetValue(e.ProjectileId, out var p))
            {
                if (p.Go) Destroy(p.Go);
                _live.Remove(e.ProjectileId);
            }
        }

        static void Yaw(float yawDeg, out Vector3 dir)
        {
            float r = yawDeg * Mathf.Deg2Rad;
            dir = new Vector3(Mathf.Sin(r), 0, Mathf.Cos(r));
        }

        void Update()
        {
            if (_live.Count == 0) return;
            _done.Clear();
            foreach (var kv in _live)
            {
                var p = kv.Value;
                float step = p.Speed * Time.deltaTime;
                p.Traveled += step;
                if (p.Traveled >= p.Range || !p.Go) { _done.Add(kv.Key); continue; }
                p.Go.transform.position += p.Dir * step;
            }
            foreach (var id in _done)
            {
                if (_live[id].Go) Destroy(_live[id].Go);
                _live.Remove(id);
            }
        }
    }
}
