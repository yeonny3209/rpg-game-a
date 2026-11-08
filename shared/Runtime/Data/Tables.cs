using System;
using System.Collections.Generic;

namespace Game.Shared.Data
{
    // Schemas for the data tables in data/shared/*.json (spec 수치 values live in those files, not in code).
    // The server loads each file, validates them together, and sends the bundle to the client in Welcome.
    // Unity JsonUtility rules apply: public fields, arrays / List<T>, no dictionaries, no properties.

    /// <summary>Everything in data/shared, bundled. Server-only tables (spawns, drops) are NOT in here.</summary>
    [Serializable]
    public sealed class SharedData
    {
        public GameConfig config = new GameConfig();
        public ExpTable exp = new ExpTable();
        public CharacterRules character = new CharacterRules();
        public ItemDef[] items = new ItemDef[0];
        public MonsterDef[] monsters = new MonsterDef[0];
        public NpcDef[] npcs = new NpcDef[0];
        public DialogueNode[] dialogues = new DialogueNode[0];
        public QuestDef[] quests = new QuestDef[0];
        public MapDef map = new MapDef();
        public JobDef[] jobs = new JobDef[0];
        public SkillRules skillRules = new SkillRules();
        public SkillDef[] skills = new SkillDef[0];
        public ElementTable elements = new ElementTable();
        public ItemRules itemRules = new ItemRules();

        [NonSerialized] Dictionary<string, ItemDef> _items;
        [NonSerialized] Dictionary<string, MonsterDef> _monsters;
        [NonSerialized] Dictionary<string, NpcDef> _npcs;
        [NonSerialized] Dictionary<string, DialogueNode> _dialogues;
        [NonSerialized] Dictionary<string, QuestDef> _quests;
        [NonSerialized] Dictionary<string, JobDef> _jobs;
        [NonSerialized] Dictionary<string, SkillDef> _skills;
        [NonSerialized] HashSet<string> _strong;

        /// <summary>Build lookup tables. Call once after loading.</summary>
        public void Index()
        {
            _items = new Dictionary<string, ItemDef>();
            foreach (var i in items) _items[i.id] = i;
            _monsters = new Dictionary<string, MonsterDef>();
            foreach (var m in monsters) _monsters[m.id] = m;
            _npcs = new Dictionary<string, NpcDef>();
            foreach (var n in npcs) _npcs[n.id] = n;
            _dialogues = new Dictionary<string, DialogueNode>();
            foreach (var d in dialogues) _dialogues[d.id] = d;
            _quests = new Dictionary<string, QuestDef>();
            foreach (var q in quests) _quests[q.id] = q;
            _jobs = new Dictionary<string, JobDef>();
            foreach (var j in jobs) _jobs[j.id] = j;
            _skills = new Dictionary<string, SkillDef>();
            foreach (var k in skills) _skills[k.id] = k;
            _strong = new HashSet<string>();
            foreach (var p in elements.strong) _strong.Add(p.attack + ">" + p.target);
        }

        public JobDef Job(string id) { return !string.IsNullOrEmpty(id) && _jobs.TryGetValue(id, out var v) ? v : null; }
        public SkillDef Skill(string id) { return id != null && _skills.TryGetValue(id, out var v) ? v : null; }
        /// <summary>마법사: the six base spells have one variant per element, id "base.element" (mg_bolt.fire). Falls back to the base spell.</summary>
        public SkillDef SkillFor(string baseId, string element)
        {
            if (!string.IsNullOrEmpty(element) && element != "none" && baseId != null && _skills.TryGetValue(baseId + "." + element, out var v)) return v;
            return Skill(baseId);
        }

        /// <summary>Spec vol.4 1.8: 유리 +25%, 불리 −20%. Void ignores everything; "none" on either side = 1.</summary>
        public float ElementMultiplier(string attack, string target)
        {
            if (string.IsNullOrEmpty(attack) || string.IsNullOrEmpty(target) || attack == "none" || target == "none") return 1f;
            if (attack == elements.ignoreAll) return 1f;
            if (_strong.Contains(attack + ">" + target)) return elements.advantage;
            if (_strong.Contains(target + ">" + attack)) return elements.disadvantage;
            return 1f;
        }

        public ItemDef Item(string id) { return id != null && _items.TryGetValue(id, out var v) ? v : null; }
        public MonsterDef Monster(string id) { return id != null && _monsters.TryGetValue(id, out var v) ? v : null; }
        public NpcDef Npc(string id) { return id != null && _npcs.TryGetValue(id, out var v) ? v : null; }
        public DialogueNode Dialogue(string id) { return id != null && _dialogues.TryGetValue(id, out var v) ? v : null; }
        public QuestDef Quest(string id) { return id != null && _quests.TryGetValue(id, out var v) ? v : null; }
    }

    /// <summary>Spec vol.2 1.3. toNext[i] = EXP needed to go from level i+1 to i+2.</summary>
    [Serializable]
    public sealed class ExpTable
    {
        public int maxLevel = 50;
        public long[] toNext = new long[0];
        /// <summary>Monsters this many levels below you give only lowLevelExpRate of their EXP.</summary>
        public int lowLevelGap = 10;
        public float lowLevelExpRate = 0.1f;

        /// <summary>0 at max level.</summary>
        public long ToNext(int level)
        {
            if (level >= maxLevel || level < 1 || level - 1 >= toNext.Length) return 0;
            return toNext[level - 1];
        }
    }

    /// <summary>Base character numbers before any job (spec vol.2 2.1~2.3).</summary>
    [Serializable]
    public sealed class CharacterRules
    {
        public int startStat = 10;
        public int statPointsPerLevel = 5;
        public int hpBase = 100;
        public int hpPerLevel = 20;
        public int hpPerCon = 20;
        public int attackBase = 10;
        public int attackPerLevel = 2;
        public float defensePerStr = 0.5f;
        public float defensePerCon = 1f;
        public float critChance = 0.05f;
        public float critDamage = 1.5f;
        /// <summary>Out of combat: regen this fraction of max HP per second (spec 2.3: 초당 최대 HP 2%).</summary>
        public float regenOutOfCombatPerSec = 0.02f;
        /// <summary>In combat: HP per second per 체력 point (spec 2.2: 체력 HP 재생 +0.1/초).</summary>
        public float regenInCombatPerCon = 0.1f;
        /// <summary>Seconds without dealing / taking damage until you are out of combat.</summary>
        public float combatTimeoutSec = 5f;
        public float respawnDelaySec = 3f;
        public float interactRange = 4f;
        public int bagSlots = 40;
        /// <summary>Attack without a weapon.</summary>
        public WeaponStats unarmed = new WeaponStats();
        public StatEffects stats = new StatEffects();
        /// <summary>1st job change gives this many stat points (spec vol.2 2.1: 전직 보너스 +10).</summary>
        public int jobChangeStatPoints = 10;
        public int jobChangeLevel = 5;
    }

    /// <summary>Spec vol.2 2.2 / 2.3. Percent-style effects count points above startStat so a fresh character has the 2.3 base values.</summary>
    [Serializable]
    public sealed class StatEffects
    {
        /// <summary>Attack per point of the job's primary stat.</summary>
        public float attackPerPrimary = 2f;
        /// <summary>체력 is the primary stat of 전사: attack +1 and defense +1 per point instead.</summary>
        public float conPrimaryAttack = 1f;
        public float conPrimaryDefense = 1f;
        public float critPerDex = 0.0005f;
        public float attackSpeedPerDex = 0.0005f;
        public float manaBase = 20f;
        public float manaPerInt = 8f;
        public float manaRegenPerInt = 0.1f;
        /// <summary>Mana per second everybody gets on top of the 지능 effect (Claude 초기값, so low-지능 mages are not stuck).</summary>
        public float manaRegenBase = 2f;
        /// <summary>Out of combat, mana comes back this many times faster.</summary>
        public float manaRegenOutOfCombatMultiplier = 2f;
        public float healPowerPerSpi = 0.001f;
        public float cdrPerSpi = 0.0003f;
        public float ccResistPerSpi = 0.0005f;
        // Spec vol.2 2.3 caps
        public float critChanceCap = 0.75f;
        public float critDamageCap = 3f;
        public float attackSpeedCap = 2f;
        public float moveSpeedCap = 1.5f;
        public float cdrCap = 0.4f;
        public float penetrationCap = 0.5f;
        public float ccResistCap = 0.5f;
        public float lifestealCap = 0.15f;
    }

    [Serializable]
    public sealed class WeaponStats
    {
        public int attack = 0;
        public float range = 1.8f;
        public float arcDeg = 100f;
        public float interval = 0.9f;
    }

    [Serializable]
    public sealed class ItemDef
    {
        public string id;
        public string name;
        /// <summary>weapon | material | consumable</summary>
        public string type;
        /// <summary>weapon / helmet / armor / gloves / boots / necklace / ring / artifact, or empty if not equippable.</summary>
        public string equipSlot;
        public int maxStack = 1;
        /// <summary>common | rare | epic | unique | legendary | transcendent | mythic (spec vol.4 2.4)</summary>
        public string grade = "common";
        public string description;
        /// <summary>Placeholder icon color (#RRGGBB) until real icons exist.</summary>
        public string color = "#999999";
        public WeaponStats weapon = new WeaponStats();
        public int defense;
        /// <summary>Weapons only: club | sword | bow (spec: 직업 전용 무기).</summary>
        public string weaponType = "club";
        /// <summary>Level needed to equip it.</summary>
        public int reqLevel;
        /// <summary>Common-grade shop price in gold [수치]. The real price is value × the grade's valueMult; selling gives 10% of that.</summary>
        public int value;
        /// <summary>Jewelry: flat attack / max HP (× the grade's statMult).</summary>
        public int bonusAttack, bonusHp;
        /// <summary>Potions: HP / mana restored when used.</summary>
        public int heal, mana;
    }

    /// <summary>Spec vol.4 2.4 / 2.7 / 2.10: the 7 grades, the random option list and the bag size rules.</summary>
    [Serializable]
    public sealed class ItemRules
    {
        public GradeDef[] grades = new GradeDef[0];
        public OptionDef[] options = new OptionDef[0];
        public BagRules bag = new BagRules();
        /// <summary>NPC buys at this share of the shop price (spec vol.4 3.2: 10%).</summary>
        public float sellRatio = 0.1f;

        public int GradeIndex(string id)
        {
            for (int i = 0; i < grades.Length; i++) if (grades[i].id == id) return i;
            return -1;
        }

        public GradeDef Grade(int index) { return index >= 0 && index < grades.Length ? grades[index] : (grades.Length > 0 ? grades[0] : new GradeDef()); }

        public OptionDef Option(string id)
        {
            foreach (var o in options) if (o.id == id) return o;
            return null;
        }
    }

    [Serializable]
    public sealed class GradeDef
    {
        public string id;
        public string name;
        public string color = "#aaaaaa";
        /// <summary>How many random option lines (커먼 0 ~ 신화 5).</summary>
        public int optionLines;
        /// <summary>Multiplier on the base stats (1.0 ~ 3.0).</summary>
        public float statMult = 1f;
        /// <summary>Multiplier on the common price.</summary>
        public float valueMult = 1f;
        /// <summary>초월 rolls its option values from a higher range.</summary>
        public float rangeBoost = 1f;
    }

    /// <summary>One kind of random option. id doubles as the stat it changes (see Rules.Stats). Values are fractions for percent options.</summary>
    [Serializable]
    public sealed class OptionDef
    {
        public string id;
        public string name;
        public float min, max;
        /// <summary>true: shown as a percentage (value 0.12 → +12%).</summary>
        public bool percent = true;
    }

    [Serializable]
    public sealed class BagRules
    {
        /// <summary>Slots added per expansion and the most a bag can have (spec vol.4 2.10).</summary>
        public int step = 10;
        public int maxSlots = 120;
        /// <summary>Gold for the first expansion; every later one costs twice as much.</summary>
        public long baseCost = 1000;
    }

    [Serializable]
    public sealed class MonsterDef
    {
        public string id;
        public string name;
        public int level = 1;
        public int maxHp = 10;
        public int attack = 1;
        public int defense = 0;
        public int exp = 1;
        /// <summary>선공: attacks players that come within aggroRange.</summary>
        public bool aggressive;
        public float aggroRange = 8f;
        /// <summary>Dragged farther than this from its spawn point → walks home and heals (spec vol.4 1.2 제안).</summary>
        public float leashRange = 20f;
        public float attackRange = 1.5f;
        public float attackInterval = 2f;
        public float moveSpeed = 4f;
        public float wanderRadius = 5f;
        public float respawnSec = 5f;
        public float bodyRadius = 0.4f;
        /// <summary>Server table data/server/drop_tables.json (contents are not sent to clients).</summary>
        public string dropTable;
        public string element = "none";
        public VisualDef visual = new VisualDef();
        /// <summary>Training dummy (수련장 허수아비): never dies, never fights, gives training for 동양 검사.</summary>
        public bool dummy;
        /// <summary>Optional crowd control its hits can apply to players (spec vol.4 1.4).</summary>
        public HitCc hitCc = new HitCc();
        /// <summary>Defense-ignoring share of its hits (0..1).</summary>
        public float penetration;
    }

    [Serializable]
    public sealed class HitCc
    {
        /// <summary>none | stun | freeze | root | knockback | slow</summary>
        public string type = "none";
        public float chance = 0f;
        public float duration = 0f;
        /// <summary>knockback: meters pushed.</summary>
        public float distance = 0f;
    }

    [Serializable]
    public sealed class VisualDef
    {
        /// <summary>Placeholder shape: humanoid | rabbit | wolf</summary>
        public string shape = "humanoid";
        /// <summary>Optional rigged model in Resources/Creatures (e.g. wolf, village_chief). Empty = use the placeholder shape.</summary>
        public string model;
        public string color = "#ffffff";
        public float scale = 1f;
    }

    [Serializable]
    public sealed class NpcDef
    {
        public string id;
        public string name;
        public string title;
        public float x, z, yaw;
        /// <summary>First rule whose quest state matches decides the opening node. Falls back to defaultNode.</summary>
        public DialogueEntry[] entries = new DialogueEntry[0];
        public string defaultNode;
        public VisualDef visual = new VisualDef();
    }

    [Serializable]
    public sealed class DialogueEntry
    {
        /// <summary>Optional: only when this quest is in the given state.</summary>
        public string quest;
        /// <summary>none | active | ready | completed</summary>
        public string state;
        /// <summary>Optional: only at this level or higher.</summary>
        public int minLevel;
        /// <summary>Optional: "none" (no job yet), "any" (has a job), or a job id.</summary>
        public string job;
        /// <summary>Optional: only while a job trial is running ("yes") or not ("no").</summary>
        public string trial;
        public string node;
    }

    [Serializable]
    public sealed class DialogueNode
    {
        public string id;
        /// <summary>Speaker name shown above the text. Empty = the NPC's name.</summary>
        public string speaker;
        public string text;
        public DialogueOption[] options = new DialogueOption[0];
    }

    [Serializable]
    public sealed class DialogueOption
    {
        public string label;
        /// <summary>Node to show next. Empty = close the dialogue.</summary>
        public string next;
        /// <summary>Run on the server, in order, when this option is picked.</summary>
        public DialogueAction[] actions = new DialogueAction[0];
    }

    [Serializable]
    public sealed class DialogueAction
    {
        /// <summary>giveItem | acceptQuest | completeQuest | startTrial (id = job id)</summary>
        public string type;
        public string id;
        public int count = 1;
        /// <summary>giveItem: equip it right away if the slot is empty (spec 결정 Q1: 몽둥이 자동 장착).</summary>
        public bool equip;
        /// <summary>giveItem: skip if the character already owns this item (so re-reading a dialogue can't farm it).</summary>
        public bool unique;
    }

    [Serializable]
    public sealed class QuestDef
    {
        public string id;
        public string title;
        /// <summary>main | sub | odd (spec vol.1 8)</summary>
        public string kind = "main";
        public string summary;
        public string giverNpc;
        public QuestObjective[] objectives = new QuestObjective[0];
        public QuestRewards rewards = new QuestRewards();
    }

    [Serializable]
    public sealed class QuestObjective
    {
        /// <summary>collect (have N of an item) | kill (kill N of a monster)</summary>
        public string type;
        public string target;
        public int count = 1;
        /// <summary>collect: remove the items when the quest is handed in.</summary>
        public bool consume = true;
        /// <summary>Tracker text, e.g. "토끼 간". Empty = item/monster name.</summary>
        public string label;
    }

    [Serializable]
    public sealed class QuestRewards
    {
        public long exp;
        public long gold;
        /// <summary>결정 Q2: if below this level, grant exactly enough EXP to reach it (instead of exp). 0 = unused.</summary>
        public int fillToLevel;
        /// <summary>equipment | leaveStartArea</summary>
        public string[] unlocks = new string[0];
        public ItemReward[] items = new ItemReward[0];
    }

    [Serializable]
    public sealed class ItemReward
    {
        public string id;
        public int count = 1;
    }

    // ───────────── jobs, skills, elements (stage 2a) ─────────────

    /// <summary>Spec vol.2 5. Only jobs with available=true can be taken; the rest show as "준비 중".</summary>
    [Serializable]
    public sealed class JobDef
    {
        public string id;
        public string name;
        /// <summary>1st / 2nd / 3rd job names (spec vol.2 4.2).</summary>
        public string[] tierNames = new string[0];
        /// <summary>str | dex | int | con | spi</summary>
        public string primaryStat = "str";
        /// <summary>sword | bow | ... — what the starter weapon is.</summary>
        public string weaponType = "sword";
        public bool available = true;
        public string description;
        public string role;
        /// <summary>The master NPC who runs the job-change trial.</summary>
        public string masterNpc;
        public string starterWeapon;
        /// <summary>Skill ids by slot: Q E T G Z X.</summary>
        public string[] skills = new string[0];
        public TrialDef trial = new TrialDef();
        /// <summary>동양 검사 only (spec vol.2 3.1).</summary>
        public TrainingRules training = new TrainingRules();
        /// <summary>궁수 only (spec vol.2 5.5).</summary>
        public BowRules bow = new BowRules();
        /// <summary>마법사 only: element choice and circles (spec vol.2 5.2).</summary>
        public MageRules mage = new MageRules();
        /// <summary>마법사·궁수: 비행 (spec vol.4 1.5).</summary>
        public FlightRules flight = new FlightRules();
        /// <summary>A ranged basic attack (마법사). kind none = the weapon's melee arc.</summary>
        public BasicAttackDef basic = new BasicAttackDef();
        /// <summary>암살자: 후방 2배 applies to every hit (spec vol.2 5.3).</summary>
        public bool backstab;
        /// <summary>서양 검사: 마나 주입 토글 (spec vol.2 5.1).</summary>
        public InfuseRules infuse = new InfuseRules();
        /// <summary>버퍼: 신성력 · 파티 버프 (spec vol.2 5.6).</summary>
        public BufferRules buffer = new BufferRules();
    }

    /// <summary>Spec vol.2 5.1 [수치]: 주입 ON이면 평타 1회에 최대 마나의 1% 소모, 피해 × (1 + 최대 마나 / 2000).</summary>
    [Serializable]
    public sealed class InfuseRules
    {
        public bool enabled;
        public float costRatio = 0.01f;
        public float divisor = 2000f;
    }

    /// <summary>Spec vol.2 5.6 [수치]: 버프 배율 = 1 + 0.25 × log2(1 + 신성력 / 100).</summary>
    [Serializable]
    public sealed class BufferRules
    {
        public bool enabled;
        /// <summary>Until a party system exists, "the party" is every player within this many meters (decision 2026-11-08).</summary>
        public float allyRange = 40f;
        public float holyBase = 100f;
        public float holyCoef = 0.25f;
        /// <summary>The same buff from different buffers stacks (합연산) up to this many (spec 5.6 제안).</summary>
        public int stackCap = 2;

        public float Multiplier(int holy)
        {
            return 1f + holyCoef * (float)(System.Math.Log(1.0 + System.Math.Max(0, holy) / holyBase) / System.Math.Log(2.0));
        }
    }

    /// <summary>Spec vol.2 4.1: 스승 NPC의 시험. Decision (2026-11-07): a 1:1 duel with an opponent the master summons.</summary>
    [Serializable]
    public sealed class TrialDef
    {
        /// <summary>Monster table id of the opponent (only the trial taker can hurt it / is hunted by it).</summary>
        public string monster;
        /// <summary>How far in front of the master (meters) it appears.</summary>
        public float spawnDistance = 6f;
        /// <summary>The trial ends (failed) after this long.</summary>
        public float timeLimitSec = 120f;
        /// <summary>Leaving this far from the master fails the trial.</summary>
        public float maxDistance = 30f;
    }

    /// <summary>Spec vol.2 3.1 수련장 / 훈련량 (동양 검사).</summary>
    [Serializable]
    public sealed class TrainingRules
    {
        public bool enabled;
        /// <summary>Training needed for the first level; later levels need more (체감).</summary>
        public int perLevel = 1000;
        /// <summary>Each later level needs this much more (fraction of perLevel).</summary>
        public float growthPerLevel = 0.2f;
        public float attackPerLevel = 0.015f;
        public int dailyCap = 1000;
        public int perHit = 1;
        public int perSkillHit = 3;
    }

    /// <summary>Spec vol.2 5.5 궁수: 차징 / 탄창.</summary>
    [Serializable]
    public sealed class BowRules
    {
        public bool enabled;
        public ChargeTier[] tiers = new ChargeTier[0];
        /// <summary>Seconds between two shots (the "즉발 1초 간격").</summary>
        public float shotInterval = 1f;
        public int magazine = 5;
        public float reloadSec = 2.5f;
        /// <summary>Move speed multiplier while holding the bow drawn (100 → 80).</summary>
        public float chargeMoveSpeed = 0.8f;
        public float projectileSpeed = 45f;
        public float range = 40f;
        public float radius = 0.4f;
    }

    [Serializable]
    public sealed class ChargeTier
    {
        /// <summary>Hold the button at least this long (seconds) to get this tier.</summary>
        public float minHold;
        public float coefficient = 1f;
        public float penetration;
        public string label;
    }

    /// <summary>Spec vol.2 5.2: 일반 속성 1개를 고르고, 서클이 오르면 그 서클의 마법을 배운다.</summary>
    [Serializable]
    public sealed class MageRules
    {
        public bool enabled;
        /// <summary>Elements a mage may choose at the 1st job change (일반 속성 6개; 상위 속성은 히든 퀘스트로).</summary>
        public string[] elements = new string[0];
        public MageCircle[] circles = new MageCircle[0];

        public MageCircle Circle(int n)
        {
            foreach (var c in circles) if (c.circle == n) return c;
            return null;
        }
    }

    /// <summary>One row of the 서클 승급표 (spec vol.2 5.2).</summary>
    [Serializable]
    public sealed class MageCircle
    {
        public int circle;
        public int level;
        public int maxMana;
        /// <summary>auto | battle (전투 시험은 단계 5)</summary>
        public string trial = "auto";
        /// <summary>false = not playable yet (the table row exists for later stages).</summary>
        public bool available = true;
    }

    /// <summary>Spec vol.4 1.5 비행: 마법사·궁수·마검사. 비행 중 공격 가능, 맞으면 추락.</summary>
    [Serializable]
    public sealed class FlightRules
    {
        public bool enabled;
        public float duration = 10f;
        public float cooldown = 30f;
        /// <summary>Spec: 비행 중 이동속도 140 (run speed × 1.4).</summary>
        public float speedMultiplier = 1.4f;
        public float maxHeight = 25f;
        public float climbSpeed = 6f;
        /// <summary>Shot down: fall damage = this fraction of max HP per 10 m of height (spec vol.4 1.5 제안).</summary>
        public float fallDamagePerTenMeters = 0.08f;
        /// <summary>After the flight ends (or a fall starts) the height check is relaxed this long so a fall is not "TooHigh".</summary>
        public float graceSec = 12f;
    }

    [Serializable]
    public sealed class BasicAttackDef
    {
        /// <summary>melee | projectile</summary>
        public string kind = "melee";
        public float coefficient = 1f;
        public float speed = 26f;
        public float range = 16f;
        public float radius = 0.4f;
    }

    [Serializable]
    public sealed class SkillRules
    {
        public int maxLevel = 10;
        /// <summary>Spec vol.2 6.1: +6% power per level.</summary>
        public float powerPerLevel = 0.06f;
        /// <summary>Uses (hits) needed to go from level n to n+1; index 0 = level 1 → 2.</summary>
        public int[] usesToNext = new int[0];
        /// <summary>Spec vol.4 1.1 fixed after-hit protection.</summary>
        public float cooldownTolerance = 0.2f;

        public int UsesToNext(int level)
        {
            if (level < 1 || level >= maxLevel || level - 1 >= usesToNext.Length) return 0;
            return usesToNext[level - 1];
        }
    }

    /// <summary>Spec vol.2 6 / data-driven skill. kind decides how it plays; the numbers are all [수치].</summary>
    [Serializable]
    public sealed class SkillDef
    {
        public string id;
        public string name;
        public string job;
        public int unlockLevel = 5;
        /// <summary>0..5 = Q E T G Z X.</summary>
        public int slot;
        public string description;
        public string color = "#cccccc";
        public float cooldown = 5f;
        /// <summary>
        /// melee (arc in front) | dash (rush forward, cutting what is on the way) | circle (around you) |
        /// projectile | volley (fan of projectiles) | backstep (hop back, then shoot) | buff | stance (counter stance) |
        /// blink (그림자 걸음: appear behind the target) | ground (lands at a point ahead after a delay) | zone (damaging area for a while) |
        /// chain (jumps between targets) | shield (absorbs damage)
        /// </summary>
        public string kind = "melee";
        public float coefficient = 1f;
        public int hits = 1;
        public float hitInterval = 0.15f;
        public float range = 3f;
        public float arcDeg = 120f;
        public int maxTargets = 3;
        /// <summary>dash / backstep distance.</summary>
        public float distance;
        public float speed = 30f;
        public float radius = 0.4f;
        public int pierce = 1;
        public int projectiles = 1;
        public float spreadDeg;
        /// <summary>Extra defense penetration for this skill (0..1).</summary>
        public float penetration;
        /// <summary>buff / stance duration.</summary>
        public float duration;
        public SkillEffect[] effects = new SkillEffect[0];
        /// <summary>마법사: unlocked by circle, not by level (spec vol.2 6.7). 0 = level based.</summary>
        public int circle;
        public int manaCost;
        /// <summary>The spell takes on the caster's chosen element (damage type, color, side effect).</summary>
        public bool useElement;
        /// <summary>Element variants only: the element this spell belongs to. Empty for normal skills.</summary>
        public string element;
        /// <summary>Client-only look of the effect (orb, column, spikes, swirl, ...). The server ignores it.</summary>
        public string fx;
        /// <summary>ground / zone: seconds before it lands; zone: damage ticks and spacing.</summary>
        public float delay;
        public int ticks = 1;
        public float tickInterval = 1f;
        /// <summary>chain: how many more targets it jumps to, how far, and how much weaker each jump is (×).</summary>
        public int chainCount;
        public float chainRange = 6f;
        public float chainFalloff = 0.7f;
        /// <summary>처형: below this share of HP the hit does executeMultiplier times damage.</summary>
        public float executeBelow;
        public float executeMultiplier = 1f;
    }

    [Serializable]
    public sealed class SkillEffect
    {
        /// <summary>cc | buff | counter</summary>
        public string type;
        /// <summary>cc: stun | freeze | root | knockback | slow</summary>
        public string cc;
        public float duration;
        public float chance = 1f;
        public float distance;
        /// <summary>buff: instantRate (instant shot speed) | chargeRate (charge speed) | damage (all damage)</summary>
        public string stat;
        public float amount;
    }

    /// <summary>Spec vol.4 1.8.</summary>
    [Serializable]
    public sealed class ElementTable
    {
        public ElementDef[] list = new ElementDef[0];

        public ElementDef Find(string id)
        {
            foreach (var e in list) if (e.id == id) return e;
            return null;
        }
        public ElementPair[] strong = new ElementPair[0];
        public float advantage = 1.25f;
        public float disadvantage = 0.8f;
        /// <summary>This element ignores every bonus and resistance (공허).</summary>
        public string ignoreAll = "void";
    }

    [Serializable]
    public sealed class ElementDef
    {
        public string id;
        public string name;
        public string color;
        /// <summary>What a mage spell of this element adds (spec vol.2 5.2: 불=화상, 얼음=둔화, 물=넉백, 번개=연쇄, 식물=속박, 금속=관통).</summary>
        public ElementEffect effect = new ElementEffect();
    }

    [Serializable]
    public sealed class ElementEffect
    {
        /// <summary>none | burn | slow | knockback | chain | root | pierce</summary>
        public string type = "none";
        public float duration;
        /// <summary>burn: damage per second as a share of the hit; chain: damage share of the jump; pierce: defense penetration.</summary>
        public float amount;
        public float distance;
        public int count;
    }

    [Serializable]
    public sealed class ElementPair
    {
        public string attack;
        public string target;
    }

    /// <summary>One region map (data/shared/maps/*.json). Terrain is generated from these numbers on both sides.</summary>
    [Serializable]
    public sealed class MapDef
    {
        public string id;
        public string name;
        public float sizeMeters = 256f;
        public TerrainDef terrain = new TerrainDef();
        public SpawnPoint playerSpawn = new SpawnPoint();
        public SpawnPoint respawnPoint = new SpawnPoint();
        /// <summary>Before the first quest, players cannot leave this circle (spec vol.1 6 순서 4).</summary>
        public CircleDef startArea = new CircleDef();
        public string startAreaMessage;
        public BuildingDef[] buildings = new BuildingDef[0];
        public TreeDef trees = new TreeDef();
    }

    [Serializable]
    public sealed class TerrainDef
    {
        public int seed = 1;
        public float hillHeight = 5f;
        public float gridStep = 2f;
        /// <summary>Areas pressed flat (village, fields). Height blends back to hills over the same radius.</summary>
        public CircleDef[] flats = new CircleDef[0];
    }

    [Serializable]
    public sealed class CircleDef
    {
        public float x, z, radius;
    }

    [Serializable]
    public sealed class SpawnPoint
    {
        public float x, z, yaw;
    }

    [Serializable]
    public sealed class BuildingDef
    {
        public string name;
        public float x, z, yaw;
        public float width = 6f, depth = 6f, height = 3f;
        public string wallColor = "#c8b89a";
        public string roofColor = "#8a3b2a";
    }

    [Serializable]
    public sealed class TreeDef
    {
        public int count = 300;
        public int seed = 7;
        /// <summary>No trees inside these circles.</summary>
        public CircleDef[] avoid = new CircleDef[0];
    }
}
