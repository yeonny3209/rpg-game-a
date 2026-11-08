using Game.Shared.Data;
using Game.Shared.Protocol;

namespace Game.Server.Data;

/// <summary>Cross-table checks: every id a table references must exist, numbers must be in sane ranges.</summary>
public static class DataValidator
{
    static readonly HashSet<string> QuestStates = new() { "none", "active", "ready", "completed" };
    static readonly HashSet<string> ActionTypes = new() { "giveItem", "acceptQuest", "completeQuest", "startTrial", "endTrial", "shop" };
    static readonly HashSet<string> Stats = new() { "str", "dex", "int", "con", "spi" };
    static readonly HashSet<string> SkillKinds = new() { "melee", "dash", "circle", "projectile", "volley", "backstep", "buff", "stance", "blink", "ground", "zone", "chain", "shield", "toggle", "taunt", "partyBuff", "heal", "groupHeal", "revive" };
    static readonly HashSet<string> ElementEffects = new() { "none", "burn", "slow", "knockback", "chain", "root", "pierce" };
    static readonly HashSet<string> CcTypes = new() { "stun", "freeze", "root", "knockback", "slow" };
    static readonly HashSet<string> BuffStats = new() { "instantInterval", "instantCoefficient", "chargeRate", "damage", "damageTaken" };
    static readonly HashSet<string> WeaponTypes = new() { "club", "sword", "longsword", "bow", "staff", "wand", "dagger", "mace", "holy" };
    static readonly HashSet<string> ObjectiveTypes = new() { "collect", "kill" };
    static readonly HashSet<string> EquipSlots = new() { "", "weapon", "helmet", "armor", "gloves", "boots", "necklace", "ring", "artifact" };
    public static readonly Dictionary<string, UnlockFlags> Unlocks = new()
    {
        ["equipment"] = UnlockFlags.Equipment,
        ["leaveStartArea"] = UnlockFlags.LeaveStartArea,
    };

    public static void Validate(SharedData s, SpawnDef[] spawns, Dictionary<string, DropTableDef> drops, List<string> e)
    {
        Config(s.config, e);

        // exp_table
        if (s.exp.maxLevel < 2) e.Add("exp_table: maxLevel must be >= 2");
        if (s.exp.toNext == null || s.exp.toNext.Length != s.exp.maxLevel - 1)
            e.Add($"exp_table: toNext needs {s.exp.maxLevel - 1} values (levels 1..{s.exp.maxLevel - 1}), got {s.exp.toNext?.Length ?? 0}");
        else
            for (int i = 0; i < s.exp.toNext.Length; i++)
                if (s.exp.toNext[i] <= 0) e.Add($"exp_table: toNext[{i}] (Lv{i + 1}) must be > 0");

        // character
        var c = s.character;
        if (c.bagSlots is < 1 or > 500) e.Add("character: bagSlots must be 1..500");
        if (c.critChance is < 0 or > 1) e.Add("character: critChance must be 0..1");
        if (c.respawnDelaySec < 0) e.Add("character: respawnDelaySec must be >= 0");
        Weapon(c.unarmed, "character.unarmed", e);

        // items
        Unique(s.items, i => i.id, "items", e);
        foreach (var i in s.items)
        {
            string at = $"items[{i.id}]";
            if (string.IsNullOrWhiteSpace(i.name)) e.Add($"{at}: name is empty");
            if (i.maxStack < 1) e.Add($"{at}: maxStack must be >= 1");
            if (!EquipSlots.Contains(i.equipSlot ?? "")) e.Add($"{at}: unknown equipSlot '{i.equipSlot}'");
            if (i.equipSlot == "weapon") Weapon(i.weapon, at + ".weapon", e);
            if (i.equipSlot == "weapon" && !WeaponTypes.Contains(i.weaponType ?? "")) e.Add($"{at}: unknown weaponType '{i.weaponType}'");
            if (!string.IsNullOrEmpty(i.equipSlot) && i.maxStack != 1) e.Add($"{at}: equipment must have maxStack 1");
        }

        ItemRulesCheck(s, e);

        // drop tables
        foreach (var t in drops.Values)
        {
            if (t.goldMin < 0 || t.goldMax < t.goldMin) e.Add($"drop_tables[{t.id}]: need 0 <= goldMin <= goldMax");
            foreach (var di in t.items)
            {
                if (s.Item(di.item) == null) e.Add($"drop_tables[{t.id}]: unknown item '{di.item}'");
                if (di.chance is < 0 or > 1) e.Add($"drop_tables[{t.id}].{di.item}: chance must be 0..1");
                if (di.min < 1 || di.max < di.min) e.Add($"drop_tables[{t.id}].{di.item}: need 1 <= min <= max");
                foreach (var g in di.grades.Keys) if (s.itemRules.GradeIndex(g) < 0) e.Add($"drop_tables[{t.id}].{di.item}: unknown grade '{g}'");
                if (di.grades.Count > 0 && string.IsNullOrEmpty(s.Item(di.item)?.equipSlot)) e.Add($"drop_tables[{t.id}].{di.item}: grades only make sense for equipment");
            }
        }

        // monsters
        Unique(s.monsters, m => m.id, "monsters", e);
        foreach (var m in s.monsters)
        {
            string at = $"monsters[{m.id}]";
            if (string.IsNullOrWhiteSpace(m.name)) e.Add($"{at}: name is empty");
            if (m.level < 1) e.Add($"{at}: level must be >= 1");
            if (m.maxHp < 1) e.Add($"{at}: maxHp must be >= 1");
            if (m.attackInterval <= 0) e.Add($"{at}: attackInterval must be > 0");
            if (m.moveSpeed <= 0) e.Add($"{at}: moveSpeed must be > 0");
            if (m.respawnSec <= 0) e.Add($"{at}: respawnSec must be > 0");
            if (m.leashRange <= 0) e.Add($"{at}: leashRange must be > 0");
            if (!string.IsNullOrEmpty(m.dropTable) && !drops.ContainsKey(m.dropTable)) e.Add($"{at}: unknown dropTable '{m.dropTable}'");
            if (m.hitCc != null && m.hitCc.type != "none" && !CcTypes.Contains(m.hitCc.type ?? "")) e.Add($"{at}: unknown hitCc type '{m.hitCc?.type}'");
            if (m.hitCc != null && m.hitCc.chance is < 0 or > 1) e.Add($"{at}: hitCc.chance must be 0..1");
            if (m.penetration is < 0 or > 1) e.Add($"{at}: penetration must be 0..1");
            if (!string.IsNullOrEmpty(m.element) && m.element != "none" && s.elements.list.All(x => x.id != m.element)) e.Add($"{at}: unknown element '{m.element}'");
            if (!string.IsNullOrEmpty(m.visual?.model) && m.visual.model.Contains('/')) e.Add($"{at}: visual.model must be a plain name");
        }

        // elements
        var elementIds = new HashSet<string>();
        foreach (var el in s.elements.list) if (!elementIds.Add(el.id ?? "")) e.Add($"elements: duplicate id '{el.id}'");
        foreach (var pr in s.elements.strong)
        {
            if (!elementIds.Contains(pr.attack ?? "")) e.Add($"elements.strong: unknown element '{pr.attack}'");
            if (!elementIds.Contains(pr.target ?? "")) e.Add($"elements.strong: unknown element '{pr.target}'");
        }
        foreach (var el in s.elements.list)
            if (el.effect != null && !ElementEffects.Contains(el.effect.type ?? "none")) e.Add($"elements[{el.id}]: unknown effect '{el.effect.type}'");
        if (s.elements.advantage <= 1) e.Add("elements: advantage must be > 1");
        if (s.elements.disadvantage is <= 0 or >= 1) e.Add("elements: disadvantage must be 0..1");

        // skills
        var sr = s.skillRules;
        if (sr.maxLevel < 2) e.Add("skillRules: maxLevel must be >= 2");
        if (sr.usesToNext == null || sr.usesToNext.Length != sr.maxLevel - 1) e.Add($"skillRules: usesToNext needs {sr.maxLevel - 1} values");
        else if (sr.usesToNext.Any(x => x <= 0)) e.Add("skillRules: usesToNext values must be > 0");
        Unique(s.skills, k => k.id, "skills", e);
        foreach (var k in s.skills)
        {
            string at = $"skills[{k.id}]";
            if (string.IsNullOrWhiteSpace(k.name)) e.Add($"{at}: name is empty");
            if (s.Job(k.job) == null) e.Add($"{at}: unknown job '{k.job}'");
            if (!SkillKinds.Contains(k.kind ?? "")) e.Add($"{at}: unknown kind '{k.kind}'");
            if (k.slot is < 0 or > 5) e.Add($"{at}: slot must be 0..5");
            if (k.cooldown <= 0) e.Add($"{at}: cooldown must be > 0");
            if (k.coefficient < 0) e.Add($"{at}: coefficient must be >= 0");
            if (k.hits < 1) e.Add($"{at}: hits must be >= 1");
            if (k.penetration is < 0 or > 1) e.Add($"{at}: penetration must be 0..1");
            if (k.kind is "projectile" or "volley" or "backstep" && (k.speed <= 0 || k.range <= 0)) e.Add($"{at}: projectile needs speed and range");
            if (k.kind == "volley" && k.projectiles < 2) e.Add($"{at}: volley needs projectiles >= 2");
            if (k.kind is "dash" or "backstep" && k.distance <= 0) e.Add($"{at}: needs distance");
            if (k.kind is "buff" or "stance" or "shield" && k.duration <= 0) e.Add($"{at}: needs duration");
            if (k.kind is "ground" or "zone" && (k.distance <= 0 || k.radius <= 0)) e.Add($"{at}: needs distance and radius");
            if (k.kind == "zone" && (k.ticks < 1 || k.tickInterval <= 0)) e.Add($"{at}: zone needs ticks and tickInterval");
            if (k.kind == "chain" && (k.chainCount < 1 || k.range <= 0)) e.Add($"{at}: chain needs chainCount and range");
            if (k.kind == "blink" && (k.range <= 0 || k.distance <= 0)) e.Add($"{at}: blink needs range and distance");
            if (k.manaCost < 0) e.Add($"{at}: manaCost must be >= 0");
            if (!string.IsNullOrEmpty(k.element))
            {
                int dot = k.id.IndexOf('.');
                if (s.elements.Find(k.element) == null) e.Add($"{at}: unknown element '{k.element}'");
                else if (dot < 0 || k.id.Substring(dot + 1) != k.element) e.Add($"{at}: element variant id must be '<base>.{k.element}'");
                else
                {
                    var baseSkill = s.Skill(k.id.Substring(0, dot));
                    if (baseSkill == null) e.Add($"{at}: no base skill '{k.id.Substring(0, dot)}'");
                    else if (baseSkill.job != k.job || baseSkill.slot != k.slot || baseSkill.circle != k.circle || baseSkill.unlockLevel != k.unlockLevel)
                        e.Add($"{at}: variant must keep the base skill's job, slot, circle and unlock level");
                }
            }
            if (k.executeBelow is < 0 or > 1) e.Add($"{at}: executeBelow must be 0..1");
            foreach (var fx in k.effects ?? Array.Empty<SkillEffect>())
            {
                if (fx.type is not ("cc" or "buff" or "counter")) { e.Add($"{at}: unknown effect type '{fx.type}'"); continue; }
                if (fx.type == "cc" && !CcTypes.Contains(fx.cc ?? "")) e.Add($"{at}: unknown cc '{fx.cc}'");
                if (fx.type == "buff" && !BuffStats.Contains(fx.stat ?? "")) e.Add($"{at}: unknown buff stat '{fx.stat}'");
                if (fx.chance is < 0 or > 1) e.Add($"{at}: effect chance must be 0..1");
            }
        }

        // jobs
        Unique(s.jobs, j => j.id, "jobs", e);
        var seenMasters = new HashSet<string>();
        foreach (var j in s.jobs)
        {
            string at = $"jobs[{j.id}]";
            if (string.IsNullOrWhiteSpace(j.name)) e.Add($"{at}: name is empty");
            if (!Stats.Contains(j.primaryStat ?? "")) e.Add($"{at}: unknown primaryStat '{j.primaryStat}'");
            if (j.tierNames == null || j.tierNames.Length != 3) e.Add($"{at}: tierNames needs 3 names (1차·2차·3차)");
            if (!j.available) continue; // "준비 중" jobs are only a name on the list
            if (s.Npc(j.masterNpc) == null) e.Add($"{at}: unknown masterNpc '{j.masterNpc}'");
            else if (!seenMasters.Add(j.masterNpc)) e.Add($"{at}: masterNpc '{j.masterNpc}' is used by another job");
            var starter = s.Item(j.starterWeapon);
            if (starter == null) e.Add($"{at}: unknown starterWeapon '{j.starterWeapon}'");
            else if (starter.equipSlot != "weapon" || starter.weaponType != j.weaponType) e.Add($"{at}: starterWeapon must be a '{j.weaponType}' weapon");
            if (j.skills == null || j.skills.Length != 6) e.Add($"{at}: needs 6 skills (Q E T G Z X)");
            else
                for (int i = 0; i < j.skills.Length; i++)
                {
                    var k = s.Skill(j.skills[i]);
                    if (k == null) { e.Add($"{at}: unknown skill '{j.skills[i]}'"); continue; }
                    if (k.job != j.id) e.Add($"{at}: skill '{k.id}' belongs to job '{k.job}'");
                    if (k.slot != i) e.Add($"{at}: skill '{k.id}' is listed at slot {i} but says slot {k.slot}");
                }
            var tm = s.Monster(j.trial?.monster);
            if (tm == null) e.Add($"{at}: unknown trial monster '{j.trial?.monster}'");
            if (j.trial != null && (j.trial.timeLimitSec <= 0 || j.trial.spawnDistance <= 0)) e.Add($"{at}: trial needs timeLimitSec and spawnDistance > 0");
            if (j.bow != null && j.bow.enabled)
            {
                var b = j.bow;
                if (b.tiers == null || b.tiers.Length < 1 || b.tiers[0].minHold != 0) e.Add($"{at}: bow.tiers must start with minHold 0");
                else for (int i = 1; i < b.tiers.Length; i++) if (b.tiers[i].minHold <= b.tiers[i - 1].minHold) e.Add($"{at}: bow.tiers minHold must increase");
                if (b.magazine < 1 || b.reloadSec <= 0 || b.shotInterval <= 0 || b.projectileSpeed <= 0 || b.range <= 0) e.Add($"{at}: bow numbers must be > 0");
            }
            if (j.training != null && j.training.enabled && (j.training.perLevel < 1 || j.training.dailyCap < 0)) e.Add($"{at}: training numbers invalid");
            if (j.flight != null && j.flight.enabled && (j.flight.duration <= 0 || j.flight.cooldown <= 0 || j.flight.maxHeight <= 0)) e.Add($"{at}: flight numbers must be > 0");
            if (j.basic != null && j.basic.kind is not ("melee" or "projectile")) e.Add($"{at}: basic.kind must be melee|projectile");
            if (j.mage != null && j.mage.enabled)
            {
                var m = j.mage;
                if (m.elements == null || m.elements.Length == 0) e.Add($"{at}: mage.elements is empty");
                else foreach (var id in m.elements) if (s.elements.Find(id) == null) e.Add($"{at}: unknown mage element '{id}'");
                if (m.circles == null || m.circles.Length == 0 || m.circles[0].circle != 1) e.Add($"{at}: mage.circles must start at circle 1");
                else for (int i = 1; i < m.circles.Length; i++)
                    if (m.circles[i].circle != m.circles[i - 1].circle + 1 || m.circles[i].level < m.circles[i - 1].level || m.circles[i].maxMana < m.circles[i - 1].maxMana)
                        e.Add($"{at}: mage.circles must go up one by one (circle, level, maxMana)");
                foreach (var sid in j.skills ?? Array.Empty<string>())
                {
                    var sk = s.Skill(sid);
                    if (sk == null) continue;
                    if (sk.circle < 1 || m.Circle(sk.circle) == null) e.Add($"{at}: mage skill '{sid}' needs a valid circle");
                }
            }
        }

        // map
        var map = s.map;
        if (map.sizeMeters <= 0) e.Add("map: sizeMeters must be > 0");
        float half = map.sizeMeters / 2;
        bool In(float x, float z) => Math.Abs(x) <= half && Math.Abs(z) <= half;
        if (!In(map.playerSpawn.x, map.playerSpawn.z)) e.Add("map: playerSpawn out of bounds");
        if (!In(map.respawnPoint.x, map.respawnPoint.z)) e.Add("map: respawnPoint out of bounds");
        if (map.startArea.radius <= 0) e.Add("map: startArea.radius must be > 0");
        if (Dist(map.playerSpawn.x, map.playerSpawn.z, map.startArea) > map.startArea.radius) e.Add("map: playerSpawn must be inside startArea");
        if (Dist(map.respawnPoint.x, map.respawnPoint.z, map.startArea) > map.startArea.radius) e.Add("map: respawnPoint must be inside startArea");

        // spawns
        foreach (var sp in spawns)
        {
            string at = $"spawns[{sp.monster}@{sp.x},{sp.z}]";
            if (s.Monster(sp.monster) == null) e.Add($"{at}: unknown monster");
            if (sp.count < 1) e.Add($"{at}: count must be >= 1");
            if (sp.radius < 0) e.Add($"{at}: radius must be >= 0");
            if (!In(sp.x - sp.radius, sp.z - sp.radius) || !In(sp.x + sp.radius, sp.z + sp.radius)) e.Add($"{at}: area leaves the map");
        }

        // quests
        Unique(s.quests, q => q.id, "quests", e);
        foreach (var q in s.quests)
        {
            string at = $"quests[{q.id}]";
            if (string.IsNullOrWhiteSpace(q.title)) e.Add($"{at}: title is empty");
            if (!string.IsNullOrEmpty(q.giverNpc) && s.Npc(q.giverNpc) == null) e.Add($"{at}: unknown giverNpc '{q.giverNpc}'");
            if (q.objectives.Length == 0) e.Add($"{at}: needs at least one objective");
            foreach (var o in q.objectives)
            {
                if (!ObjectiveTypes.Contains(o.type ?? "")) { e.Add($"{at}: unknown objective type '{o.type}'"); continue; }
                if (o.count < 1) e.Add($"{at}: objective count must be >= 1");
                if (o.type == "collect" && s.Item(o.target) == null) e.Add($"{at}: collect target '{o.target}' is not an item");
                if (o.type == "kill" && s.Monster(o.target) == null) e.Add($"{at}: kill target '{o.target}' is not a monster");
            }
            var r = q.rewards;
            if (r.exp < 0 || r.gold < 0) e.Add($"{at}: rewards must be >= 0");
            if (r.fillToLevel < 0 || r.fillToLevel > s.exp.maxLevel) e.Add($"{at}: fillToLevel out of range");
            foreach (var u in r.unlocks ?? Array.Empty<string>())
                if (!Unlocks.ContainsKey(u)) e.Add($"{at}: unknown unlock '{u}'");
            foreach (var it in r.items ?? Array.Empty<ItemReward>())
                if (s.Item(it.id) == null) e.Add($"{at}: unknown reward item '{it.id}'");
        }

        // dialogues
        Unique(s.dialogues, d => d.id, "dialogues", e);
        foreach (var d in s.dialogues)
        {
            string at = $"dialogues[{d.id}]";
            if (string.IsNullOrWhiteSpace(d.text)) e.Add($"{at}: text is empty");
            if (d.options.Length == 0) e.Add($"{at}: needs at least one option (use next \"\" to close)");
            if (d.options.Length > 8) e.Add($"{at}: at most 8 options");
            foreach (var o in d.options)
            {
                if (string.IsNullOrWhiteSpace(o.label)) e.Add($"{at}: option without label");
                if (!string.IsNullOrEmpty(o.next) && s.Dialogue(o.next) == null) e.Add($"{at}: unknown next node '{o.next}'");
                foreach (var a in o.actions ?? Array.Empty<DialogueAction>())
                {
                    if (!ActionTypes.Contains(a.type ?? "")) { e.Add($"{at}: unknown action '{a.type}'"); continue; }
                    if (a.type == "giveItem" && s.Item(a.id) == null) e.Add($"{at}: giveItem unknown item '{a.id}'");
                    if (a.type == "giveItem" && a.count < 1) e.Add($"{at}: giveItem count must be >= 1");
                    if (a.type is "acceptQuest" or "completeQuest" && s.Quest(a.id) == null) e.Add($"{at}: unknown quest '{a.id}'");
                    if (a.type == "startTrial" && s.Job(a.id) == null) e.Add($"{at}: startTrial unknown job '{a.id}'");
                }
            }
        }

        // npcs
        Unique(s.npcs, n => n.id, "npcs", e);
        foreach (var n in s.npcs)
        {
            string at = $"npcs[{n.id}]";
            if (string.IsNullOrWhiteSpace(n.name)) e.Add($"{at}: name is empty");
            if (!In(n.x, n.z)) e.Add($"{at}: position out of bounds");
            if (s.Dialogue(n.defaultNode) == null) e.Add($"{at}: unknown defaultNode '{n.defaultNode}'");
            foreach (var en in n.entries)
            {
                bool hasQuest = !string.IsNullOrEmpty(en.quest);
                if (hasQuest && s.Quest(en.quest) == null) e.Add($"{at}: entry quest '{en.quest}' unknown");
                if (hasQuest && !QuestStates.Contains(en.state ?? "")) e.Add($"{at}: entry state '{en.state}' (none|active|ready|completed)");
                if (!string.IsNullOrEmpty(en.job) && en.job != "none" && en.job != "any" && s.Job(en.job) == null) e.Add($"{at}: entry job '{en.job}' unknown");
                if (!string.IsNullOrEmpty(en.trial) && en.trial != "yes" && en.trial != "no") e.Add($"{at}: entry trial must be yes|no");
                if (s.Dialogue(en.node) == null) e.Add($"{at}: entry node '{en.node}' unknown");
            }
        }
    }

    static void Config(GameConfig c, List<string> errors)
    {
        void Pos(float v, string name) { if (!(v > 0)) errors.Add($"game_config: {name} must be > 0 (got {v})"); }
        Pos(c.movement.runSpeedMps, "movement.runSpeedMps");
        Pos(c.movement.gravity, "movement.gravity");
        Pos(c.movement.jumpHeight, "movement.jumpHeight");
        Pos(c.movement.dashCooldown, "movement.dashCooldown");
        Pos(c.movement.dashDuration, "movement.dashDuration");
        if (c.movement.maxJumps < 1) errors.Add("game_config: movement.maxJumps must be >= 1");
        if (c.validation.speedTolerance < 1) errors.Add("game_config: validation.speedTolerance must be >= 1");
        if (c.validation.attackIntervalTolerance is <= 0 or > 1) errors.Add("game_config: validation.attackIntervalTolerance must be 0..1");
        if (c.net.serverTickRate is < 5 or > 60) errors.Add("game_config: net.serverTickRate must be 5..60");
        Pos(c.combat.defenseBase, "combat.defenseBase");
        if (c.combat.randomMin <= 0 || c.combat.randomMax < c.combat.randomMin) errors.Add("game_config: combat.randomMin/Max invalid");
    }

    static void Weapon(WeaponStats w, string at, List<string> e)
    {
        if (w == null) { e.Add($"{at}: missing"); return; }
        if (w.interval <= 0) e.Add($"{at}: interval must be > 0");
        if (w.range <= 0) e.Add($"{at}: range must be > 0");
        if (w.arcDeg is <= 0 or > 360) e.Add($"{at}: arcDeg must be 0..360");
    }

    static float Dist(float x, float z, CircleDef c) => MathF.Sqrt((x - c.x) * (x - c.x) + (z - c.z) * (z - c.z));

    static void Unique<T>(IEnumerable<T> list, Func<T, string> id, string table, List<string> e)
    {
        var seen = new HashSet<string>();
        foreach (var x in list)
        {
            var k = id(x);
            if (string.IsNullOrEmpty(k)) e.Add($"{table}: entry without id");
            else if (!seen.Add(k)) e.Add($"{table}: duplicate id '{k}'");
        }
    }

    static readonly HashSet<string> OptionIds = new() { "attackPct", "defensePct", "maxHpPct", "maxManaPct", "critChance", "critDamage", "attackSpeed", "cdr", "penetration", "ccResist", "hpRegen", "lifesteal" };

    static void ItemRulesCheck(SharedData s, List<string> e)
    {
        var r = s.itemRules;
        if (r.grades.Length < 1 || r.grades[0].id != "common") e.Add("item_rules: the first grade must be 'common'");
        var ids = new HashSet<string>();
        foreach (var g in r.grades)
        {
            if (!ids.Add(g.id)) e.Add($"item_rules: duplicate grade '{g.id}'");
            if (g.statMult <= 0 || g.valueMult <= 0 || g.optionLines < 0 || g.rangeBoost <= 0) e.Add($"item_rules.grades[{g.id}]: bad numbers");
        }
        if (r.options.Length < 5) e.Add("item_rules: need at least 5 option kinds");
        foreach (var o in r.options)
        {
            if (!OptionIds.Contains(o.id)) e.Add($"item_rules.options: unknown option '{o.id}'");
            if (o.min < 0 || o.max < o.min) e.Add($"item_rules.options[{o.id}]: need 0 <= min <= max");
        }
        foreach (var g in r.grades) if (g.optionLines > r.options.Length) e.Add($"item_rules.grades[{g.id}]: more option lines than option kinds");
        if (r.bag.step < 1 || r.bag.maxSlots < s.character.bagSlots || r.bag.baseCost < 0) e.Add("item_rules.bag: bad numbers");
        if (r.sellRatio is < 0 or > 1) e.Add("item_rules: sellRatio must be 0..1");
        foreach (var i in s.items)
        {
            if (i.value < 0 || i.reqLevel < 0 || i.heal < 0 || i.mana < 0) e.Add($"items[{i.id}]: negative value/reqLevel/heal/mana");
            if (i.type == "consumable" && i.heal <= 0 && i.mana <= 0) e.Add($"items[{i.id}]: a consumable must heal or give mana");
        }
    }

    /// <summary>Shops (data/server/shops.json): known NPC, known items and grades, and a price that makes sense.</summary>
    public static void ValidateShops(SharedData s, IEnumerable<ShopDef> shops, List<string> e)
    {
        var seen = new HashSet<string>();
        foreach (var sh in shops)
        {
            string at = $"shops[{sh.id}]";
            if (string.IsNullOrEmpty(sh.id) || !seen.Add(sh.id)) e.Add($"{at}: missing or duplicate id");
            if (s.Npc(sh.npc) == null) e.Add($"{at}: unknown npc '{sh.npc}'");
            if (sh.entries.Length == 0) e.Add($"{at}: no entries");
            foreach (var en in sh.entries)
            {
                var def = s.Item(en.item);
                if (def == null) { e.Add($"{at}: unknown item '{en.item}'"); continue; }
                if (s.itemRules.GradeIndex(en.grade) < 0) e.Add($"{at}: unknown grade '{en.grade}'");
                if (def.value <= 0) e.Add($"{at}: item '{en.item}' has no value (price)");
                if (!string.IsNullOrEmpty(def.equipSlot) && s.itemRules.GradeIndex(en.grade) > s.itemRules.GradeIndex("epic")) e.Add($"{at}: shops sell up to epic only (spec vol.4 3.5)");
            }
        }
    }
}
