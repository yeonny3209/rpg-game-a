using Game.Server.Logic;
using Game.Server.Net;
using Game.Shared.Data;
using Game.Shared.Protocol;
using Game.Shared.World;

namespace Game.Server.World;

/// <summary>Stat points, jobs, the 1st job-change trial (결정 기록 2026-11-07), skill sync and 동양 검사 training.</summary>
public sealed partial class Zone
{
    /// <summary>Test hook: "today" for the daily training cap.</summary>
    internal Func<DateTime> Clock = () => DateTime.Now;

    // ───────────── test hooks ─────────────
    internal int CurrentDay() => Today();
    internal void TestDirtySkills(PlayerEntity p) { p.SkillDirty = true; }
    internal void TestApplyCc(PlayerEntity p, string type, float duration, float distance = 0f) =>
        ApplyCcToPlayer(p, type, duration, distance, p.Move.X, p.Move.Z - 2f);

    // ───────────── stat points (spec vol.2 2.1) ─────────────

    void OnAllocateStats(PlayerEntity p, AllocateStats m)
    {
        int total = m.Str + m.Dex + m.Int + m.Con + m.Spi;
        if (total <= 0) return;
        if (total > p.Rec.StatPoints)
        {
            p.Conn.Send(new Notice { Kind = NoticeKind.Error, Text = "스탯 포인트가 모자라." });
            p.CharDirty = true; // resync what the client believes it has
            return;
        }
        p.Rec.Str += m.Str; p.Rec.Dex += m.Dex; p.Rec.Int += m.Int; p.Rec.Con += m.Con; p.Rec.Spi += m.Spi;
        p.Rec.StatPoints -= total;
        RecalcStats(p);
        p.SaveDirty = true;
        Audit(p, "stats_allocated", new { m.Str, m.Dex, m.Int, m.Con, m.Spi, left = p.Rec.StatPoints });
    }

    /// <summary>Points a character is entitled to in total: 5 per level above 1 and the job-change bonus.</summary>
    int TotalStatPoints(CharacterRecord r) =>
        _d.character.statPointsPerLevel * (r.Level - 1) + _d.character.jobChangeStatPoints * Math.Min(r.JobTier, 3);

    /// <summary>Free reset (spec vol.2 2.1: 전직 때 무료). Used by the job change and the dev command.</summary>
    void ResetStats(PlayerEntity p)
    {
        int s = _d.character.startStat;
        p.Rec.Str = p.Rec.Dex = p.Rec.Int = p.Rec.Con = p.Rec.Spi = s;
        p.Rec.StatPoints = TotalStatPoints(p.Rec);
        RecalcStats(p);
        p.SaveDirty = true;
    }

    // ───────────── skill sync ─────────────

    SkillSync BuildSkillSync(PlayerEntity p)
    {
        var job = EffectiveJob(p);
        var m = new SkillSync { Job = p.Rec.Job ?? "", JobTier = (byte)p.Rec.JobTier, Training = p.Rec.Training, TrainingToday = TrainingTodayNow(p), Element = p.Rec.Element ?? "", Circle = (byte)p.Rec.Circle };
        if (job != null)
            foreach (var id in job.skills)
            {
                var row = p.Rec.Skills.FirstOrDefault(r => r.SkillId == id);
                m.Skills.Add(new SkillStatus { SkillId = id, Level = (byte)(row?.Level ?? 1), Uses = row?.Uses ?? 0 });
            }
        return m;
    }

    StatsSync BuildStatsSync(PlayerEntity p)
    {
        var s = p.Stats;
        return new StatsSync
        {
            Attack = s.Attack, Defense = s.Defense, MaxHp = s.MaxHp, MaxMana = s.MaxMana,
            CritChance = s.CritChance, CritDamage = s.CritDamage, AttackSpeed = s.AttackSpeed, MoveSpeed = s.MoveSpeed,
            CdReduction = s.CdReduction, Penetration = s.Penetration, CcResist = s.CcResist, Lifesteal = s.Lifesteal,
            HpRegen = s.HpRegen, ManaRegen = s.ManaRegen, HealPower = s.HealPower,
            TrainingLevel = s.TrainingLevel, TrainingBonus = s.TrainingBonus,
        };
    }

    // ───────────── 훈련량 (spec vol.2 3.1) ─────────────

    int Today() { var d = Clock(); return d.Year * 10000 + d.Month * 100 + d.Day; }

    int TrainingTodayNow(PlayerEntity p) => p.Rec.TrainingDay == Today() ? p.Rec.TrainingToday : 0;

    /// <summary>Every hit adds training (skill hits more). The dummies at the training ground count too, but only up to a daily cap.</summary>
    void GainTraining(PlayerEntity p, bool skillHit, bool onDummy)
    {
        var job = _d.Job(p.Rec.Job);
        var t = job?.training;
        if (t == null || !t.enabled) return;
        int gain = skillHit ? t.perSkillHit : t.perHit;
        if (onDummy)
        {
            int today = Today();
            if (p.Rec.TrainingDay != today) { p.Rec.TrainingDay = today; p.Rec.TrainingToday = 0; }
            gain = Math.Min(gain, Math.Max(0, t.dailyCap - p.Rec.TrainingToday));
            if (gain <= 0)
            {
                if (_now - p.LastCapNoticeAt > 8)
                {
                    p.LastCapNoticeAt = _now;
                    p.Conn.Send(new Notice { Kind = NoticeKind.Info, Text = "오늘 수련장에서 쌓을 수 있는 훈련량을 다 채웠어." });
                }
                return;
            }
            p.Rec.TrainingToday += gain;
        }
        if (gain <= 0) return;
        int before = Rules.TrainingLevel(t, p.Rec.Training);
        p.Rec.Training += gain;
        p.SkillDirty = p.SaveDirty = true;
        if (Rules.TrainingLevel(t, p.Rec.Training) > before)
        {
            RecalcStats(p);
            Gain(p, GainKind.TrainingLevelUp, "", Rules.TrainingLevel(t, p.Rec.Training));
            Audit(p, "training_level", new { level = Rules.TrainingLevel(t, p.Rec.Training), training = p.Rec.Training });
        }
    }

    // ───────────── job change ─────────────

    /// <summary>1st job change (spec vol.2 4.1): the job, +10 stat points, the starter weapon. Stats are not reset automatically — the points are simply new.</summary>
    void ChangeJob(PlayerEntity p, JobDef job, string source)
    {
        p.Rec.Job = job.id;
        p.Rec.JobTier = 1;
        p.Rec.StatPoints += _d.character.jobChangeStatPoints;
        p.Rec.Training = 0; p.Rec.TrainingToday = 0; p.Rec.TrainingDay = 0;
        p.Rec.Skills.Clear();
        p.Rec.Circle = 0; p.Rec.Element = ""; p.ShieldAmount = 0; p.Infused = false; p.Rec.Holy = 0;
        if (!string.IsNullOrEmpty(job.starterWeapon) && p.Inv.EquipReplacing(job.starterWeapon))
        {
            Gain(p, GainKind.Item, job.starterWeapon, 1);
            p.InvDirty = true;
        }
        p.Buffs.Clear();
        p.SkillReadyAt.Clear();
        RecalcStats(p);          // for a mage this also opens circle 1
        ResetAmmo(p);
        FillMana(p);
        p.SkillDirty = p.CharDirty = p.SaveDirty = true;
        p.SaveNow = true;
        Gain(p, GainKind.JobChange, job.id, 1);
        Audit(p, "job_changed", new { job = job.id, source, level = p.Rec.Level });
        Log.Info($"job   #{p.EntityId} '{p.Name}' → {job.id} ({source})");
    }

    /// <summary>Dev command only: back to no job (weapon becomes the starting club again).</summary>
    void ClearJob(PlayerEntity p)
    {
        EndTrial(p, TrialRun.Outcome.Cancelled, null);
        p.Rec.Job = ""; p.Rec.JobTier = 0;
        p.Rec.Circle = 0; p.Rec.Element = ""; p.ShieldAmount = 0; p.Infused = false; p.Rec.Holy = 0;
        p.Move.FlyUntil = 0; p.Move.FlightGraceUntil = 0; p.FlyReadyAt = 0;
        p.Rec.Training = 0; p.Rec.TrainingToday = 0; p.Rec.TrainingDay = 0;
        p.Rec.Skills.Clear();
        p.Buffs.Clear(); p.SkillReadyAt.Clear();
        p.Inv.EquipReplacing("wood_club");   // the starting club is a free item
        p.InvDirty = true;
        ResetStats(p);
        ResetAmmo(p);
        p.SkillDirty = p.CharDirty = p.SaveDirty = true;
        p.SaveNow = true;
    }

    // ───────────── the trial ─────────────

    /// <returns>null on success, otherwise what to tell the player.</returns>
    string StartTrial(PlayerEntity p, string jobId)
    {
        var job = _d.Job(jobId);
        if (job == null || !job.available) return "그 길은 아직 준비 중이야.";
        if (!string.IsNullOrEmpty(p.Rec.Job)) return "이미 직업이 있어.";
        if (p.Rec.Level < _d.character.jobChangeLevel) return $"{_d.character.jobChangeLevel}레벨이 되어야 시험을 볼 수 있어.";
        if (p.Trial != null && p.Trial.Result == TrialRun.Outcome.Running) return "이미 시험 중이야.";
        var master = _npcs.Values.FirstOrDefault(n => n.Def.id == job.masterNpc);
        var def = _d.Monster(job.trial.monster);
        if (master == null || def == null) return "시험을 준비하지 못했어.";

        Yaw.ToDirection(p.Move.Yaw, out float dx, out float dz);
        float x = p.Move.X + dx * job.trial.spawnDistance, z = p.Move.Z + dz * job.trial.spawnDistance;
        if (!_terrain.InBounds(x, z)) { x = p.Move.X - dx * job.trial.spawnDistance; z = p.Move.Z - dz * job.trial.spawnDistance; }
        var m = new MonsterEntity
        {
            EntityId = _nextEntityId++, Def = def, Spawn = null,
            HomeX = x, HomeZ = z, X = x, Z = z, Y = _terrain.Height(x, z),
            Yaw = Yaw.FromDirection(p.Move.X - x, p.Move.Z - z), Hp = def.maxHp, Ai = MonsterAi.Chase,
            Owner = p, Target = p, NextAttackAt = _now + 2.0,
        };
        _monsters[m.EntityId] = m;
        Broadcast(SpawnOf(m));

        p.Trial = new TrialRun { JobId = job.id, Opponent = m, Master = master, EndsAt = _now + job.trial.timeLimitSec, Result = TrialRun.Outcome.Running };
        RecalcStats(p);                 // lent starter weapon + first skill
        ResetAmmo(p);
        FillMana(p);                    // a mage trial starts with full mana
        p.SkillDirty = true;
        p.Conn.Send(new TrialState { Active = true, JobId = job.id, TimeLeft = job.trial.timeLimitSec });
        Audit(p, "trial_started", new { job = job.id });
        return null;
    }

    /// <summary>Called from the dialogue ("포기한다"), a death, a time-out, or leaving the game. Safe when no trial is running.</summary>
    void EndTrial(PlayerEntity p, TrialRun.Outcome outcome, string reason)
    {
        var t = p.Trial;
        if (t == null || t.Result != TrialRun.Outcome.Running) return;
        t.Result = outcome;
        t.FailReason = reason;
    }

    /// <summary>Decides running trials. Runs once a tick, outside the monster loop, so the opponent can be removed safely.</summary>
    void UpdateTrials()
    {
        foreach (var p in _players.Values)
        {
            var t = p.Trial;
            if (t == null) continue;
            if (t.Result == TrialRun.Outcome.Running)
            {
                var job = _d.Job(t.JobId);
                if (t.Opponent.Dead || t.Opponent.Hp <= 0) t.Result = TrialRun.Outcome.Won;
                else if (p.Dead) { t.Result = TrialRun.Outcome.Failed; t.FailReason = "쓰러져서 시험에 실패했어. 스승에게 다시 말을 걸어 줘."; }
                else if (_now > t.EndsAt) { t.Result = TrialRun.Outcome.Failed; t.FailReason = "시간이 다 되어 시험에 실패했어. 스승에게 다시 말을 걸어 줘."; }
                else if (Dist2(p.Move.X, p.Move.Z, t.Master.X, t.Master.Z) > job.trial.maxDistance * job.trial.maxDistance)
                { t.Result = TrialRun.Outcome.Failed; t.FailReason = "스승에게서 너무 멀어져 시험에 실패했어."; }
                else continue;
            }
            FinishTrial(p);
        }
    }

    void FinishTrial(PlayerEntity p)
    {
        var t = p.Trial;
        p.Trial = null;
        RemoveTrialOpponent(t);
        var job = _d.Job(t.JobId);
        CancelCharge(p);
        p.Conn.Send(new TrialState { Active = false, JobId = t.JobId, TimeLeft = 0 });
        switch (t.Result)
        {
            case TrialRun.Outcome.Won:
                p.Conn.Send(new Notice { Kind = NoticeKind.Info, Text = $"시험을 통과했어! 이제 {job.name}이야." });
                ChangeJob(p, job, "trial");
                break;
            case TrialRun.Outcome.Failed:
                p.Conn.Send(new Notice { Kind = NoticeKind.Error, Text = t.FailReason ?? "시험에 실패했어." });
                Audit(p, "trial_failed", new { job = job.id, reason = t.FailReason });
                RecalcStats(p); ResetAmmo(p); p.SkillDirty = true;
                break;
            default:
                Audit(p, "trial_cancelled", new { job = job.id });
                RecalcStats(p); ResetAmmo(p); p.SkillDirty = true;
                break;
        }
    }

    void RemoveTrialOpponent(TrialRun t)
    {
        if (t?.Opponent == null) return;
        if (_monsters.Remove(t.Opponent.EntityId)) Broadcast(new EntityDespawn { EntityId = t.Opponent.EntityId });
    }
}
