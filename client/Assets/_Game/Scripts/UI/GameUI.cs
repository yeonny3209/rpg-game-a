using System.Collections.Generic;
using System.Text;
using Game.Client.Core;
using Game.Client.Entities;
using Game.Client.Net;
using Game.Client.Player;
using Game.Client.World;
using Game.Shared.Data;
using Game.Shared.Protocol;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace Game.Client.UI
{
    /// <summary>
    /// All in-game UI, built in code with uGUI and the Korean font (spec vol.5 12.4 HUD: HP 바, 데미지 숫자, 퀘스트 추적창).
    /// Shows only what the server told us (ClientState); every action goes back to the server as a request.
    /// Keys: V 대화 · I 가방 · P 캐릭터 · 1~9 대화 선택지 · Esc 닫기.
    /// </summary>
    public sealed partial class GameUI : MonoBehaviour
    {
        NetClient _net;
        ClientState _st;
        RemoteEntities _ents;
        PlayerMotor _motor;
        Transform _player;
        Camera _cam;
        RectTransform _root;

        // HUD
        Text _hudName, _hudGold;
        Bar _hpBar, _expBar, _manaBar;
        Text _hudFly;
        Text _tracker, _prompt, _center, _help;
        Image _crosshair;
        float _centerUntil;
        RectTransform _toastRoot;
        readonly List<(Text text, float born)> _toasts = new List<(Text, float)>();

        // dialogue
        GameObject _dlg;
        Text _dlgSpeaker, _dlgText;
        RectTransform _dlgOptions;
        readonly List<Button> _dlgButtons = new List<Button>();
        uint _dlgNpc;
        string _dlgNode;
        bool _dlgWaiting;

        // windows
        GameObject _invWin, _charWin, _equipLock;
        Text _invTitle, _charStats;
        SlotView[] _bag, _equip;

        // death
        GameObject _death;
        Text _deathText, _respawnLabel;
        Button _respawnBtn;
        float _respawnAt;
        bool _respawnRequested;

        // look picker (right after login, until a gender is saved)
        GameObject _gender;
        Button _gMale, _gFemale, _gConfirm;
        Gender _gPick = Gender.Male;
        bool _genderSent;
        /// <summary>Fired when the player highlights a look, so the world can show it on their character right away.</summary>
        public event System.Action<Gender> GenderPreview;

        // world-space overlays
        readonly Dictionary<uint, Nameplate> _plates = new Dictionary<uint, Nameplate>();
        readonly List<DamageNumber> _numbers = new List<DamageNumber>();
        readonly Stack<Text> _numberPool = new Stack<Text>();

        WorldEntity _interact;

        public bool DialogueOpen => _dlg && _dlg.activeSelf;
        public bool DeathOpen => _death && _death.activeSelf;
        /// <summary>A window that needs the mouse is open: no camera look, no attacks.</summary>
        public bool GenderOpen => _gender && _gender.activeSelf;
        public bool Blocking => DialogueOpen || DeathOpen || GenderOpen || ElementOpen || ShopOpen || SkillWindowOpen || SettingsOpen || ChatOpen || _invWin.activeSelf || _charWin.activeSelf;

        public void Init(NetClient net, ClientState st, RemoteEntities ents, PlayerMotor motor, Camera cam)
        {
            _net = net; _st = st; _ents = ents; _motor = motor; _player = motor.transform; _cam = cam;
            Build();
            _net.OnDialogue += HandleDialogue;
            _net.OnShop += HandleShop;
            _net.OnGain += HandleGain;
            _net.OnDeath += HandleDeath;
            _net.OnCc += HandleSelfCc;
            _st.Changed += Refresh;
            _ents.OnDamage += HandleDamage;
            _motor.AreaBlocked += () => ShowCenter(_st.Data.map.startAreaMessage, false);
            Refresh();
        }

        void OnDestroy()
        {
            if (_net != null)
            {
                _net.OnDialogue -= HandleDialogue;
                _net.OnShop -= HandleShop;
                _net.OnGain -= HandleGain;
                _net.OnDeath -= HandleDeath;
                _net.OnCc -= HandleSelfCc;
            }
            if (_st != null) _st.Changed -= Refresh;
            if (_ents) _ents.OnDamage -= HandleDamage;
            if (_root) Destroy(_root.gameObject);
        }

        // ───────────── build ─────────────

        void Build()
        {
            if (EventSystem.current == null)
            {
                var es = new GameObject("EventSystem", typeof(EventSystem));
                var module = es.AddComponent<InputSystemUIInputModule>();
                module.AssignDefaultActions();
            }

            var canvasGo = new GameObject("GameUI", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1600, 900);
            scaler.matchWidthOrHeight = 0.5f;
            _root = (RectTransform)canvasGo.transform;

            // Overlay layer first so windows draw on top of nameplates / numbers.
            var overlay = UiKit.Rect("Overlay", _root).Fill();
            _overlay = overlay;

            BuildHud();
            BuildSkillBar();
            BuildChat();
            BuildDialogue();
            BuildInventory();
            BuildCharacter();
            BuildSkillWindow();
            BuildSettings();
            BuildDeath();
            BuildGender();
            BuildElementPicker();
            BuildItemsUi();
        }

        RectTransform _overlay;

        void BuildHud()
        {
            var hud = UiKit.Panel("Hud", _root, UiKit.PanelBg).rectTransform.Place(new Vector2(0, 0), new Vector2(0, 0), new Vector2(16, 16), new Vector2(380, 126));
            _hudName = UiKit.Label("Name", hud, 18, TextAnchor.MiddleLeft, UiKit.TextMain);
            _hudName.rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(12, -6), new Vector2(260, 26));
            _hudGold = UiKit.Label("Gold", hud, 15, TextAnchor.MiddleRight, UiKit.Accent);
            _hudGold.rectTransform.Place(new Vector2(1, 1), new Vector2(1, 1), new Vector2(-12, -6), new Vector2(140, 26));
            _hpBar = new Bar("Hp", hud, new Color(0.78f, 0.2f, 0.2f), 13);
            _hpBar.Root.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(12, -38), new Vector2(356, 22));
            _expBar = new Bar("Exp", hud, new Color(0.3f, 0.6f, 0.95f), 12);
            _expBar.Root.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(12, -66), new Vector2(356, 16));
            _manaBar = new Bar("Mana", hud, new Color(0.25f, 0.45f, 0.95f), 12);
            _manaBar.Root.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(12, -88), new Vector2(356, 16));
            _manaBar.Root.gameObject.SetActive(false);
            _hudFly = UiKit.Label("Fly", hud, 13, TextAnchor.MiddleLeft, new Color(0.7f, 0.9f, 1f));
            _hudFly.rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(12, -106), new Vector2(356, 18));

            _tracker = UiKit.Label("QuestTracker", _root, 15, TextAnchor.UpperLeft, UiKit.TextMain, true);
            _tracker.rectTransform.Place(new Vector2(1, 1), new Vector2(1, 1), new Vector2(-20, -70), new Vector2(330, 200));

            _prompt = UiKit.Label("Prompt", _root, 18, TextAnchor.MiddleCenter, Color.white, true);
            _prompt.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, -130), new Vector2(500, 30));

            _center = UiKit.Label("Center", _root, 22, TextAnchor.MiddleCenter, Color.white, true);
            _center.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -120), new Vector2(900, 60));

            _toastRoot = UiKit.Rect("Toasts", _root).Place(new Vector2(0, 0), new Vector2(0, 0), new Vector2(20, 336), new Vector2(420, 220));

            _help = UiKit.Label("Help", _root, 13, TextAnchor.LowerRight, UiKit.TextDim, true);
            _help.rectTransform.Place(new Vector2(1, 0), new Vector2(1, 0), new Vector2(-16, 12), new Vector2(520, 44));
            _help.text = "WASD 이동 · Space 점프(공중 길게: 활공) · Shift 대시 · 좌클릭 공격 · 우클릭 구르기/조준 · QETGZX 스킬 · C 비행(마법사·궁수) · V 대화 · I 가방 · P 캐릭터 · K 스킬 · O 설정 · Enter 채팅 · Esc 마우스 풀기";

            _crosshair = UiKit.Panel("Crosshair", _root, new Color(1, 1, 1, 0.8f));
            _crosshair.raycastTarget = false;
            _crosshair.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(4, 4));
        }

        void BuildDialogue()
        {
            var p = UiKit.Panel("Dialogue", _root, UiKit.PanelBg);
            _dlg = p.gameObject;
            p.rectTransform.Place(new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 140), new Vector2(820, 250));
            _dlgSpeaker = UiKit.Label("Speaker", p.transform, 19, TextAnchor.MiddleLeft, UiKit.Accent);
            _dlgSpeaker.rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(20, -10), new Vector2(600, 28));
            _dlgText = UiKit.Label("Text", p.transform, 17, TextAnchor.UpperLeft, UiKit.TextMain);
            _dlgText.rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(20, -44), new Vector2(780, 90));
            _dlgOptions = UiKit.Rect("Options", p.transform).Place(new Vector2(0, 0), new Vector2(0, 0), new Vector2(20, 14), new Vector2(780, 100));
            _dlg.SetActive(false);
        }

        void BuildInventory()
        {
            var w = Window("Inventory", new Vector2(0.72f, 0.55f), new Vector2(8 * 62 + 30, 5 * 62 + 124), out _invTitle, () => _invWin.SetActive(false));
            _invWin = w.gameObject;
            int bagSize = _st.Data.character.bagSlots;
            _bag = new SlotView[bagSize];
            for (int i = 0; i < bagSize; i++)
            {
                int cx = i % 8, cy = i / 8;
                _bag[i] = new SlotView(w, new Vector2(15 + cx * 62, -50 - cy * 62), 58, null);
            }
            _invWin.SetActive(false);
        }

        void BuildCharacter()
        {
            var w = Window("Character", new Vector2(0.28f, 0.52f), new Vector2(590, 660), out var title, () => _charWin.SetActive(false));
            _charWin = w.gameObject;
            title.text = "캐릭터";
            _charStats = UiKit.Label("Stats", w, 15, TextAnchor.UpperLeft, UiKit.TextMain);
            _charStats.rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(18, -46), new Vector2(230, 118));
            BuildStatPanel(w);

            var eq = UiKit.Rect("Equipment", w).Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(250, -46), new Vector2(300, 410));
            var eqTitle = UiKit.Label("Title", eq, 15, TextAnchor.MiddleLeft, UiKit.Accent);
            eqTitle.rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 0), new Vector2(300, 24));
            eqTitle.text = "장비";
            _equip = new SlotView[EquipLayout.Count];
            for (int i = 0; i < EquipLayout.Count; i++)
            {
                int cx = i % 4, cy = i / 4;
                _equip[i] = new SlotView(eq, new Vector2(cx * 74, -30 - cy * 92), 66, EquipLayout.SlotLabels[i]);
            }
            var lockPanel = UiKit.Panel("Locked", eq, new Color(0.03f, 0.03f, 0.05f, 0.82f));
            lockPanel.rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(-4, -26), new Vector2(300, 290));
            var lockText = UiKit.Label("Text", lockPanel.transform, 15, TextAnchor.MiddleCenter, UiKit.TextMain);
            lockText.rectTransform.Fill(16);
            lockText.text = "장비 시스템 잠김\n\n이장의 첫 부탁을 끝내면 열립니다.";
            _equipLock = lockPanel.gameObject;
            _charWin.SetActive(false);
        }

        void BuildDeath()
        {
            var p = UiKit.Panel("Death", _root, new Color(0.15f, 0, 0, 0.55f));
            p.rectTransform.Fill();
            _death = p.gameObject;
            var title = UiKit.Label("Title", p.transform, 40, TextAnchor.MiddleCenter, new Color(1f, 0.85f, 0.8f), true);
            title.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, 90), new Vector2(700, 60));
            title.text = "쓰러졌습니다";
            _deathText = UiKit.Label("Text", p.transform, 18, TextAnchor.MiddleCenter, UiKit.TextMain, true);
            _deathText.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, 30), new Vector2(700, 50));
            _respawnBtn = UiKit.Button("Respawn", p.transform, "마을에서 부활", 18, () =>
            {
                if (_respawnRequested || Time.time < _respawnAt) return;
                _respawnRequested = true;
                _net.Send(new RespawnRequest());
            });
            _respawnBtn.GetComponent<RectTransform>().Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, -40), new Vector2(240, 48));
            _respawnLabel = _respawnBtn.GetComponentInChildren<Text>();
            _respawnLabel.alignment = TextAnchor.MiddleCenter;
            _respawnLabel.rectTransform.offsetMin = Vector2.zero;
            _death.SetActive(false);
        }

        void BuildGender()
        {
            // A bar at the bottom so the character stays visible above it.
            var p = UiKit.Panel("GenderPicker", _root, UiKit.PanelBg);
            _gender = p.gameObject;
            p.rectTransform.Place(new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 70), new Vector2(620, 210));
            var title = UiKit.Label("Title", p.transform, 24, TextAnchor.MiddleCenter, UiKit.Accent, true);
            title.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -14), new Vector2(580, 36));
            title.text = "어떤 모습으로 모험을 시작할래?";
            var hint = UiKit.Label("Hint", p.transform, 14, TextAnchor.MiddleCenter, UiKit.TextDim);
            hint.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -52), new Vector2(580, 24));
            hint.text = "누르면 바로 보여 줘 (1 남자 · 2 여자 · Enter 확정). 한 번 정하면 지금은 바꿀 수 없어.";
            _gMale = UiKit.Button("Male", p.transform, "남자", 20, () => PickGender(Gender.Male));
            _gMale.GetComponent<RectTransform>().Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-130, -6), new Vector2(230, 52));
            _gFemale = UiKit.Button("Female", p.transform, "여자", 20, () => PickGender(Gender.Female));
            _gFemale.GetComponent<RectTransform>().Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(130, -6), new Vector2(230, 52));
            foreach (var b in new[] { _gMale, _gFemale })
            {
                var t = b.GetComponentInChildren<Text>();
                t.alignment = TextAnchor.MiddleCenter;
                t.rectTransform.offsetMin = Vector2.zero;
            }
            _gConfirm = UiKit.Button("Confirm", p.transform, "이 모습으로 시작", 20, ConfirmGender);
            _gConfirm.GetComponent<RectTransform>().Place(new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 18), new Vector2(300, 52));
            var ct = _gConfirm.GetComponentInChildren<Text>();
            ct.alignment = TextAnchor.MiddleCenter;
            ct.rectTransform.offsetMin = Vector2.zero;
            _gender.SetActive(false);
        }

        static readonly Color GenderOff = new Color(0.22f, 0.24f, 0.3f, 0.95f);
        static readonly Color GenderOn = new Color(0.78f, 0.6f, 0.2f, 1f);

        void PickGender(Gender g)
        {
            _gPick = g;
            _gMale.image.color = g == Gender.Male ? GenderOn : GenderOff;
            _gFemale.image.color = g == Gender.Female ? GenderOn : GenderOff;
            GenderPreview?.Invoke(g);
        }

        void ConfirmGender()
        {
            if (_genderSent) return;
            _genderSent = true;
            _net.Send(new ChooseGender { Value = _gPick });
            _gender.SetActive(false);
            GenderPreview?.Invoke(Gender.Unset); // Unset = picker closed, camera goes back behind the character
        }

        /// <summary>Open the picker while the saved character has no look yet; close it once the server says it has one.</summary>
        void UpdateGenderPicker()
        {
            var c = _st.Char;
            if (c == null) return;
            if (c.Gender != Gender.Unset)
            {
                if (GenderOpen) { _gender.SetActive(false); GenderPreview?.Invoke(Gender.Unset); }
                return;
            }
            if (!GenderOpen && !_genderSent && !c.Dead)
            {
                _gender.SetActive(true);
                PickGender(_gPick);
            }
        }

        RectTransform Window(string name, Vector2 anchor, Vector2 size, out Text title, System.Action close)
        {
            var w = UiKit.Panel(name, _root, UiKit.PanelBg).rectTransform.Place(anchor, new Vector2(0.5f, 0.5f), Vector2.zero, size);
            title = UiKit.Label("Title", w, 18, TextAnchor.MiddleLeft, UiKit.Accent);
            title.rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(16, -8), new Vector2(size.x - 70, 28));
            var x = UiKit.Button("Close", w, "X", 16, close);
            x.GetComponent<RectTransform>().Place(new Vector2(1, 1), new Vector2(1, 1), new Vector2(-8, -8), new Vector2(32, 28));
            var xt = x.GetComponentInChildren<Text>();
            xt.alignment = TextAnchor.MiddleCenter;
            xt.rectTransform.offsetMin = Vector2.zero;
            return w;
        }

        // ───────────── data → UI ─────────────

        void Refresh()
        {
            var c = _st.Char;
            if (c == null) return;
            _hudName.text = $"{c.Name}  <color=#f2c75a>Lv.{c.Level}</color>" + (c.StatPoints > 0 ? $"  <size=13><color=#9fd18b>스탯 포인트 {c.StatPoints}</color></size>" : "");
            _hudGold.text = $"골드 {c.Gold:N0}";
            _hpBar.Set(c.MaxHp > 0 ? c.Hp / (float)c.MaxHp : 0, $"HP {c.Hp} / {c.MaxHp}");
            if (c.ExpToNext > 0) _expBar.Set(c.Exp / (float)c.ExpToNext, $"EXP {c.Exp} / {c.ExpToNext}  ({c.Exp * 100.0 / c.ExpToNext:0.0}%)");
            else _expBar.Set(1, "최고 레벨");

            UpdateGenderPicker();
            RefreshMage();
            RefreshTracker();
            RefreshInventory();
            RefreshCharacter();
            RefreshSkillWindow();
        }

        void RefreshTracker()
        {
            var sb = new StringBuilder();
            if (_st.Quests != null)
            {
                foreach (var q in _st.Quests.Quests)
                {
                    if (q.State != QuestState.Active && q.State != QuestState.Ready) continue;
                    var def = _st.Data.Quest(q.QuestId);
                    if (def == null) continue;
                    sb.Append("<color=#f2c75a><b>").Append(def.title).Append("</b></color>\n");
                    for (int i = 0; i < def.objectives.Length; i++)
                    {
                        var o = def.objectives[i];
                        int have = q.Progress != null && i < q.Progress.Length ? q.Progress[i] : 0;
                        string label = !string.IsNullOrEmpty(o.label) ? o.label
                            : o.type == "kill" ? (_st.Data.Monster(o.target)?.name ?? o.target) : _st.ItemName(o.target);
                        bool done = have >= o.count;
                        sb.Append(done ? "<color=#9fd18b>" : "").Append(" · ").Append(label).Append(' ')
                          .Append(Mathf.Min(have, o.count)).Append('/').Append(o.count).Append(done ? "</color>" : "").Append('\n');
                    }
                    if (q.State == QuestState.Ready)
                    {
                        string giver = _st.Data.Npc(def.giverNpc)?.name ?? "의뢰인";
                        sb.Append(" <color=#9fd18b>→ ").Append(giver).Append("에게 돌아가기</color>\n");
                    }
                    sb.Append('\n');
                }
            }
            _tracker.text = sb.ToString();
        }

        void RefreshInventory() { RefreshInventoryPaged(); }

        void RefreshCharacter()
        {
            var c = _st.Char;
            if (c == null || _charStats == null) return;
            var job = _st.Job;
            string jobLine = job == null ? "직업 없음" : $"{job.name} <color=#a8adb3>({(job.tierNames != null && c.JobTier >= 1 && c.JobTier <= job.tierNames.Length ? job.tierNames[c.JobTier - 1] : job.name)})</color>";
            if (job != null && !string.IsNullOrEmpty(c.Job) == false) jobLine += "  <color=#ffd27a>(시험 중)</color>";
            _charStats.text =
                $"<b>{c.Name}</b>  Lv.{c.Level}\n{jobLine}\n" +
                (c.ExpToNext > 0 ? $"경험치 {c.Exp} / {c.ExpToNext}\n" : "경험치 최고 레벨\n") +
                $"HP {c.Hp} / {c.MaxHp}";
            RefreshStatPanel();
            _equipLock.SetActive(!_st.Unlocked(UnlockFlags.Equipment));
        }

        // ───────────── server events ─────────────

        void HandleDialogue(DialogueOpen d)
        {
            var talker = _ents != null ? _ents.Get(d.NpcEntityId) : null;
            if (talker && talker.View && !string.IsNullOrEmpty(d.NodeId)) talker.View.PlayTalk();
            _dlgWaiting = false;
            var node = string.IsNullOrEmpty(d.NodeId) ? null : _st.Data.Dialogue(d.NodeId);
            if (node == null) { CloseDialogueLocal(); return; }
            _dlgNpc = d.NpcEntityId;
            _dlgNode = d.NodeId;
            var npcEnt = _ents.Get(d.NpcEntityId);
            _dlgSpeaker.text = !string.IsNullOrEmpty(node.speaker) ? node.speaker : npcEnt != null ? npcEnt.DisplayName : "";
            _dlgText.text = node.text;

            foreach (var b in _dlgButtons) Destroy(b.gameObject);
            _dlgButtons.Clear();
            var opts = node.options ?? new DialogueOption[0];
            for (int i = 0; i < opts.Length; i++)
            {
                int idx = i;
                var b = UiKit.Button("Option" + i, _dlgOptions, $"{i + 1}. {opts[i].label}", 16, () => Choose(idx));
                b.GetComponent<RectTransform>().Place(new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, (opts.Length - 1 - i) * 40), new Vector2(520, 36));
                _dlgButtons.Add(b);
            }
            _dlg.SetActive(true);
            FreeMouse();
        }

        void Choose(int idx)
        {
            if (!DialogueOpen || _dlgWaiting || idx >= _dlgButtons.Count) return;
            _dlgWaiting = true;
            foreach (var b in _dlgButtons) b.interactable = false;
            _net.Send(new DialogueChoice { NpcEntityId = _dlgNpc, NodeId = _dlgNode, Option = (byte)idx });
        }

        void CloseDialogue()
        {
            if (!DialogueOpen) return;
            _net.Send(new DialogueChoice { NpcEntityId = _dlgNpc, NodeId = _dlgNode, Option = DialogueChoice.Close });
            CloseDialogueLocal();
        }

        void CloseDialogueLocal()
        {
            _dlg.SetActive(false);
            _dlgNpc = 0;
            _dlgNode = null;
        }

        void HandleGain(GainEvent g)
        {
            switch (g.Kind)
            {
                case GainKind.Exp: Toast($"경험치 +{g.Amount}", "#8fc3ff"); break;
                case GainKind.Gold: Toast($"골드 +{g.Amount}", "#f2c75a"); break;
                case GainKind.Item: Toast($"{_st.ItemName(g.RefId)} +{g.Amount}", "#ffffff"); break;
                case GainKind.ItemLost: Toast($"가방이 가득 차서 {_st.ItemName(g.RefId)} {g.Amount}개를 얻지 못했습니다", "#ff8a7a"); break;
                case GainKind.LevelUp:
                    Toast($"레벨 업! Lv.{g.Amount}", "#f2c75a");
                    ShowCenter($"<color=#f2c75a>레벨 업!</color>  Lv.{g.Amount}", false);
                    break;
                case GainKind.QuestAccepted: Toast($"퀘스트 수락: {_st.Data.Quest(g.RefId)?.title ?? g.RefId}", "#9fd18b"); break;
                case GainKind.QuestCompleted:
                {
                    string t = _st.Data.Quest(g.RefId)?.title ?? g.RefId;
                    Toast($"퀘스트 완료: {t}", "#9fd18b");
                    ShowCenter($"<color=#9fd18b>퀘스트 완료</color>  {t}", false);
                    break;
                }
                case GainKind.JobChange:
                {
                    string jn = _st.Data.Job(g.RefId)?.name ?? g.RefId;
                    Toast($"전직! {jn}", "#f2c75a");
                    ShowCenter($"<color=#f2c75a>전직 완료</color>  {jn}", false);
                    break;
                }
                case GainKind.SkillLevelUp: Toast($"스킬 레벨 업: {_st.Data.Skill(g.RefId)?.name ?? g.RefId}  Lv.{g.Amount}", "#8fe0ff"); break;
                case GainKind.TrainingLevelUp: Toast($"훈련 단계 {g.Amount}!  공격력이 올랐어", "#9fd18b"); break;
                case GainKind.Unlock:
                    if (g.RefId == "equipment") Toast("장비 시스템 해금! (P: 캐릭터 창)", "#f2c75a");
                    else if (g.RefId == "leaveStartArea") Toast("마을 밖으로 나갈 수 있습니다", "#f2c75a");
                    else Toast($"해금: {g.RefId}", "#f2c75a");
                    break;
            }
        }

        void HandleDeath(DeathEvent d)
        {
            CloseDialogueLocal();
            _invWin.SetActive(false);
            _charWin.SetActive(false);
            _skillWin.SetActive(false);
            _deathText.text = string.IsNullOrEmpty(d.KillerName) ? "" : $"{d.KillerName}에게 당했습니다. 잃는 것은 없습니다.";
            _respawnAt = Time.time + d.RespawnAvailableIn;
            _respawnRequested = false;
            _death.SetActive(true);
            FreeMouse();
        }

        public void OnRespawned()
        {
            _death.SetActive(false);
        }

        void HandleDamage(WorldEntity target, int amount, HitFlags flags, bool mine)
        {
            bool crit = (flags & HitFlags.Crit) != 0, back = (flags & HitFlags.Back) != 0, pierce = (flags & HitFlags.Pierce) != 0;
            Color c;
            if ((flags & HitFlags.Heal) != 0) c = new Color(0.45f, 0.95f, 0.5f);       // 치유
            else if ((flags & HitFlags.Burn) != 0) c = new Color(1f, 0.55f, 0.2f);       // 화상
            else if (amount <= 0) c = new Color(0.65f, 0.85f, 1f);                  // blocked (반격 자세)
            else if (target.IsSelf) c = new Color(1f, 0.45f, 0.4f);
            else if (back) c = new Color(0.8f, 0.5f, 1f);                          // spec 12.4 제안: 후방 2배 보라
            else if (pierce) c = new Color(0.5f, 0.85f, 1f);                       // 관통 하늘색
            else if (crit) c = new Color(1f, 0.88f, 0.2f);                         // 치명타 노랑
            else if (mine) c = Color.white;                                        // 일반 흰색
            else c = new Color(0.8f, 0.8f, 0.8f, 0.85f);
            var t = _numberPool.Count > 0 ? _numberPool.Pop() : UiKit.Label("Dmg", _overlay, 22, TextAnchor.MiddleCenter, Color.white, true);
            t.gameObject.SetActive(true);
            t.rectTransform.sizeDelta = new Vector2(140, 40);
            bool big = crit || back;
            t.fontSize = big ? 30 : 22;
            t.color = c;
            t.text = (flags & HitFlags.Heal) != 0 ? "+" + amount : amount <= 0 ? "막음!" : big ? amount + "!" : amount.ToString();
            var jitter = new Vector3(Random.Range(-0.3f, 0.3f), 0, Random.Range(-0.3f, 0.3f));
            _numbers.Add(new DamageNumber { Text = t, World = target.transform.position + Vector3.up * (target.Height + 0.2f) + jitter, Born = Time.time, Color = c });
        }

        void HandleSelfCc(CcApplied c)
        {
            if (c.TargetId != _st.SelfId) return;
            string label = c.Type == CcType.Stun ? "기절" : c.Type == CcType.Freeze ? "빙결" : c.Type == CcType.Root ? "속박" : c.Type == CcType.Knockback ? "넉백" : "둔화";
            ShowCenter($"{label}!", true);
        }

        public void ShowCenter(string text, bool error)
        {
            if (string.IsNullOrEmpty(text)) return;
            _center.text = text;
            _center.color = error ? new Color(1f, 0.6f, 0.55f) : Color.white;
            _centerUntil = Time.time + 3.5f;
        }

        void Toast(string text, string hex)
        {
            var t = UiKit.Label("Toast", _toastRoot, 16, TextAnchor.LowerLeft, Placeholders.ParseColor(hex, Color.white), true);
            t.rectTransform.Place(new Vector2(0, 0), new Vector2(0, 0), Vector2.zero, new Vector2(420, 24));
            t.text = text;
            _toasts.Add((t, Time.time));
            while (_toasts.Count > 8) { Destroy(_toasts[0].text.gameObject); _toasts.RemoveAt(0); }
        }

        // ───────────── per frame ─────────────

        void Update()
        {
            var kb = Keyboard.current;
            bool dead = _st.Char != null && _st.Char.Dead;
            if (!dead && DeathOpen) _death.SetActive(false); // CharacterSync says we are alive again

            if (kb != null)
            {
                UpdateKeyCapture(kb);
                bool typing = ChatOpen || _keyWait.HasValue;
                if (!typing && kb.escapeKey.wasPressedThisFrame)
                {
                    if (DialogueOpen) CloseDialogue();
                    else if (ShopOpen) CloseShop();
                    else if (GenderOpen) { }
                    else if (SettingsOpen) CloseSettings();
                    else { _invWin.SetActive(false); _charWin.SetActive(false); _skillWin.SetActive(false); }
                }
                if (GenderOpen)
                {
                    if (kb.digit1Key.wasPressedThisFrame || kb.numpad1Key.wasPressedThisFrame) PickGender(Gender.Male);
                    if (kb.digit2Key.wasPressedThisFrame || kb.numpad2Key.wasPressedThisFrame) PickGender(Gender.Female);
                    if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame) ConfirmGender();
                }
                if (ElementOpen && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)) ConfirmElement();
                if (!dead && !DialogueOpen && !GenderOpen && !ElementOpen && !typing)
                {
                    if (kb[KeyBindings.Get(BindAction.HpPotion)].wasPressedThisFrame) UsePotion(true);
                    if (kb[KeyBindings.Get(BindAction.MpPotion)].wasPressedThisFrame) UsePotion(false);
                    if (kb[KeyBindings.Get(BindAction.Inventory)].wasPressedThisFrame) Toggle(_invWin);
                    if (kb[KeyBindings.Get(BindAction.Character)].wasPressedThisFrame) Toggle(_charWin);
                    if (kb[KeyBindings.Get(BindAction.Skills)].wasPressedThisFrame) Toggle(_skillWin);
                    if (kb[KeyBindings.Get(BindAction.Settings)].wasPressedThisFrame) { if (SettingsOpen) CloseSettings(); else OpenSettings(); }
                    if (kb[KeyBindings.Get(BindAction.Interact)].wasPressedThisFrame && _interact != null)
                        _net.Send(new Interact { EntityId = _interact.EntityId });
                    if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame) OpenChat("");
                    else if (kb.slashKey.wasPressedThisFrame) OpenChat("/");
                }
                if (DialogueOpen)
                {
                    for (int i = 0; i < 9 && i < _dlgButtons.Count; i++)
                        if (kb[Key.Digit1 + i].wasPressedThisFrame || kb[Key.Numpad1 + i].wasPressedThisFrame) Choose(i);
                    if (_dlgButtons.Count == 0 && (kb.spaceKey.wasPressedThisFrame || kb.enterKey.wasPressedThisFrame)) CloseDialogue();
                }
            }

            if (Blocking && Cursor.lockState == CursorLockMode.Locked) FreeMouse();
            _motor.InputEnabled = !DialogueOpen && !dead && !GenderOpen && !ElementOpen && !ChatOpen && !_keyWait.HasValue;
            _crosshair.enabled = Cursor.lockState == CursorLockMode.Locked;

            UpdateInteract(dead);
            UpdateSkillUi();
            UpdateItemsUi();
            UpdateMageUi();
            UpdateChatUi();

            if (DeathOpen)
            {
                float left = _respawnAt - Time.time;
                _respawnBtn.interactable = left <= 0 && !_respawnRequested;
                _respawnLabel.text = _respawnRequested ? "부활 중..." : left > 0 ? $"마을에서 부활 ({Mathf.CeilToInt(left)})" : "마을에서 부활";
            }

            float now = Time.time;
            _center.enabled = now < _centerUntil;
            for (int i = _toasts.Count - 1; i >= 0; i--)
            {
                var (t, born) = _toasts[i];
                float age = now - born;
                if (age > 5f) { Destroy(t.gameObject); _toasts.RemoveAt(i); continue; }
                var col = t.color; col.a = Mathf.Clamp01(5f - age); t.color = col;
            }
            for (int i = 0; i < _toasts.Count; i++)
                _toasts[i].text.rectTransform.anchoredPosition = new Vector2(0, (_toasts.Count - 1 - i) * 24);
        }

        void UpdateInteract(bool dead)
        {
            _interact = null;
            if (!dead && !DialogueOpen)
            {
                float best = _st.Data.character.interactRange;
                best *= best;
                foreach (var e in _ents.All)
                {
                    if (e.Kind != EntityKind.Npc) continue;
                    float d = (e.transform.position - _player.position).sqrMagnitude;
                    if (d <= best) { best = d; _interact = e; }
                }
            }
            _prompt.enabled = _interact != null;
            if (_interact != null) _prompt.text = $"<color=#f2c75a>[V]</color> {_interact.DisplayName}{WaGwa(_interact.DisplayName)} 대화하기";
        }

        void LateUpdate()
        {
            if (!_cam) return;
            UpdatePlates();
            float life = Mathf.Max(0.3f, _st.Data.config.combat.damageNumberSec);
            for (int i = _numbers.Count - 1; i >= 0; i--)
            {
                var n = _numbers[i];
                float k = (Time.time - n.Born) / life;
                if (k >= 1f) { n.Text.gameObject.SetActive(false); _numberPool.Push(n.Text); _numbers.RemoveAt(i); continue; }
                var sp = _cam.WorldToScreenPoint(n.World + Vector3.up * (k * 1.2f));
                n.Text.enabled = sp.z > 0;
                n.Text.rectTransform.position = new Vector3(sp.x, sp.y, 0);
                var c = n.Color; c.a *= 1f - k * k; n.Text.color = c;
            }
        }

        void UpdatePlates()
        {
            _seen.Clear();
            var camPos = _cam.transform.position;
            foreach (var e in _ents.All)
            {
                if (e.IsSelf) continue;
                _seen.Add(e.EntityId);
                if (!_plates.TryGetValue(e.EntityId, out var plate)) _plates[e.EntityId] = plate = new Nameplate(_overlay);

                float maxDist = e.Kind == EntityKind.Npc ? 45f : 35f;
                var head = e.transform.position + Vector3.up * (e.Height + 0.35f);
                float dist = (head - camPos).magnitude;
                var sp = _cam.WorldToScreenPoint(head);
                bool show = sp.z > 0 && dist < maxDist && !(e.Kind == EntityKind.Monster && e.Dead);
                plate.Root.gameObject.SetActive(show);
                if (!show) continue;
                plate.Root.position = new Vector3(sp.x, sp.y, 0);

                switch (e.Kind)
                {
                    case EntityKind.Npc:
                    {
                        string title = string.IsNullOrEmpty(e.Title) ? "" : $"\n<size=12><color=#c9d3b5>{e.Title}</color></size>";
                        plate.SetName($"<color=#e8f0a0>{e.DisplayName}</color>{title}");
                        plate.SetMarker(_st.QuestMarker(e.TypeId) ?? _st.JobMarker(e.TypeId));
                        plate.SetStatus(null);
                        plate.SetHp(-1);
                        break;
                    }
                    case EntityKind.Monster:
                    {
                        var def = _st.Data.Monster(e.TypeId);
                        string col = def != null && def.aggressive ? "#ff9a7a" : "#ffffff";
                        plate.SetName($"<size=12><color=#f2c75a>Lv.{e.Level}</color></size> <color={col}>{e.DisplayName}</color>");
                        plate.SetMarker(null);
                        plate.SetStatus(Time.time < e.StatusUntil ? e.StatusText : null);
                        bool hurt = e.Hp < e.MaxHp || Time.time - e.LastHitAt < 5f;
                        plate.SetHp(hurt && e.MaxHp > 0 ? e.Hp / (float)e.MaxHp : -1);
                        break;
                    }
                    default:
                        plate.SetName(e.DisplayName);
                        plate.SetMarker(null);
                        plate.SetStatus(Time.time < e.StatusUntil ? e.StatusText : null);
                        plate.SetHp(e.Hp < e.MaxHp && e.MaxHp > 0 ? e.Hp / (float)e.MaxHp : -1);
                        break;
                }
            }
            _gone.Clear();
            foreach (var id in _plates.Keys) if (!_seen.Contains(id)) _gone.Add(id);
            foreach (var id in _gone) { Destroy(_plates[id].Root.gameObject); _plates.Remove(id); }
        }

        readonly HashSet<uint> _seen = new HashSet<uint>();
        readonly List<uint> _gone = new List<uint>();

        void Toggle(GameObject w)
        {
            w.SetActive(!w.activeSelf);
            if (w.activeSelf) { Refresh(); FreeMouse(); }
        }

        static void FreeMouse()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        /// <summary>와/과 by whether the last Hangul syllable has a final consonant.</summary>
        static string WaGwa(string word)
        {
            if (string.IsNullOrEmpty(word)) return "와";
            char c = word[word.Length - 1];
            if (c < 0xAC00 || c > 0xD7A3) return "와";
            return (c - 0xAC00) % 28 != 0 ? "과" : "와";
        }

        struct DamageNumber
        {
            public Text Text;
            public Vector3 World;
            public float Born;
            public Color Color;
        }
    }

    /// <summary>Name + optional HP bar + quest marker above an entity.</summary>
    sealed class Nameplate
    {
        public readonly RectTransform Root;
        readonly Text _name, _marker, _status;
        readonly Bar _hp;
        string _lastName, _lastMarker, _lastStatus;

        public Nameplate(Transform parent)
        {
            Root = UiKit.Rect("Plate", parent);
            Root.sizeDelta = new Vector2(200, 60);
            Root.pivot = new Vector2(0.5f, 0);
            _name = UiKit.Label("Name", Root, 15, TextAnchor.LowerCenter, Color.white, true);
            _name.rectTransform.Place(new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 10), new Vector2(220, 40));
            _hp = new Bar("Hp", Root, new Color(0.85f, 0.22f, 0.2f), 0);
            _hp.Root.Place(new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 0), new Vector2(90, 7));
            _marker = UiKit.Label("Marker", Root, 34, TextAnchor.LowerCenter, new Color(1f, 0.85f, 0.2f), true);
            _marker.rectTransform.Place(new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 46), new Vector2(60, 44));
            _status = UiKit.Label("Status", Root, 16, TextAnchor.LowerCenter, new Color(1f, 0.7f, 0.3f), true);
            _status.rectTransform.Place(new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 52), new Vector2(140, 24));
            _status.enabled = false;
        }

        public void SetStatus(string text)
        {
            if (text == _lastStatus) return;
            _lastStatus = text;
            _status.text = text ?? "";
            _status.enabled = !string.IsNullOrEmpty(text);
        }

        public void SetName(string s) { if (s != _lastName) { _name.text = s; _lastName = s; } }

        public void SetMarker(string m)
        {
            if (m == _lastMarker) return;
            _lastMarker = m;
            _marker.text = m ?? "";
            _marker.enabled = m != null;
        }

        public void SetHp(float ratio)
        {
            bool on = ratio >= 0;
            if (_hp.Root.gameObject.activeSelf != on) _hp.Root.gameObject.SetActive(on);
            if (on) _hp.Set(ratio);
        }
    }

    /// <summary>One inventory / equipment cell. Placeholder: color square + item name + count.</summary>
    sealed class SlotView
    {
        readonly Image _bg, _icon;
        readonly Text _name, _count;
        readonly Outline _border;
        public readonly SlotPointer Pointer;
        public ItemDef Def;
        public int Grade;
        public string Opts = "";
        public int Count;

        public SlotView(Transform parent, Vector2 pos, float size, string caption)
        {
            _bg = UiKit.Panel("Slot", parent, UiKit.SlotBg);
            _bg.rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), pos, new Vector2(size, size));
            _border = _bg.gameObject.AddComponent<Outline>();
            _border.effectDistance = new Vector2(2f, -2f);
            _border.effectColor = new Color(0, 0, 0, 0);
            Pointer = _bg.gameObject.AddComponent<SlotPointer>();
            _icon = UiKit.Panel("Icon", _bg.transform, Color.white);
            _icon.raycastTarget = false;
            _icon.rectTransform.Fill(5);
            _name = UiKit.Label("Name", _bg.transform, 11, TextAnchor.MiddleCenter, Color.white, true);
            _name.rectTransform.Fill(2);
            _count = UiKit.Label("Count", _bg.transform, 12, TextAnchor.LowerRight, Color.white, true);
            _count.rectTransform.Fill(3);
            if (caption != null)
            {
                var c = UiKit.Label("Caption", _bg.transform, 11, TextAnchor.UpperCenter, UiKit.TextDim);
                c.rectTransform.Place(new Vector2(0.5f, 0), new Vector2(0.5f, 1), new Vector2(0, -2), new Vector2(size + 8, 18));
                c.text = caption;
            }
        }

        public void Set(ItemDef def, int count, Color gradeColor = default, int grade = 0, string opts = "")
        {
            Def = def; Count = count; Grade = grade; Opts = opts ?? "";
            bool has = def != null;
            _icon.enabled = has;
            if (has)
            {
                var col = Placeholders.ParseColor(def.color, Color.gray);
                col.a = 0.55f;
                _icon.color = col;
            }
            _border.effectColor = has && grade > 0 ? new Color(gradeColor.r, gradeColor.g, gradeColor.b, 1f) : new Color(0, 0, 0, 0);
            _name.text = has ? def.name : "";
            _count.text = has && count > 1 ? count.ToString() : "";
        }
    }

    /// <summary>Click / hover events of an inventory slot (right click or double click = use, hover = tooltip).</summary>
    sealed class SlotPointer : MonoBehaviour, UnityEngine.EventSystems.IPointerClickHandler, UnityEngine.EventSystems.IPointerEnterHandler, UnityEngine.EventSystems.IPointerExitHandler
    {
        public System.Action<UnityEngine.EventSystems.PointerEventData> Clicked;
        public System.Action<bool> Hover;
        public void OnPointerClick(UnityEngine.EventSystems.PointerEventData e) { Clicked?.Invoke(e); }
        public void OnPointerEnter(UnityEngine.EventSystems.PointerEventData e) { Hover?.Invoke(true); }
        public void OnPointerExit(UnityEngine.EventSystems.PointerEventData e) { Hover?.Invoke(false); }
        void OnDisable() { Hover?.Invoke(false); }
    }
}
