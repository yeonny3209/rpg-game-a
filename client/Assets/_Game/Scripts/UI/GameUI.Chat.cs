using System.Runtime.InteropServices;
using System.Text;
using Game.Client.Core;
using Game.Shared.Protocol;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI
{
    /// <summary>
    /// Typing goes through an HTML input on the page (see the WebGL template): Unity WebGL cannot take Korean (IME) text itself.
    /// Enter opens it, Enter sends, Esc cancels, Tab switches 일반 ↔ 월드. "/" opens it with a slash for dev commands.
    /// </summary>
    public static class WebBridge
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void RpgChatOpen(int channel, string prefill);
        [DllImport("__Internal")] static extern void RpgChatClose();
        public static bool ChatAvailable => true;
        public static void ChatOpen(int channel, string prefill) { RpgChatOpen(channel, prefill ?? ""); }
        public static void ChatClose() { RpgChatClose(); }
#else
        public static bool ChatAvailable => false;
        public static void ChatOpen(int channel, string prefill) { Debug.Log("[chat] 채팅 입력창은 WebGL 빌드에서 열려요."); }
        public static void ChatClose() { }
#endif
    }

    public sealed partial class GameUI
    {
        Text _chatLog, _chatHint;
        int _chatChannel;
        /// <summary>True while the HTML input is open: the game ignores movement keys and clicks.</summary>
        public bool ChatOpen { get; private set; }

        void BuildChat()
        {
            _chatLog = UiKit.Label("ChatLog", _root, 15, TextAnchor.LowerLeft, UiKit.TextMain, true);
            _chatLog.rectTransform.Place(new Vector2(0, 0), new Vector2(0, 0), new Vector2(16, 128), new Vector2(460, 200));
            _chatLog.verticalOverflow = VerticalWrapMode.Truncate;
            _chatHint = UiKit.Label("ChatHint", _root, 12, TextAnchor.LowerLeft, UiKit.TextDim, true);
            _chatHint.rectTransform.Place(new Vector2(0, 0), new Vector2(0, 0), new Vector2(18, 124), new Vector2(460, 18));
            _chatHint.text = "Enter: 채팅 · /: 명령";
        }

        void OpenChat(string prefill)
        {
            if (ChatOpen || !WebBridge.ChatAvailable) return;
            ChatOpen = true;
            FreeMouse();
            WebBridge.ChatOpen(_chatChannel, prefill);
        }

        /// <summary>Called by the page when the input closed without sending (Esc / focus lost).</summary>
        public void OnChatClosed() { ChatOpen = false; }

        /// <summary>Called by the page with what was typed.</summary>
        public void SubmitChat(int channel, string text)
        {
            ChatOpen = false;
            _chatChannel = Mathf.Clamp(channel, 0, 1);
            if (string.IsNullOrWhiteSpace(text)) return;
            _net.Send(new ChatSend { Channel = (ChatChannel)_chatChannel, Text = text.Trim() });
        }

        static string Plain(string s) { return (s ?? "").Replace("<", "＜").Replace(">", "＞"); }

        void UpdateChatUi()
        {
            if (_chatLog == null) return;
            var sb = new StringBuilder();
            int shown = 0;
            for (int i = _st.Chat.Count - 1; i >= 0 && shown < 9; i--)
            {
                float age = Time.time - _st.ChatTimes[i];
                if (!ChatOpen && age > 25f) break;
                var m = _st.Chat[i];
                string line;
                switch (m.Channel)
                {
                    case ChatChannel.World: line = $"<color=#ffcf6a>[월드] {Plain(m.Sender)}: {Plain(m.Text)}</color>"; break;
                    case ChatChannel.System: line = $"<color=#9fd0ff>{Plain(m.Text)}</color>"; break;
                    default: line = $"<color=#a8d8ff>{Plain(m.Sender)}</color>: {Plain(m.Text)}"; break;
                }
                if (!ChatOpen && age > 18f) line = $"<alpha=#88>{line}";
                sb.Insert(0, line + "\n");
                shown++;
            }
            _chatLog.text = sb.ToString();
            _chatHint.enabled = ChatOpen || _st.Chat.Count == 0 || Time.time - (_st.ChatTimes.Count > 0 ? _st.ChatTimes[_st.ChatTimes.Count - 1] : 0f) > 25f;
        }
    }
}
