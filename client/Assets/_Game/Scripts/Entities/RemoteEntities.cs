using System;
using System.Collections.Generic;
using Game.Client.Core;
using Game.Client.Net;
using Game.Client.World;
using Game.Shared.Data;
using Game.Shared.Protocol;
using UnityEngine;

namespace Game.Client.Entities
{
    /// <summary>Anything the server spawned for us: other players, monsters, NPCs (and a stub entry for ourselves).</summary>
    public sealed class WorldEntity : MonoBehaviour
    {
        struct Sample { public double T; public Vector3 P; public float Yaw; public MoveFlags F; }

        readonly List<Sample> _buf = new List<Sample>(32);
        public uint EntityId;
        public EntityKind Kind;
        public string TypeId;
        public string DisplayName;
        public string Title;
        public int Level;
        public int Hp, MaxHp;
        public MoveFlags Flags;
        public bool IsSelf;
        public CreatureView View;
        public GameObject Wings;
        public float LastHitAt = -100f;
        /// <summary>CC text shown above the head (기절, 둔화 ...) until StatusUntil.</summary>
        public string StatusText;
        public float StatusUntil;

        public bool Dead => (Flags & MoveFlags.Dead) != 0;
        public float Height => View ? View.Height : 2f;

        public void Push(double serverTime, Vector3 pos, float yaw, MoveFlags flags)
        {
            if (_buf.Count > 0 && serverTime <= _buf[_buf.Count - 1].T) return;
            _buf.Add(new Sample { T = serverTime, P = pos, Yaw = yaw, F = flags });
            if (_buf.Count > 30) _buf.RemoveAt(0);
        }

        public void Render(double renderTime)
        {
            if (IsSelf || _buf.Count == 0) return;
            Sample a, b;
            if (renderTime <= _buf[0].T)
            {
                a = b = _buf[0];
            }
            else
            {
                int i = 1;
                while (i < _buf.Count && _buf[i].T < renderTime) i++;
                if (i >= _buf.Count) a = b = _buf[_buf.Count - 1]; // hold the newest instead of guessing
                else { a = _buf[i - 1]; b = _buf[i]; }
                int drop = Mathf.Max(0, i - 2);
                if (drop > 0) _buf.RemoveRange(0, drop);
            }
            float t = b.T > a.T ? Mathf.Clamp01((float)((renderTime - a.T) / (b.T - a.T))) : 1f;
            var prev = transform.position;
            transform.position = Vector3.Lerp(a.P, b.P, t);
            transform.rotation = Quaternion.Euler(0, Mathf.LerpAngle(a.Yaw, b.Yaw, t), 0);
            Flags = b.F;
            if (View)
            {
                View.SetDead(Dead);
                View.Moving = (transform.position - prev).sqrMagnitude > 0.0001f;
                View.Airborne = (b.F & (MoveFlags.Grounded | MoveFlags.Gliding)) == 0;
            }
            if (Wings) Wings.SetActive((b.F & MoveFlags.Gliding) != 0);
        }
    }

    /// <summary>
    /// Spawns/despawns server entities, feeds snapshots (rendered interpolationDelay in the past),
    /// and applies combat / HP / action events to them.
    /// </summary>
    public sealed class RemoteEntities : MonoBehaviour
    {
        readonly Dictionary<uint, WorldEntity> _entities = new Dictionary<uint, WorldEntity>();
        NetClient _net;
        ClientState _state;
        uint _selfId;
        float _interpDelay = 0.1f;

        public int Count => _entities.Count;
        public IEnumerable<WorldEntity> All => _entities.Values;

        /// <summary>Damage dealt: target, amount, hit flags (crit / back / pierce), attacker is me.</summary>
        public event Action<WorldEntity, int, HitFlags, bool> OnDamage;

        public void Init(NetClient net, ClientState state, float interpolationDelay)
        {
            _net = net;
            _state = state;
            _selfId = state.SelfId;
            _interpDelay = interpolationDelay;
            _net.OnSpawn += HandleSpawn;
            _net.OnDespawn += HandleDespawn;
            _net.OnSnapshot += HandleSnapshot;
            _net.OnCombat += HandleCombat;
            _net.OnHp += HandleHp;
            _net.OnAction += HandleAction;
            _net.OnSkillCast += HandleSkillCast;
            _net.OnCc += HandleCc;
        }

        /// <summary>The local player is drawn by PlayerMotor; this entry only carries its HP / name for nameplates and damage numbers.</summary>
        public WorldEntity RegisterSelf(GameObject player, CreatureView view, string name)
        {
            var e = player.AddComponent<WorldEntity>();
            e.EntityId = _selfId;
            e.Kind = EntityKind.Player;
            e.DisplayName = name;
            e.IsSelf = true;
            e.View = view;
            _entities[_selfId] = e;
            return e;
        }

        public WorldEntity Get(uint id) { return _entities.TryGetValue(id, out var e) ? e : null; }

        public void Clear()
        {
            foreach (var e in _entities.Values) if (e && !e.IsSelf) Destroy(e.gameObject);
            _entities.Clear();
        }

        void OnDestroy()
        {
            if (_net == null) return;
            _net.OnSpawn -= HandleSpawn;
            _net.OnDespawn -= HandleDespawn;
            _net.OnSnapshot -= HandleSnapshot;
            _net.OnCombat -= HandleCombat;
            _net.OnHp -= HandleHp;
            _net.OnAction -= HandleAction;
            _net.OnSkillCast -= HandleSkillCast;
            _net.OnCc -= HandleCc;
        }

        void HandleSpawn(EntitySpawn s)
        {
            if (_entities.TryGetValue(s.EntityId, out var existing))
            {
                existing.Hp = s.Hp; existing.MaxHp = s.MaxHp; existing.Level = s.Level;
                return;
            }
            var data = _state.Data;
            VisualDef visual = null;
            string title = null;
            Material bodyMat = null;
            switch (s.Kind)
            {
                case EntityKind.Monster: visual = data.Monster(s.TypeId)?.visual; break;
                case EntityKind.Npc:
                {
                    var n = data.Npc(s.TypeId);
                    visual = n?.visual;
                    title = n?.title;
                    break;
                }
                default: bodyMat = Placeholders.Remote; break;
            }
            string viewName = $"{s.Kind}#{s.EntityId} {s.Name}";
            var view = s.Kind == EntityKind.Player
                ? Placeholders.CreateHero(viewName, otherPlayer: true, s.Gender)
                : Placeholders.CreateCreature(viewName, visual, bodyMat);
            var e = view.gameObject.AddComponent<WorldEntity>();
            e.EntityId = s.EntityId;
            e.Kind = s.Kind;
            e.TypeId = s.TypeId;
            e.DisplayName = s.Name;
            e.Title = title;
            e.Level = s.Level;
            e.Hp = s.Hp;
            e.MaxHp = s.MaxHp;
            e.Flags = s.Flags;
            e.View = view;
            if (s.Kind == EntityKind.Player)
            {
                e.Wings = Placeholders.AddWings(view);
                view.SetWeaponVisible(true);
                view.SetWeaponKind(s.Weapon);
            }
            view.SetDead(e.Dead);
            view.transform.SetPositionAndRotation(new Vector3(s.X, s.Y, s.Z), Quaternion.Euler(0, s.Yaw, 0));
            e.Push(_net.ServerNow - 1.0, view.transform.position, s.Yaw, s.Flags);
            _entities[s.EntityId] = e;
        }

        void HandleDespawn(EntityDespawn d)
        {
            if (_entities.TryGetValue(d.EntityId, out var e) && !e.IsSelf)
            {
                if (e) Destroy(e.gameObject);
                _entities.Remove(d.EntityId);
            }
        }

        void HandleSnapshot(Snapshot s)
        {
            for (int i = 0; i < s.Count; i++)
            {
                var es = s.Entities[i];
                if (es.EntityId == _selfId)
                {
                    if (_entities.TryGetValue(_selfId, out var me)) me.Flags = es.Flags;
                    continue;
                }
                if (_entities.TryGetValue(es.EntityId, out var e))
                    e.Push(s.ServerTime, new Vector3(es.X, es.Y, es.Z), es.Yaw, es.Flags);
            }
        }

        void HandleCombat(CombatEvent c)
        {
            if (!_entities.TryGetValue(c.TargetId, out var t)) return;
            t.Hp = c.TargetHp;
            t.MaxHp = c.TargetMaxHp;
            t.LastHitAt = Time.time;
            if ((c.Flags & HitFlags.Kill) != 0) t.Flags |= MoveFlags.Dead;
            if (t.View) { if ((c.Flags & HitFlags.Heal) == 0) t.View.Flash(); if (t.Dead) t.View.SetDead(true); }
            if (c.AttackerId == _selfId && _entities.TryGetValue(_selfId, out var me)) me.LastHitAt = Time.time;
            OnDamage?.Invoke(t, c.Damage, c.Flags, c.AttackerId == _selfId);
        }

        void HandleHp(HpUpdate h)
        {
            if (!_entities.TryGetValue(h.EntityId, out var e)) return;
            e.Hp = h.Hp;
            e.MaxHp = h.MaxHp;
        }

        void HandleAction(ActionEvent a)
        {
            if (!_entities.TryGetValue(a.EntityId, out var e) || e.IsSelf || !e.View) return;
            float dur = 0.35f;
            if (e.Kind == EntityKind.Monster)
            {
                var def = _state.Data.Monster(e.TypeId);
                if (def != null) dur = Mathf.Min(0.4f, def.attackInterval * 0.5f);
            }
            e.View.PlaySwing(dur);
        }

        /// <summary>Another player used a skill: play their swing / shot and the effect (your own is played when you press the key).</summary>
        void HandleSkillCast(SkillCast c)
        {
            if (!_entities.TryGetValue(c.EntityId, out var e) || e.IsSelf || !e.View) return;
            var s = _state.Data.Skill(c.SkillId);
            if (s == null) return;
            switch (s.kind)
            {
                case "melee": case "circle": case "dash": case "stance": e.View.PlaySwing(0.4f); break;
                default: e.View.PlayShoot(); break;
            }
            SkillFx.Play(s, e.transform.position, c.Yaw);
        }

        static string CcLabel(CcType t)
        {
            switch (t)
            {
                case CcType.Stun: return "기절";
                case CcType.Freeze: return "빙결";
                case CcType.Root: return "속박";
                case CcType.Knockback: return "넉백";
                case CcType.Slow: return "둔화";
                default: return null;
            }
        }

        void HandleCc(CcApplied c)
        {
            if (!_entities.TryGetValue(c.TargetId, out var e)) return;
            e.StatusText = CcLabel(c.Type);
            e.StatusUntil = Time.time + Mathf.Max(0.4f, c.Duration);
        }

        void LateUpdate()
        {
            if (_net == null) return;
            double renderTime = _net.ServerNow - _interpDelay;
            foreach (var e in _entities.Values) if (e) e.Render(renderTime);
        }
    }
}
