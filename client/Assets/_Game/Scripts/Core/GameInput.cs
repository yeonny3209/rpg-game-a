using Game.Shared.World;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Game.Client.Core
{
    /// <summary>
    /// Settings shared by input, camera and UI: which camera is used (spec vol.5 12.1: 3인칭 뒷시점 / 쿼터뷰) and where the player aims.
    /// The server decides hits from the yaw we send, so both views behave the same there.
    /// </summary>
    public static class GameInput
    {
        const string QuarterPref = "camera_quarter";

        static bool _loaded;
        static bool _quarter;

        /// <summary>true = quarter view (cursor free, aim with the mouse cursor), false = over-the-shoulder (cursor locked, aim with the crosshair).</summary>
        public static bool Quarter
        {
            get { Load(); return _quarter; }
            set
            {
                Load();
                if (_quarter == value) return;
                _quarter = value;
                PlayerPrefs.SetInt(QuarterPref, value ? 1 : 0);
                PlayerPrefs.Save();
            }
        }

        static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            _quarter = PlayerPrefs.GetInt(QuarterPref, 0) == 1;
        }

        /// <summary>Yaw (0 = +Z) the player is aiming at right now.</summary>
        public static float AimYaw(Transform player, Camera cam)
        {
            if (!cam) return player.eulerAngles.y;
            if (!Quarter)
            {
                var f = cam.transform.forward;
                return Yaw.FromDirection(f.x, f.z);
            }
            var mouse = Mouse.current;
            if (mouse == null) return player.eulerAngles.y;
            var ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            float planeY = player.position.y + 0.9f;
            if (Mathf.Abs(ray.direction.y) < 0.001f) return player.eulerAngles.y;
            float t = (planeY - ray.origin.y) / ray.direction.y;
            if (t < 0) return player.eulerAngles.y;
            var hit = ray.origin + ray.direction * t;
            float dx = hit.x - player.position.x, dz = hit.z - player.position.z;
            if (dx * dx + dz * dz < 0.01f) return player.eulerAngles.y;
            return Yaw.FromDirection(dx, dz);
        }

        /// <summary>True when the pointer can be used for the game (not over a window; in 3rd person: the mouse is locked).</summary>
        public static bool PointerReady()
        {
            if (!Quarter) return Cursor.lockState == CursorLockMode.Locked;
            return EventSystem.current == null || !EventSystem.current.IsPointerOverGameObject();
        }
    }

    public enum BindAction
    {
        Skill1, Skill2, Skill3, Skill4, Skill5, Skill6,
        Interact, Inventory, Character, Skills, Settings, Fly, HpPotion, MpPotion,
    }

    /// <summary>Rebindable keys (spec vol.5 12.2: "설정에서 변경 가능"). Saved in PlayerPrefs. Movement, Space, Shift, Enter and Esc stay fixed.</summary>
    public static class KeyBindings
    {
        static readonly Key[] Defaults =
        {
            Key.Q, Key.E, Key.T, Key.G, Key.Z, Key.X,   // 2권 6.3: 일반 스킬 QETGZX
            Key.V, Key.I, Key.P, Key.K, Key.O, Key.C, Key.R, Key.F,
        };

        static readonly string[] Labels =
        {
            "스킬 1", "스킬 2", "스킬 3", "스킬 4", "스킬 5", "스킬 6",
            "상호작용", "가방", "캐릭터", "스킬 창", "설정", "비행", "HP 포션", "마나 포션",
        };

        static readonly Key[] Reserved =
        {
            Key.W, Key.A, Key.S, Key.D, Key.Space, Key.LeftShift, Key.RightShift, Key.Escape, Key.Enter, Key.NumpadEnter,
            Key.Tab, Key.Slash, Key.F3, Key.F10,
        };

        static Key[] _keys;

        static void Load()
        {
            if (_keys != null) return;
            _keys = new Key[Defaults.Length];
            for (int i = 0; i < _keys.Length; i++)
            {
                int v = PlayerPrefs.GetInt("bind_" + i, (int)Defaults[i]);
                _keys[i] = System.Enum.IsDefined(typeof(Key), v) ? (Key)v : Defaults[i];
            }
        }

        public static int Count => Defaults.Length;
        public static Key Get(BindAction a) { Load(); return _keys[(int)a]; }
        public static Key Skill(int slot) { return Get((BindAction)slot); }
        public static string Label(BindAction a) { return Labels[(int)a]; }

        public static bool IsAllowed(Key k)
        {
            if (k == Key.None) return false;
            foreach (var r in Reserved) if (r == k) return false;
            return true;
        }

        /// <summary>Binds the key. If another action had it, the two swap so no key ends up doing two things.</summary>
        public static void Set(BindAction a, Key k)
        {
            Load();
            if (!IsAllowed(k)) return;
            for (int i = 0; i < _keys.Length; i++)
                if (i != (int)a && _keys[i] == k) { _keys[i] = _keys[(int)a]; PlayerPrefs.SetInt("bind_" + i, (int)_keys[i]); }
            _keys[(int)a] = k;
            PlayerPrefs.SetInt("bind_" + (int)a, (int)k);
            PlayerPrefs.Save();
        }

        public static void ResetAll()
        {
            Load();
            for (int i = 0; i < _keys.Length; i++) { _keys[i] = Defaults[i]; PlayerPrefs.DeleteKey("bind_" + i); }
            PlayerPrefs.Save();
        }

        /// <summary>Short text for a key as shown on buttons.</summary>
        public static string KeyName(Key k)
        {
            switch (k)
            {
                case Key.LeftShift: case Key.RightShift: return "Shift";
                case Key.Digit0: return "0"; case Key.Digit1: return "1"; case Key.Digit2: return "2"; case Key.Digit3: return "3";
                case Key.Digit4: return "4"; case Key.Digit5: return "5"; case Key.Digit6: return "6"; case Key.Digit7: return "7";
                case Key.Digit8: return "8"; case Key.Digit9: return "9";
                case Key.Semicolon: return ";"; case Key.Comma: return ","; case Key.Period: return ".";
                case Key.Quote: return "'"; case Key.Backquote: return "`"; case Key.Minus: return "-"; case Key.Equals: return "=";
                case Key.LeftBracket: return "["; case Key.RightBracket: return "]"; case Key.Backslash: return "\\";
                default: return k.ToString();
            }
        }
    }
}
