using Game.Server.Data;
using Game.Server.Db;
using Game.Server.Logic;
using Game.Server.Net;
using Game.Shared.Data;
using Game.Shared.Protocol;

namespace Game.Server.World;

/// <summary>A timed buff from a skill (spec vol.2 6 / vol.4 1.1: same kind adds, different kinds multiply).</summary>
public sealed class ActiveBuff
{
    public string SkillId;
    /// <summary>instantInterval | instantCoefficient | chargeRate | damage</summary>
    public string Stat;
    public float Amount;
    public double Until;
    /// <summary>Who cast it (buffers' party buffs stack per caster, spec vol.2 5.6).</summary>
    public uint Source;
}

/// <summary>A job-change trial in progress (decision 2026-11-07: duel against an opponent the master summons).</summary>
public sealed class TrialRun
{
    public string JobId;
    public MonsterEntity Opponent;
    public NpcEntity Master;
    public double EndsAt;
    public enum Outcome { Running, Won, Failed, Cancelled }
    public Outcome Result;
    public string FailReason;
}

public sealed class PlayerEntity
{
    public uint EntityId;
    public IClientConnection Conn;
    public CharacterRecord Rec;
    public Inventory Inv;
    public QuestLog Quests;
    public CombatStats Stats;
    public readonly MoveAuthority Move = new();

    public string Name => Rec.Name;

    /// <summary>club | sword | bow of what is in hand (the trial's lent weapon counts). Others see it.</summary>
    public string WeaponType = "";

    public bool Dead;
    public double DeadAt;
    public double LastAttackAt = -999;
    public double LastCombatAt = -999;
    public float RegenAcc;
    public uint AttackSeq;

    // Open dialogue (server remembers it so a client can't pick options of a node it was never shown).
    public uint DialogueNpc;
    /// <summary>The NPC whose shop is open and what it showed (item, grade index), so ShopBuy indices cannot be forged.</summary>
    public uint ShopNpc;
    public readonly List<(string item, int grade)> ShopView = new();
    public string DialogueNode;

    // Pending client syncs, flushed once at the end of the tick.
    public bool CharDirty, InvDirty, QuestDirty, SkillDirty;

    // Persistence.
    public bool SaveDirty;
    public bool SaveNow;
    public double LastSaveAt;
    public readonly List<AuditEntry> PendingAudits = new();

    // ── skills ──
    public readonly Dictionary<string, double> SkillReadyAt = new();
    public readonly List<ActiveBuff> Buffs = new();
    /// <summary>반격 자세: until this time the next hit is blocked and answered.</summary>
    public double CounterUntil;
    public SkillDef CounterSkill;
    /// <summary>Dodge roll: no damage until then.</summary>
    public double InvulnUntil;

    // ── crowd control (spec vol.4 1.4) ──
    public double StunUntil, FrozenUntil, RootUntil, SlowUntil, CcImmuneUntil;

    // ── archer ──
    public double ChargeStartedAt = -1;
    public int Ammo;
    public double ReloadEndsAt;
    public double NextShotAt;

    // ── job trial ──
    public TrialRun Trial;
    public string TrialJob => Trial != null && Trial.Result == TrialRun.Outcome.Running ? Trial.JobId : null;

    // ── chat ──
    public double LastChatAt = -999;
    public double LastWorldChatAt = -999;
    public double LastCapNoticeAt = -999;

    // ── mage / flight ──
    public float Mana;
    public int LastManaSent = -1;
    /// <summary>마력 방패: damage it can still absorb until ShieldUntil.</summary>
    public float ShieldAmount;
    /// <summary>서양 검사: 마나 주입 토글.</summary>
    public bool Infused;
    public double ShieldUntil;
    public double FlyReadyAt;

    public bool InCombat(double now, float timeout) => now - LastCombatAt < timeout;
    public bool CannotAct(double now) => now < StunUntil || now < FrozenUntil;
    public bool Immobile(double now) => now < StunUntil || now < FrozenUntil || now < RootUntil;

    public float BuffSum(string stat, double now)
    {
        float sum = 0;
        foreach (var b in Buffs) if (b.Stat == stat && b.Until > now) sum += b.Amount;
        return sum;
    }

    /// <summary>Latest buff value for "set" style stats (instantInterval / instantCoefficient / chargeRate). 0 when none.</summary>
    public float BuffValue(string stat, double now)
    {
        float v = 0;
        foreach (var b in Buffs) if (b.Stat == stat && b.Until > now) v = b.Amount;
        return v;
    }
}

public enum MonsterAi : byte { Idle, Chase, Returning }

public sealed class MonsterEntity
{
    public uint EntityId;
    /// <summary>전사의 도발: until then the monster keeps its target.</summary>
    public double TauntUntil;
    public MonsterDef Def;
    public SpawnDef Spawn;
    public float HomeX, HomeZ;
    public float X, Y, Z, Yaw;
    public int Hp;
    public MonsterAi Ai;
    /// <summary>Spec vol.4 1.2 확정: monsters attack whoever hit them last.</summary>
    public PlayerEntity Target;
    public double NextAttackAt;
    public double NextWanderAt;
    public bool HasWanderGoal;
    public float WanderX, WanderZ;
    public bool Dead;
    public double RemoveAt;

    /// <summary>Set for a job-trial opponent: only this player can hurt it and it only hunts this player.</summary>
    public PlayerEntity Owner;

    public double StunUntil, FrozenUntil, RootUntil, SlowUntil, CcImmuneUntil;
    public bool CannotAct(double now) => now < StunUntil || now < FrozenUntil;

    // 화상 (burn) from a fire spell
    public double BurnUntil, BurnNextTick;
    public int BurnTickDamage;
    public PlayerEntity BurnOwner;
}

public sealed class NpcEntity
{
    public uint EntityId;
    public NpcDef Def;
    public float X, Y, Z, Yaw;
}

/// <summary>A flying arrow / sword wave. Clients fly their own copy; only the server decides who it hits.</summary>
public sealed class ProjectileEntity
{
    public uint Id;
    public PlayerEntity Owner;
    public float X, Y, Z;
    public float DirX, DirZ, Yaw;
    public float Speed, Range, Radius;
    public float Traveled;
    public int Pierce;
    public float Coefficient, Penetration;
    public SkillDef Skill;
    public ProjectileKind Kind;
    /// <summary>Element of a mage spell ("none" for arrows and waves).</summary>
    public string Element;
    public readonly HashSet<uint> Hit = new();
    /// <summary>Counts the skill's mastery once, on the first hit.</summary>
    public CastContext Cast;
}

/// <summary>One use of a skill: lets a multi-hit or multi-projectile skill count mastery and training only once.</summary>
public sealed class CastContext
{
    public PlayerEntity Player;
    public SkillDef Skill;
    public int SkillLevel;
    public bool Landed;
}
