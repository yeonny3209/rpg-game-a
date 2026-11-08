using System;
using System.Collections.Generic;

namespace Game.Shared.Protocol
{
    public static class ProtocolInfo
    {
        /// <summary>Bump whenever any message layout changes. Client and server must match.</summary>
        public const ushort Version = 8;
        public const string WebSocketPath = "/ws";
    }

    public enum MsgId : ushort
    {
        // client → server
        Hello = 1,
        MoveState = 2,
        Ping = 3,
        Attack = 4,
        Interact = 5,
        DialogueChoice = 6,
        RespawnRequest = 7,
        ChooseGender = 8,
        AllocateStats = 9,
        UseSkill = 10,
        ChargeStart = 11,
        ChargeRelease = 12,
        ChatSend = 13,
        ChooseElement = 14,
        StartFlight = 15,
        EquipItem = 16,
        UnequipItem = 17,
        UseItem = 18,
        ExpandBag = 19,
        ShopBuy = 20,
        ShopSell = 21,

        // server → client
        Welcome = 100,
        Snapshot = 101,
        EntitySpawn = 102,
        EntityDespawn = 103,
        PositionCorrection = 104,
        Pong = 105,
        Notice = 106,
        CharacterSync = 107,
        InventorySync = 108,
        QuestSync = 109,
        CombatEvent = 110,
        HpUpdate = 111,
        DialogueOpen = 112,
        GainEvent = 113,
        ActionEvent = 114,
        DeathEvent = 115,
        StatsSync = 116,
        SkillSync = 117,
        SkillCast = 118,
        SkillCooldown = 119,
        ProjectileSpawn = 120,
        ProjectileEnd = 121,
        AmmoState = 122,
        CcApplied = 123,
        ChatMessage = 124,
        ChargeCancel = 125,
        TrialState = 126,
        ManaState = 127,
        FlightState = 128,
        ShopOpen = 129,
    }

    [Flags]
    public enum MoveFlags : byte
    {
        None = 0,
        Grounded = 1,
        Gliding = 2,
        Dashing = 4,
        /// <summary>Set on the first state of a dash. Server checks the dash cooldown against it.</summary>
        DashStart = 8,
        /// <summary>Set on the state where a jump (1st or 2nd) began.</summary>
        JumpStart = 16,
        /// <summary>Server only: entity is dead (players wait for respawn, monsters are about to despawn).</summary>
        Dead = 32,
        /// <summary>Server only: monster is walking back to its home and cannot be hit.</summary>
        Returning = 64,
        /// <summary>Set on the first state of a dodge roll. Server checks the dodge cooldown against it and grants a short invulnerable moment.</summary>
        DodgeStart = 128,
    }

    public interface INetMessage
    {
        MsgId Id { get; }
        void Write(NetWriter w);
    }

    public static class Packet
    {
        [ThreadStatic] static NetWriter _scratch;

        public static byte[] Encode(INetMessage msg)
        {
            var w = _scratch ?? (_scratch = new NetWriter(512));
            w.Reset();
            w.WriteU16((ushort)msg.Id);
            msg.Write(w);
            return w.ToArray();
        }

        public static MsgId PeekId(NetReader r) { return (MsgId)r.ReadU16(); }
    }

    // ───────────── client → server ─────────────

    public sealed class Hello : INetMessage
    {
        public MsgId Id => MsgId.Hello;
        public ushort ProtocolVersion;
        public string Name;
        public string Password = "";
        /// <summary>"계정 만들기": create the account with this password if the nickname is free.</summary>
        public bool Create;

        public void Write(NetWriter w) { w.WriteU16(ProtocolVersion); w.WriteString(Name); w.WriteString(Password ?? ""); w.WriteBool(Create); }

        public static Hello Read(NetReader r)
        {
            return new Hello { ProtocolVersion = r.ReadU16(), Name = r.ReadString(64), Password = r.ReadString(256), Create = r.ReadBool() };
        }
    }

    public sealed class MoveState : INetMessage
    {
        public MsgId Id => MsgId.MoveState;
        public uint Seq;
        /// <summary>Echo of the last PositionCorrection.Epoch the client applied. Server ignores stale epochs.</summary>
        public byte CorrectionEpoch;
        public float X, Y, Z;
        public float Yaw;
        public MoveFlags Flags;
        public byte JumpCount;

        public void Write(NetWriter w)
        {
            w.WriteU32(Seq);
            w.WriteU8(CorrectionEpoch);
            w.WritePos(X, Y, Z);
            w.WriteU16(Quant.YawToU16(Yaw));
            w.WriteU8((byte)Flags);
            w.WriteU8(JumpCount);
        }

        public static MoveState Read(NetReader r)
        {
            var m = new MoveState();
            m.Seq = r.ReadU32();
            m.CorrectionEpoch = r.ReadU8();
            r.ReadPos(out m.X, out m.Y, out m.Z);
            m.Yaw = Quant.YawFromU16(r.ReadU16());
            m.Flags = (MoveFlags)r.ReadU8();
            m.JumpCount = r.ReadU8();
            return m;
        }
    }

    public sealed class Ping : INetMessage
    {
        public MsgId Id => MsgId.Ping;
        public double ClientTime;
        public void Write(NetWriter w) { w.WriteF64(ClientTime); }
        public static Ping Read(NetReader r) { return new Ping { ClientTime = r.ReadF64() }; }
    }

    /// <summary>Basic attack (left mouse). Yaw = the direction the player swings. The server decides what was hit.</summary>
    public sealed class Attack : INetMessage
    {
        public MsgId Id => MsgId.Attack;
        public uint Seq;
        public float Yaw;
        public void Write(NetWriter w) { w.WriteU32(Seq); w.WriteU16(Quant.YawToU16(Yaw)); }
        public static Attack Read(NetReader r) { return new Attack { Seq = r.ReadU32(), Yaw = Quant.YawFromU16(r.ReadU16()) }; }
    }

    /// <summary>V key: talk to / use the entity (NPC for now).</summary>
    public sealed class Interact : INetMessage
    {
        public MsgId Id => MsgId.Interact;
        public uint EntityId;
        public void Write(NetWriter w) { w.WriteU32(EntityId); }
        public static Interact Read(NetReader r) { return new Interact { EntityId = r.ReadU32() }; }
    }

    /// <summary>Pick option #Option of dialogue node NodeId. Option 255 = close.</summary>
    public sealed class DialogueChoice : INetMessage
    {
        public const byte Close = 255;
        public MsgId Id => MsgId.DialogueChoice;
        public uint NpcEntityId;
        public string NodeId;
        public byte Option;
        public void Write(NetWriter w) { w.WriteU32(NpcEntityId); w.WriteString(NodeId); w.WriteU8(Option); }
        public static DialogueChoice Read(NetReader r)
        {
            return new DialogueChoice { NpcEntityId = r.ReadU32(), NodeId = r.ReadString(64), Option = r.ReadU8() };
        }
    }

    /// <summary>"마을에서 부활" button.</summary>
    public sealed class RespawnRequest : INetMessage
    {
        public MsgId Id => MsgId.RespawnRequest;
        public void Write(NetWriter w) { }
        public static RespawnRequest Read(NetReader r) { return new RespawnRequest(); }
    }

    /// <summary>Character look. Unset until the player picks one right after the first login (Unset renders like Male).</summary>
    public enum Gender : byte { Unset = 0, Male = 1, Female = 2 }

    /// <summary>Pick the character's look. The server accepts it once per character.</summary>
    public sealed class ChooseGender : INetMessage
    {
        public MsgId Id => MsgId.ChooseGender;
        public Gender Value;
        public void Write(NetWriter w) { w.WriteU8((byte)Value); }
        public static ChooseGender Read(NetReader r) { return new ChooseGender { Value = (Gender)r.ReadU8() }; }
    }

    /// <summary>Spend stat points. The numbers are how many points go to each stat; the server checks them against the points you have.</summary>
    public sealed class AllocateStats : INetMessage
    {
        public MsgId Id => MsgId.AllocateStats;
        public ushort Str, Dex, Int, Con, Spi;
        public void Write(NetWriter w) { w.WriteU16(Str); w.WriteU16(Dex); w.WriteU16(Int); w.WriteU16(Con); w.WriteU16(Spi); }
        public static AllocateStats Read(NetReader r) { return new AllocateStats { Str = r.ReadU16(), Dex = r.ReadU16(), Int = r.ReadU16(), Con = r.ReadU16(), Spi = r.ReadU16() }; }
    }

    /// <summary>Use the skill in slot Slot (0..5 = Q E T G Z X) aimed along Yaw. The server checks level, cooldown and state.</summary>
    public sealed class UseSkill : INetMessage
    {
        public MsgId Id => MsgId.UseSkill;
        public byte Slot;
        public float Yaw;
        public void Write(NetWriter w) { w.WriteU8(Slot); w.WriteU16(Quant.YawToU16(Yaw)); }
        public static UseSkill Read(NetReader r) { return new UseSkill { Slot = r.ReadU8(), Yaw = Quant.YawFromU16(r.ReadU16()) }; }
    }

    /// <summary>Archer: started drawing the bow. The server times the hold itself, so a client cannot fake a full charge.</summary>
    public sealed class ChargeStart : INetMessage
    {
        public MsgId Id => MsgId.ChargeStart;
        public void Write(NetWriter w) { }
        public static ChargeStart Read(NetReader r) { return new ChargeStart(); }
    }

    /// <summary>Archer: let go of the bow. Yaw = where the shot goes.</summary>
    public sealed class ChargeRelease : INetMessage
    {
        public MsgId Id => MsgId.ChargeRelease;
        public float Yaw;
        public void Write(NetWriter w) { w.WriteU16(Quant.YawToU16(Yaw)); }
        public static ChargeRelease Read(NetReader r) { return new ChargeRelease { Yaw = Quant.YawFromU16(r.ReadU16()) }; }
    }

    public enum ChatChannel : byte { General = 0, World = 1, System = 2 }

    public sealed class ChatSend : INetMessage
    {
        public MsgId Id => MsgId.ChatSend;
        public ChatChannel Channel;
        public string Text;
        public void Write(NetWriter w) { w.WriteU8((byte)Channel); w.WriteString(Text ?? ""); }
        public static ChatSend Read(NetReader r) { return new ChatSend { Channel = (ChatChannel)r.ReadU8(), Text = r.ReadString(512) }; }
    }

    /// <summary>마법사: pick the element once, right after the first job change (spec vol.2 5.2, 미결 1 결정 2026-11-08).</summary>
    public sealed class ChooseElement : INetMessage
    {
        public MsgId Id => MsgId.ChooseElement;
        public string ElementId;
        public void Write(NetWriter w) { w.WriteString(ElementId ?? ""); }
        public static ChooseElement Read(NetReader r) { return new ChooseElement { ElementId = r.ReadString(64) }; }
    }

    /// <summary>C key: start flying (or land early). The server checks the job, the cooldown and the duration.</summary>
    public sealed class StartFlight : INetMessage
    {
        public MsgId Id => MsgId.StartFlight;
        public void Write(NetWriter w) { }
        public static StartFlight Read(NetReader r) { return new StartFlight(); }
    }

    /// <summary>Equip the item in bag slot BagSlot (swaps with what is worn there).</summary>
    public sealed class EquipItem : INetMessage
    {
        public MsgId Id => MsgId.EquipItem;
        public ushort BagSlot;
        public void Write(NetWriter w) { w.WriteU16(BagSlot); }
        public static EquipItem Read(NetReader r) { return new EquipItem { BagSlot = r.ReadU16() }; }
    }

    /// <summary>Take off the item in equipment slot EquipSlot (it goes to the bag).</summary>
    public sealed class UnequipItem : INetMessage
    {
        public MsgId Id => MsgId.UnequipItem;
        public byte EquipSlot;
        public void Write(NetWriter w) { w.WriteU8(EquipSlot); }
        public static UnequipItem Read(NetReader r) { return new UnequipItem { EquipSlot = r.ReadU8() }; }
    }

    /// <summary>Use the consumable in bag slot BagSlot (potions: no cooldown, spec vol.4 2.9).</summary>
    public sealed class UseItem : INetMessage
    {
        public MsgId Id => MsgId.UseItem;
        public ushort BagSlot;
        public void Write(NetWriter w) { w.WriteU16(BagSlot); }
        public static UseItem Read(NetReader r) { return new UseItem { BagSlot = r.ReadU16() }; }
    }

    /// <summary>Buy 10 more bag slots for gold (each expansion costs twice the last, up to 120).</summary>
    public sealed class ExpandBag : INetMessage
    {
        public MsgId Id => MsgId.ExpandBag;
        public void Write(NetWriter w) { }
        public static ExpandBag Read(NetReader r) { return new ExpandBag(); }
    }

    /// <summary>Buy Count of entry Index of the shop the player has open.</summary>
    public sealed class ShopBuy : INetMessage
    {
        public MsgId Id => MsgId.ShopBuy;
        public ushort Index, Count;
        public void Write(NetWriter w) { w.WriteU16(Index); w.WriteU16(Count); }
        public static ShopBuy Read(NetReader r) { return new ShopBuy { Index = r.ReadU16(), Count = r.ReadU16() }; }
    }

    /// <summary>Sell Count of the bag slot to the open shop's NPC (10% of the shop price).</summary>
    public sealed class ShopSell : INetMessage
    {
        public MsgId Id => MsgId.ShopSell;
        public ushort BagSlot, Count;
        public void Write(NetWriter w) { w.WriteU16(BagSlot); w.WriteU16(Count); }
        public static ShopSell Read(NetReader r) { return new ShopSell { BagSlot = r.ReadU16(), Count = r.ReadU16() }; }
    }

    // ───────────── server → client ─────────────

    public sealed class Welcome : INetMessage
    {
        public MsgId Id => MsgId.Welcome;
        public uint YourEntityId;
        public double ServerTime;
        public float X, Y, Z, Yaw;
        /// <summary>All data/shared tables bundled as one JSON object (Game.Shared.Data.SharedData). Server values always win.</summary>
        public string SharedDataJson;

        public void Write(NetWriter w)
        {
            w.WriteU32(YourEntityId);
            w.WriteF64(ServerTime);
            w.WritePos(X, Y, Z);
            w.WriteU16(Quant.YawToU16(Yaw));
            w.WriteLongString(SharedDataJson);
        }

        public static Welcome Read(NetReader r)
        {
            var m = new Welcome();
            m.YourEntityId = r.ReadU32();
            m.ServerTime = r.ReadF64();
            r.ReadPos(out m.X, out m.Y, out m.Z);
            m.Yaw = Quant.YawFromU16(r.ReadU16());
            m.SharedDataJson = r.ReadLongString(4 * 1024 * 1024);
            return m;
        }
    }

    public struct EntityState
    {
        public uint EntityId;
        public float X, Y, Z;
        public float Yaw;
        public MoveFlags Flags;
    }

    public sealed class Snapshot : INetMessage
    {
        public MsgId Id => MsgId.Snapshot;
        public uint Tick;
        public double ServerTime;
        public EntityState[] Entities = Array.Empty<EntityState>();
        public int Count;

        public void Write(NetWriter w)
        {
            w.WriteU32(Tick);
            w.WriteF64(ServerTime);
            w.WriteU16((ushort)Count);
            for (int i = 0; i < Count; i++)
            {
                var e = Entities[i];
                w.WriteU32(e.EntityId);
                w.WritePos(e.X, e.Y, e.Z);
                w.WriteU16(Quant.YawToU16(e.Yaw));
                w.WriteU8((byte)e.Flags);
            }
        }

        public static Snapshot Read(NetReader r)
        {
            var m = new Snapshot();
            m.Tick = r.ReadU32();
            m.ServerTime = r.ReadF64();
            m.Count = r.ReadU16();
            m.Entities = new EntityState[m.Count];
            for (int i = 0; i < m.Count; i++)
            {
                var e = new EntityState();
                e.EntityId = r.ReadU32();
                r.ReadPos(out e.X, out e.Y, out e.Z);
                e.Yaw = Quant.YawFromU16(r.ReadU16());
                e.Flags = (MoveFlags)r.ReadU8();
                m.Entities[i] = e;
            }
            return m;
        }
    }

    public enum EntityKind : byte { Player = 1, Monster = 2, Npc = 3 }

    public sealed class EntitySpawn : INetMessage
    {
        public MsgId Id => MsgId.EntitySpawn;
        public uint EntityId;
        public EntityKind Kind;
        /// <summary>Monster / NPC table id ("rabbit", "elder"). Empty for players.</summary>
        public string TypeId;
        public string Name;
        public ushort Level;
        public int Hp, MaxHp;
        public MoveFlags Flags;
        public float X, Y, Z;
        public float Yaw;
        /// <summary>Players only.</summary>
        public Gender Gender;
        /// <summary>Players only: weapon type in hand (club | sword | bow, empty = none) so everyone sees the right weapon.</summary>
        public string Weapon = "";

        public void Write(NetWriter w)
        {
            w.WriteU32(EntityId);
            w.WriteU8((byte)Kind);
            w.WriteString(TypeId);
            w.WriteString(Name);
            w.WriteU16(Level);
            w.WriteI32(Hp);
            w.WriteI32(MaxHp);
            w.WriteU8((byte)Flags);
            w.WritePos(X, Y, Z);
            w.WriteU16(Quant.YawToU16(Yaw));
            w.WriteU8((byte)Gender);
            w.WriteString(Weapon ?? "");
        }

        public static EntitySpawn Read(NetReader r)
        {
            var m = new EntitySpawn();
            m.EntityId = r.ReadU32();
            m.Kind = (EntityKind)r.ReadU8();
            m.TypeId = r.ReadString(64);
            m.Name = r.ReadString(64);
            m.Level = r.ReadU16();
            m.Hp = r.ReadI32();
            m.MaxHp = r.ReadI32();
            m.Flags = (MoveFlags)r.ReadU8();
            r.ReadPos(out m.X, out m.Y, out m.Z);
            m.Yaw = Quant.YawFromU16(r.ReadU16());
            m.Gender = (Gender)r.ReadU8();
            m.Weapon = r.ReadString(32);
            return m;
        }
    }

    public sealed class EntityDespawn : INetMessage
    {
        public MsgId Id => MsgId.EntityDespawn;
        public uint EntityId;
        public void Write(NetWriter w) { w.WriteU32(EntityId); }
        public static EntityDespawn Read(NetReader r) { return new EntityDespawn { EntityId = r.ReadU32() }; }
    }

    public enum CorrectionReason : byte
    {
        TooFast = 1,
        TooHigh = 2,
        UnderGround = 3,
        DashCooldown = 4,
        OutOfBounds = 5,
        TooManyJumps = 6,
        /// <summary>Tried to leave the starting area before the first quest unlocks it.</summary>
        AreaLocked = 7,
        /// <summary>Not a violation: respawned in the village.</summary>
        Respawn = 8,
        /// <summary>Not a violation: you are stunned / frozen / rooted and may not move.</summary>
        CcLocked = 9,
        /// <summary>Not a violation: knocked back.</summary>
        Knockback = 10,
        /// <summary>Not a violation: moved by a skill (그림자 걸음).</summary>
        Teleport = 11,
    }

    public sealed class PositionCorrection : INetMessage
    {
        public MsgId Id => MsgId.PositionCorrection;
        public byte Epoch;
        public CorrectionReason Reason;
        public float X, Y, Z;

        public void Write(NetWriter w)
        {
            w.WriteU8(Epoch);
            w.WriteU8((byte)Reason);
            w.WritePos(X, Y, Z);
        }

        public static PositionCorrection Read(NetReader r)
        {
            var m = new PositionCorrection();
            m.Epoch = r.ReadU8();
            m.Reason = (CorrectionReason)r.ReadU8();
            r.ReadPos(out m.X, out m.Y, out m.Z);
            return m;
        }
    }

    public sealed class Pong : INetMessage
    {
        public MsgId Id => MsgId.Pong;
        public double ClientTime;
        public double ServerTime;
        public void Write(NetWriter w) { w.WriteF64(ClientTime); w.WriteF64(ServerTime); }
        public static Pong Read(NetReader r) { return new Pong { ClientTime = r.ReadF64(), ServerTime = r.ReadF64() }; }
    }

    public enum NoticeKind : byte { Info = 0, Error = 1, Fatal = 2 }

    public sealed class Notice : INetMessage
    {
        public MsgId Id => MsgId.Notice;
        public NoticeKind Kind;
        public string Text;
        public void Write(NetWriter w) { w.WriteU8((byte)Kind); w.WriteString(Text); }
        public static Notice Read(NetReader r) { return new Notice { Kind = (NoticeKind)r.ReadU8(), Text = r.ReadString(1024) }; }
    }

    /// <summary>Bit flags for systems unlocked by quests (spec vol.1 6: 장비 시스템 해금, 맵 이동 해금).</summary>
    [Flags]
    public enum UnlockFlags : uint
    {
        None = 0,
        Equipment = 1,
        LeaveStartArea = 2,
    }

    /// <summary>Everything the HUD / character window shows about the local character. Sent whenever it changes.</summary>
    public sealed class CharacterSync : INetMessage
    {
        public MsgId Id => MsgId.CharacterSync;
        public string Name;
        public ushort Level;
        public long Exp;
        /// <summary>EXP needed for the next level (0 at max level).</summary>
        public long ExpToNext;
        public long Gold;
        public int Hp, MaxHp;
        public int Attack, Defense;
        public int StatPoints;
        public ushort Str, Dex, Int, Con, Spi;
        public UnlockFlags Unlocks;
        public bool Dead;
        public Gender Gender;
        /// <summary>Job id (data/shared/jobs.json) or empty before the first job change.</summary>
        public string Job = "";
        public byte JobTier;

        public void Write(NetWriter w)
        {
            w.WriteString(Name);
            w.WriteU16(Level);
            w.WriteU64((ulong)Exp);
            w.WriteU64((ulong)ExpToNext);
            w.WriteU64((ulong)Gold);
            w.WriteI32(Hp); w.WriteI32(MaxHp);
            w.WriteI32(Attack); w.WriteI32(Defense);
            w.WriteI32(StatPoints);
            w.WriteU16(Str); w.WriteU16(Dex); w.WriteU16(Int); w.WriteU16(Con); w.WriteU16(Spi);
            w.WriteU32((uint)Unlocks);
            w.WriteBool(Dead);
            w.WriteU8((byte)Gender);
            w.WriteString(Job ?? "");
            w.WriteU8(JobTier);
        }

        public static CharacterSync Read(NetReader r)
        {
            var m = new CharacterSync();
            m.Name = r.ReadString(64);
            m.Level = r.ReadU16();
            m.Exp = (long)r.ReadU64();
            m.ExpToNext = (long)r.ReadU64();
            m.Gold = (long)r.ReadU64();
            m.Hp = r.ReadI32(); m.MaxHp = r.ReadI32();
            m.Attack = r.ReadI32(); m.Defense = r.ReadI32();
            m.StatPoints = r.ReadI32();
            m.Str = r.ReadU16(); m.Dex = r.ReadU16(); m.Int = r.ReadU16(); m.Con = r.ReadU16(); m.Spi = r.ReadU16();
            m.Unlocks = (UnlockFlags)r.ReadU32();
            m.Dead = r.ReadBool();
            m.Gender = (Gender)r.ReadU8();
            m.Job = r.ReadString(64);
            m.JobTier = r.ReadU8();
            return m;
        }
    }

    public enum ItemContainer : byte { Bag = 0, Equipment = 1 }

    public struct ItemSlotState
    {
        public ItemContainer Container;
        public ushort Slot;
        public string ItemId;
        public int Count;
        /// <summary>Index into itemRules.grades (0 = common) and the rolled options as "id:value;id:value".</summary>
        public byte Grade;
        public string Opts;
    }

    /// <summary>Full bag + equipment contents. Small enough at 40 slots to resend whole on change.</summary>
    public sealed class InventorySync : INetMessage
    {
        public MsgId Id => MsgId.InventorySync;
        public ushort BagSize;
        public List<ItemSlotState> Items = new List<ItemSlotState>();

        public void Write(NetWriter w)
        {
            w.WriteU16(BagSize);
            w.WriteU16((ushort)Items.Count);
            foreach (var it in Items)
            {
                w.WriteU8((byte)it.Container);
                w.WriteU16(it.Slot);
                w.WriteString(it.ItemId);
                w.WriteI32(it.Count);
                w.WriteU8(it.Grade);
                w.WriteString(it.Opts ?? "");
            }
        }

        public static InventorySync Read(NetReader r)
        {
            var m = new InventorySync { BagSize = r.ReadU16() };
            int n = r.ReadU16();
            for (int i = 0; i < n; i++)
            {
                m.Items.Add(new ItemSlotState
                {
                    Container = (ItemContainer)r.ReadU8(),
                    Slot = r.ReadU16(),
                    ItemId = r.ReadString(64),
                    Count = r.ReadI32(),
                    Grade = r.ReadU8(),
                    Opts = r.ReadString(256),
                });
            }
            return m;
        }
    }

    public enum QuestState : byte
    {
        None = 0,
        Active = 1,
        /// <summary>Active and every objective met — go back to the NPC.</summary>
        Ready = 2,
        Completed = 3,
    }

    public struct QuestStatus
    {
        public string QuestId;
        public QuestState State;
        /// <summary>Current value per objective (collected / killed so far), same order as the quest table.</summary>
        public int[] Progress;
    }

    public sealed class QuestSync : INetMessage
    {
        public MsgId Id => MsgId.QuestSync;
        public List<QuestStatus> Quests = new List<QuestStatus>();

        public void Write(NetWriter w)
        {
            w.WriteU16((ushort)Quests.Count);
            foreach (var q in Quests)
            {
                w.WriteString(q.QuestId);
                w.WriteU8((byte)q.State);
                var p = q.Progress ?? Array.Empty<int>();
                w.WriteU8((byte)p.Length);
                foreach (var v in p) w.WriteI32(v);
            }
        }

        public static QuestSync Read(NetReader r)
        {
            var m = new QuestSync();
            int n = r.ReadU16();
            for (int i = 0; i < n; i++)
            {
                var q = new QuestStatus { QuestId = r.ReadString(64), State = (QuestState)r.ReadU8() };
                int k = r.ReadU8();
                q.Progress = new int[k];
                for (int j = 0; j < k; j++) q.Progress[j] = r.ReadI32();
                m.Quests.Add(q);
            }
            return m;
        }
    }

    [Flags]
    public enum HitFlags : byte
    {
        None = 0,
        Crit = 1,
        Kill = 2,
        /// <summary>후방 2배 (damage number: purple).</summary>
        Back = 4,
        /// <summary>Hit with defense penetration (damage number: sky blue).</summary>
        Pierce = 8,
        /// <summary>Fixed damage (dark red).</summary>
        Fixed = 16,
        /// <summary>Burn damage over time (orange).</summary>
        Burn = 32,
        /// <summary>Healing: Damage is the amount healed (damage number: green).</summary>
        Heal = 64,
    }

    /// <summary>Damage dealt (server decided). Drives damage numbers and HP bars.</summary>
    public sealed class CombatEvent : INetMessage
    {
        public MsgId Id => MsgId.CombatEvent;
        public uint AttackerId, TargetId;
        public int Damage;
        public HitFlags Flags;
        public int TargetHp, TargetMaxHp;

        public void Write(NetWriter w)
        {
            w.WriteU32(AttackerId); w.WriteU32(TargetId);
            w.WriteI32(Damage);
            w.WriteU8((byte)Flags);
            w.WriteI32(TargetHp); w.WriteI32(TargetMaxHp);
        }

        public static CombatEvent Read(NetReader r)
        {
            return new CombatEvent
            {
                AttackerId = r.ReadU32(), TargetId = r.ReadU32(),
                Damage = r.ReadI32(),
                Flags = (HitFlags)r.ReadU8(),
                TargetHp = r.ReadI32(), TargetMaxHp = r.ReadI32(),
            };
        }
    }

    /// <summary>HP changed without a hit (regen, leash heal, respawn).</summary>
    public sealed class HpUpdate : INetMessage
    {
        public MsgId Id => MsgId.HpUpdate;
        public uint EntityId;
        public int Hp, MaxHp;
        public void Write(NetWriter w) { w.WriteU32(EntityId); w.WriteI32(Hp); w.WriteI32(MaxHp); }
        public static HpUpdate Read(NetReader r) { return new HpUpdate { EntityId = r.ReadU32(), Hp = r.ReadI32(), MaxHp = r.ReadI32() }; }
    }

    /// <summary>Show dialogue node NodeId of the NPC (text lives in data/shared/dialogues.json). Empty NodeId = close.</summary>
    public sealed class DialogueOpen : INetMessage
    {
        public MsgId Id => MsgId.DialogueOpen;
        public uint NpcEntityId;
        public string NodeId;
        public void Write(NetWriter w) { w.WriteU32(NpcEntityId); w.WriteString(NodeId); }
        public static DialogueOpen Read(NetReader r) { return new DialogueOpen { NpcEntityId = r.ReadU32(), NodeId = r.ReadString(64) }; }
    }

    public enum GainKind : byte
    {
        Exp = 1,
        Gold = 2,
        Item = 3,
        LevelUp = 4,
        QuestAccepted = 5,
        QuestCompleted = 6,
        Unlock = 7,
        /// <summary>Bag full: the item (Id) was lost.</summary>
        ItemLost = 8,
        /// <summary>RefId = job id.</summary>
        JobChange = 9,
        /// <summary>RefId = skill id, Amount = new level.</summary>
        SkillLevelUp = 10,
        /// <summary>Amount = new training level.</summary>
        TrainingLevelUp = 11,
        /// <summary>Amount = new circle (서클).</summary>
        CircleUp = 12,
    }

    /// <summary>Something the player received — shown as a toast line.</summary>
    public sealed class GainEvent : INetMessage
    {
        public MsgId Id => MsgId.GainEvent;
        public GainKind Kind;
        public string RefId;
        public long Amount;
        public void Write(NetWriter w) { w.WriteU8((byte)Kind); w.WriteString(RefId); w.WriteU64((ulong)Amount); }
        public static GainEvent Read(NetReader r) { return new GainEvent { Kind = (GainKind)r.ReadU8(), RefId = r.ReadString(64), Amount = (long)r.ReadU64() }; }
    }

    public enum ActionKind : byte { Attack = 1 }

    /// <summary>An entity started an action (swing, bite) — other clients play the animation.</summary>
    public sealed class ActionEvent : INetMessage
    {
        public MsgId Id => MsgId.ActionEvent;
        public uint EntityId;
        public ActionKind Action;
        public float Yaw;
        public void Write(NetWriter w) { w.WriteU32(EntityId); w.WriteU8((byte)Action); w.WriteU16(Quant.YawToU16(Yaw)); }
        public static ActionEvent Read(NetReader r) { return new ActionEvent { EntityId = r.ReadU32(), Action = (ActionKind)r.ReadU8(), Yaw = Quant.YawFromU16(r.ReadU16()) }; }
    }

    /// <summary>You died. RespawnAvailableIn = seconds until the respawn button works.</summary>
    public sealed class DeathEvent : INetMessage
    {
        public MsgId Id => MsgId.DeathEvent;
        public uint KillerId;
        public string KillerName;
        public float RespawnAvailableIn;
        public void Write(NetWriter w) { w.WriteU32(KillerId); w.WriteString(KillerName); w.WriteF32(RespawnAvailableIn); }
        public static DeathEvent Read(NetReader r) { return new DeathEvent { KillerId = r.ReadU32(), KillerName = r.ReadString(64), RespawnAvailableIn = r.ReadFiniteF32() }; }
    }

    public enum CcType : byte { None = 0, Stun = 1, Freeze = 2, Root = 3, Knockback = 4, Slow = 5 }

    /// <summary>Derived combat numbers for the character window (spec vol.2 2.2 / 2.3). Sent whenever they change.</summary>
    public sealed class StatsSync : INetMessage
    {
        public MsgId Id => MsgId.StatsSync;
        public int Attack, Defense, MaxHp, MaxMana;
        public float CritChance, CritDamage, AttackSpeed, MoveSpeed, CdReduction, Penetration, CcResist, Lifesteal, HpRegen, ManaRegen, HealPower;
        /// <summary>동양 검사 훈련 단계 (0 for other jobs).</summary>
        public int TrainingLevel;
        public float TrainingBonus;

        public void Write(NetWriter w)
        {
            w.WriteI32(Attack); w.WriteI32(Defense); w.WriteI32(MaxHp); w.WriteI32(MaxMana);
            w.WriteF32(CritChance); w.WriteF32(CritDamage); w.WriteF32(AttackSpeed); w.WriteF32(MoveSpeed);
            w.WriteF32(CdReduction); w.WriteF32(Penetration); w.WriteF32(CcResist); w.WriteF32(Lifesteal);
            w.WriteF32(HpRegen); w.WriteF32(ManaRegen); w.WriteF32(HealPower);
            w.WriteI32(TrainingLevel); w.WriteF32(TrainingBonus);
        }

        public static StatsSync Read(NetReader r)
        {
            return new StatsSync
            {
                Attack = r.ReadI32(), Defense = r.ReadI32(), MaxHp = r.ReadI32(), MaxMana = r.ReadI32(),
                CritChance = r.ReadFiniteF32(), CritDamage = r.ReadFiniteF32(), AttackSpeed = r.ReadFiniteF32(), MoveSpeed = r.ReadFiniteF32(),
                CdReduction = r.ReadFiniteF32(), Penetration = r.ReadFiniteF32(), CcResist = r.ReadFiniteF32(), Lifesteal = r.ReadFiniteF32(),
                HpRegen = r.ReadFiniteF32(), ManaRegen = r.ReadFiniteF32(), HealPower = r.ReadFiniteF32(),
                TrainingLevel = r.ReadI32(), TrainingBonus = r.ReadFiniteF32(),
            };
        }
    }

    public struct SkillStatus
    {
        public string SkillId;
        public byte Level;
        public int Uses;
    }

    /// <summary>Your job and skill levels (spec vol.2 6.1). Skills you have not used yet are listed at level 1.</summary>
    public sealed class SkillSync : INetMessage
    {
        public MsgId Id => MsgId.SkillSync;
        public string Job = "";
        public byte JobTier;
        public long Training;
        public int TrainingToday;
        /// <summary>마법사: the chosen element id (empty until chosen) and the circle (서클).</summary>
        public string Element = "";
        public byte Circle;
        public List<SkillStatus> Skills = new List<SkillStatus>();

        public void Write(NetWriter w)
        {
            w.WriteString(Job ?? ""); w.WriteU8(JobTier);
            w.WriteU64((ulong)Training); w.WriteI32(TrainingToday);
            w.WriteString(Element ?? ""); w.WriteU8(Circle);
            w.WriteU8((byte)Skills.Count);
            foreach (var s in Skills) { w.WriteString(s.SkillId); w.WriteU8(s.Level); w.WriteI32(s.Uses); }
        }

        public static SkillSync Read(NetReader r)
        {
            var m = new SkillSync { Job = r.ReadString(64), JobTier = r.ReadU8(), Training = (long)r.ReadU64(), TrainingToday = r.ReadI32() };
            m.Element = r.ReadString(64); m.Circle = r.ReadU8();
            int n = r.ReadU8();
            for (int i = 0; i < n; i++) m.Skills.Add(new SkillStatus { SkillId = r.ReadString(64), Level = r.ReadU8(), Uses = r.ReadI32() });
            return m;
        }
    }

    /// <summary>An entity used a skill — other clients play the swing / effect.</summary>
    public sealed class SkillCast : INetMessage
    {
        public MsgId Id => MsgId.SkillCast;
        public uint EntityId;
        public string SkillId;
        public float Yaw;
        public void Write(NetWriter w) { w.WriteU32(EntityId); w.WriteString(SkillId); w.WriteU16(Quant.YawToU16(Yaw)); }
        public static SkillCast Read(NetReader r) { return new SkillCast { EntityId = r.ReadU32(), SkillId = r.ReadString(64), Yaw = Quant.YawFromU16(r.ReadU16()) }; }
    }

    /// <summary>Your skill went on cooldown (or was refused: Seconds is what is left).</summary>
    public sealed class SkillCooldown : INetMessage
    {
        public MsgId Id => MsgId.SkillCooldown;
        public string SkillId;
        public float Seconds;
        public void Write(NetWriter w) { w.WriteString(SkillId); w.WriteF32(Seconds); }
        public static SkillCooldown Read(NetReader r) { return new SkillCooldown { SkillId = r.ReadString(64), Seconds = r.ReadFiniteF32() }; }
    }

    /// <summary>Magic: Tier = index of the element in the element table (for the color).</summary>
    public enum ProjectileKind : byte { Arrow = 1, SwordWave = 2, Magic = 3, Dagger = 4 }

    /// <summary>A projectile appeared. Clients fly it themselves; the server decides hits and sends ProjectileEnd.</summary>
    public sealed class ProjectileSpawn : INetMessage
    {
        public MsgId Id => MsgId.ProjectileSpawn;
        public uint ProjectileId, OwnerId;
        public float X, Y, Z, Yaw, Speed, Range;
        public ProjectileKind Kind;
        /// <summary>Charge tier of an arrow (0..2) for the look of the shot.</summary>
        public byte Tier;
        public void Write(NetWriter w)
        {
            w.WriteU32(ProjectileId); w.WriteU32(OwnerId); w.WritePos(X, Y, Z); w.WriteU16(Quant.YawToU16(Yaw));
            w.WriteF32(Speed); w.WriteF32(Range); w.WriteU8((byte)Kind); w.WriteU8(Tier);
        }
        public static ProjectileSpawn Read(NetReader r)
        {
            var m = new ProjectileSpawn { ProjectileId = r.ReadU32(), OwnerId = r.ReadU32() };
            r.ReadPos(out m.X, out m.Y, out m.Z);
            m.Yaw = Quant.YawFromU16(r.ReadU16());
            m.Speed = r.ReadFiniteF32(); m.Range = r.ReadFiniteF32(); m.Kind = (ProjectileKind)r.ReadU8(); m.Tier = r.ReadU8();
            return m;
        }
    }

    public sealed class ProjectileEnd : INetMessage
    {
        public MsgId Id => MsgId.ProjectileEnd;
        public uint ProjectileId;
        public bool Hit;
        public void Write(NetWriter w) { w.WriteU32(ProjectileId); w.WriteBool(Hit); }
        public static ProjectileEnd Read(NetReader r) { return new ProjectileEnd { ProjectileId = r.ReadU32(), Hit = r.ReadBool() }; }
    }

    /// <summary>Archer magazine (spec vol.2 5.5: 5발마다 2.5초 장전).</summary>
    public sealed class AmmoState : INetMessage
    {
        public MsgId Id => MsgId.AmmoState;
        public byte Ammo, Max;
        /// <summary>Seconds until the reload finishes (0 = not reloading).</summary>
        public float ReloadIn;
        public void Write(NetWriter w) { w.WriteU8(Ammo); w.WriteU8(Max); w.WriteF32(ReloadIn); }
        public static AmmoState Read(NetReader r) { return new AmmoState { Ammo = r.ReadU8(), Max = r.ReadU8(), ReloadIn = r.ReadFiniteF32() }; }
    }

    /// <summary>Your charge was cancelled by a CC.</summary>
    public sealed class ChargeCancel : INetMessage
    {
        public MsgId Id => MsgId.ChargeCancel;
        public void Write(NetWriter w) { }
        public static ChargeCancel Read(NetReader r) { return new ChargeCancel(); }
    }

    /// <summary>A crowd control landed on someone (stun, freeze, root, knockback, slow).</summary>
    public sealed class CcApplied : INetMessage
    {
        public MsgId Id => MsgId.CcApplied;
        public uint TargetId;
        public CcType Type;
        public float Duration;
        public void Write(NetWriter w) { w.WriteU32(TargetId); w.WriteU8((byte)Type); w.WriteF32(Duration); }
        public static CcApplied Read(NetReader r) { return new CcApplied { TargetId = r.ReadU32(), Type = (CcType)r.ReadU8(), Duration = r.ReadFiniteF32() }; }
    }

    public sealed class ChatMessage : INetMessage
    {
        public MsgId Id => MsgId.ChatMessage;
        public ChatChannel Channel;
        public string Sender;
        public string Text;
        public void Write(NetWriter w) { w.WriteU8((byte)Channel); w.WriteString(Sender ?? ""); w.WriteString(Text ?? ""); }
        public static ChatMessage Read(NetReader r) { return new ChatMessage { Channel = (ChatChannel)r.ReadU8(), Sender = r.ReadString(64), Text = r.ReadString(512) }; }
    }

    /// <summary>The job-change trial started / ended (banner + timer).</summary>
    public sealed class TrialState : INetMessage
    {
        public MsgId Id => MsgId.TrialState;
        public bool Active;
        public string JobId = "";
        public float TimeLeft;
        public void Write(NetWriter w) { w.WriteBool(Active); w.WriteString(JobId ?? ""); w.WriteF32(TimeLeft); }
        public static TrialState Read(NetReader r) { return new TrialState { Active = r.ReadBool(), JobId = r.ReadString(64), TimeLeft = r.ReadFiniteF32() }; }
    }

    /// <summary>Your current mana (changes every tick while regenerating, so it is sent a few times a second).</summary>
    public sealed class ManaState : INetMessage
    {
        public MsgId Id => MsgId.ManaState;
        public int Mana, MaxMana;
        /// <summary>서양 검사: 마나 주입이 켜져 있다.</summary>
        public bool Infused;
        public void Write(NetWriter w) { w.WriteI32(Mana); w.WriteI32(MaxMana); w.WriteBool(Infused); }
        public static ManaState Read(NetReader r) { return new ManaState { Mana = r.ReadI32(), MaxMana = r.ReadI32(), Infused = r.ReadBool() }; }
    }

    public struct ShopEntryState
    {
        public string ItemId;
        public byte Grade;
        public int Price;
    }

    /// <summary>The shop of an NPC opened: what it sells (already filtered for the player's job) and for how much.</summary>
    public sealed class ShopOpen : INetMessage
    {
        public MsgId Id => MsgId.ShopOpen;
        public uint NpcEntityId;
        public string Name = "";
        public List<ShopEntryState> Entries = new List<ShopEntryState>();

        public void Write(NetWriter w)
        {
            w.WriteU32(NpcEntityId);
            w.WriteString(Name ?? "");
            w.WriteU16((ushort)Entries.Count);
            foreach (var e in Entries) { w.WriteString(e.ItemId); w.WriteU8(e.Grade); w.WriteI32(e.Price); }
        }

        public static ShopOpen Read(NetReader r)
        {
            var m = new ShopOpen { NpcEntityId = r.ReadU32(), Name = r.ReadString(64) };
            int n = r.ReadU16();
            for (int i = 0; i < n; i++) m.Entries.Add(new ShopEntryState { ItemId = r.ReadString(64), Grade = r.ReadU8(), Price = r.ReadI32() });
            return m;
        }
    }

    /// <summary>Someone started / stopped flying. Fell = shot down (spec vol.4 1.5). CooldownLeft is for the owner's HUD.</summary>
    public sealed class FlightState : INetMessage
    {
        public MsgId Id => MsgId.FlightState;
        public uint EntityId;
        public bool Active;
        public bool Fell;
        public float Duration;
        public float CooldownLeft;
        public void Write(NetWriter w) { w.WriteU32(EntityId); w.WriteBool(Active); w.WriteBool(Fell); w.WriteF32(Duration); w.WriteF32(CooldownLeft); }
        public static FlightState Read(NetReader r) { return new FlightState { EntityId = r.ReadU32(), Active = r.ReadBool(), Fell = r.ReadBool(), Duration = r.ReadFiniteF32(), CooldownLeft = r.ReadFiniteF32() }; }
    }
}
