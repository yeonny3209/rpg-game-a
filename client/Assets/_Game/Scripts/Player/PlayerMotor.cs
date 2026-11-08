using Game.Shared.Data;
using Game.Shared.Protocol;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Client.Player
{
    /// <summary>
    /// Local, client-predicted movement: WASD relative to camera, Space jump / double jump,
    /// hold Space in the air to glide, Shift dash. All numbers come from game_config.json (sent by the server).
    /// The server re-checks everything (MovementValidator) and corrects us if we break the rules.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class PlayerMotor : MonoBehaviour
    {
        public Transform CameraTransform;

        MovementConfig _m;
        CharacterController _cc;
        Vector3 _horizVel;
        float _vy;
        int _jumpCount;
        bool _gliding;
        float _spaceHeld;
        float _dashTimeLeft;
        double _dashReadyAt;
        Vector3 _dashDir;
        bool _wasGrounded;

        // Latched one-shot flags; LocalPlayerNet clears them after sending.
        public bool PendingDashStart;
        public bool PendingJumpStart;
        /// <summary>Set on the first state of a dodge roll; LocalPlayerNet sends it as MoveFlags.DodgeStart.</summary>
        public bool PendingDodgeStart;

        /// <summary>1 normally; the bow lowers it while drawn (spec vol.2 5.5: 이속 100 → 80).</summary>
        public float SpeedScale = 1f;
        public float SlowMultiplier = 0.5f;

        // Crowd control from the server (stun / freeze: no actions, root: no movement, slow: slower).
        float _stunUntil, _rootUntil, _slowUntil;
        // A forced move: skill dash / back step / dodge roll.
        float _forcedTime;
        Vector3 _forcedVel;
        public bool Stunned => Time.time < _stunUntil;
        public bool Rooted => Time.time < _rootUntil;
        public bool Slowed => Time.time < _slowUntil;
        public bool ForcedMoving => _forcedTime > 0;

        // 비행 (spec vol.4 1.5): the server sets the limits; we fly with Space up / Ctrl down.
        public bool Flying { get; private set; }
        float _flyMul = 1.4f, _flyClimb = 6f, _flyMaxH = 25f;
        bool _landing;

        public void SetFlight(bool on, FlightRules f)
        {
            if (on && f != null) { _flyMul = f.speedMultiplier; _flyClimb = f.climbSpeed; _flyMaxH = f.maxHeight; }
            if (on && !Flying) { _vy = 0; _gliding = false; _forcedTime = 0; }
            if (!on && Flying) _landing = true;           // glide down gently so the landing is smooth
            Flying = on;
        }

        float GroundHeightBelow()
        {
            if (Physics.Raycast(transform.position + Vector3.up * 0.5f, Vector3.down, out var hit, 200f, ~0, QueryTriggerInteraction.Ignore))
                return transform.position.y - hit.point.y;
            return 0f;
        }

        void FlyStep(float dt, bool canMove, Vector3 wish, float speedMul, bool stunned)
        {
            var kb = Keyboard.current;
            float up = 0f;
            if (canMove && kb != null)
            {
                if (kb.spaceKey.isPressed) up += 1f;
                if (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed) up -= 1f;
            }
            if (!canMove) wish = Vector3.zero;
            Vector3 target = wish * _m.runSpeedMps * speedMul * _flyMul;
            _horizVel = Vector3.Lerp(_horizVel, target, 1f - Mathf.Exp(-8f * dt));
            float h = GroundHeightBelow();
            if (up > 0 && h >= _flyMaxH - 1f) up = 0f;     // the ceiling
            _vy = Mathf.Lerp(_vy, up * _flyClimb, 1f - Mathf.Exp(-8f * dt));
            _cc.Move((_horizVel + Vector3.up * _vy) * dt);
            if (stunned) return;
            Face(dt);
        }

        void Face(float dt)
        {
            if (_faceLock > 0) { _faceLock -= dt; return; }
            Vector3 face = _horizVel; face.y = 0;
            if (face.sqrMagnitude > 0.05f)
            {
                var target = Quaternion.LookRotation(face.normalized, Vector3.up);
                transform.rotation = Quaternion.Slerp(transform.rotation, target, 1f - Mathf.Exp(-14f * dt));
            }
        }

        public void ApplyCc(CcType type, float duration)
        {
            float until = Time.time + Mathf.Max(0f, duration);
            switch (type)
            {
                case CcType.Stun:
                case CcType.Freeze: _stunUntil = Mathf.Max(_stunUntil, until); _forcedTime = 0; break;
                case CcType.Root: _rootUntil = Mathf.Max(_rootUntil, until); break;
                case CcType.Slow: _slowUntil = Mathf.Max(_slowUntil, until); break;
            }
        }

        /// <summary>Move `distance` meters along dir in `duration` seconds (dash skills, back step, dodge). The server allows exactly that extra distance.</summary>
        public void ForcedMove(Vector3 dir, float distance, float duration, bool dodge)
        {
            if (Stunned) return;
            dir.y = 0;
            if (dir.sqrMagnitude < 0.0001f) dir = transform.forward;
            dir.Normalize();
            _forcedVel = dir * (distance / Mathf.Max(0.05f, duration));
            _forcedTime = duration;
            if (dodge) PendingDodgeStart = true;
        }

        /// <summary>WASD direction relative to the camera right now (zero when no key is held).</summary>
        public Vector3 WishDirection()
        {
            var kb = Keyboard.current;
            if (kb == null) return Vector3.zero;
            Vector2 i = Vector2.zero;
            if (kb.wKey.isPressed) i.y += 1;
            if (kb.sKey.isPressed) i.y -= 1;
            if (kb.dKey.isPressed) i.x += 1;
            if (kb.aKey.isPressed) i.x -= 1;
            if (i.sqrMagnitude < 0.01f) return Vector3.zero;
            Vector3 f = CameraTransform ? CameraTransform.forward : Vector3.forward;
            f.y = 0;
            f = f.sqrMagnitude > 0.001f ? f.normalized : Vector3.forward;
            return (f * i.y + new Vector3(f.z, 0, -f.x) * i.x).normalized;
        }

        public bool InputEnabled = true;
        /// <summary>Until the first quest unlocks it, we may not leave this circle (null = free).</summary>
        public CircleDef AreaLimit;
        public event System.Action AreaBlocked;
        float _areaWarnAt = -100f;
        float _faceLock;

        /// <summary>Turn instantly (attack direction) and keep that facing briefly.</summary>
        public void FaceYaw(float yaw, float hold)
        {
            transform.rotation = Quaternion.Euler(0, yaw, 0);
            _faceLock = hold;
        }
        public bool Grounded => _cc != null && _cc.isGrounded;
        public bool Gliding => _gliding;
        public bool Dashing => _dashTimeLeft > 0;
        public int JumpCount => _jumpCount;
        public float Yaw => transform.eulerAngles.y;
        public float DashCooldownLeft => Mathf.Max(0f, (float)(_dashReadyAt - Time.timeAsDouble));
        public float HorizontalSpeed => new Vector2(_horizVel.x, _horizVel.z).magnitude;

        public void Init(MovementConfig cfg)
        {
            _m = cfg;
            _cc = GetComponent<CharacterController>();
        }

        public MoveFlags CurrentFlags()
        {
            var f = MoveFlags.None;
            if (Grounded) f |= MoveFlags.Grounded;
            if (_gliding) f |= MoveFlags.Gliding;
            if (Dashing) f |= MoveFlags.Dashing;
            if (PendingDashStart) f |= MoveFlags.DashStart;
            if (PendingJumpStart) f |= MoveFlags.JumpStart;
            if (PendingDodgeStart) f |= MoveFlags.DodgeStart;
            return f;
        }

        /// <summary>Hard-set position (spawn / server correction).</summary>
        public void Teleport(Vector3 pos)
        {
            _cc.enabled = false;
            transform.position = pos;
            _cc.enabled = true;
            Physics.SyncTransforms(); // new colliders (world just built) must be in the physics scene before the first Move
            _horizVel = Vector3.zero;
            _vy = 0;
            _gliding = false;
            _dashTimeLeft = 0;
            _forcedTime = 0;
            _landing = false;
        }

        void Update()
        {
            if (_m == null || _cc == null) return;
            float dt = Mathf.Min(Time.deltaTime, 0.1f); // a loading hitch must not turn into one giant fall step
            var kb = Keyboard.current;
            bool stunned = Stunned, rooted = Rooted;
            bool canAct = InputEnabled && !stunned;      // 기절·빙결: 아무것도 못 한다
            bool canMove = canAct && !rooted;            // 속박: 공격은 되지만 이동·점프·대시는 안 된다

            // ── input direction relative to camera ──
            Vector2 input = Vector2.zero;
            if (canMove && kb != null)
            {
                if (kb.wKey.isPressed) input.y += 1;
                if (kb.sKey.isPressed) input.y -= 1;
                if (kb.dKey.isPressed) input.x += 1;
                if (kb.aKey.isPressed) input.x -= 1;
            }
            input = Vector2.ClampMagnitude(input, 1f);
            Vector3 camFwd = CameraTransform ? CameraTransform.forward : Vector3.forward;
            camFwd.y = 0;
            camFwd = camFwd.sqrMagnitude > 0.001f ? camFwd.normalized : Vector3.forward;
            Vector3 camRight = new Vector3(camFwd.z, 0, -camFwd.x);
            Vector3 wish = camFwd * input.y + camRight * input.x;

            if (Flying)
            {
                FlyStep(dt, canMove, wish, SpeedScale * (Slowed ? SlowMultiplier : 1f), stunned);
                return;
            }

            bool grounded = _cc.isGrounded;
            if (grounded)
            {
                _landing = false;
                if (_vy < 0) _vy = -2f; // keep snapped to slopes
                _jumpCount = 0;
                _gliding = false;
            }
            else if (_wasGrounded && _jumpCount == 0)
            {
                _jumpCount = 1; // walked off a ledge: only the air jump remains
            }
            _wasGrounded = grounded;

            bool spaceDown = canMove && kb != null && kb.spaceKey.wasPressedThisFrame;
            bool spaceHeld = canMove && kb != null && kb.spaceKey.isPressed;
            bool dashDown = canMove && kb != null && (kb.leftShiftKey.wasPressedThisFrame || kb.rightShiftKey.wasPressedThisFrame);

            // ── jump / double jump ──
            if (spaceDown)
            {
                _spaceHeld = 0;
                if (grounded)
                {
                    _vy = Mathf.Sqrt(2f * _m.gravity * _m.jumpHeight);
                    _jumpCount = 1;
                    PendingJumpStart = true;
                }
                else if (_jumpCount < _m.maxJumps)
                {
                    _vy = Mathf.Sqrt(2f * _m.gravity * _m.doubleJumpHeight);
                    _jumpCount++;
                    _gliding = false;
                    PendingJumpStart = true;
                }
            }

            // ── glide: hold Space in the air ──
            if (!grounded && (spaceHeld || _landing))
            {
                _spaceHeld += dt;
                if ((_spaceHeld >= _m.glideHoldDelay || _landing) && _vy <= 0) _gliding = true;
            }
            else
            {
                _gliding = false;
                if (!spaceHeld) _spaceHeld = 0;
            }

            // ── dash ──
            if (dashDown && Time.timeAsDouble >= _dashReadyAt)
            {
                _dashDir = wish.sqrMagnitude > 0.01f ? wish.normalized : transform.forward;
                _dashTimeLeft = _m.dashDuration;
                _dashReadyAt = Time.timeAsDouble + _m.dashCooldown;
                PendingDashStart = true;
            }

            // ── horizontal velocity ──
            float speedMul = SpeedScale * (Slowed ? SlowMultiplier : 1f);
            if (stunned && grounded)
            {
                _horizVel = Vector3.zero;
            }
            else if (_forcedTime > 0)
            {
                _horizVel = _forcedVel;
                _forcedTime -= dt;
                if (_forcedTime <= 0) _horizVel = Vector3.zero;
            }
            else if (rooted && grounded)
            {
                _horizVel = Vector3.zero;
            }
            else if (_dashTimeLeft > 0)
            {
                _horizVel = _dashDir * (_m.dashDistance / _m.dashDuration);
                _dashTimeLeft -= dt;
                if (_dashTimeLeft <= 0) _horizVel = _dashDir * _m.runSpeedMps * speedMul;
            }
            else if (_gliding)
            {
                Vector3 dir = wish.sqrMagnitude > 0.01f ? wish.normalized : transform.forward;
                _horizVel = Vector3.Lerp(_horizVel, dir * _m.glideSpeedMps, 1f - Mathf.Exp(-4f * dt));
            }
            else if (grounded)
            {
                _horizVel = wish * _m.runSpeedMps * speedMul;
            }
            else
            {
                Vector3 target = wish * _m.runSpeedMps * speedMul;
                _horizVel = Vector3.Lerp(_horizVel, target, 1f - Mathf.Exp(-8f * _m.airControl * dt));
            }

            // ── vertical ──
            if (_dashTimeLeft > 0 || _forcedTime > 0) _vy = 0f; // flat dash
            else if (_gliding) _vy = Mathf.Max(_vy - _m.gravity * dt, -_m.glideFallSpeed);
            else _vy -= _m.gravity * dt;

            var flags = _cc.Move((_horizVel + Vector3.up * _vy) * dt);
            if ((flags & CollisionFlags.Above) != 0 && _vy > 0) _vy = 0;

            // ── start area (server enforces it too; this just stops us running into corrections) ──
            if (AreaLimit != null && AreaLimit.radius > 0)
            {
                var p = transform.position;
                float dx = p.x - AreaLimit.x, dz = p.z - AreaLimit.z;
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                float max = AreaLimit.radius - 0.2f;
                if (d > max)
                {
                    var target = new Vector3(AreaLimit.x + dx / d * max, p.y, AreaLimit.z + dz / d * max);
                    _cc.Move(target - p);
                    if (Time.time - _areaWarnAt > 3f) { _areaWarnAt = Time.time; AreaBlocked?.Invoke(); }
                }
            }

            // ── face movement direction (unless an attack just turned us) ──
            if (stunned) return;
            Face(dt);
        }
    }
}
