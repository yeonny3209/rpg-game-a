using Game.Client.CameraRig;
using Game.Client.Core;
using Game.Client.Net;
using Game.Client.World;
using Game.Shared.Data;
using Game.Shared.Protocol;
using Game.Shared.World;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Client.Player
{
    /// <summary>
    /// What the player does with the mouse and skill keys (spec vol.5 12.2):
    ///   left mouse  — basic attack (hold to keep swinging); archers hold to draw the bow and release to shoot
    ///   right mouse — melee: dodge roll · archer: aim (zoom)
    ///   Q E T G Z X — the six skill slots (rebindable)
    /// The client only plays animations and asks; the server checks cooldowns and decides every hit.
    /// </summary>
    public sealed class PlayerCombat : MonoBehaviour
    {
        NetClient _net;
        ClientState _state;
        PlayerMotor _motor;
        CreatureView _view;
        Transform _cam;
        Camera _camera;
        uint _seq;
        float _readyAt;
        float _dodgeReadyAt;

        /// <summary>False while a window / dialogue is open.</summary>
        public System.Func<bool> CanAttack;
        public ThirdPersonCamera Rig;
        public System.Action<string> Notice;

        // ── archer state, read by the UI ──
        public bool Charging { get; private set; }
        public float ChargeHold { get; private set; }
        public int ChargeTier { get; private set; }
        public string ChargeLabel { get; private set; } = "";
        float _chargeStart;
        float _nextShotAt;

        public float DodgeCooldownLeft => Mathf.Max(0f, _dodgeReadyAt - Time.time);

        public void Init(NetClient net, ClientState state, PlayerMotor motor, CreatureView view, Transform cam)
        {
            _net = net; _state = state; _motor = motor; _view = view; _cam = cam;
            _camera = cam ? cam.GetComponent<Camera>() : null;
            _net.OnChargeCancel += HandleChargeCancel;
            _net.OnSkillCooldown += HandleSkillCooldown;
            _net.OnCc += HandleCc;
            _net.OnAmmo += HandleAmmo;
        }

        void OnDestroy()
        {
            if (_net == null) return;
            _net.OnChargeCancel -= HandleChargeCancel;
            _net.OnSkillCooldown -= HandleSkillCooldown;
            _net.OnCc -= HandleCc;
            _net.OnAmmo -= HandleAmmo;
        }

        public WeaponStats CurrentWeapon(out bool armed)
        {
            armed = false;
            var inv = _state.Inv;
            if (inv != null)
            {
                foreach (var it in inv.Items)
                {
                    if (it.Container != ItemContainer.Equipment || it.Slot != EquipLayout.Weapon) continue;
                    var def = _state.Data.Item(it.ItemId);
                    if (def != null && def.weapon != null) { armed = true; return def.weapon; }
                }
            }
            return _state.Data.character.unarmed;
        }

        /// <summary>club | sword | bow | "" of what the character holds (during a job trial: the lent starter weapon).</summary>
        string WeaponTypeInHand()
        {
            var job = _state.Job;
            if (_state.TrialActive && job != null)
            {
                var lent = _state.Data.Item(job.starterWeapon);
                if (lent != null) return lent.weaponType;
            }
            var inv = _state.Inv;
            if (inv != null)
                foreach (var it in inv.Items)
                {
                    if (it.Container != ItemContainer.Equipment || it.Slot != EquipLayout.Weapon) continue;
                    var def = _state.Data.Item(it.ItemId);
                    if (def != null) return def.weaponType;
                }
            return "";
        }

        // ───────────── server messages ─────────────

        void HandleChargeCancel() { StopCharge(); }

        /// <summary>The server's word on a cooldown wins (also when it refused the skill).</summary>
        void HandleSkillCooldown(SkillCooldown c) { _state.StartCooldown(c.SkillId, c.Seconds); }

        void HandleCc(CcApplied c)
        {
            if (c.TargetId != _state.SelfId) return;
            _motor.ApplyCc(c.Type, c.Duration);
            StopCharge();
        }

        void HandleAmmo(AmmoState a) { _state.Ammo = a; _state.AmmoAt = Time.time; }

        // ───────────── per frame ─────────────

        void Update()
        {
            if (_net == null || !_net.InGame || _state.Char == null) return;
            CurrentWeapon(out bool armed);
            _view.SetWeaponVisible(armed);
            _view.SetWeaponKind(WeaponTypeInHand());

            if (_state.Char.Dead) { StopCharge(); _motor.SpeedScale = 1f; return; }

            var job = _state.Job;
            var bow = job != null && job.bow != null && job.bow.enabled ? job.bow : null;
            bool allowed = (CanAttack == null || CanAttack()) && !_motor.Stunned;
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            float aim = GameInput.AimYaw(transform, _camera);

            // Zoom / dodge on the right mouse button.
            if (Rig) Rig.ZoomAim = bow != null && allowed && mouse != null && mouse.rightButton.isPressed && GameInput.PointerReady();
            if (allowed && mouse != null && mouse.rightButton.wasPressedThisFrame && GameInput.PointerReady() && bow == null) TryDodge();

            // Skills.
            if (allowed && kb != null)
                for (int slot = 0; slot < 6; slot++)
                    if (kb[KeyBindings.Skill(slot)].wasPressedThisFrame) TryUseSkill(slot, aim);

            // 비행: one key toggles it; the server decides (cooldown, job).
            if (allowed && kb != null && _state.CanFly && kb[KeyBindings.Get(BindAction.Fly)].wasPressedThisFrame)
            {
                if (_state.Flying || Time.time >= _state.FlyReadyAt) _net.Send(new StartFlight());
                else Notice?.Invoke($"비행은 {Mathf.CeilToInt(_state.FlyReadyAt - Time.time)}초 뒤에 다시 쓸 수 있어.");
            }

            if (bow != null) UpdateBow(bow, allowed, mouse, aim);
            else
            {
                StopCharge();
                UpdateMelee(allowed, mouse, aim);
            }
        }

        // ───────────── melee basic attack ─────────────

        void UpdateMelee(bool allowed, Mouse mouse, float aim)
        {
            _motor.SpeedScale = 1f;
            if (!allowed || mouse == null || !mouse.leftButton.isPressed || !GameInput.PointerReady()) return;
            if (Time.time < _readyAt) return;

            var w = CurrentWeapon(out _);
            float speed = _state.Stats != null ? Mathf.Max(0.1f, _state.Stats.AttackSpeed) : 1f;
            float interval = Mathf.Max(0.1f, w.interval / speed);
            _readyAt = Time.time + interval;

            _motor.FaceYaw(aim, Mathf.Min(0.35f, interval * 0.6f));
            _view.PlaySwing(Mathf.Min(0.4f, interval * 0.6f));
            _net.Send(new Attack { Seq = ++_seq, Yaw = aim });
        }

        // ───────────── archer: draw, release, magazine ─────────────

        void UpdateBow(BowRules bow, bool allowed, Mouse mouse, float aim)
        {
            bool reloading = _state.Ammo != null && _state.Ammo.ReloadIn > 0 && Time.time - _state.AmmoAt < _state.Ammo.ReloadIn;
            int ammo = _state.Ammo != null ? _state.Ammo.Ammo : bow.magazine;
            bool held = mouse != null && mouse.leftButton.isPressed && GameInput.PointerReady();

            if (!Charging)
            {
                _motor.SpeedScale = 1f;
                bool pressed = mouse != null && mouse.leftButton.wasPressedThisFrame && GameInput.PointerReady();
                if (allowed && pressed && !reloading && ammo > 0 && Time.time >= _nextShotAt - 0.05f)
                {
                    Charging = true;
                    _chargeStart = Time.time;
                    _net.Send(new ChargeStart());
                }
                if (!Charging) return;
            }

            // Drawing the bow: slower walk, face where we aim, and show the tier the server will give.
            _motor.SpeedScale = bow.chargeMoveSpeed;
            _motor.FaceYaw(aim, 0.15f);
            float rate = _state.BuffValue("chargeRate");
            ChargeHold = (Time.time - _chargeStart) * (rate > 0 ? rate : 1f);
            ChargeTier = 0;
            for (int i = 0; i < bow.tiers.Length; i++) if (ChargeHold >= bow.tiers[i].minHold) ChargeTier = i;
            ChargeLabel = bow.tiers[ChargeTier].label;

            if (held && allowed) return;

            // Released (or something stopped us): shoot.
            Charging = false;
            _motor.SpeedScale = 1f;
            if (!allowed) { _net.Send(new ChargeRelease { Yaw = aim }); return; }
            float interval = bow.shotInterval;
            if (ChargeTier == 0 && _state.BuffValue("instantInterval") > 0) interval = _state.BuffValue("instantInterval");
            float speed = _state.Stats != null ? Mathf.Max(0.1f, _state.Stats.AttackSpeed) : 1f;
            _nextShotAt = Time.time + interval / speed;
            _net.Send(new ChargeRelease { Yaw = aim });
            _view.PlayShoot();
            // Local guess of the magazine until the server's AmmoState arrives.
            if (_state.Ammo != null && _state.Ammo.Ammo > 0)
            {
                var a = _state.Ammo;
                a.Ammo--;
                if (a.Ammo == 0) a.ReloadIn = bow.reloadSec;
                _state.Ammo = a; _state.AmmoAt = Time.time;
            }
        }

        void StopCharge()
        {
            if (!Charging) return;
            Charging = false;
            _motor.SpeedScale = 1f;
        }

        // ───────────── skills & dodge ─────────────

        void TryUseSkill(int slot, float aim)
        {
            var s = _state.SkillAt(slot);
            if (s == null || !_state.SkillUnlocked(s)) return;
            if (_state.CooldownLeft(s.id) > 0.05f) return;
            if (s.useElement && _state.NeedsElement) { Notice?.Invoke("먼저 속성을 골라야 마법을 쓸 수 있어."); return; }
            if (s.manaCost > 0 && _state.Mana >= 0 && _state.Mana < s.manaCost) { Notice?.Invoke("마나가 부족해."); return; }

            float cdr = _state.Stats != null ? _state.Stats.CdReduction : 0f;
            _state.StartCooldown(s.id, s.cooldown * (1f - cdr));    // the server's SkillCooldown corrects this if it disagrees
            _net.Send(new UseSkill { Slot = (byte)slot, Yaw = aim });
            StopCharge();

            Yaw.ToDirection(aim, out float dx, out float dz);
            var dir = new Vector3(dx, 0, dz);
            _motor.FaceYaw(aim, 0.35f);
            switch (s.kind)
            {
                case "melee": case "circle": case "stance": case "blink": _view.PlaySwing(0.4f); break;
                case "ground": case "zone": case "chain": case "shield": _view.PlayShoot(); break;
                case "taunt": case "partyBuff": case "heal": case "groupHeal": case "revive": _view.PlayShoot(); break;
                case "dash": _view.PlaySwing(0.35f); _motor.ForcedMove(dir, s.distance, 0.18f, false); break;
                case "backstep": _motor.ForcedMove(-dir, s.distance, 0.22f, false); _view.PlayShoot(); break;
                case "projectile": case "volley": _view.PlayShoot(); break;
            }
            foreach (var fx in s.effects)
                if (fx.type == "buff") _state.SetBuff(fx.stat, fx.amount, s.duration);
            SkillFx.Play(s, transform.position, aim);
        }

        void TryDodge()
        {
            if (Time.time < _dodgeReadyAt || !_motor.Grounded || _motor.Rooted) return;
            var m = _state.Data.config.movement;
            _dodgeReadyAt = Time.time + m.dodgeCooldown;
            var dir = _motor.WishDirection();
            if (dir.sqrMagnitude < 0.01f) dir = -transform.forward;       // no key held: roll backwards
            _motor.ForcedMove(dir, m.dodgeDistance, m.dodgeDuration, true);
            _view.PlayDodge(m.dodgeDuration);
        }
    }
}
