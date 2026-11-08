using Game.Server.Data;
using Game.Server.Logic;
using Game.Server.Net;
using Game.Shared.Data;
using Game.Shared.Protocol;

namespace Game.Server.World;

public sealed partial class Zone
{
    readonly Dictionary<uint, NpcEntity> _npcs = new();

    void SpawnNpcs()
    {
        foreach (var def in _d.npcs)
        {
            var n = new NpcEntity { EntityId = _nextEntityId++, Def = def, X = def.x, Z = def.z, Yaw = def.yaw, Y = _terrain.Height(def.x, def.z) };
            _npcs[n.EntityId] = n;
        }
    }

    bool NpcInRange(PlayerEntity p, NpcEntity n, float extra)
    {
        float r = _d.character.interactRange + extra;
        return Dist2(p.Move.X, p.Move.Z, n.X, n.Z) <= r * r;
    }

    // ───────────── V: talk ─────────────

    void OnInteract(PlayerEntity p, Interact i)
    {
        if (p.Dead || !_npcs.TryGetValue(i.EntityId, out var n)) return;
        // Small tolerance: the client measures from its predicted position.
        if (!NpcInRange(p, n, 1f)) return;
        OpenNode(p, n, PickEntryNode(p, n.Def));
    }

    string PickEntryNode(PlayerEntity p, NpcDef def)
    {
        foreach (var e in def.entries)
        {
            if (!string.IsNullOrEmpty(e.quest) && QuestLog.StateName(p.Quests.State(e.quest, p.Inv)) != e.state) continue;
            if (e.minLevel > 0 && p.Rec.Level < e.minLevel) continue;
            if (!string.IsNullOrEmpty(e.job))
            {
                bool hasJob = !string.IsNullOrEmpty(p.Rec.Job);
                if (e.job == "none" && hasJob) continue;
                if (e.job == "any" && !hasJob) continue;
                if (e.job != "none" && e.job != "any" && p.Rec.Job != e.job) continue;
            }
            if (!string.IsNullOrEmpty(e.trial) && (p.TrialJob != null) != (e.trial == "yes")) continue;
            return e.node;
        }
        return def.defaultNode;
    }

    void OpenNode(PlayerEntity p, NpcEntity n, string nodeId)
    {
        p.DialogueNpc = string.IsNullOrEmpty(nodeId) ? 0 : n.EntityId;
        p.DialogueNode = nodeId ?? "";
        p.Conn.Send(new DialogueOpen { NpcEntityId = n.EntityId, NodeId = p.DialogueNode });
    }

    void OnDialogueChoice(PlayerEntity p, DialogueChoice c)
    {
        if (c.Option == DialogueChoice.Close) { p.DialogueNpc = 0; return; }
        if (p.Dead || p.DialogueNpc == 0 || c.NpcEntityId != p.DialogueNpc || c.NodeId != p.DialogueNode) return;
        if (!_npcs.TryGetValue(c.NpcEntityId, out var n)) return;
        if (!NpcInRange(p, n, 3f)) { OpenNode(p, n, ""); return; } // walked away
        var node = _d.Dialogue(c.NodeId);
        if (node == null || c.Option >= node.options.Length) return;
        var opt = node.options[c.Option];

        foreach (var a in opt.actions ?? Array.Empty<DialogueAction>())
        {
            string fail = RunAction(p, a);
            if (fail != null)
            {
                p.Conn.Send(new Notice { Kind = NoticeKind.Error, Text = fail });
                OpenNode(p, n, "");
                return;
            }
        }
        OpenNode(p, n, opt.next);
    }

    /// <returns>null on success, otherwise a message for the player.</returns>
    string RunAction(PlayerEntity p, DialogueAction a)
    {
        switch (a.type)
        {
            case "giveItem":
            {
                if (a.unique && p.Inv.Owns(a.id)) return null;
                if (a.equip && a.count == 1 && p.Inv.TryEquipNew(a.id))
                {
                    Gain(p, GainKind.Item, a.id, 1);
                    p.InvDirty = p.SaveDirty = true;
                    RecalcStats(p);
                    Audit(p, "item_given", new { item = a.id, count = 1, equipped = true });
                    return null;
                }
                if (p.Inv.Room(a.id) < a.count) return "가방이 가득 찼어. 자리를 비우고 다시 말을 걸어 줘.";
                GiveItem(p, a.id, a.count);
                Audit(p, "item_given", new { item = a.id, a.count });
                return null;
            }
            case "acceptQuest":
            {
                if (p.Quests.Accept(a.id))
                {
                    Gain(p, GainKind.QuestAccepted, a.id, 0);
                    p.QuestDirty = true;
                    Audit(p, "quest_accepted", new { quest = a.id });
                    p.SaveNow = true;
                }
                return null;
            }
            case "completeQuest":
                return CompleteQuest(p, a.id);
            case "shop":
                return OpenShop(p, a.id);
            case "startTrial":
                return StartTrial(p, a.id);
            case "endTrial":
                EndTrial(p, TrialRun.Outcome.Cancelled, null);
                return null;
        }
        return "알 수 없는 행동이야.";
    }

    string CompleteQuest(PlayerEntity p, string questId)
    {
        var q = _d.Quest(questId);
        var r = q.rewards;
        // Make sure reward items fit before taking anything.
        foreach (var it in r.items ?? Array.Empty<ItemReward>())
            if (p.Inv.Room(it.id) < it.count) return "가방이 가득 차서 보상을 받을 수 없어.";
        if (!p.Quests.Complete(questId, p.Inv)) return "아직 부탁한 걸 다 모으지 못했어.";

        p.InvDirty = p.QuestDirty = p.CharDirty = true;
        Gain(p, GainKind.QuestCompleted, questId, 0);

        // 결정 Q2: 2레벨 미만이면 2레벨까지 채우고, 이미 넘었으면 고정 경험치.
        long exp = r.fillToLevel > 0 && p.Rec.Level < r.fillToLevel ? Rules.ExpToReach(_d.exp, p.Rec, r.fillToLevel) : r.exp;
        GrantExp(p, exp, "quest:" + questId);
        if (r.gold > 0) { p.Rec.Gold += r.gold; Gain(p, GainKind.Gold, "", r.gold); }
        foreach (var it in r.items ?? Array.Empty<ItemReward>()) GiveItem(p, it.id, it.count);
        foreach (var u in r.unlocks ?? Array.Empty<string>())
        {
            var flag = DataValidator.Unlocks[u];
            if ((p.Rec.Unlocks & flag) != 0) continue;
            p.Rec.Unlocks |= flag;
            Gain(p, GainKind.Unlock, u, 0);
        }
        Audit(p, "quest_completed", new { quest = questId, exp, gold = r.gold, level = p.Rec.Level });
        p.SaveNow = true;
        Log.Info($"quest #{p.EntityId} '{p.Name}' completed {questId} → Lv{p.Rec.Level}");
        return null;
    }
}
