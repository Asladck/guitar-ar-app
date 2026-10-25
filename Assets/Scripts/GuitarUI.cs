using System.Collections.Generic;
using UnityEngine;

namespace GuitarAR
{
    /// <summary>
    /// The look of the on-screen interface, for IMGUI: dark see-through rounded panels, one warm
    /// accent colour, sizes in points so they match iOS, and the screen's safe area (clear of the
    /// Dynamic Island and the home bar). Also remembers where controls were drawn, so touches on
    /// them don't play or move the guitar.
    /// </summary>
    public static class GuitarUI
    {
        public static readonly Color Panel = new Color(0.07f, 0.07f, 0.09f, 0.62f);
        public static readonly Color PanelStrong = new Color(0.07f, 0.07f, 0.09f, 0.86f);
        public static readonly Color Track = new Color(1f, 1f, 1f, 0.1f);
        public static readonly Color Accent = new Color(1f, 0.71f, 0.25f);
        public static readonly Color OnAccent = new Color(0.12f, 0.08f, 0.02f);
        public static readonly Color Text = new Color(1f, 1f, 1f, 0.95f);
        public static readonly Color TextDim = new Color(1f, 1f, 1f, 0.6f);
        public static readonly Color Good = new Color(0.2f, 0.84f, 0.35f);
        public static readonly Color Bad = new Color(1f, 0.3f, 0.26f);
        public static readonly Color Guide = new Color(0.39f, 0.82f, 1f);

        const int k_DiscSize = 128;

        static readonly Dictionary<int, GUIStyle> s_Rounded = new Dictionary<int, GUIStyle>();
        static readonly Dictionary<long, GUIStyle> s_Outlines = new Dictionary<long, GUIStyle>();
        static readonly Dictionary<int, GUIStyle> s_TextStyles = new Dictionary<int, GUIStyle>();
        static readonly List<Rect> s_Blocking = new List<Rect>();
        static Texture2D s_Disc;
        static Texture2D s_Ring;
        static float s_StylesPt;

        /// <summary>Pixels per point: about 3 on a current iPhone.</summary>
        public static float Pt => Mathf.Max(1f, Mathf.Min(Screen.width, Screen.height) / 400f);

        /// <summary>The part of the screen not covered by the camera cut-out or the home bar, in GUI coordinates.</summary>
        public static Rect SafeArea
        {
            get
            {
                var area = Screen.safeArea;
                return new Rect(area.x, Screen.height - area.yMax, area.width, area.height);
            }
        }

        /// <summary>A filled circle, white; tint it with GUI.color.</summary>
        public static Texture2D Disc
        {
            get
            {
                if (s_Disc == null)
                    s_Disc = MakeDisc(0f);
                return s_Disc;
            }
        }

        // ---- Where controls are, for touch input ----

        /// <summary>Call at the start of each frame's drawing.</summary>
        public static void BeginFrame()
        {
            if (Event.current.type == EventType.Repaint)
                s_Blocking.Clear();
        }

        /// <summary>Touches that start here belong to the interface.</summary>
        public static void Block(Rect rect)
        {
            if (Event.current.type == EventType.Repaint)
                s_Blocking.Add(rect);
        }

        /// <summary>True if a screen position (pixels, origin bottom left, as from touch input) is on a control.</summary>
        public static bool IsOverUI(Vector2 screenPosition)
        {
            var point = new Vector2(screenPosition.x, Screen.height - screenPosition.y);
            foreach (var rect in s_Blocking)
            {
                if (rect.Contains(point))
                    return true;
            }
            return false;
        }

        // ---- Shapes ----

        /// <summary>A rounded rectangle; a radius of half the height makes a pill.</summary>
        public static void Fill(Rect rect, float radius, Color color)
        {
            if (Event.current.type != EventType.Repaint || rect.width <= 0f || rect.height <= 0f)
                return;
            var previous = GUI.color;
            GUI.color = color;
            Rounded(ClampRadius(rect, radius)).Draw(rect, false, false, false, false);
            GUI.color = previous;
        }

        /// <summary>The outline of a rounded rectangle.</summary>
        public static void Outline(Rect rect, float radius, float thickness, Color color)
        {
            if (Event.current.type != EventType.Repaint || rect.width <= 0f || rect.height <= 0f)
                return;
            int r = ClampRadius(rect, radius);
            int t = Mathf.Clamp(Mathf.RoundToInt(thickness), 1, Mathf.Max(1, r));
            long key = ((long)r << 16) | (uint)t;
            if (!s_Outlines.TryGetValue(key, out var style) || style.normal.background == null)
            {
                style = NineSlice(MakeRounded(r, t), r);
                s_Outlines[key] = style;
            }
            var previous = GUI.color;
            GUI.color = color;
            style.Draw(rect, false, false, false, false);
            GUI.color = previous;
        }

        public static void Dot(Vector2 center, float diameter, Color color)
        {
            DrawTinted(new Rect(center.x - diameter * 0.5f, center.y - diameter * 0.5f, diameter, diameter), Disc, color);
        }

        /// <summary>A circle outline, its line a fraction of the diameter thick.</summary>
        public static void Ring(Vector2 center, float diameter, Color color)
        {
            if (s_Ring == null)
                s_Ring = MakeDisc(0.14f);
            DrawTinted(new Rect(center.x - diameter * 0.5f, center.y - diameter * 0.5f, diameter, diameter), s_Ring, color);
        }

        // ---- Text ----

        /// <summary>Text in a rect, sized in points.</summary>
        public static void Label(Rect rect, string text, float sizePt, Color color, bool bold = false, TextAnchor anchor = TextAnchor.MiddleCenter, bool wrap = false)
        {
            var previous = GUI.contentColor;
            GUI.contentColor = color;
            GUI.Label(rect, text, TextStyle(sizePt, bold, anchor, wrap));
            GUI.contentColor = previous;
        }

        public static float TextHeight(string text, float width, float sizePt, bool bold = false)
        {
            return TextStyle(sizePt, bold, TextAnchor.MiddleCenter, true).CalcHeight(new GUIContent(text), width);
        }

        public static float TextWidth(string text, float sizePt, bool bold = false)
        {
            return TextStyle(sizePt, bold, TextAnchor.MiddleCenter, false).CalcSize(new GUIContent(text)).x;
        }

        // ---- Controls ----

        /// <summary>True when an area was tapped. Draws nothing; draw the look first.</summary>
        public static bool Tapped(Rect rect)
        {
            Block(rect);
            return GUI.Button(rect, GUIContent.none, GUIStyle.none);
        }

        /// <summary>A rounded button with a label, filled with the accent colour when selected.</summary>
        public static bool Button(Rect rect, string text, float sizePt, bool selected = false, float radius = -1f)
        {
            Fill(rect, radius < 0f ? rect.height * 0.5f : radius, selected ? Accent : Panel);
            Label(rect, text, sizePt, selected ? OnAccent : Text, true);
            return Tapped(rect);
        }

        /// <summary>
        /// A row of choices on one track, the chosen one as an accent pill. Returns the choice,
        /// changed if one was tapped. Draw each segment's content with the callback (rect, index,
        /// selected), or leave it null for plain labels.
        /// </summary>
        public static int Segmented(Rect rect, int selected, string[] labels, float sizePt, System.Action<Rect, int, bool> content = null)
        {
            Fill(rect, rect.height * 0.5f, Panel);
            float inset = 3f * Pt;
            float width = (rect.width - inset * 2f) / labels.Length;
            int chosen = selected;
            for (int i = 0; i < labels.Length; i++)
            {
                var segment = new Rect(rect.x + inset + i * width, rect.y + inset, width, rect.height - inset * 2f);
                bool on = i == selected;
                if (on)
                    Fill(segment, segment.height * 0.5f, Accent);
                if (content != null)
                    content(segment, i, on);
                else
                    Label(segment, labels[i], sizePt, on ? OnAccent : Text, on);
                if (Tapped(segment))
                    chosen = i;
            }
            Block(rect);
            return chosen;
        }

        /// <summary>An on/off switch like iOS's. Returns the new state.</summary>
        public static bool Switch(Rect rect, bool on)
        {
            Fill(rect, rect.height * 0.5f, on ? Accent : Track);
            float knob = rect.height - 4f * Pt;
            float x = on ? rect.xMax - knob - 2f * Pt : rect.x + 2f * Pt;
            Dot(new Vector2(x + knob * 0.5f, rect.center.y), knob, Color.white);
            return Tapped(rect) ? !on : on;
        }

        // ---- Making the textures ----

        static int ClampRadius(Rect rect, float radius)
        {
            int limit = Mathf.FloorToInt(Mathf.Min(rect.width, rect.height) * 0.5f) - 2;
            return Mathf.Clamp(Mathf.RoundToInt(radius), 0, Mathf.Max(0, limit));
        }

        static GUIStyle Rounded(int radius)
        {
            if (!s_Rounded.TryGetValue(radius, out var style) || style.normal.background == null)
            {
                style = NineSlice(MakeRounded(radius, 0), radius);
                s_Rounded[radius] = style;
            }
            return style;
        }

        static GUIStyle NineSlice(Texture2D texture, int radius)
        {
            int border = radius + 2;
            return new GUIStyle { normal = { background = texture }, border = new RectOffset(border, border, border, border) };
        }

        /// <summary>
        /// A white rounded square, corners of the given radius around a small stretchable middle;
        /// filled, or an outline that many pixels thick.
        /// </summary>
        static Texture2D MakeRounded(int radius, int thickness)
        {
            int size = radius * 2 + 6;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Distance outside the rounded shape, through the corner circles.
                    float px = x + 0.5f, py = y + 0.5f;
                    float cx = Mathf.Clamp(px, radius, size - radius);
                    float cy = Mathf.Clamp(py, radius, size - radius);
                    float inside = radius - Vector2.Distance(new Vector2(px, py), new Vector2(cx, cy));
                    if (radius == 0)
                        inside = Mathf.Min(Mathf.Min(px, size - px), Mathf.Min(py, size - py));
                    float alpha = Mathf.Clamp01(inside + 0.5f);
                    if (thickness > 0)
                        alpha = Mathf.Min(alpha, Mathf.Clamp01(thickness - inside + 0.5f));
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        /// <summary>A white circle, or a ring whose line is that fraction of the diameter.</summary>
        static Texture2D MakeDisc(float ringFraction)
        {
            var texture = new Texture2D(k_DiscSize, k_DiscSize, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[k_DiscSize * k_DiscSize];
            float radius = k_DiscSize * 0.5f - 1f;
            float thickness = ringFraction * k_DiscSize;
            for (int y = 0; y < k_DiscSize; y++)
            {
                for (int x = 0; x < k_DiscSize; x++)
                {
                    float inside = radius - Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(k_DiscSize * 0.5f, k_DiscSize * 0.5f));
                    float alpha = Mathf.Clamp01(inside + 0.5f);
                    if (thickness > 0f)
                        alpha = Mathf.Min(alpha, Mathf.Clamp01(thickness - inside + 0.5f));
                    pixels[y * k_DiscSize + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        static void DrawTinted(Rect rect, Texture texture, Color color)
        {
            if (Event.current.type != EventType.Repaint)
                return;
            var previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, texture);
            GUI.color = previous;
        }

        static GUIStyle TextStyle(float sizePt, bool bold, TextAnchor anchor, bool wrap)
        {
            float pt = Pt;
            if (pt != s_StylesPt)
            {
                s_TextStyles.Clear();
                s_StylesPt = pt;
            }
            int size = Mathf.Max(8, Mathf.RoundToInt(sizePt * pt));
            int key = ((size * 2 + (bold ? 1 : 0)) * 16 + (int)anchor) * 2 + (wrap ? 1 : 0);
            if (!s_TextStyles.TryGetValue(key, out var style))
            {
                style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = size,
                    fontStyle = bold ? FontStyle.Bold : FontStyle.Normal,
                    alignment = anchor,
                    wordWrap = wrap,
                    clipping = TextClipping.Overflow,
                    padding = new RectOffset(0, 0, 0, 0),
                    margin = new RectOffset(0, 0, 0, 0),
                    normal = { textColor = Color.white },
                };
                s_TextStyles[key] = style;
            }
            return style;
        }
    }
}
