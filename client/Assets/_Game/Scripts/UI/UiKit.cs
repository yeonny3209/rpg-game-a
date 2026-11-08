using System;
using Game.Client.World;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI
{
    /// <summary>Tiny helpers to build uGUI from code (no prefabs, no scene YAML).</summary>
    public static class UiKit
    {
        public static readonly Color PanelBg = new Color(0.07f, 0.08f, 0.1f, 0.82f);
        public static readonly Color SlotBg = new Color(0.16f, 0.17f, 0.2f, 0.95f);
        public static readonly Color Accent = new Color(0.95f, 0.78f, 0.35f);
        public static readonly Color TextMain = new Color(0.94f, 0.94f, 0.92f);
        public static readonly Color TextDim = new Color(0.68f, 0.7f, 0.72f);

        public static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        /// <summary>Anchor to a point (0..1) with a pivot and pixel offset/size.</summary>
        public static RectTransform Place(this RectTransform r, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            r.anchorMin = r.anchorMax = anchor;
            r.pivot = pivot;
            r.anchoredPosition = pos;
            r.sizeDelta = size;
            return r;
        }

        public static RectTransform Fill(this RectTransform r, float pad = 0)
        {
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = new Vector2(pad, pad);
            r.offsetMax = new Vector2(-pad, -pad);
            return r;
        }

        public static Image Panel(string name, Transform parent, Color color)
        {
            var r = Rect(name, parent);
            var img = r.gameObject.AddComponent<Image>();
            img.color = color;
            return img;
        }

        public static Text Label(string name, Transform parent, int size, TextAnchor align, Color color, bool outline = false)
        {
            var r = Rect(name, parent);
            var t = r.gameObject.AddComponent<Text>();
            t.font = Placeholders.Font;
            t.fontSize = size;
            t.alignment = align;
            t.color = color;
            t.supportRichText = true;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            if (outline)
            {
                var o = r.gameObject.AddComponent<Outline>();
                o.effectColor = new Color(0, 0, 0, 0.85f);
                o.effectDistance = new Vector2(1.2f, -1.2f);
            }
            return t;
        }

        public static Button Button(string name, Transform parent, string text, int size, Action onClick)
        {
            var img = Panel(name, parent, new Color(0.22f, 0.24f, 0.3f, 0.95f));
            var b = img.gameObject.AddComponent<Button>();
            var colors = b.colors;
            colors.highlightedColor = new Color(1.25f, 1.2f, 1f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f);
            colors.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.6f);
            b.colors = colors;
            b.onClick.AddListener(() => onClick());
            var t = Label("Text", img.transform, size, TextAnchor.MiddleLeft, TextMain);
            t.rectTransform.Fill();
            t.rectTransform.offsetMin = new Vector2(14, 0);
            t.text = text;
            return b;
        }
    }

    /// <summary>Horizontal bar: background + fill scaled by anchorMax.x + centered text.</summary>
    public sealed class Bar
    {
        public RectTransform Root;
        readonly RectTransform _fill;
        readonly Text _text;

        public Bar(string name, Transform parent, Color fill, int fontSize)
        {
            var bg = UiKit.Panel(name, parent, new Color(0, 0, 0, 0.6f));
            Root = bg.rectTransform;
            var f = UiKit.Panel("Fill", Root, fill);
            _fill = f.rectTransform;
            _fill.anchorMin = Vector2.zero;
            _fill.anchorMax = new Vector2(1, 1);
            _fill.offsetMin = new Vector2(1, 1);
            _fill.offsetMax = new Vector2(-1, -1);
            if (fontSize > 0)
            {
                _text = UiKit.Label("Text", Root, fontSize, TextAnchor.MiddleCenter, Color.white, true);
                _text.rectTransform.Fill();
            }
        }

        public void Set(float ratio, string text = null)
        {
            ratio = Mathf.Clamp01(ratio);
            _fill.anchorMax = new Vector2(ratio, 1);
            _fill.gameObject.SetActive(ratio > 0.01f);
            if (_text != null && text != null) _text.text = text;
        }
    }
}
