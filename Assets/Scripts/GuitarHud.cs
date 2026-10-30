using System.Collections.Generic;
using UnityEngine;

namespace GuitarAR
{
    /// <summary>
    /// The on-screen interface. At the top: the chord being played (it flashes on each strum),
    /// whether the right hand can play, the strumming pattern with its next strike and the beat,
    /// hints, and the settings. At the bottom: the chord buttons and the mode switch. Over the
    /// camera image: a ring on the playing fingertip, and on the neck where the chord's fingers go.
    /// Everything stays inside the safe area, clear of the Dynamic Island and the home bar.
    /// </summary>
    public class GuitarHud : MonoBehaviour
    {
        // Sizes are in points (GuitarUI.Pt pixels each).
        const float k_Margin = 12f;
        const float k_Gap = 8f;
        const float k_MaxWidth = 560f;
        const float k_RowHeight = 44f;
        const float k_ModeBarHeight = 54f;
        const float k_PulseSeconds = 0.25f;
        const float k_HintFadeSeconds = 0.25f;
        const string k_DiagnosticsKey = "GuitarAR.Diagnostics";

        // In GuitarMode order.
        static readonly string[] k_ModeLabels = { "На человеке", "Селфи", "Поверхность" };
        // In GuitarChordReader.Mode order.
        static readonly string[] k_ChordModeLabels = { "Гриф", "Жесты", "Кнопки" };
        static readonly string[] k_ChordSources = { "по грифу", "жестами", "кнопками" };
        static readonly string[] k_ChordModeHelp =
        {
            "Поставь пальцы левой руки на гриф, как в аккорде; голубые точки подскажут куда",
            "Подними пальцы левой руки: сколько пальцев, такой аккорд (подсказки на кнопках)",
            "Аккорд выбирается кнопками внизу",
        };
        static readonly float[] k_SliderKnobs = { 0.3f, 0.68f, 0.45f };

        GuitarModeSwitcher m_Switcher;
        GuitarHandPlayer m_Hands;
        bool m_SettingsOpen;
        bool m_Diagnostics;
        string[] m_PatternLabels;
        string[] m_ChordLabels;
        readonly List<string> m_Groups = new List<string>();
        string m_Hint;
        float m_HintAlpha;
        System.Action<Rect, int, bool> m_DrawModeSegment;

        void Awake()
        {
            m_Hands = GetComponent<GuitarHandPlayer>();
            m_DrawModeSegment = DrawModeSegment;
            try
            {
                m_Diagnostics = PlayerPrefs.GetInt(k_DiagnosticsKey, 0) != 0;
            }
            catch (System.Exception)
            {
                m_Diagnostics = false;
            }
        }

        void OnGUI()
        {
            if (m_Switcher == null)
                m_Switcher = Object.FindAnyObjectByType<GuitarModeSwitcher>();
            if (m_Hands == null)
                m_Hands = GetComponent<GuitarHandPlayer>();
            if (m_PatternLabels == null)
            {
                m_PatternLabels = new string[GuitarHandPlayer.PatternCount];
                for (int i = 0; i < m_PatternLabels.Length; i++)
                    m_PatternLabels[i] = GuitarHandPlayer.PatternName(i);
                m_ChordLabels = new string[GuitarStrummer.ChordCount];
                for (int i = 0; i < m_ChordLabels.Length; i++)
                    m_ChordLabels[i] = GuitarStrummer.ChordName(i);
            }

            GuitarUI.BeginFrame();
            float pt = GuitarUI.Pt;
            var safe = GuitarUI.SafeArea;
            float width = Mathf.Min(safe.width - 2f * k_Margin * pt, k_MaxWidth * pt);
            float left = safe.center.x - width * 0.5f;
            bool playable = AnyPlayable();

            // Under the controls: marks on the camera image.
            if (playable && m_Hands != null)
            {
                m_Hands.ChordReader.DrawGuide();
                DrawFingertip(pt);
            }

            // Bottom, from the home bar up: mode switch, then chords.
            var modes = new Rect(left, safe.yMax - k_Gap * pt - k_ModeBarHeight * pt, width, k_ModeBarHeight * pt);
            DrawModes(modes);
            if (playable)
                DrawChords(new Rect(left, modes.y - k_Gap * pt, width, 0f), pt);

            // Top, from the camera cut-out down.
            var row = new Rect(left, safe.y + 4f * pt, width, k_RowHeight * pt);
            float bottom = DrawTopRow(row, playable, pt);
            if (m_SettingsOpen)
                bottom = DrawSettings(new Rect(left, bottom + k_Gap * pt, width, 0f), pt, safe);
            else if (playable && m_Hands != null)
                bottom = DrawRhythm(new Rect(left, bottom + k_Gap * pt, width, 84f * pt), pt);
            bottom = DrawHint(new Rect(left, bottom + k_Gap * pt, width, 0f), pt);
            if (m_Diagnostics)
                DrawDiagnostics(new Rect(left, bottom + k_Gap * pt, width, 0f), pt);
        }

        // ---- Top ----

        /// <summary>Chord badge, hand status and the settings button. Returns the row's bottom.</summary>
        float DrawTopRow(Rect row, bool playable, float pt)
        {
            var settings = new Rect(row.xMax - row.height, row.y, row.height, row.height);
            GuitarUI.Fill(settings, settings.height * 0.5f, m_SettingsOpen ? GuitarUI.Accent : GuitarUI.Panel);
            float icon = 20f * pt;
            DrawSettingsIcon(new Rect(settings.center.x - icon * 0.5f, settings.center.y - icon * 0.5f, icon, icon), m_SettingsOpen ? GuitarUI.OnAccent : GuitarUI.Text);
            if (GuitarUI.Tapped(settings))
                m_SettingsOpen = !m_SettingsOpen;

            if (!playable)
                return row.yMax;

            // The chord, lit up for a moment by each strum.
            var badge = new Rect(row.x, row.y, 76f * pt, row.height);
            float pulse = 1f - Mathf.Clamp01((Time.time - GuitarStrummer.LastPlayTime) / k_PulseSeconds);
            GuitarUI.Fill(badge, 14f * pt, Color.Lerp(GuitarUI.Panel, GuitarUI.Accent, pulse));
            bool lit = pulse > 0.5f; // Switch the text colour at once, so it never fades into the background.
            var ink = lit ? GuitarUI.OnAccent : GuitarUI.Text;
            GuitarUI.Label(new Rect(badge.x, badge.y + 3f * pt, badge.width, 26f * pt), GuitarStrummer.ChordName(GuitarStrummer.CurrentChord), 21f, ink, true);
            int source = m_Hands != null ? (int)m_Hands.ChordMode : (int)GuitarChordReader.Mode.Off;
            GuitarUI.Label(new Rect(badge.x, badge.y + 27f * pt, badge.width, 13f * pt), k_ChordSources[source], 9.5f, lit ? GuitarUI.OnAccent : GuitarUI.TextDim);
            GuitarUI.Block(badge);

            // How the right hand is doing.
            var status = new Rect(badge.xMax + k_Gap * pt, row.y, settings.x - k_Gap * pt - badge.xMax - k_Gap * pt, row.height);
            if (status.width > 40f * pt)
            {
                var state = m_Hands != null ? m_Hands.RightHand : GuitarHandPlayer.HandState.NotSeen;
                Color dot;
                string text;
                switch (state)
                {
                    case GuitarHandPlayer.HandState.Ready:
                        dot = GuitarUI.Good;
                        text = "Можно играть";
                        break;
                    case GuitarHandPlayer.HandState.FingerDown:
                        dot = GuitarUI.Bad;
                        text = "Палец опущен: пауза";
                        break;
                    default:
                        dot = GuitarUI.TextDim;
                        text = "Покажи правую руку";
                        break;
                }
                GuitarUI.Fill(status, status.height * 0.5f, GuitarUI.Panel);
                GuitarUI.Dot(new Vector2(status.x + 20f * pt, status.center.y), 10f * pt, dot);
                GuitarUI.Label(new Rect(status.x + 33f * pt, status.y, status.width - 40f * pt, status.height), text, 14f, GuitarUI.Text, true, TextAnchor.MiddleLeft);
                GuitarUI.Block(status);
            }
            return row.yMax;
        }

        /// <summary>The strumming pattern: its choice, its strikes with the next one lit, and the beat.</summary>
        float DrawRhythm(Rect card, float pt)
        {
            GuitarUI.Fill(card, 18f * pt, GuitarUI.Panel);
            GuitarUI.Block(card);
            float pad = 8f * pt;

            var choice = new Rect(card.x + pad, card.y + pad, card.width - pad * 2f, 32f * pt);
            int pattern = GuitarUI.Segmented(choice, m_Hands.CurrentPattern, m_PatternLabels, 13f);
            if (pattern != m_Hands.CurrentPattern)
                m_Hands.CurrentPattern = pattern;

            var steps = new Rect(card.x + pad, choice.yMax + 6f * pt, card.width - pad * 2f, card.yMax - pad - choice.yMax - 6f * pt);
            int next = m_Hands.PatternGroups(m_Groups);
            if (m_Groups.Count == 0)
            {
                GuitarUI.Label(steps, "Свободный бой: бей вниз и вверх как хочешь", 13f, GuitarUI.TextDim);
                return card.yMax;
            }

            // The beat light and tempo on the right, once the tempo is known.
            float beatWidth = 0f;
            if (m_Hands.TryGetBeat(out int bpm, out bool onBeat))
            {
                beatWidth = 78f * pt;
                var beat = new Rect(steps.xMax - beatWidth, steps.y, beatWidth, steps.height);
                GuitarUI.Dot(new Vector2(beat.x + 8f * pt, beat.center.y), 11f * pt, onBeat ? GuitarUI.Accent : new Color(1f, 1f, 1f, 0.2f));
                GuitarUI.Label(new Rect(beat.x + 18f * pt, beat.y, beat.width - 18f * pt, beat.height), $"{bpm} уд/мин", 11f, GuitarUI.TextDim, false, TextAnchor.MiddleLeft);
            }

            // One chip per strike, centred in the rest of the row.
            float chipGap = 6f * pt;
            float total = -chipGap;
            foreach (var group in m_Groups)
                total += GuitarUI.TextWidth(group, 17f, true) + 18f * pt + chipGap;
            var area = new Rect(steps.x, steps.y, steps.width - beatWidth, steps.height);
            float x = area.center.x - total * 0.5f;
            for (int i = 0; i < m_Groups.Count; i++)
            {
                float w = GuitarUI.TextWidth(m_Groups[i], 17f, true) + 18f * pt;
                var chip = new Rect(x, area.y + 1f * pt, w, area.height - 2f * pt);
                bool lit = i == next;
                GuitarUI.Fill(chip, chip.height * 0.5f, lit ? GuitarUI.Accent : GuitarUI.Track);
                GuitarUI.Label(chip, m_Groups[i], 17f, lit ? GuitarUI.OnAccent : GuitarUI.Text, true);
                x += w + chipGap;
            }
            return card.yMax;
        }

        /// <summary>
        /// The settings sheet: strum help, the speed of a pattern's pairs, how the chord is picked,
        /// diagnostics. One column, or two side by side when one would run off the bottom of the
        /// screen (the phone held sideways). Returns its bottom.
        /// </summary>
        float DrawSettings(Rect area, float pt, Rect safe)
        {
            float pad = 14f * pt;
            int chordMode = m_Hands != null ? (int)m_Hands.ChordMode : (int)GuitarChordReader.Mode.Off;
            float oneInner = area.width - pad * 2f;
            float pairs = AssistHeight(pt) + 1f + PairsHeight(pt);
            float oneHeight = pad + pairs + 1f + ChordHeight(chordMode, oneInner, pt) + 1f + DiagnosticsHeight(pt) + pad * 0.5f;
            bool twoColumns = area.y + oneHeight > safe.yMax - k_Gap * pt && area.width >= 480f * pt;

            if (!twoColumns)
            {
                var sheet = new Rect(area.x, area.y, area.width, oneHeight);
                GuitarUI.Fill(sheet, 20f * pt, GuitarUI.PanelStrong);
                GuitarUI.Block(sheet);
                float x = sheet.x + pad, y = sheet.y + pad;
                y = DrawAssist(x, y, oneInner, pt);
                y = Separator(x, y, oneInner);
                y = DrawPairs(x, y, oneInner, pt);
                y = Separator(x, y, oneInner);
                y = DrawChordMode(x, y, oneInner, chordMode, pt);
                y = Separator(x, y, oneInner);
                DrawDiagnosticsSwitch(x, y, oneInner, pt);
                return sheet.yMax;
            }

            float inner = (area.width - pad * 3f) * 0.5f;
            float right = pad + ChordHeight(chordMode, inner, pt) + 1f + DiagnosticsHeight(pt);
            var wide = new Rect(area.x, area.y, area.width, Mathf.Max(pad + pairs, right) + pad * 0.5f);
            GuitarUI.Fill(wide, 20f * pt, GuitarUI.PanelStrong);
            GuitarUI.Block(wide);
            float leftX = wide.x + pad, rightX = leftX + inner + pad, top = wide.y + pad;
            float ly = DrawAssist(leftX, top, inner, pt);
            ly = Separator(leftX, ly, inner);
            DrawPairs(leftX, ly, inner, pt);
            float ry = DrawChordMode(rightX, top, inner, chordMode, pt);
            ry = Separator(rightX, ry, inner);
            DrawDiagnosticsSwitch(rightX, ry, inner, pt);
            return wide.yMax;
        }

        static float AssistHeight(float pt) => 52f * pt;
        static float PairsHeight(float pt) => 12f * pt + 24f * pt + 34f * pt + 44f * pt;
        static float ChordHeight(int chordMode, float inner, float pt) => 12f * pt + 22f * pt + 34f * pt + 6f * pt + GuitarUI.TextHeight(k_ChordModeHelp[chordMode], inner, 11f) + 12f * pt;
        static float DiagnosticsHeight(float pt) => 48f * pt;

        static Vector2 SwitchSize(float pt) => new Vector2(50f * pt, 30f * pt);

        float DrawAssist(float x, float y, float inner, float pt)
        {
            var size = SwitchSize(pt);
            GuitarUI.Label(new Rect(x, y + 4f * pt, inner - size.x, 22f * pt), "Помощь боя", 15f, GuitarUI.Text, true, TextAnchor.MiddleLeft);
            GuitarUI.Label(new Rect(x, y + 26f * pt, inner - size.x - 8f * pt, 18f * pt), "Быстрый взмах = целый удар", 11f, GuitarUI.TextDim, false, TextAnchor.MiddleLeft);
            bool assist = GuitarUI.Switch(new Rect(x + inner - size.x, y + (52f * pt - size.y) * 0.5f, size.x, size.y), GuitarHandPlayer.StrumAssist);
            if (assist != GuitarHandPlayer.StrumAssist)
                GuitarHandPlayer.StrumAssist = assist;
            return y + AssistHeight(pt);
        }

        /// <summary>How quickly the strokes of a pattern's pair follow each other, and whether they follow the player's tempo.</summary>
        float DrawPairs(float x, float y, float inner, float pt)
        {
            y += 12f * pt;
            float step = GuitarHandPlayer.PairStep;
            GuitarUI.Label(new Rect(x, y, inner, 24f * pt), "Скорость пар", 15f, GuitarUI.Text, true, TextAnchor.MiddleLeft);
            GuitarUI.Label(new Rect(x, y, inner, 24f * pt), $"{step:0.00} с между ударами", 12f, GuitarUI.Accent, true, TextAnchor.MiddleRight);
            y += 24f * pt;
            // Left is quick, right is slow: the gap between the strokes.
            float chosen = GuitarUI.Slider(new Rect(x, y, inner, 34f * pt), step, GuitarHandPlayer.MinPairStep, GuitarHandPlayer.MaxPairStep);
            if (Mathf.Abs(chosen - step) > 0.001f)
                GuitarHandPlayer.PairStep = Mathf.Round(chosen * 100f) / 100f;
            y += 34f * pt;

            var size = SwitchSize(pt);
            GuitarUI.Label(new Rect(x, y + 3f * pt, inner - size.x, 20f * pt), "Под мой темп", 13f, GuitarUI.Text, true, TextAnchor.MiddleLeft);
            GuitarUI.Label(new Rect(x, y + 22f * pt, inner - size.x - 8f * pt, 18f * pt), "Немного подстраивается под тебя", 11f, GuitarUI.TextDim, false, TextAnchor.MiddleLeft);
            bool follow = GuitarUI.Switch(new Rect(x + inner - size.x, y + (44f * pt - size.y) * 0.5f, size.x, size.y), GuitarHandPlayer.PairFollowsTempo);
            if (follow != GuitarHandPlayer.PairFollowsTempo)
                GuitarHandPlayer.PairFollowsTempo = follow;
            return y + 44f * pt;
        }

        float DrawChordMode(float x, float y, float inner, int chordMode, float pt)
        {
            string help = k_ChordModeHelp[chordMode];
            y += 12f * pt;
            GuitarUI.Label(new Rect(x, y, inner, 22f * pt), "Аккорд выбирает", 15f, GuitarUI.Text, true, TextAnchor.MiddleLeft);
            y += 22f * pt;
            if (m_Hands != null)
            {
                int chosen = GuitarUI.Segmented(new Rect(x, y, inner, 34f * pt), chordMode, k_ChordModeLabels, 13f);
                if (chosen != chordMode)
                    m_Hands.ChordMode = (GuitarChordReader.Mode)chosen;
            }
            y += 34f * pt + 6f * pt;
            float helpHeight = GuitarUI.TextHeight(help, inner, 11f);
            GuitarUI.Label(new Rect(x, y, inner, helpHeight), help, 11f, GuitarUI.TextDim, false, TextAnchor.UpperLeft, true);
            return y + helpHeight + 12f * pt;
        }

        void DrawDiagnosticsSwitch(float x, float y, float inner, float pt)
        {
            var size = SwitchSize(pt);
            GuitarUI.Label(new Rect(x, y, inner - size.x, 48f * pt), "Диагностика", 15f, GuitarUI.Text, true, TextAnchor.MiddleLeft);
            bool diagnostics = GuitarUI.Switch(new Rect(x + inner - size.x, y + (48f * pt - size.y) * 0.5f, size.x, size.y), m_Diagnostics);
            if (diagnostics != m_Diagnostics)
            {
                m_Diagnostics = diagnostics;
                try
                {
                    PlayerPrefs.SetInt(k_DiagnosticsKey, diagnostics ? 1 : 0);
                }
                catch (System.Exception)
                {
                }
            }
        }

        /// <summary>A thin line across a column; returns the y below it.</summary>
        static float Separator(float x, float y, float width)
        {
            GuitarUI.Fill(new Rect(x, y, width, 1f), 0f, GuitarUI.Track);
            return y + 1f;
        }

        /// <summary>What to do next, fading in and out. Returns its bottom (or the top it was given, when hidden).</summary>
        float DrawHint(Rect area, float pt)
        {
            string hint = m_SettingsOpen ? null : (m_Switcher != null ? m_Switcher.Hint() : null) ?? GuitarStrummer.Hint;
            if (Event.current.type == EventType.Repaint)
            {
                float step = Time.unscaledDeltaTime / k_HintFadeSeconds;
                if (hint != m_Hint)
                {
                    // The old hint fades out before the new one fades in.
                    if (m_HintAlpha <= 0f)
                        m_Hint = hint;
                    else
                        m_HintAlpha = Mathf.Max(0f, m_HintAlpha - step);
                }
                if (hint == m_Hint)
                    m_HintAlpha = Mathf.Clamp01(m_HintAlpha + (hint != null ? step : -step));
            }
            if (m_Hint == null || m_HintAlpha <= 0f)
                return area.y - k_Gap * pt;

            float pad = 14f * pt;
            float textWidth = Mathf.Min(GuitarUI.TextWidth(m_Hint, 14f), area.width - pad * 2f);
            float textHeight = GuitarUI.TextHeight(m_Hint, textWidth + 1f, 14f);
            var banner = new Rect(area.center.x - textWidth * 0.5f - pad, area.y, textWidth + pad * 2f, textHeight + 18f * pt);
            var panel = GuitarUI.Panel;
            panel.a *= m_HintAlpha;
            var ink = GuitarUI.Text;
            ink.a *= m_HintAlpha;
            GuitarUI.Fill(banner, Mathf.Min(banner.height * 0.5f, 20f * pt), panel);
            GuitarUI.Label(new Rect(banner.x + pad, banner.y, textWidth + 1f, banner.height), m_Hint, 14f, ink, false, TextAnchor.MiddleCenter, true);
            return banner.yMax;
        }

        void DrawDiagnostics(Rect area, float pt)
        {
            string text = GuitarStrummer.Diagnostics;
            string selfie = m_Switcher != null ? m_Switcher.SelfieDiagnostic : null;
            if (!string.IsNullOrEmpty(selfie))
                text += "\n" + selfie;
            float pad = 8f * pt;
            float height = GuitarUI.TextHeight(text, area.width - pad * 2f, 9f) + pad * 2f;
            var card = new Rect(area.x, area.y, area.width, height);
            GuitarUI.Fill(card, 12f * pt, GuitarUI.PanelStrong);
            GuitarUI.Label(new Rect(card.x + pad, card.y + pad, card.width - pad * 2f, height - pad * 2f), text, 9f, new Color(1f, 0.85f, 0.2f), false, TextAnchor.UpperLeft, true);
        }

        // ---- Bottom ----

        void DrawModes(Rect bar)
        {
            if (m_Switcher == null)
                return;
            int mode = GuitarUI.Segmented(bar, (int)m_Switcher.Mode, k_ModeLabels, 11f, m_DrawModeSegment);
            if (mode != (int)m_Switcher.Mode)
                m_Switcher.SetMode((GuitarMode)mode);
        }

        void DrawModeSegment(Rect segment, int mode, bool selected)
        {
            float pt = GuitarUI.Pt;
            var ink = selected ? GuitarUI.OnAccent : GuitarUI.Text;
            float icon = 20f * pt;
            DrawModeIcon(mode, new Rect(segment.center.x - icon * 0.5f, segment.y + 5f * pt, icon, icon), ink);
            GuitarUI.Label(new Rect(segment.x, segment.y + 27f * pt, segment.width, 16f * pt), k_ModeLabels[mode], 11f, ink, selected);
        }

        /// <summary>The chord buttons, standing on the given bottom edge.</summary>
        void DrawChords(Rect area, float pt)
        {
            bool gestures = m_Hands != null && m_Hands.ChordMode == GuitarChordReader.Mode.Gestures;
            float height = (gestures ? 58f : 50f) * pt;
            var row = new Rect(area.x, area.y - height, area.width, height);
            float gap = 6f * pt;
            int count = m_ChordLabels.Length;
            float width = (row.width - gap * (count - 1)) / count;
            for (int i = 0; i < count; i++)
            {
                var chip = new Rect(row.x + i * (width + gap), row.y, width, height);
                bool selected = i == GuitarStrummer.CurrentChord;
                GuitarUI.Fill(chip, 14f * pt, selected ? GuitarUI.Accent : GuitarUI.Panel);
                var ink = selected ? GuitarUI.OnAccent : GuitarUI.Text;
                if (gestures)
                {
                    GuitarUI.Label(new Rect(chip.x, chip.y + 6f * pt, chip.width, 28f * pt), m_ChordLabels[i], 20f, ink, true);
                    GuitarUI.Label(new Rect(chip.x, chip.y + 34f * pt, chip.width, 16f * pt), GuitarChordReader.GestureHint(i), 10f, selected ? GuitarUI.OnAccent : GuitarUI.TextDim);
                }
                else
                {
                    GuitarUI.Label(chip, m_ChordLabels[i], 20f, ink, true);
                }
                if (GuitarUI.Tapped(chip))
                    GuitarStrummer.SetChord(i);
            }
        }

        // ---- Over the camera image ----

        /// <summary>A ring on the right index fingertip: green when it plays, red when the finger is down.</summary>
        void DrawFingertip(float pt)
        {
            var state = m_Hands.RightHand;
            if (state == GuitarHandPlayer.HandState.NotSeen)
                return;
            var tip = m_Hands.Fingertip;
            var center = new Vector2(tip.x, Screen.height - tip.y);
            if (state == GuitarHandPlayer.HandState.Ready)
            {
                GuitarUI.Ring(center, 34f * pt, GuitarUI.Good);
                GuitarUI.Dot(center, 9f * pt, GuitarUI.Good);
            }
            else
            {
                var red = GuitarUI.Bad;
                red.a = 0.75f;
                GuitarUI.Ring(center, 28f * pt, red);
            }
        }

        // ---- Icons, drawn from shapes ----

        static void DrawModeIcon(int mode, Rect r, Color ink)
        {
            float s = r.width;
            switch (mode)
            {
                case (int)GuitarMode.Body:
                    // A person.
                    GuitarUI.Dot(new Vector2(r.center.x, r.y + s * 0.2f), s * 0.38f, ink);
                    GuitarUI.Fill(new Rect(r.x + s * 0.17f, r.y + s * 0.47f, s * 0.66f, s * 0.53f), s * 0.26f, ink);
                    break;
                case (int)GuitarMode.Selfie:
                    // A phone showing a face.
                    GuitarUI.Outline(new Rect(r.x + s * 0.2f, r.y, s * 0.6f, s), s * 0.16f, Mathf.Max(2f, s * 0.09f), ink);
                    GuitarUI.Dot(new Vector2(r.center.x, r.y + s * 0.4f), s * 0.24f, ink);
                    GuitarUI.Fill(new Rect(r.x + s * 0.35f, r.y + s * 0.57f, s * 0.3f, s * 0.17f), s * 0.08f, ink);
                    break;
                default:
                    // Something standing on a table.
                    GuitarUI.Fill(new Rect(r.x + s * 0.3f, r.y + s * 0.26f, s * 0.4f, s * 0.4f), s * 0.07f, ink);
                    GuitarUI.Fill(new Rect(r.x, r.y + s * 0.72f, s, s * 0.13f), s * 0.06f, ink);
                    break;
            }
        }

        static void DrawSettingsIcon(Rect r, Color ink)
        {
            // Three sliders.
            float s = r.width;
            for (int i = 0; i < k_SliderKnobs.Length; i++)
            {
                float y = r.y + s * (0.2f + 0.3f * i);
                GuitarUI.Fill(new Rect(r.x, y - s * 0.05f, s, s * 0.1f), s * 0.05f, ink);
                GuitarUI.Dot(new Vector2(r.x + s * k_SliderKnobs[i], y), s * 0.27f, ink);
            }
        }

        static bool AnyPlayable()
        {
            foreach (var guitar in GuitarStrings.Active)
            {
                if (guitar.IsPlayable)
                    return true;
            }
            return false;
        }
    }
}
