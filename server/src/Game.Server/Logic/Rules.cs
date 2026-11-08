using Game.Shared.Data;
using Game.Shared.World;

namespace Game.Server.Logic;

/// <summary>Derived combat stats of a character (spec vol.2 2.2 / 2.3).</summary>
public readonly record struct CombatStats(
    int MaxHp, int Attack, int Defense, float CritChance, float CritDamage, WeaponStats Weapon,
    float AttackSpeed, float MoveSpeed, float CdReduction, float Penetration, float CcResist, float Lifesteal,
    float HpRegen, float ManaRegen, int MaxMana, float HealPower, int TrainingLevel, float TrainingBonus);

/// <summary>Everything the damage formula (spec vol.4 1.1) needs, with 1 = "no effect" defaults for the optional multipliers.</summary>
public struct DamageInput
{
    public float Attack;
    public float Coefficient;
    public int AttackerLevel;
    public int TargetDefense;
    public float Penetration;
    public float CritChance;
    public float CritDamage;
    /// <summary>Skill level bonus: 1 + powerPerLevel × (level − 1).</summary>
    public float LevelMultiplier;
    public float ElementMultiplier;
    /// <summary>후방·은신 2배 etc.</summary>
    public float ConditionMultiplier;
    /// <summary>Buff / debuff multipliers (same kind adds, different kinds multiply).</summary>
    public float BuffMultiplier;

    public static DamageInput Basic(float attack, int attackerLevel, int targetDefense, float critChance, float critDamage) => new()
    {
        Attack = attack, Coefficient = 1f, AttackerLevel = attackerLevel, TargetDefense = targetDefense,
        CritChance = critChance, CritDamage = critDamage,
        LevelMultiplier = 1f, ElementMultiplier = 1f, ConditionMultiplier = 1f, BuffMultiplier = 1f,
    };
}

/// <summary>Pure formulas. Every number comes from the data tables.</summary>
public static class Rules
{
    /// <summary>Training needed to finish level `level` (0-based): perLevel, growing a little each level. Rounded so float noise cannot move a threshold.</summary>
    static long TrainingNeed(TrainingRules t, int level) => (long)Math.Round(t.perLevel * (1.0 + (double)t.growthPerLevel * level), MidpointRounding.AwayFromZero);

    /// <summary>Training levels from the total (spec vol.2 3.1: 단계가 오를수록 필요 훈련량이 늘어난다).</summary>
    public static int TrainingLevel(TrainingRules t, long training)
    {
        if (t == null || !t.enabled || t.perLevel < 1) return 0;
        int level = 0;
        long need = TrainingNeed(t, 0);
        while (training >= need && level < 500)
        {
            training -= need;
            level++;
            need = TrainingNeed(t, level);
        }
        return level;
    }

    /// <summary>Training still needed for the next level (0 when the job has no training).</summary>
    public static long TrainingToNext(TrainingRules t, long training)
    {
        if (t == null || !t.enabled || t.perLevel < 1) return 0;
        int level = TrainingLevel(t, training);
        long spent = 0;
        for (int i = 0; i <= level; i++) spent += TrainingNeed(t, i);
        return Math.Max(0, spent - training);
    }

    public static float StatValue(CharacterRecord c, string stat) => stat switch
    {
        "str" => c.Str, "dex" => c.Dex, "int" => c.Int, "con" => c.Con, "spi" => c.Spi, _ => 0,
    };

    public static CombatStats Stats(SharedData d, CharacterRecord c, Inventory inv, string trialJob = null)
    {
        var r = d.character;
        var fx = r.stats;
        var job = d.Job(c.Job);
        var weaponDef = inv.Weapon;
        // During a job trial the master lends the job's starter weapon and first skill.
        if (job == null && trialJob != null)
        {
            job = d.Job(trialJob);
            weaponDef = d.Item(job?.starterWeapon) ?? weaponDef;
        }
        var weapon = weaponDef?.weapon ?? r.unarmed;
        var gear = GearBonus.Sum(d, inv, trialJob != null);
        if (weaponDef != null && trialJob == null && gear.WeaponAttack > 0)
            weapon = new WeaponStats { attack = gear.WeaponAttack, range = weapon.range, arcDeg = weapon.arcDeg, interval = weapon.interval };
        int start = r.startStat;

        int maxHp = r.hpBase + r.hpPerLevel * (c.Level - 1) + r.hpPerCon * c.Con + gear.Hp;
        float attack = r.attackBase + r.attackPerLevel * (c.Level - 1) + weapon.attack + gear.Attack;
        float defense = c.Str * r.defensePerStr + c.Con * r.defensePerCon + gear.Defense;

        // 직업의 주 스탯은 공격력을 올린다 (체력이 주 스탯인 전사는 공격력 +1, 방어력 +1).
        if (job != null)
        {
            if (job.primaryStat == "con")
            {
                attack += fx.conPrimaryAttack * (c.Con - start);
                defense += fx.conPrimaryDefense * (c.Con - start);
            }
            else attack += fx.attackPerPrimary * (StatValue(c, job.primaryStat) - start);
        }
        int trainingLevel = job != null ? TrainingLevel(job.training, c.Training) : 0;
        float trainingBonus = job != null && job.training != null && job.training.enabled ? trainingLevel * job.training.attackPerLevel : 0f;
        attack *= 1f + trainingBonus;
        attack *= 1f + gear.Opt("attackPct");
        defense *= 1f + gear.Opt("defensePct");
        maxHp = (int)MathF.Round(maxHp * (1f + gear.Opt("maxHpPct")));

        float dex = Math.Max(0, c.Dex - start), spi = Math.Max(0, c.Spi - start);
        float crit = Math.Min(fx.critChanceCap, r.critChance + dex * fx.critPerDex + gear.Opt("critChance"));
        float critDmg = Math.Min(fx.critDamageCap, r.critDamage + gear.Opt("critDamage"));
        float atkSpeed = Math.Min(fx.attackSpeedCap, 1f + dex * fx.attackSpeedPerDex + gear.Opt("attackSpeed"));
        float cdr = Math.Min(fx.cdrCap, spi * fx.cdrPerSpi + gear.Opt("cdr"));
        float ccRes = Math.Min(fx.ccResistCap, spi * fx.ccResistPerSpi + gear.Opt("ccResist"));
        int maxMana = (int)MathF.Round((fx.manaBase + fx.manaPerInt * c.Int) * (1f + gear.Opt("maxManaPct")));
        float manaRegen = fx.manaRegenPerInt * c.Int;
        float healPower = spi * fx.healPowerPerSpi;

        return new CombatStats(Math.Max(1, maxHp), Math.Max(0, (int)MathF.Round(attack)), Math.Max(0, (int)MathF.Round(defense)),
            crit, critDmg, weapon, atkSpeed, Math.Min(fx.moveSpeedCap, 1f), cdr, Math.Min(0.9f, gear.Opt("penetration")), ccRes, gear.Opt("lifesteal"),
            c.Con * r.regenInCombatPerCon + gear.Opt("hpRegen"), manaRegen, Math.Max(0, maxMana), healPower, trainingLevel, trainingBonus);
    }

    /// <summary>Spec vol.4 1.1 in full: 공격력 × 계수 × 스킬 레벨 × (1 − 피해 감소율) × 치명타 × 속성 × 조건 × 버프 × 무작위.</summary>
    public static int Compute(CombatConfig cfg, in DamageInput i, Random rng, out bool crit)
    {
        float def = Math.Max(0, i.TargetDefense) * (1f - Math.Clamp(i.Penetration, 0f, 1f));
        float reduction = def / (def + cfg.defenseBase + cfg.defensePerAttackerLevel * i.AttackerLevel);
        crit = rng.NextDouble() < i.CritChance;
        float roll = cfg.randomMin + (float)rng.NextDouble() * (cfg.randomMax - cfg.randomMin);
        float dmg = i.Attack * i.Coefficient * i.LevelMultiplier * (1f - reduction) * (crit ? i.CritDamage : 1f)
                    * i.ElementMultiplier * i.ConditionMultiplier * i.BuffMultiplier * roll;
        return Math.Max(1, (int)Math.Round(dmg, MidpointRounding.AwayFromZero));
    }

    /// <summary>True when the attacker stands in the 120° fan behind the target (spec vol.4 1.3). Yaw: 0 = +Z, same as Yaw.FromDirection.</summary>
    public static bool IsBehind(float targetX, float targetZ, float targetYaw, float attackerX, float attackerZ, float arcDeg)
    {
        float toAttacker = Yaw.FromDirection(attackerX - targetX, attackerZ - targetZ);
        return Yaw.Delta(toAttacker, targetYaw + 180f) <= arcDeg * 0.5f;
    }

    /// <summary>
    /// Spec vol.4 1.1 (the stage 1 subset: 공격력 × 계수 × (1 − 피해 감소율) × 치명타 × 무작위).
    /// 피해 감소율 = 방어력 / (방어력 + defenseBase + defensePerAttackerLevel × 공격자 레벨).
    /// </summary>
    public static int Damage(CombatConfig cfg, int attack, int attackerLevel, int targetDefense,
                             float coefficient, float critChance, float critDamage, Random rng, out bool crit)
    {
        var i = DamageInput.Basic(attack, attackerLevel, targetDefense, critChance, critDamage);
        i.Coefficient = coefficient;
        return Compute(cfg, i, rng, out crit);
    }

    /// <summary>Spec vol.2 1.3: monsters 10+ levels below you give 10% EXP.</summary>
    public static long MonsterExp(ExpTable t, int monsterExp, int monsterLevel, int playerLevel)
    {
        if (playerLevel - monsterLevel >= t.lowLevelGap)
            return Math.Max(1, (long)Math.Round(monsterExp * (double)t.lowLevelExpRate, MidpointRounding.AwayFromZero));
        return monsterExp;
    }

    /// <summary>Spec vol.4 3.2: 1~3 × (1 + 몬스터 레벨 / 10).</summary>
    public static int MonsterGold(EconomyConfig e, int goldMin, int goldMax, int monsterLevel, Random rng)
    {
        if (goldMax <= 0) return 0;
        int baseGold = rng.Next(goldMin, goldMax + 1);
        return (int)Math.Round(baseGold * (1.0 + monsterLevel * (double)e.goldPerMonsterLevel), MidpointRounding.AwayFromZero);
    }

    /// <summary>Adds EXP and applies level-ups. Returns the number of levels gained.</summary>
    public static int AddExp(SharedData d, CharacterRecord c, long amount, DateTime now)
    {
        if (amount <= 0) return 0;
        var t = d.exp;
        int gained = 0;
        c.Exp += amount;
        while (c.Level < t.maxLevel)
        {
            long need = t.ToNext(c.Level);
            if (need <= 0 || c.Exp < need) break;
            c.Exp -= need;
            c.Level++;
            c.StatPoints += d.character.statPointsPerLevel;
            gained++;
        }
        if (c.Level >= t.maxLevel) c.Exp = 0;
        if (gained > 0) c.LastLevelUpAt = now;
        return gained;
    }

    /// <summary>EXP that takes the character exactly to the start of targetLevel (0 if already there).</summary>
    public static long ExpToReach(ExpTable t, CharacterRecord c, int targetLevel)
    {
        if (c.Level >= targetLevel) return 0;
        long total = t.ToNext(c.Level) - c.Exp;
        for (int lv = c.Level + 1; lv < targetLevel; lv++) total += t.ToNext(lv);
        return Math.Max(0, total);
    }

    public static CharacterRecord NewCharacter(SharedData d, string name, string zone)
    {
        int s = d.character.startStat;
        var c = new CharacterRecord
        {
            Name = name, Level = 1, Zone = zone,
            Str = s, Dex = s, Int = s, Con = s, Spi = s,
            LastLevelUpAt = DateTime.UtcNow,
        };
        c.Hp = Stats(d, c, new Inventory(d, d.character.bagSlots)).MaxHp;
        return c;
    }
}
