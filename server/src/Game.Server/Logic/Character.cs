using Game.Shared.Protocol;

namespace Game.Server.Logic;

/// <summary>
/// Persistent character state exactly as stored in the DB (one row in characters + item and quest rows).
/// The zone owns a live copy; saves send a deep Clone() to the DB worker so the zone never shares mutable state.
/// </summary>
public sealed class CharacterRecord
{
    public long Id;
    public long AccountId;
    public string Name;
    public int Level = 1;
    public long Exp;
    public long Gold;
    public int StatPoints;
    public int Str, Dex, Int, Con, Spi;
    public int Hp;
    public string Zone;
    public bool HasPosition;
    public float X, Y, Z, Yaw;
    public UnlockFlags Unlocks;
    /// <summary>0 = not chosen yet (the client shows the picker), 1 male, 2 female. Chosen once.</summary>
    public Gender Gender;
    /// <summary>Job id (data/shared/jobs.json) or empty before the first job change.</summary>
    public string Job = "";
    public int JobTier;
    /// <summary>동양 검사 훈련량 (total) and what was gained today (resets when the date changes).</summary>
    public long Training;
    public int TrainingToday;
    public int TrainingDay;
    public List<SkillRow> Skills = new();
    /// <summary>마법사: current mana (-1 = full), the chosen element id and the circle (서클).</summary>
    public int Mana = -1;
    public string Element = "";
    public int Circle;
    /// <summary>버퍼: 신성력 (spec vol.2 5.6). 상한 없음.</summary>
    public int Holy;
    /// <summary>Bag size (40 at the start, +10 per purchase up to 120). 0 = the table's default.</summary>
    public int BagSlots;
    /// <summary>Kept from day one for the dragon-contract rule "레벨이 일주일 오르지 않으면 계약 해지" (vol.3).</summary>
    public DateTime LastLevelUpAt = DateTime.UtcNow;
    public List<ItemRow> Items = new();
    public List<QuestRow> Quests = new();

    public CharacterRecord Clone()
    {
        var c = (CharacterRecord)MemberwiseClone();
        c.Items = Items.Select(i => i with { }).ToList();
        c.Quests = Quests.Select(q => q with { Counters = (int[])q.Counters.Clone() }).ToList();
        c.Skills = Skills.Select(k => k with { }).ToList();
        return c;
    }
}

/// <summary>Grade is an index into itemRules.grades; Opts the rolled options as "id:value;id:value" (empty for plain stacks).</summary>
public sealed record ItemRow(ItemContainer Container, int Slot, string ItemId, int Count, byte Grade = 0, string Opts = "");

/// <summary>State: Active or Completed (Ready is computed, never stored).</summary>
public sealed record QuestRow(string QuestId, QuestState State, int[] Counters);

public sealed record SkillRow(string SkillId, int Level, int Uses);
