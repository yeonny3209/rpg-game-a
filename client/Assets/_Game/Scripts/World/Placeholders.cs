using System.Collections.Generic;
using Game.Shared.Data;
using Game.Shared.Protocol;
using UnityEngine;

namespace Game.Client.World
{
    /// <summary>
    /// Placeholder visuals built from boxes / spheres / capsules until real models exist (spec vol.5: 리소스는 나중에 교체).
    /// Materials live in Resources/Materials so the URP shader is included in WebGL builds — they are created
    /// by the menu "RPG > 1. 프로젝트 설정 적용". Colored parts clone the white "Tint" material once per color.
    /// </summary>
    public static class Placeholders
    {
        static readonly Dictionary<string, Material> _cache = new Dictionary<string, Material>();
        static readonly Dictionary<string, Material> _tints = new Dictionary<string, Material>();
        static Font _font;

        static Material Load(string name)
        {
            if (_cache.TryGetValue(name, out var c) && c) return c;
            var m = Resources.Load<Material>("Materials/" + name);
            if (m == null) Debug.LogError($"Missing material Resources/Materials/{name}. Run menu RPG > 1. 프로젝트 설정 적용.");
            _cache[name] = m;
            return m;
        }

        public static Material Ground => Load("Ground");
        public static Material Trunk => Load("Trunk");
        public static Material Leaves => Load("Leaves");
        public static Material Local => Load("Local");
        public static Material Remote => Load("Remote");
        public static Material Wing => Load("Wing");
        public static Material Dark => Load("Nose");

        /// <summary>Noto Sans KR (OFL) subset with all 11,172 Hangul syllables. The built-in font has no Korean glyphs in WebGL.</summary>
        public static Font Font
        {
            get
            {
                if (_font) return _font;
                _font = Resources.Load<Font>("Fonts/NotoSansKR-Regular");
                if (_font == null)
                {
                    Debug.LogError("Missing Resources/Fonts/NotoSansKR-Regular — Korean text will not render.");
                    _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                }
                return _font;
            }
        }

        public static Color ParseColor(string hex, Color fallback)
        {
            return !string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex, out var c) ? c : fallback;
        }

        public static Material Tinted(string hex)
        {
            if (string.IsNullOrEmpty(hex)) hex = "#ffffff";
            if (_tints.TryGetValue(hex, out var m) && m) return m;
            var baseMat = Load("Tint");
            m = baseMat ? new Material(baseMat) : new Material(Shader.Find("Universal Render Pipeline/Lit"));
            m.name = "Tint " + hex;
            m.SetColor("_BaseColor", ParseColor(hex, Color.white));
            _tints[hex] = m;
            return m;
        }

        public static Material Tinted(Color c) { return Tinted("#" + ColorUtility.ToHtmlStringRGB(c)); }

        static readonly Dictionary<string, Material> _fx = new Dictionary<string, Material>();

        /// <summary>Flat unlit color for skill effects and projectiles (Resources/Materials/Fx, cloned per color).</summary>
        public static Material FxMat(Color c)
        {
            string key = ColorUtility.ToHtmlStringRGB(c);
            if (_fx.TryGetValue(key, out var m) && m) return m;
            var baseMat = Load("Fx");
            m = baseMat ? new Material(baseMat) : new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            m.name = "Fx " + key;
            m.SetColor("_BaseColor", c);
            _fx[key] = m;
            return m;
        }

        // ───────────── creatures ─────────────

        /// <summary>Player / NPC / monster body from its visual table entry.</summary>
        public static CreatureView CreateCreature(string name, VisualDef visual, Material bodyOverride = null)
        {
            if (visual != null && !string.IsNullOrEmpty(visual.model))
            {
                var modeled = CreateModelCreature(name, visual);
                if (modeled) return modeled;
            }
            var root = new GameObject(name);
            var view = root.AddComponent<CreatureView>();
            var model = new GameObject("Model");
            model.transform.SetParent(root.transform, false);
            view.Model = model.transform;

            string shape = visual != null ? visual.shape : "humanoid";
            float scale = visual != null && visual.scale > 0 ? visual.scale : 1f;
            var mat = bodyOverride ? bodyOverride : Tinted(visual != null ? visual.color : "#ffffff");
            var box = SimpleMeshes.Box();
            var sph = SimpleMeshes.Sphere(10, 7);

            switch (shape)
            {
                case "rabbit":
                {
                    Part(model.transform, sph, mat, new Vector3(0, 0.25f, 0), new Vector3(0.45f, 0.4f, 0.6f));
                    Part(model.transform, sph, mat, new Vector3(0, 0.45f, 0.28f), new Vector3(0.3f, 0.3f, 0.3f));
                    Part(model.transform, box, mat, new Vector3(-0.07f, 0.68f, 0.24f), new Vector3(0.06f, 0.25f, 0.1f));
                    Part(model.transform, box, mat, new Vector3(0.07f, 0.68f, 0.24f), new Vector3(0.06f, 0.25f, 0.1f));
                    Part(model.transform, sph, Tinted("#ffffff"), new Vector3(0, 0.3f, -0.3f), new Vector3(0.15f, 0.15f, 0.15f));
                    Part(model.transform, box, Dark, new Vector3(0, 0.48f, 0.43f), new Vector3(0.06f, 0.05f, 0.04f));
                    view.Height = 0.8f;
                    break;
                }
                case "wolf":
                {
                    var dark = Tinted(Color.Lerp(ParseColor(visual.color, Color.gray), Color.black, 0.35f));
                    Part(model.transform, box, mat, new Vector3(0, 0.75f, 0), new Vector3(0.5f, 0.5f, 1.2f));
                    Part(model.transform, box, mat, new Vector3(0, 0.95f, 0.75f), new Vector3(0.4f, 0.4f, 0.45f));
                    Part(model.transform, box, dark, new Vector3(0, 0.88f, 1.05f), new Vector3(0.22f, 0.2f, 0.3f));
                    Part(model.transform, box, dark, new Vector3(-0.12f, 1.22f, 0.7f), new Vector3(0.08f, 0.18f, 0.08f));
                    Part(model.transform, box, dark, new Vector3(0.12f, 1.22f, 0.7f), new Vector3(0.08f, 0.18f, 0.08f));
                    foreach (var lx in new[] { -0.17f, 0.17f })
                    foreach (var lz in new[] { -0.45f, 0.45f })
                        Part(model.transform, box, dark, new Vector3(lx, 0.27f, lz), new Vector3(0.12f, 0.55f, 0.12f));
                    Part(model.transform, box, mat, new Vector3(0, 0.85f, -0.75f), new Vector3(0.12f, 0.12f, 0.45f)).transform.localRotation = Quaternion.Euler(-25, 0, 0);
                    Part(model.transform, box, Tinted("#e0c040"), new Vector3(0, 1.02f, 0.98f), new Vector3(0.3f, 0.05f, 0.02f));
                    view.Height = 1.4f;
                    break;
                }
                default:
                {
                    var capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                    Object.Destroy(capsule.GetComponent<Collider>());
                    capsule.name = "Body";
                    capsule.transform.SetParent(model.transform, false);
                    capsule.transform.localPosition = new Vector3(0, 0.9f, 0);
                    capsule.transform.localScale = new Vector3(0.8f, 0.9f, 0.8f);
                    capsule.GetComponent<MeshRenderer>().sharedMaterial = mat;
                    Part(model.transform, box, Dark, new Vector3(0, 1.45f, 0.38f), new Vector3(0.25f, 0.12f, 0.12f));

                    // Right arm pivot at the shoulder; weapon sits in the hand.
                    var arm = new GameObject("ArmPivot").transform;
                    arm.SetParent(model.transform, false);
                    arm.localPosition = new Vector3(0.45f, 1.25f, 0.05f);
                    Part(arm, box, mat, new Vector3(0, -0.3f, 0), new Vector3(0.16f, 0.6f, 0.16f));
                    var weapon = Part(arm, box, Tinted("#8a6a42"), new Vector3(0, -0.55f, 0.45f), new Vector3(0.12f, 0.12f, 0.9f));
                    weapon.name = "Weapon";
                    weapon.SetActive(false);
                    view.Arm = arm;
                    view.Weapon = weapon;
                    view.Height = 2f;
                    break;
                }
            }
            model.transform.localScale = Vector3.one * scale;
            view.Height *= scale;
            view.CollectRenderers();
            return view;
        }

        /// <summary>
        /// A rigged village model (Resources/Creatures/&lt;model&gt;.fbx + &lt;model&gt;_ctrl). Returns null when the model is missing
        /// so the caller can fall back to the placeholder shape.
        /// </summary>
        static CreatureView CreateModelCreature(string name, VisualDef visual)
        {
            var prefab = Resources.Load<GameObject>("Creatures/" + visual.model);
            var ctrl = Resources.Load<RuntimeAnimatorController>("Creatures/" + visual.model + "_ctrl");
            if (prefab == null || ctrl == null)
            {
                Debug.LogError($"Missing Resources/Creatures/{visual.model}. Run menu RPG > 1. 프로젝트 설정 적용. Using the placeholder shape.");
                return null;
            }
            var root = new GameObject(name);
            var view = root.AddComponent<CreatureView>();
            var model = new GameObject("Model");
            model.transform.SetParent(root.transform, false);
            view.Model = model.transform;

            var body = Object.Instantiate(prefab, model.transform, false);
            body.name = "Body";
            foreach (var r in body.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    string n = mats[i] ? mats[i].name : "";
                    if (n.EndsWith(" (Instance)")) n = n.Substring(0, n.Length - 11);
                    var m = Load("Cr_" + n);
                    if (m) mats[i] = m;
                }
                r.sharedMaterials = mats;
                if (r is SkinnedMeshRenderer smr) smr.updateWhenOffscreen = false;
            }
            float scale = visual.scale > 0 ? visual.scale : 1f;
            model.transform.localScale = Vector3.one * scale;

            float top = 0f;
            foreach (var r in body.GetComponentsInChildren<Renderer>(true)) top = Mathf.Max(top, r.bounds.max.y - root.transform.position.y);
            view.Height = Mathf.Max(0.5f, top) * scale;

            var anim = body.GetComponent<Animator>();
            if (anim == null) anim = body.AddComponent<Animator>();
            anim.runtimeAnimatorController = ctrl;
            anim.applyRootMotion = false;
            anim.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            view.Anim = anim;
            view.CollectRenderers();
            return view;
        }

        static string HeroResource(Gender g) => g == Gender.Female ? "Hero/fantasy_hero_female" : "Hero/fantasy_hero";

        /// <summary>
        /// The protagonist (Resources/Hero/fantasy_hero[_female].fbx, set up by HeroSetup). Falls back to the capsule body if the
        /// model is missing so the game still runs. otherPlayer swaps the red cape for an orange one. Unset counts as male.
        /// </summary>
        public static CreatureView CreateHero(string name, bool otherPlayer, Gender gender = Gender.Male)
        {
            if (Resources.Load<GameObject>(HeroResource(gender)) == null || Resources.Load<RuntimeAnimatorController>("Hero/HeroAnimator") == null)
            {
                Debug.LogError("Missing Resources/Hero model or HeroAnimator. Run menu RPG > 1. 프로젝트 설정 적용. Using the placeholder body.");
                return CreateCreature(name, null, otherPlayer ? Remote : Local);
            }

            var root = new GameObject(name);
            var view = root.AddComponent<CreatureView>();
            var model = new GameObject("Model");
            model.transform.SetParent(root.transform, false);
            view.Model = model.transform;
            view.Height = 1.95f;
            SetHeroGender(view, gender, otherPlayer);
            return view;
        }

        /// <summary>Builds (or rebuilds) the hero body under view.Model for this look. Safe to call again to switch gender.</summary>
        public static void SetHeroGender(CreatureView view, Gender gender, bool otherPlayer)
        {
            if (gender == Gender.Unset) gender = Gender.Male;
            if (view.HeroGender == gender && view.Anim) return;
            var prefab = Resources.Load<GameObject>(HeroResource(gender));
            var ctrl = Resources.Load<RuntimeAnimatorController>("Hero/HeroAnimator");
            if (prefab == null || ctrl == null) return;

            var old = view.Model.Find("Hero");
            if (old) { old.gameObject.SetActive(false); Object.Destroy(old.gameObject); }
            var body = Object.Instantiate(prefab, view.Model, false);
            body.name = "Hero";
            body.transform.localPosition = Vector3.zero;
            body.transform.localRotation = Quaternion.identity;
            body.transform.localScale = Vector3.one;

            // Swap each imported slot for the URP material of the same name (Resources/Materials/Hero_<name>; the female
            // model's "_F" materials share the male colors).
            foreach (var r in body.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    string n = mats[i] ? mats[i].name : "";
                    if (n.EndsWith(" (Instance)")) n = n.Substring(0, n.Length - 11);
                    if (n.EndsWith("_F")) n = n.Substring(0, n.Length - 2);
                    if (otherPlayer && n == "Cape") n = "CapeRemote";
                    var m = Load("Hero_" + n);
                    if (m) mats[i] = m;
                }
                r.sharedMaterials = mats;
                if (r is SkinnedMeshRenderer smr)
                {
                    smr.updateWhenOffscreen = false;
                    smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                }
            }

            var anim = body.GetComponent<Animator>();
            if (anim == null) anim = body.AddComponent<Animator>();
            anim.runtimeAnimatorController = ctrl;
            anim.applyRootMotion = false;
            anim.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            view.Anim = anim;
            view.HeroGender = gender;
            AttachHeroWeapons(view, body);
            view.CollectRenderers();
        }

        /// <summary>
        /// Weapons are the planner's models (Resources/Weapons) built lazily at the hands when first needed; only the starting club is a
        /// placeholder of boxes. The old sword mesh inside the hero body stays hidden (the katana / longsword replace it).
        /// </summary>
        static void AttachHeroWeapons(CreatureView view, GameObject body)
        {
            view.SwordObj = view.ClubObj = view.BowObj = view.StaffObj = view.DaggerObj = view.ShieldObj = view.RelicObj = null;
            foreach (var t in body.GetComponentsInChildren<Transform>(true))
                if (t.name == "Sword") { t.gameObject.SetActive(false); view.SwordObj = t.gameObject; }
            Transform handR = null, handL = null;
            foreach (var t in body.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "Hand.R") handR = t;
                if (t.name == "Hand.L") handL = t;
            }
            view.HandR = handR; view.HandL = handL;
            view.BindViewRot = view.transform.rotation;
            if (handR) { view.HandRBindPos = handR.position; view.HandRBindRot = handR.rotation; }
            if (handL) { view.HandLBindPos = handL.position; view.HandLBindRot = handL.rotation; }
            if (handR)
            {
                var box = SimpleMeshes.Box();
                var sph = SimpleMeshes.Sphere(8, 6);
                var root = new GameObject("Club").transform;
                root.position = handR.position;
                root.rotation = view.transform.rotation;
                var wood = Tinted("#7a5430");
                Part(root, box, wood, new Vector3(0, 0, 0.32f), new Vector3(0.06f, 0.06f, 0.62f)).name = "Handle";
                Part(root, sph, wood, new Vector3(0, 0, 0.66f), new Vector3(0.16f, 0.16f, 0.2f)).name = "Head";
                root.SetParent(handR, true);
                root.gameObject.SetActive(false);
                view.ClubObj = root.gameObject;
            }
            view.SetWeaponKind(null);   // hidden until the owner says what is in hand
            view.ResetWeaponKind();
        }

        /// <summary>weapon kind → (model, right hand?, pose, grip offset along the model's long axis).</summary>
        public struct WeaponPose { public string Model; public bool Left; public int Pose; public float Grip; public float Roll; }
        // Pose 0 = pointing forward (swords, daggers, maces), 1 = upright (staff, scepter), 2 = bow (upright, belly forward), 3 = shield (on the forearm)
        public static bool TryWeaponPose(string kind, out WeaponPose p)
        {
            switch (kind)
            {
                case "sword": p = new WeaponPose { Model = "katana", Pose = 0, Grip = 0.12f, Roll = 90f }; return true;
                case "longsword": p = new WeaponPose { Model = "longsword", Pose = 0, Grip = 0.10f, Roll = 90f }; return true;
                case "dagger": p = new WeaponPose { Model = "dagger", Pose = 0, Grip = 0.07f, Roll = 90f }; return true;
                case "mace": p = new WeaponPose { Model = "mace", Pose = 0, Grip = 0.12f, Roll = 0f }; return true;
                case "staff": case "wand": p = new WeaponPose { Model = "staff", Pose = 1, Grip = 0.85f }; return true;
                case "holy": p = new WeaponPose { Model = "scepter", Pose = 1, Grip = 0.18f }; return true;
                case "bow": p = new WeaponPose { Model = "bow", Left = true, Pose = 2, Grip = 0.70f }; return true;
                case "shield": p = new WeaponPose { Model = "shield", Left = true, Pose = 3, Grip = 0.32f }; return true;
            }
            p = default;
            return false;
        }

        /// <summary>Instantiates a weapon model at the right place for the hero's bind pose and parents it to the hand bone.</summary>
        public static GameObject BuildWeapon(CreatureView view, string kind)
        {
            if (!TryWeaponPose(kind, out var wp)) return null;
            var hand = wp.Left ? view.HandL : view.HandR;
            var prefab = Resources.Load<GameObject>("Weapons/" + wp.Model);
            if (!hand || !prefab) return null;
            var mat = Resources.Load<Material>("Weapons/Mat_" + wp.Model);
            var go = Object.Instantiate(prefab);
            go.name = "W_" + wp.Model;
            if (mat) foreach (var r in go.GetComponentsInChildren<Renderer>(true)) { r.sharedMaterial = mat; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; }
            Quaternion q;
            switch (wp.Pose)
            {
                case 0: q = Quaternion.Euler(90f, 0, 0) * Quaternion.AngleAxis(wp.Roll, Vector3.up); break;   // model +Y → hero forward
                case 2: q = Quaternion.Euler(0, 90f, 0); break;                                              // bow arc faces forward
                case 3: q = Quaternion.Euler(0, -45f, 0); break;                                             // shield faces forward-outward
                default: q = Quaternion.identity; break;
            }
            var rot = view.BindViewRot * q;
            Vector3 axis = rot * Vector3.up;
            Vector3 handPos = wp.Left ? view.HandLBindPos : view.HandRBindPos;
            Quaternion handRot = wp.Left ? view.HandLBindRot : view.HandRBindRot;
            Vector3 pos = handPos - axis * wp.Grip;
            if (wp.Pose == 3) pos += view.BindViewRot * new Vector3(wp.Left ? -0.06f : 0.06f, 0, 0.09f);
            go.transform.SetParent(hand, false);
            var inv = Quaternion.Inverse(handRot);
            go.transform.localPosition = inv * (pos - handPos) + Vector3.zero;
            go.transform.localRotation = inv * rot;
            go.transform.localScale = Vector3.one;
            return go;
        }

        /// <summary>Glide wings for players (shown while gliding).</summary>
        public static GameObject AddWings(CreatureView v)
        {
            // Higher and further back when the body is the rigged hero (cape hangs behind).
            var pos = v.Anim ? new Vector3(0, 1.45f, -0.35f) : new Vector3(0, 1.5f, -0.1f);
            var w = Part(v.Model, SimpleMeshes.Box(), Wing, pos, new Vector3(2.6f, 0.05f, 0.7f));
            w.name = "GlideWings";
            w.SetActive(false);
            return w;
        }

        public static GameObject Part(Transform parent, Mesh mesh, Material mat, Vector3 pos, Vector3 scale)
        {
            var go = new GameObject(mesh.name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }
    }

    /// <summary>Simple procedural animation for a placeholder body: swing, hit flash, death tilt, idle bob.</summary>
    public sealed class CreatureView : MonoBehaviour
    {
        public Transform Model;
        public Transform Arm;
        public GameObject Weapon;
        /// <summary>Set for the rigged hero: Idle/Walk/Slash come from the Animator instead of the procedural motion below.</summary>
        Animator _anim;
        HashSet<int> _params;
        bool _hasDeathClip;
        float _attackClipSeconds = 20f / 24f;
        public Gender HeroGender;
        // The hero's held weapons: the sword is part of the model (a separate mesh), club and bow are built from boxes at the hands.
        public GameObject SwordObj, ClubObj, BowObj, StaffObj, DaggerObj, ShieldObj, RelicObj;
        public Transform HandR, HandL;
        /// <summary>The hands' world pose at the bind pose (arms down), so weapons built later still sit right on the bones.</summary>
        public Vector3 HandRBindPos, HandLBindPos;
        public Quaternion HandRBindRot = Quaternion.identity, HandLBindRot = Quaternion.identity, BindViewRot = Quaternion.identity;
        readonly Dictionary<string, GameObject> _weaponModels = new Dictionary<string, GameObject>();
        GameObject _shieldModel;

        public Animator Anim
        {
            get { return _anim; }
            set
            {
                _anim = value;
                _params = new HashSet<int>();
                _hasDeathClip = false;
                _attackClipSeconds = 20f / 24f;
                if (!value) return;
                foreach (var p in value.parameters) _params.Add(p.nameHash);
                var ctrl = value.runtimeAnimatorController;
                if (ctrl == null) return;
                foreach (var c in ctrl.animationClips)
                {
                    if (c.name.EndsWith("Death")) _hasDeathClip = true;
                    if (c.name.EndsWith("Slash") || c.name.EndsWith("Bite") || c.name.EndsWith("Thrust")) _attackClipSeconds = Mathf.Max(0.2f, c.length);
                }
            }
        }

        bool Has(int hash) { return _params != null && _params.Contains(hash); }
        public float Height = 2f;

        Renderer[] _renderers;
        MaterialPropertyBlock _mpb;
        float _swingT = -1f, _swingDur = 0.35f;
        float _flashT;
        bool _dead;
        float _deadBlend;
        float _hopPhase;
        public bool Moving;

        public void CollectRenderers()
        {
            _renderers = GetComponentsInChildren<Renderer>(true);
            _mpb = new MaterialPropertyBlock();
        }

        public void SetWeaponVisible(bool on) { if (Weapon) Weapon.SetActive(on); }

        string _weaponKind = "\u0000";
        public void ResetWeaponKind() { _weaponKind = "\u0000"; }

        /// <summary>sword | bow | club | "" — which weapon the hero is holding right now.</summary>
        public void SetWeaponKind(string kind)
        {
            kind = kind ?? "";
            if (kind == _weaponKind) return;
            _weaponKind = kind;
            if (ClubObj) ClubObj.SetActive(kind == "club");
            foreach (var kv in _weaponModels) if (kv.Value) kv.Value.SetActive(kv.Key == kind);
            if (kind.Length > 0 && kind != "club" && !_weaponModels.ContainsKey(kind))
                _weaponModels[kind] = Placeholders.BuildWeapon(this, kind);
            if (_weaponModels.TryGetValue(kind, out var w) && w) w.SetActive(true);
            // 전사: the mace comes with a shield on the other arm.
            bool wantShield = kind == "mace";
            if (wantShield && !_shieldModel && HandL) _shieldModel = Placeholders.BuildWeapon(this, "shield");
            if (_shieldModel) _shieldModel.SetActive(wantShield);
        }

        static readonly int MovingHash = Animator.StringToHash("Moving");
        static readonly int SlashHash = Animator.StringToHash("Slash");
        static readonly int SlashSpeedHash = Animator.StringToHash("SlashSpeed");
        static readonly int DeadHash = Animator.StringToHash("Dead");
        static readonly int TalkHash = Animator.StringToHash("Talk");
        static readonly int HitHash = Animator.StringToHash("Hit");
        Vector3 _lastPos;
        bool _hasLastPos;

        public void PlaySwing(float duration)
        {
            _swingDur = Mathf.Clamp(duration, 0.15f, 0.6f);
            _swingT = 0f;
            if (Anim)
            {
                if (Has(SlashSpeedHash)) Anim.SetFloat(SlashSpeedHash, _attackClipSeconds / _swingDur);
                if (Has(SlashHash)) Anim.SetTrigger(SlashHash);
            }
        }

        float _shootT = -1f;
        float _dodgeT = -1f, _dodgeDur = 0.3f;

        /// <summary>Bow shot / ranged skill: the body leans back for a moment.</summary>
        public void PlayShoot() { _shootT = 0f; }

        /// <summary>Dodge roll: a full somersault around the middle of the body.</summary>
        public void PlayDodge(float duration) { _dodgeDur = Mathf.Max(0.1f, duration); _dodgeT = 0f; }

        public void Flash() { _flashT = 0.12f; if (Anim && Has(HitHash)) Anim.SetTrigger(HitHash); }

        public void SetDead(bool dead)
        {
            _dead = dead;
            if (Anim && Has(DeadHash)) Anim.SetBool(DeadHash, dead);
        }

        /// <summary>NPC talking animation (when a dialogue opens or moves to the next line).</summary>
        public void PlayTalk() { if (Anim && Has(TalkHash)) Anim.SetTrigger(TalkHash); }

        void LateUpdate()
        {
            float dt = Time.deltaTime;

            if (Anim)
            {
                // Walk when the body is actually moving on the ground (works for you and for interpolated remote players).
                var pos = transform.position;
                float speed = 0f;
                if (_hasLastPos && dt > 0f) speed = new Vector2(pos.x - _lastPos.x, pos.z - _lastPos.z).magnitude / dt;
                _lastPos = pos; _hasLastPos = true;
                if (Has(MovingHash)) Anim.SetBool(MovingHash, !_dead && speed > 0.6f);
                if (_swingT >= 0f) { _swingT += dt; if (_swingT >= _swingDur) _swingT = -1f; }
            }
            else if (Arm)
            {
                float a = 0f;
                if (_swingT >= 0f)
                {
                    _swingT += dt;
                    float k = _swingT / _swingDur;
                    // wind up a little, then strike down and forward
                    a = k < 0.3f ? Mathf.Lerp(0, -70, k / 0.3f) : Mathf.Lerp(-70, 60, Mathf.SmoothStep(0, 1, (k - 0.3f) / 0.7f));
                    if (k >= 1f) _swingT = -1f;
                }
                Arm.localRotation = Quaternion.Euler(a, 0, 0);
            }
            else if (_swingT >= 0f)
            {
                // Animals lunge forward instead of swinging.
                _swingT += dt;
                float k = _swingT / _swingDur;
                Model.localPosition = new Vector3(0, 0, Mathf.Sin(Mathf.Clamp01(k) * Mathf.PI) * 0.35f);
                if (k >= 1f) { _swingT = -1f; Model.localPosition = Vector3.zero; }
            }

            _deadBlend = Mathf.MoveTowards(_deadBlend, _dead ? 1f : 0f, dt * 4f);
            float hop = 0f;
            if (!_dead && Moving && !Arm && !Anim) { _hopPhase += dt * 9f; hop = Mathf.Abs(Mathf.Sin(_hopPhase)) * 0.12f; }
            Model.localRotation = _hasDeathClip ? Quaternion.identity : Quaternion.Euler(0, 0, 90f * _deadBlend);
            if (_swingT < 0f || Arm || Anim) Model.localPosition = new Vector3(0, hop, 0);

            // One-shot body motions that sit on top of the animation.
            if (_shootT >= 0f)
            {
                _shootT += dt;
                float k = _shootT / 0.2f;
                if (k >= 1f) _shootT = -1f;
                else Model.localRotation = Quaternion.Euler(-14f * Mathf.Sin(k * Mathf.PI), 0, 0) * Model.localRotation;
            }
            if (Anim && HeroGender != Gender.Unset) JumpPose(dt);
            if (_dodgeT >= 0f)
            {
                _dodgeT += dt;
                float k = Mathf.Clamp01(_dodgeT / _dodgeDur);
                if (k >= 1f) _dodgeT = -1f;
                else
                {
                    var roll = Quaternion.Euler(360f * k, 0, 0);
                    var center = new Vector3(0, Height * 0.45f, 0);
                    Model.localRotation = roll * Model.localRotation;
                    Model.localPosition += center - roll * center;
                }
            }

            if (_renderers != null && (_flashT > 0f || _flashWas))
            {
                _flashT -= dt;
                bool on = _flashT > 0f;
                foreach (var r in _renderers)
                {
                    if (!r) continue;
                    if (on) { _mpb.SetColor("_BaseColor", new Color(1f, 0.55f, 0.5f)); r.SetPropertyBlock(_mpb); }
                    else r.SetPropertyBlock(null);
                }
                _flashWas = on;
            }
        }

        bool _flashWas;

        // ── jump motion (hero only) ──
        // No jump clips exist, so the pose is procedural: stretch on the way up, lean into the fall, squash on landing,
        // and a full forward somersault on the air jump. Works for remote players too: it reads the height change of the
        // body, so a sudden upward kick while already airborne is the second jump (no protocol flag needed).
        /// <summary>Set by the owner each frame: feet off the ground and not gliding/flying.</summary>
        public bool Airborne;
        Game.Client.Player.PlayerMotor _motorRef;
        bool _motorLooked;
        float _lastY, _prevVy, _flipT = -1f, _landT = -1f, _airTime;
        bool _wasAir, _hasY;
        public const float FlipDuration = 0.55f;

        /// <summary>Air-jump somersault (also callable by hand).</summary>
        public void PlayFlip() { _flipT = 0f; }

        float _airW, _tuckW, _crouchW;
        /// <summary>Editor render probe only: show a jump pose on the current animation frame.</summary>
        public void DebugPose(float air, float tuck, float crouch, float rise)
        {
            _airW = air; _tuckW = tuck; _crouchW = crouch;
            FindBones();
            var before = _uArmR != null ? _uArmR.rotation : Quaternion.identity;
            string Rel(Transform t) { return t ? transform.InverseTransformPoint(t.position).ToString("F2") : "-"; }
            string pre = $"handR {Rel(HandR)} handL {Rel(HandL)} footR {Rel(_footR)} footL {Rel(_footL)} kneeR {Rel(_lLegR)}";
            LimbPose(rise);
            Debug.Log($"[pose3] {pre} ## handR {Rel(HandR)} handL {Rel(HandL)} footR {Rel(_footR)} footL {Rel(_footL)} kneeR {Rel(_lLegR)}");
            Debug.Log($"[pose] bones hips={_hips != null} uArmR={_uArmR != null} side={transform.right} rot {before.eulerAngles} -> {(_uArmR != null ? _uArmR.rotation.eulerAngles : Vector3.zero)}");
        }
        Transform _boneRoot;
        Transform _hips, _spine, _chest, _uArmL, _uArmR, _lArmL, _lArmR, _uLegL, _uLegR, _lLegL, _lLegR, _footL, _footR;

        void FindBones()
        {
            var hero = Model != null ? Model.Find("Hero") : null;
            if (hero == _boneRoot) return;
            _boneRoot = hero;
            _hips = _spine = _chest = _uArmL = _uArmR = _lArmL = _lArmR = _uLegL = _uLegR = _lLegL = _lLegR = _footL = _footR = null;
            if (hero == null) return;
            foreach (var t in hero.GetComponentsInChildren<Transform>(true))
                switch (t.name)
                {
                    case "Hips": _hips = t; break;
                    case "Spine": _spine = t; break;
                    case "Chest": _chest = t; break;
                    case "UpperArm.L": _uArmL = t; break;
                    case "UpperArm.R": _uArmR = t; break;
                    case "LowerArm.L": _lArmL = t; break;
                    case "LowerArm.R": _lArmR = t; break;
                    case "UpperLeg.L": _uLegL = t; break;
                    case "UpperLeg.R": _uLegR = t; break;
                    case "LowerLeg.L": _lLegL = t; break;
                    case "LowerLeg.R": _lLegR = t; break;
                    case "Foot.L": _footL = t; break;
                    case "Foot.R": _footR = t; break;
                }
        }

        // Angles are about the body's own side axis, so they do not depend on how the rig's bones are oriented.
        // Negative = forward/up, positive = backward. Each bone adds on top of its parent's turn.
        static void Turn(Transform bone, Vector3 axis, float deg)
        {
            if (bone != null && Mathf.Abs(deg) > 0.01f) bone.rotation = Quaternion.AngleAxis(deg, axis) * bone.rotation;
        }

        void LimbPose(float rise)
        {
            if (_airW <= 0.001f && _tuckW <= 0.001f && _crouchW <= 0.001f) return;
            FindBones();
            if (_hips == null) return;
            Vector3 side = transform.right, fwd = transform.forward;
            float a = _airW, t = _tuckW, c = _crouchW;
            // jump up: arms swing up, one knee drawn high, the other leg trails.  fall: arms out and a little back, legs reach for the ground.
            float armUp = Mathf.Lerp(-35f, -95f, rise), armOut = Mathf.Lerp(38f, 18f, rise);
            float elbow = Mathf.Lerp(-20f, -35f, rise);
            float legA = Mathf.Lerp(-18f, -62f, rise), kneeA = Mathf.Lerp(28f, 85f, rise);    // leading leg
            float legB = Mathf.Lerp(10f, 8f, rise), kneeB = Mathf.Lerp(20f, 55f, rise);       // trailing leg
            Turn(_spine, side, a * Mathf.Lerp(4f, -4f, rise) + t * 28f + c * 14f);
            Turn(_chest, side, t * 18f + c * 8f);
            // arms: tuck folds them in front of the chest
            float armTuck = -80f;
            float aU = a * armUp + t * armTuck + c * -25f, aE = a * elbow + t * -70f + c * -30f;
            Turn(_uArmR, side, aU); Turn(_uArmL, side, aU);
            Turn(_lArmR, side, aE); Turn(_lArmL, side, aE);
            float sR = _uArmR != null && Vector3.Dot(_uArmR.position - transform.position, side) < 0f ? -1f : 1f;   // which way is "out" for the R arm
            Turn(_uArmR, fwd, sR * (a * armOut + c * 12f)); Turn(_uArmL, fwd, -sR * (a * armOut + c * 12f));
            // legs
            bool rightLeads = true;
            float tuckThigh = -105f, tuckKnee = 120f, crouchThigh = -38f, crouchKnee = 70f;
            Turn(_uLegR, side, a * (rightLeads ? legA : legB) + t * tuckThigh + c * crouchThigh);
            Turn(_uLegL, side, a * (rightLeads ? legB : legA) + t * tuckThigh + c * crouchThigh);
            Turn(_lLegR, side, a * (rightLeads ? kneeA : kneeB) + t * tuckKnee + c * crouchKnee);
            Turn(_lLegL, side, a * (rightLeads ? kneeB : kneeA) + t * tuckKnee + c * crouchKnee);
            Turn(_footR, side, a * -10f + c * -25f + t * -20f); Turn(_footL, side, a * -10f + c * -25f + t * -20f);
            if (_hips != null && c > 0.01f) _hips.position += Vector3.down * (0.16f * c);
        }

        void JumpPose(float dt)
        {
            if (dt <= 0f) return;
            if (!_motorLooked) { _motorRef = GetComponent<Game.Client.Player.PlayerMotor>(); _motorLooked = true; }
            if (_motorRef != null) Airborne = !_motorRef.Grounded && !_motorRef.Flying && !_motorRef.Gliding;

            float y = transform.position.y;
            float vy = _hasY ? (y - _lastY) / dt : 0f;
            _lastY = y; _hasY = true;

            bool air = Airborne && !_dead;
            if (air && _wasAir && vy > 2.5f && vy - _prevVy > 3f && _flipT < 0f) _flipT = 0f;   // the air jump kicked us upward
            if (air) _airTime += dt; else _airTime = 0f;
            if (_wasAir && !air && _airTime == 0f && _prevVy < -3f) _landT = 0f;                  // came down hard enough to squash
            _wasAir = air;
            _prevVy = vy;

            // How strongly each pose applies (smoothed so the limbs ease in and out).
            float rise = Mathf.InverseLerp(-5f, 6f, vy);                              // 1 going up, 0 falling
            float airTarget = air && _flipT < 0f ? 1f : 0f;
            float tuckTarget = air && _flipT >= 0f ? 1f : 0f;
            float crouchTarget = 0f;
            if (_landT >= 0f)
            {
                _landT += dt;
                float k = _landT / 0.22f;
                if (k >= 1f) _landT = -1f; else crouchTarget = Mathf.Sin(k * Mathf.PI);
            }
            _airW = Mathf.MoveTowards(_airW, airTarget, dt * 9f);
            _tuckW = Mathf.MoveTowards(_tuckW, tuckTarget, dt * 12f);
            _crouchW = crouchTarget > 0f ? crouchTarget : Mathf.MoveTowards(_crouchW, 0f, dt * 10f);
            LimbPose(rise);
            float pitch = air && _flipT < 0f ? Mathf.Lerp(10f, -4f, rise) : 0f;      // lean into the fall, back a hair going up
            if (pitch != 0f) Model.localRotation = Quaternion.Euler(pitch, 0, 0) * Model.localRotation;

            if (_flipT >= 0f)
            {
                _flipT += dt;
                float k = Mathf.Clamp01(_flipT / FlipDuration);
                if (k >= 1f) _flipT = -1f;
                else
                {
                    float e = k * k * (3f - 2f * k) * 0.35f + k * 0.65f;   // slow start, fast middle, settles before the landing
                    var roll = Quaternion.Euler(360f * e, 0, 0);
                    var center = new Vector3(0, Height * 0.5f, 0);
                    Model.localRotation = roll * Model.localRotation;
                    Model.localPosition += center - roll * center;
                }
            }
        }
    }
}
