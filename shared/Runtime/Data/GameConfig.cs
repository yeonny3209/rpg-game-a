using System;

namespace Game.Shared.Data
{
    // Schema for data/shared/game_config.json.
    // Public camelCase fields so both Unity JsonUtility and System.Text.Json (IncludeFields) can read it.
    // Never hardcode these values in code — always read them from the loaded config.

    [Serializable]
    public sealed class GameConfig
    {
        public MovementConfig movement = new MovementConfig();
        public ValidationConfig validation = new ValidationConfig();
        public NetConfig net = new NetConfig();
        public CombatConfig combat = new CombatConfig();
        public EconomyConfig economy = new EconomyConfig();
    }

    [Serializable]
    public sealed class MovementConfig
    {
        /// <summary>Spec "이동속도 100" stat value that equals runSpeedMps.</summary>
        public float baseMoveSpeedStat = 100f;
        /// <summary>Meters per second at move speed stat 100.</summary>
        public float runSpeedMps = 6f;
        public float gravity = 22f;
        public float jumpHeight = 1.5f;
        public float doubleJumpHeight = 1.3f;
        public int maxJumps = 2;
        /// <summary>Horizontal control while airborne (0..1).</summary>
        public float airControl = 0.6f;
        /// <summary>Holding Space in the air this long starts gliding.</summary>
        public float glideHoldDelay = 0.25f;
        public float glideFallSpeed = 2.5f;
        public float glideSpeedMps = 7f;
        public float dashDistance = 5f;
        public float dashDuration = 0.15f;
        public float dashCooldown = 2f;
        /// <summary>Right mouse for melee jobs: a quick roll with a short invulnerable moment (spec vol.5 12.2 조준 / 회피).</summary>
        public float dodgeDistance = 4f;
        public float dodgeDuration = 0.3f;
        public float dodgeCooldown = 3f;
        public float dodgeInvulnSec = 0.4f;
    }

    [Serializable]
    public sealed class ValidationConfig
    {
        /// <summary>Multiplier on the max legal speed to absorb network jitter.</summary>
        public float speedTolerance = 1.3f;
        /// <summary>Unused movement budget can be carried this long (seconds) to absorb bursts.</summary>
        public float budgetWindowSec = 0.5f;
        /// <summary>Extra meters allowed above the max jump apex.</summary>
        public float heightTolerance = 0.6f;
        /// <summary>How far below the terrain surface a position may be before it is corrected.</summary>
        public float underGroundTolerance = 1.0f;
        /// <summary>Attacks may arrive this fraction early (network jitter) before they count as too fast.</summary>
        public float attackIntervalTolerance = 0.8f;
        /// <summary>Extra degrees / meters the server accepts on top of the weapon's hit arc / range.</summary>
        public float hitArcToleranceDeg = 20f;
        public float hitRangeTolerance = 0.6f;
        /// <summary>Messages per second a client may send before it is disconnected.</summary>
        public int maxMessagesPerSecond = 120;
    }

    [Serializable]
    public sealed class NetConfig
    {
        public int serverTickRate = 20;
        public int clientSendRate = 20;
        public float interpolationDelaySec = 0.1f;
        public float pingIntervalSec = 2f;
        public float idleTimeoutSec = 30f;
        public int maxPlayersPerZone = 100;
        /// <summary>Changed characters are written to the DB at most this often (and always on logout).</summary>
        public float saveDebounceSec = 5f;
    }

    /// <summary>Spec vol.4 1.1 damage formula constants.</summary>
    [Serializable]
    public sealed class CombatConfig
    {
        /// <summary>피해 감소율 = 유효 방어력 / (유효 방어력 + defenseBase + defensePerAttackerLevel × 공격자 레벨)</summary>
        public float defenseBase = 400f;
        public float defensePerAttackerLevel = 40f;
        public float randomMin = 0.95f;
        public float randomMax = 1.05f;
        /// <summary>Monster body radius added to the hit range.</summary>
        public float defaultTargetRadius = 0.4f;
        /// <summary>Damage numbers etc. disappear after this long (client).</summary>
        public float damageNumberSec = 0.9f;
        /// <summary>Spec vol.4 1.3: 대상 등 뒤 120도 부채꼴이 후방, 후방 피해 2배.</summary>
        public float backArcDeg = 120f;
        public float backMultiplier = 2f;
        /// <summary>Spec vol.4 1.4: after a CC ends the target is immune to CC for this long.</summary>
        public float ccImmunitySec = 3f;
        /// <summary>둔화: move speed multiplier (a weak effect, not blocked by the immunity).</summary>
        public float slowMultiplier = 0.5f;
        /// <summary>Hit by a knockback: also stunned this long.</summary>
        public float knockbackStunSec = 0.3f;
        /// <summary>Monsters this far away from a projectile's path still count as hit (body radius is added).</summary>
        public float projectileHeightTolerance = 3f;
    }

    /// <summary>Spec vol.4 3.2 gold rules.</summary>
    [Serializable]
    public sealed class EconomyConfig
    {
        /// <summary>일반 몬스터 골드 = 기본 × (1 + 몬스터 레벨 × goldPerMonsterLevel)</summary>
        public float goldPerMonsterLevel = 0.1f;
    }
}
