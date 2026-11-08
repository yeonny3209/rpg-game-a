using System;
using System.Runtime.InteropServices;
using Game.Client.CameraRig;
using Game.Client.Entities;
using Game.Client.Net;
using Game.Client.Player;
using Game.Client.UI;
using Game.Client.World;
using Game.Shared.Data;
using Game.Shared.Protocol;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Client.Core
{
    /// <summary>
    /// Entry point. The scene contains only this object (named "Bootstrap"); everything else is created here.
    /// Flow: login (HTML overlay in WebGL / OnGUI in editor) → connect → Welcome(all shared tables) → build region → play.
    /// </summary>
    public sealed class Bootstrap : MonoBehaviour
    {
        [Tooltip("Editor/standalone only. WebGL derives the URL from the page address.")]
        public string EditorServerUrl = "ws://localhost:7777/ws";

        NetClient _net;
        GraphicsQuality _quality;
        FpsOverlay _fps;
        Camera _cam;
        Light _sun;
        ThirdPersonCamera _camRig;
        ProjectileManager _projectiles;

        SharedData _data;
        ClientState _state;
        GameObject _world;
        string _worldMapId;
        VillageBuilder.Stats _worldStats;
        GameObject _player;
        PlayerMotor _motor;
        LocalPlayerNet _localNet;
        RemoteEntities _remotes;
        GameUI _ui;

        bool _connecting;
        string _name = "";
        string _password = "";
        bool _create;
#pragma warning disable CS0414 // only shown by the editor login box
        string _editorNotice = "";
#pragma warning restore CS0414

        bool InGame => _state != null;

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern string RpgGetNickname();
        [DllImport("__Internal")] static extern void RpgShowMessage(string text, int kind);
        [DllImport("__Internal")] static extern void RpgShowLogin(int show);
#endif

        void Awake()
        {
            gameObject.name = "Bootstrap"; // HTML calls SendMessage("Bootstrap", ...)
            Application.runInBackground = true;
            Application.targetFrameRate = -1;
#if UNITY_WEBGL && !UNITY_EDITOR
            WebGLInput.captureAllKeyboardInput = false; // the HTML chat / login inputs need the keyboard when they have focus
#endif

            _cam = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)).GetComponent<Camera>();
            _cam.tag = "MainCamera";
            _cam.nearClipPlane = 0.2f;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.62f, 0.74f, 0.86f);
            _cam.transform.position = new Vector3(0, 30, -40);
            _cam.transform.LookAt(Vector3.zero);
            RenderSettings.fogColor = _cam.backgroundColor;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.55f, 0.62f, 0.72f);
            RenderSettings.ambientEquatorColor = new Color(0.42f, 0.45f, 0.45f);
            RenderSettings.ambientGroundColor = new Color(0.25f, 0.24f, 0.2f);

            _sun = new GameObject("Sun", typeof(Light)).GetComponent<Light>();
            _sun.type = LightType.Directional;
            _sun.intensity = 1.2f;
            _sun.color = new Color(1f, 0.96f, 0.88f);
            _sun.transform.rotation = Quaternion.Euler(48f, -35f, 0);

            _quality = gameObject.AddComponent<GraphicsQuality>();
            _quality.Init(_sun, _cam);

            _net = gameObject.AddComponent<NetClient>();
            _net.OnWelcome += HandleWelcome;
            _net.OnNotice += HandleNotice;
            _net.OnDisconnected += HandleDisconnected;
            _net.OnCharacter += c => { if (_state == null) return; _state.Char = c; ApplyUnlocks(); ApplyLook(c.Gender); _state.Notify(); };
            _net.OnInventory += i => { if (_state == null) return; _state.Inv = i; _state.Notify(); };
            _net.OnQuests += q => { if (_state == null) return; _state.Quests = q; _state.Notify(); };
            _net.OnHp += h => SelfHp(h.EntityId, h.Hp, h.MaxHp);
            _net.OnCombat += c => SelfHp(c.TargetId, c.TargetHp, c.TargetMaxHp);
            _net.OnStats += st => { if (_state == null) return; _state.Stats = st; _state.Notify(); };
            _net.OnSkills += sk => { if (_state == null) return; _state.Skills = sk; _state.Notify(); };
            _net.OnTrial += tr => { if (_state == null) return; _state.Trial = tr; _state.TrialAt = Time.time; _state.Notify(); };
            _net.OnChat += m => { if (_state != null) _state.AddChat(m); };

            _fps = gameObject.AddComponent<FpsOverlay>();
            _fps.ExtraInfo = BuildInfo;
        }

        void Start()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            var pending = RpgGetNickname();
            if (!string.IsNullOrEmpty(pending)) OnHtmlLogin(pending);
            else RpgShowLogin(1);
#else
            _name = PlayerPrefs.GetString("nick", "Tester" + UnityEngine.Random.Range(1, 99));
#endif
        }

        /// <summary>
        /// Called by the HTML overlay (unityInstance.SendMessage) with "닉네임비밀번호만들기(0/1)".
        /// The password only travels to the server; it is never saved on this side.
        /// </summary>
        public void OnHtmlLogin(string payload)
        {
            if (InGame || _connecting) return;
            var parts = (payload ?? "").Split('');
            _name = parts[0];
            _password = parts.Length > 1 ? parts[1] : "";
            _create = parts.Length > 2 && parts[2] == "1";
            Connect();
        }

        string ServerUrl()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            var uri = new Uri(Application.absoluteURL);
            string scheme = uri.Scheme == "https" ? "wss" : "ws";
            return $"{scheme}://{uri.Authority}{ProtocolInfo.WebSocketPath}";
#else
            return EditorServerUrl;
#endif
        }

        void Connect()
        {
            _connecting = true;
            _editorNotice = "";
            _net.Connect(ServerUrl(), _name, _password, _create);
            _password = "";
        }

        void HandleWelcome(Welcome w)
        {
            _connecting = false;
            TearDownGame();

            _data = JsonUtility.FromJson<SharedData>(w.SharedDataJson);
            _data.Index();
            SkillFx.SetElements(_data);
            _state = new ClientState { Data = _data, SelfId = w.YourEntityId };
            var cfg = _data.config;
            _net.SetPingInterval(cfg.net.pingIntervalSec);

            if (_world == null || _worldMapId != _data.map.id)
            {
                if (_world) Destroy(_world);
                _world = VillageBuilder.Build(_data.map, out _worldStats);
                _worldMapId = _data.map.id;
            }

            // Local player
            var view = Placeholders.CreateHero($"LocalPlayer {_name}", otherPlayer: false);
            _player = view.gameObject;
            var wings = Placeholders.AddWings(view);
            var cc = _player.AddComponent<CharacterController>();
            cc.height = 1.8f;
            cc.radius = 0.4f;
            cc.center = new Vector3(0, 0.9f, 0);
            cc.stepOffset = 0.4f;
            cc.slopeLimit = 50f;
            _motor = _player.AddComponent<PlayerMotor>();
            _motor.Init(cfg.movement);
            _motor.AreaLimit = _data.map.startArea;
            _player.AddComponent<GlideWingsView>().Init(_motor, wings);
            _motor.Teleport(new Vector3(w.X, w.Y, w.Z));
            _player.transform.rotation = Quaternion.Euler(0, w.Yaw, 0);

            _localNet = _player.AddComponent<LocalPlayerNet>();
            _localNet.Init(_net, _motor, cfg.net.clientSendRate);

            if (_camRig == null) _camRig = _cam.gameObject.AddComponent<ThirdPersonCamera>();
            _camRig.Target = _player.transform;
            _camRig.SnapBehind();
            _motor.CameraTransform = _cam.transform;

            _remotes = new GameObject("WorldEntities").AddComponent<RemoteEntities>();
            _remotes.Init(_net, _state, cfg.net.interpolationDelaySec);
            _projectiles = new GameObject("Projectiles").AddComponent<ProjectileManager>();
            _projectiles.Init(_net);
            _motor.SlowMultiplier = cfg.combat.slowMultiplier;
            _remotes.RegisterSelf(_player, view, _name);

            _ui = new GameObject("GameUIHost").AddComponent<GameUI>();
            _ui.Init(_net, _state, _remotes, _motor, _cam);
            _ui.GenderPreview += OnGenderPreview;
            _camRig.LockBlocked = () => _ui && _ui.Blocking;

            var combat = _player.AddComponent<PlayerCombat>();
            combat.Init(_net, _state, _motor, view, _cam.transform);
            combat.CanAttack = () => _ui && !_ui.Blocking;
            combat.Rig = _camRig;
            _ui.BindCombat(combat);

            combat.Notice = t => { if (_ui) _ui.ShowCenter(t, true); };

            // 마나 / 비행 (the server keeps both; these only feed the bars and the flying pose)
            _net.OnMana += m => { if (_state == null) return; _state.Mana = m.Mana; _state.MaxMana = m.MaxMana; _state.Infused = m.Infused; _state.Notify(); };
            _net.OnFlight += f =>
            {
                if (_state == null) return;
                if (f.EntityId == _state.SelfId)
                {
                    _state.Flying = f.Active;
                    _state.FlyReadyAt = Time.time + f.CooldownLeft;
                    _state.FlyEndAt = f.Active ? Time.time + f.Duration : 0f;
                    var job = _data.Job(_state.Char != null ? _state.Char.Job : "");
                    _motor.SetFlight(f.Active, job != null ? job.flight : null);
                    if (f.Fell && _ui) _ui.ShowCenter("격추당해 떨어진다!", true);
                    _state.Notify();
                }
            };

            _localNet.Corrected += r => { if (r == CorrectionReason.Respawn && _ui) _ui.OnRespawned(); };

#if UNITY_WEBGL && !UNITY_EDITOR
            RpgShowLogin(0);
            RpgShowMessage("접속 완료 · 화면을 클릭하면 마우스가 잠겨", 0);
#endif
            Debug.Log($"[game] welcome #{w.YourEntityId}, map {_data.map.id} chunks={_worldStats.Chunks} trees={_worldStats.Trees} buildings={_worldStats.Buildings}");
        }

        /// <summary>Called by the page when the chat input sends: "채널|내용" (채널 0 일반, 1 월드).</summary>
        public void OnHtmlChat(string payload)
        {
            if (!_ui || string.IsNullOrEmpty(payload)) return;
            int bar = payload.IndexOf('|');
            if (bar < 0) { _ui.OnChatClosed(); return; }
            int.TryParse(payload.Substring(0, bar), out int ch);
            _ui.SubmitChat(ch, payload.Substring(bar + 1));
        }

        /// <summary>Called by the page when the chat input closed without sending.</summary>
        public void OnHtmlChatClosed() { if (_ui) _ui.OnChatClosed(); }

        /// <summary>Saved look from the server: show it on your own character (unset = leave the preview alone).</summary>
        void ApplyLook(Gender g)
        {
            if (g == Gender.Unset || !_player) return;
            var v = _player.GetComponent<CreatureView>();
            if (v) Placeholders.SetHeroGender(v, g, false);
        }

        /// <summary>The look picker highlighted a gender (Unset = picker closed): show it on your character and aim the camera at it.</summary>
        void OnGenderPreview(Gender g)
        {
            if (!_player) return;
            var v = _player.GetComponent<CreatureView>();
            if (g != Gender.Unset && v) Placeholders.SetHeroGender(v, g, false);
            if (_camRig)
            {
                _camRig.FrontPreview = g != Gender.Unset;
                if (g == Gender.Unset) _camRig.SnapBehind();
            }
        }

        /// <summary>Lift the start-area clamp once the first quest unlocks it (the server is the real gate).</summary>
        void ApplyUnlocks()
        {
            if (_motor == null || _state == null) return;
            _motor.AreaLimit = _state.Unlocked(UnlockFlags.LeaveStartArea) ? null : _data.map.startArea;
        }

        void SelfHp(uint id, int hp, int maxHp)
        {
            if (_state == null || _state.Char == null || id != _state.SelfId) return;
            _state.Char.Hp = hp;
            _state.Char.MaxHp = maxHp;
            _state.Notify();
        }

        void HandleNotice(Notice n)
        {
            Debug.Log($"[notice] {n.Kind}: {n.Text}");
            if (n.Kind != NoticeKind.Fatal && _ui)
            {
                _ui.ShowCenter(n.Text, n.Kind == NoticeKind.Error);
                return;
            }
#if UNITY_WEBGL && !UNITY_EDITOR
            RpgShowMessage(n.Text, (int)n.Kind);
#else
            _editorNotice = n.Text;
#endif
            if (n.Kind == NoticeKind.Fatal) BackToLogin();
        }

        void HandleDisconnected()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            RpgShowMessage("서버와 연결이 끊겼어. 다시 접속해 줘.", 1);
#else
            _editorNotice = "서버와 연결이 끊겼어.";
#endif
            BackToLogin();
        }

        void TearDownGame()
        {
            if (_ui) Destroy(_ui.gameObject);
            if (_player) Destroy(_player);
            if (_remotes) { _remotes.Clear(); Destroy(_remotes.gameObject); }
            if (_projectiles) Destroy(_projectiles.gameObject);
            _projectiles = null;
            _ui = null; _player = null; _motor = null; _remotes = null; _localNet = null;
            _state = null;
        }

        void BackToLogin()
        {
            _net.Disconnect(silent: true);
            TearDownGame();
            _connecting = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
#if UNITY_WEBGL && !UNITY_EDITOR
            RpgShowLogin(1);
#endif
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb.f10Key.wasPressedThisFrame) _quality.Cycle();
            if (kb.f3Key.wasPressedThisFrame) _fps.Visible = !_fps.Visible;
        }

        string BuildInfo()
        {
            string s = $"quality {_quality.Tier} (scale {_quality.CurrentRenderScale:0.00})  [F10 change, F3 hide]";
            if (InGame && _motor != null)
            {
                s += $"\nping {_net.RttMs:F0} ms  in {_net.MessagesPerSecond} msg/s {_net.BytesPerSecond / 1024f:F1} KB/s  entities {(_remotes ? _remotes.Count : 0)}";
                s += $"\nspeed {_motor.HorizontalSpeed:F1} m/s  jumps {_motor.JumpCount}  {(_motor.Gliding ? "GLIDE " : "")}{(_motor.Dashing ? "DASH " : "")}dash cd {_motor.DashCooldownLeft:F1}s";
                s += $"\nserver corrections {_localNet.Corrections} (last {_localNet.LastCorrection})";
                s += $"\nworld: {_worldStats.Chunks} chunks, {_worldStats.Trees} trees, {_worldStats.Buildings} buildings, {_worldStats.Triangles / 1000}k terrain tris";
                var p = _player.transform.position;
                s += $"\npos {p.x:F1}, {p.y:F1}, {p.z:F1}";
            }
            else if (_connecting) s += "\nconnecting...";
            return s;
        }

#if UNITY_EDITOR || !UNITY_WEBGL
        string _editorPassword = "";

        void OnGUI()
        {
            if (InGame) return;
            var r = new Rect(Screen.width / 2 - 170, Screen.height / 2 - 110, 340, 230);
            GUI.Box(r, "Login (editor)");
            GUI.Label(new Rect(r.x + 15, r.y + 30, 80, 22), "Name");
            _name = GUI.TextField(new Rect(r.x + 95, r.y + 30, 225, 22), _name, 12);
            GUI.Label(new Rect(r.x + 15, r.y + 60, 80, 22), "Server");
            EditorServerUrl = GUI.TextField(new Rect(r.x + 95, r.y + 60, 225, 22), EditorServerUrl);
            GUI.Label(new Rect(r.x + 15, r.y + 90, 80, 22), "Password");
            _editorPassword = GUI.PasswordField(new Rect(r.x + 95, r.y + 90, 225, 22), _editorPassword, '*', 32);
            _create = GUI.Toggle(new Rect(r.x + 95, r.y + 118, 225, 22), _create, "새 계정 만들기");
            if (!_connecting && GUI.Button(new Rect(r.x + 95, r.y + 145, 225, 30), "Connect") && _name.Trim().Length >= 2)
            {
                PlayerPrefs.SetString("nick", _name.Trim());
                _name = _name.Trim();
                _password = _editorPassword;
                Connect();
            }
            if (!string.IsNullOrEmpty(_editorNotice))
                GUI.Label(new Rect(r.x + 15, r.y + 182, 310, 44), _editorNotice);
        }
#endif
    }

    /// <summary>Shows the placeholder glide wings on the local player while gliding.</summary>
    public sealed class GlideWingsView : MonoBehaviour
    {
        PlayerMotor _motor;
        GameObject _wings;
        public void Init(PlayerMotor m, GameObject wings) { _motor = m; _wings = wings; }
        void LateUpdate() { if (_wings) _wings.SetActive(_motor != null && _motor.Gliding); }
    }
}
