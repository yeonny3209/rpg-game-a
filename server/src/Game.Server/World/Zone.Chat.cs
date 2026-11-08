using Game.Server.Logic;
using Game.Server.Net;
using Game.Shared.Data;
using Game.Shared.Protocol;

namespace Game.Server.World;

/// <summary>
/// Chat: 일반(주변) and 월드 외치기 (spec vol.5 12.4 / 결정 기록). Dev commands ("/레벨 12" ...) are for the admin nicknames in
/// data/server/server_config.json only and are written to the audit log.
/// </summary>
public sealed partial class Zone
{
    // [수치] first values; move into data/shared once more channels exist.
    const int ChatMaxLength = 100;
    const double ChatMinGapSec = 0.6;
    const double WorldChatGapSec = 15;
    const float GeneralChatRadius = 40f;

    void OnChatSend(PlayerEntity p, ChatSend m)
    {
        string text = (m.Text ?? "").Trim();
        if (text.Length == 0) return;
        if (text.Length > ChatMaxLength) text = text[..ChatMaxLength];
        text = new string(text.Where(ch => !char.IsControl(ch)).ToArray());
        if (text.Length == 0) return;

        if (_now - p.LastChatAt < ChatMinGapSec)
        {
            p.Conn.Send(new ChatMessage { Channel = ChatChannel.System, Sender = "", Text = "채팅이 너무 빨라. 잠깐 쉬었다가 보내 줘." });
            return;
        }
        p.LastChatAt = _now;

        if (text[0] == '/') { RunCommand(p, text); return; }

        switch (m.Channel)
        {
            case ChatChannel.World:
                if (_now - p.LastWorldChatAt < WorldChatGapSec)
                {
                    int left = (int)Math.Ceiling(WorldChatGapSec - (_now - p.LastWorldChatAt));
                    p.Conn.Send(new ChatMessage { Channel = ChatChannel.System, Sender = "", Text = $"월드 외치기는 {left}초 뒤에 다시 할 수 있어." });
                    return;
                }
                p.LastWorldChatAt = _now;
                Broadcast(new ChatMessage { Channel = ChatChannel.World, Sender = p.Name, Text = text });
                break;

            case ChatChannel.General:
            {
                var bytes = Packet.Encode(new ChatMessage { Channel = ChatChannel.General, Sender = p.Name, Text = text });
                foreach (var other in _players.Values)
                    if (Dist2(other.Move.X, other.Move.Z, p.Move.X, p.Move.Z) <= GeneralChatRadius * GeneralChatRadius) other.Conn.SendRaw(bytes);
                break;
            }
        }
    }

    void SystemMsg(PlayerEntity p, string text) => p.Conn.Send(new ChatMessage { Channel = ChatChannel.System, Sender = "", Text = text });

    // ───────────── dev commands (admins only) ─────────────

    void RunCommand(PlayerEntity p, string line)
    {
        if (!_data.Server.IsAdmin(p.Name)) { SystemMsg(p, "알 수 없는 명령이야."); return; }
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string cmd = parts[0];
        string arg1 = parts.Length > 1 ? parts[1] : "";
        bool num = int.TryParse(arg1, out int n);
        Audit(p, "dev_command", new { line });

        switch (cmd)
        {
            case "/레벨":
                if (!num || n < 1 || n > _d.exp.maxLevel) { SystemMsg(p, $"/레벨 1~{_d.exp.maxLevel}"); return; }
                p.Rec.Level = n; p.Rec.Exp = 0; p.Rec.LastLevelUpAt = DateTime.UtcNow;
                if (p.Rec.Level >= 2) p.Rec.Unlocks |= UnlockFlags.Equipment | UnlockFlags.LeaveStartArea;
                ResetStats(p);
                p.Rec.Hp = p.Stats.MaxHp;
                p.SkillDirty = p.CharDirty = true; p.SaveNow = true;
                SystemMsg(p, $"레벨 {n}로 바꿨어. 스탯은 초기화되고 포인트 {p.Rec.StatPoints}개를 다시 나눌 수 있어.");
                break;

            case "/아이템":   // /아이템 <id> [등급id] [개수] : give an item (equipment gets that grade and fresh random options)
            {
                var def = _d.Item(arg1);
                if (def == null) { SystemMsg(p, "/아이템 <아이템id> [common|rare|epic|unique|legendary|transcendent|mythic] [개수]"); return; }
                int grade = parts.Length > 2 ? Math.Max(0, _d.itemRules.GradeIndex(parts[2])) : 0;
                int cnt = parts.Length > 3 && int.TryParse(parts[3], out int c2) ? Math.Clamp(c2, 1, 999) : 1;
                if (!string.IsNullOrEmpty(def.equipSlot)) { for (int i = 0; i < Math.Min(cnt, 10); i++) GiveGear(p, def.id, grade); }
                else GiveItem(p, def.id, cnt);
                SystemMsg(p, $"{def.name} 지급.");
                break;
            }

            case "/골드":
                if (!num || n < 0) { SystemMsg(p, "/골드 숫자"); return; }
                p.Rec.Gold = n; p.CharDirty = p.SaveDirty = true;
                SystemMsg(p, $"골드 {n}.");
                break;

            case "/스탯초기화":
                ResetStats(p);
                SystemMsg(p, $"스탯을 초기화했어. 포인트 {p.Rec.StatPoints}개.");
                break;

            case "/직업초기화":
                ClearJob(p);
                SystemMsg(p, "직업을 지웠어. 5레벨 이상이면 스승에게 다시 전직할 수 있어.");
                break;

            case "/전직":
            {
                var job = _d.Job(arg1);
                if (job == null || !job.available) { SystemMsg(p, "/전직 eastern_swordsman 또는 archer"); return; }
                EndTrial(p, TrialRun.Outcome.Cancelled, null);
                if (!string.IsNullOrEmpty(p.Rec.Job)) ClearJob(p);
                ChangeJob(p, job, "dev");
                SystemMsg(p, $"{job.name}(으)로 바꿨어.");
                break;
            }

            case "/스킬":     // /스킬 N : every skill of your job at level N
            {
                var job = _d.Job(p.Rec.Job);
                if (job == null || !num) { SystemMsg(p, "직업이 있어야 해. /스킬 1~10"); return; }
                n = Math.Clamp(n, 1, _d.skillRules.maxLevel);
                p.Rec.Skills.Clear();
                foreach (var id in job.skills) p.Rec.Skills.Add(new SkillRow(id, n, 0));
                p.SkillDirty = p.SaveDirty = true;
                SystemMsg(p, $"스킬 레벨을 모두 {n}로 바꿨어.");
                break;
            }

            case "/훈련량":
                if (!num || n < 0) { SystemMsg(p, "/훈련량 숫자"); return; }
                p.Rec.Training = n; p.SkillDirty = p.SaveDirty = true;
                RecalcStats(p);
                SystemMsg(p, $"훈련량 {n} (단계 {p.Stats.TrainingLevel}).");
                break;

            case "/속성":     // /속성 fire : choose / change the mage element (dev only; players cannot change it yet)
            {
                var job = _d.Job(p.Rec.Job);
                if (job == null || !job.mage.enabled || Array.IndexOf(job.mage.elements, arg1) < 0)
                { SystemMsg(p, "마법사만 쓸 수 있어. /속성 fire|ice|water|lightning|plant|metal"); return; }
                p.Rec.Element = arg1; p.SkillDirty = p.SaveDirty = true;
                SystemMsg(p, $"속성을 {arg1}(으)로 바꿨어.");
                break;
            }

            case "/신성력":   // /신성력 N : set the buffer's 신성력 (the 설교 that raises it comes with the temple)
            {
                var job = _d.Job(p.Rec.Job);
                if (job == null || !job.buffer.enabled || !int.TryParse(arg1, out int holy) || holy < 0) { SystemMsg(p, "버퍼만 쓸 수 있어. /신성력 0~100000"); return; }
                p.Rec.Holy = Math.Min(holy, 100000); p.CharDirty = p.SaveDirty = true;
                SystemMsg(p, $"신성력 {p.Rec.Holy} · 버프 배율 ×{job.buffer.Multiplier(p.Rec.Holy):0.00}");
                break;
            }

            case "/마나":
                FillMana(p);
                SystemMsg(p, "마나를 가득 채웠어.");
                break;

            case "/쿨타임":
                p.FlyReadyAt = 0;
                p.SkillReadyAt.Clear();
                foreach (var id in (EffectiveJob(p)?.skills ?? Array.Empty<string>())) p.Conn.Send(new SkillCooldown { SkillId = id, Seconds = 0 });
                SystemMsg(p, "스킬 쿨타임을 모두 지웠어.");
                break;

            default:
                SystemMsg(p, "명령: /레벨 /골드 /스탯초기화 /직업초기화 /전직 /스킬 /훈련량 /속성 /마나 /신성력 /아이템 /쿨타임");
                break;
        }
    }
}
