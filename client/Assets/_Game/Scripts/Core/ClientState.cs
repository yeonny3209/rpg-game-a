using System;
using System.Collections.Generic;
using Game.Shared.Data;
using Game.Shared.Protocol;
using UnityEngine;

namespace Game.Client.Core
{
    /// <summary>
    /// What the client knows about its own character, exactly as the server last said.
    /// The client never changes these itself (server authority); UI just reads them.
    /// Skill cooldowns and buffs are tracked here only so the bar can show them; the server enforces them.
    /// </summary>
    public sealed class ClientState
    {
        public SharedData Data;
        public uint SelfId;
        public CharacterSync Char;
        public InventorySync Inv;
        public QuestSync Quests;
        public StatsSync Stats;
        public SkillSync Skills;
        public AmmoState Ammo;
        /// <summary>Time.time when Ammo arrived (the reload countdown runs from there).</summary>
        public float AmmoAt;
        public TrialState Trial;
        public int Mana = -1, MaxMana;
        /// <summary>서양 검사: 마나 주입이 켜져 있다.</summary>
        public bool Infused;
        public bool Flying;
        /// <summary>Time.time at which flight may be used again / ends (display only; the server enforces both).</summary>
        public float FlyReadyAt, FlyEndAt;
        public float TrialAt;
        public readonly List<ChatMessage> Chat = new List<ChatMessage>();
        public readonly List<float> ChatTimes = new List<float>();

        public event Action Changed;
        public event Action ChatAdded;

        public void Notify() { Changed?.Invoke(); }

        public void AddChat(ChatMessage m)
        {
            Chat.Add(m);
            ChatTimes.Add(Time.time);
            while (Chat.Count > 60) { Chat.RemoveAt(0); ChatTimes.RemoveAt(0); }
            ChatAdded?.Invoke();
        }

        public bool Unlocked(UnlockFlags f) { return Char != null && (Char.Unlocks & f) != 0; }

        public QuestState QuestStateOf(string questId)
        {
            if (Quests == null) return QuestState.None;
            foreach (var q in Quests.Quests) if (q.QuestId == questId) return q.State;
            return QuestState.None;
        }

        public string ItemName(string id)
        {
            var d = Data != null ? Data.Item(id) : null;
            return d != null ? d.name : id;
        }

        // ───────────── jobs & skills ─────────────

        public bool IsMage { get { var j = Job; return j != null && j.mage != null && j.mage.enabled && Char != null && Char.Job == j.id; } }
        /// <summary>Jobs that use a mana bar: 마법사 and 서양 검사 (also while their trial runs).</summary>
        public bool UsesMana { get { var j = Job; return j != null && ((j.mage != null && j.mage.enabled) || (j.infuse != null && j.infuse.enabled)); } }
        public bool NeedsElement => IsMage && Skills != null && string.IsNullOrEmpty(Skills.Element);
        public bool CanFly { get { var j = Data != null && Char != null ? Data.Job(Char.Job) : null; return j != null && j.flight != null && j.flight.enabled; } }

        public bool TrialActive => Trial != null && Trial.Active;

        /// <summary>The job whose skills are shown: the real one, or the trial's job while a trial runs.</summary>
        public JobDef Job
        {
            get
            {
                if (Data == null) return null;
                var j = Char != null ? Data.Job(Char.Job) : null;
                if (j == null && TrialActive) j = Data.Job(Trial.JobId);
                return j;
            }
        }

        public SkillDef SkillAt(int slot)
        {
            var j = Job;
            if (j == null || j.skills == null || slot < 0 || slot >= j.skills.Length) return null;
            return Data.SkillFor(j.skills[slot], j.mage != null && j.mage.enabled && Skills != null ? Skills.Element : null);
        }

        /// <summary>Works at its unlock level; during a trial the master lends the job's first skill.</summary>
        public bool SkillUnlocked(SkillDef s)
        {
            if (s == null || Char == null) return false;
            if (Char.Job == s.job) return s.circle > 0 ? (Skills != null && Skills.Circle >= s.circle) : Char.Level >= s.unlockLevel;
            return TrialActive && Trial.JobId == s.job && s.slot == 0;
        }

        public int SkillLevel(string skillId)
        {
            if (Skills != null) foreach (var s in Skills.Skills) if (s.SkillId == skillId) return s.Level;
            return 1;
        }

        public int SkillUses(string skillId)
        {
            if (Skills != null) foreach (var s in Skills.Skills) if (s.SkillId == skillId) return s.Uses;
            return 0;
        }

        // cooldowns (Time.time at which the skill is ready again)
        readonly Dictionary<string, float> _cooldownEnd = new Dictionary<string, float>();
        readonly Dictionary<string, float> _cooldownLen = new Dictionary<string, float>();

        public void StartCooldown(string skillId, float seconds)
        {
            _cooldownEnd[skillId] = Time.time + Mathf.Max(0f, seconds);
            _cooldownLen[skillId] = Mathf.Max(0.01f, seconds);
        }

        public float CooldownLeft(string skillId)
        {
            return _cooldownEnd.TryGetValue(skillId, out var t) ? Mathf.Max(0f, t - Time.time) : 0f;
        }

        /// <summary>0..1 of the cooldown still to go (for the bar's sweep).</summary>
        public float CooldownRatio(string skillId)
        {
            return _cooldownLen.TryGetValue(skillId, out var len) ? Mathf.Clamp01(CooldownLeft(skillId) / len) : 0f;
        }

        public void ClearCooldowns() { _cooldownEnd.Clear(); _cooldownLen.Clear(); }

        // buffs of the archer's skills, so the charge bar matches what the server will do
        readonly Dictionary<string, (float until, float amount)> _buffs = new Dictionary<string, (float, float)>();

        public void SetBuff(string stat, float amount, float seconds) { _buffs[stat] = (Time.time + seconds, amount); }

        public float BuffValue(string stat)
        {
            return _buffs.TryGetValue(stat, out var b) && b.until > Time.time ? b.amount : 0f;
        }

        public float BuffLeft(string stat)
        {
            return _buffs.TryGetValue(stat, out var b) ? Mathf.Max(0f, b.until - Time.time) : 0f;
        }

        public void ClearBuffs() { _buffs.Clear(); }

        /// <summary>"!" if this NPC has a quest to give, "?" if one is ready to hand in, else null.</summary>
        public string QuestMarker(string npcId)
        {
            if (Data == null) return null;
            string mark = null;
            foreach (var q in Data.quests)
            {
                if (q.giverNpc != npcId) continue;
                var st = QuestStateOf(q.id);
                if (st == QuestState.Ready) return "?";
                if (st == QuestState.None) mark = "!";
            }
            return mark;
        }

        /// <summary>A master NPC gets a marker while you could take the trial.</summary>
        public string JobMarker(string npcId)
        {
            if (Data == null || Char == null || !string.IsNullOrEmpty(Char.Job) || TrialActive) return null;
            if (Char.Level < Data.character.jobChangeLevel) return null;
            foreach (var j in Data.jobs) if (j.available && j.masterNpc == npcId) return "!";
            return null;
        }
    }
}
