using Game.Shared.Data;
using Game.Shared.Protocol;

namespace Game.Server.Logic;

/// <summary>A character's quests. Collect objectives are read live from the inventory; kill objectives keep counters.</summary>
public sealed class QuestLog
{
    readonly SharedData _data;
    readonly Dictionary<string, QuestRow> _rows = new();

    public QuestLog(SharedData data, IEnumerable<QuestRow> rows)
    {
        _data = data;
        foreach (var r in rows)
            if (data.Quest(r.QuestId) != null) _rows[r.QuestId] = r;
    }

    public List<QuestRow> ToRows() => _rows.Values.Select(r => r with { Counters = (int[])r.Counters.Clone() }).ToList();

    public QuestState State(string questId, Inventory inv)
    {
        if (!_rows.TryGetValue(questId, out var r)) return QuestState.None;
        if (r.State == QuestState.Completed) return QuestState.Completed;
        var q = _data.Quest(questId);
        return q != null && ObjectivesMet(q, r, inv) ? QuestState.Ready : QuestState.Active;
    }

    public static string StateName(QuestState s) => s switch
    {
        QuestState.None => "none",
        QuestState.Active => "active",
        QuestState.Ready => "ready",
        _ => "completed",
    };

    int[] Progress(QuestDef q, QuestRow r, Inventory inv)
    {
        var p = new int[q.objectives.Length];
        for (int i = 0; i < p.Length; i++)
        {
            var o = q.objectives[i];
            int v = o.type == "collect" ? inv.Count(o.target) : (i < r.Counters.Length ? r.Counters[i] : 0);
            p[i] = Math.Min(v, o.count);
        }
        return p;
    }

    bool ObjectivesMet(QuestDef q, QuestRow r, Inventory inv)
    {
        var p = Progress(q, r, inv);
        for (int i = 0; i < p.Length; i++) if (p[i] < q.objectives[i].count) return false;
        return true;
    }

    public bool Accept(string questId)
    {
        var q = _data.Quest(questId);
        if (q == null || _rows.ContainsKey(questId)) return false;
        _rows[questId] = new QuestRow(questId, QuestState.Active, new int[q.objectives.Length]);
        return true;
    }

    /// <summary>Counts a kill toward active kill objectives. Returns true if anything changed.</summary>
    public bool OnKill(string monsterId)
    {
        bool changed = false;
        foreach (var id in _rows.Keys.ToList())
        {
            var r = _rows[id];
            if (r.State != QuestState.Active) continue;
            var q = _data.Quest(id);
            for (int i = 0; i < q.objectives.Length; i++)
            {
                var o = q.objectives[i];
                if (o.type != "kill" || o.target != monsterId) continue;
                if (r.Counters.Length <= i) { var c = new int[q.objectives.Length]; r.Counters.CopyTo(c, 0); r = r with { Counters = c }; }
                if (r.Counters[i] < o.count) { r.Counters[i]++; changed = true; }
            }
            _rows[id] = r;
        }
        return changed;
    }

    /// <summary>Hands the quest in: consumes collect items and marks it completed. Rewards are applied by the caller.</summary>
    public bool Complete(string questId, Inventory inv)
    {
        if (State(questId, inv) != QuestState.Ready) return false;
        var q = _data.Quest(questId);
        foreach (var o in q.objectives)
            if (o.type == "collect" && o.consume && !inv.Remove(o.target, o.count)) return false;
        _rows[questId] = _rows[questId] with { State = QuestState.Completed };
        return true;
    }

    /// <summary>True if any active collect objective uses this item (so its pickup changes the tracker).</summary>
    public bool Tracks(string itemId)
    {
        foreach (var r in _rows.Values)
        {
            if (r.State != QuestState.Active) continue;
            foreach (var o in _data.Quest(r.QuestId).objectives) if (o.type == "collect" && o.target == itemId) return true;
        }
        return false;
    }

    public QuestSync ToSync(Inventory inv)
    {
        var m = new QuestSync();
        foreach (var r in _rows.Values)
        {
            var q = _data.Quest(r.QuestId);
            m.Quests.Add(new QuestStatus { QuestId = r.QuestId, State = State(r.QuestId, inv), Progress = Progress(q, r, inv) });
        }
        return m;
    }
}
