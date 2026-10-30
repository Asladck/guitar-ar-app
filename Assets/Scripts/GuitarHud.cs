using System.Collections.Generic;
using UnityEngine;

namespace GuitarAR
{
    /// <summary>
    /// The on-screen interface. At the top: the chord being played (it flashes on each strum),
    /// whether the strumming hand can play, the play mode card (Easy's start/stop and pattern,
    /// the metronome), hints, and the settings. At the bottom: the chord buttons and the mode
    /// switch. Over the camera image: a ring on the strumming point, and on the neck where the
    /// chord's fingers go.
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
        // In PlayMode order.
        static readonly string[] k_PlayModeLabels = { "Лёгкий (авто)", "С помощью", "Свободный" };
        static readonly string[] k_SourceLabels = { "Ладонь", "Палец" };
        static readonly string[] k_SourceHelp = { "Бьёт центр ладони: проще всего", "Бьёт кончик указательного: точнее; если он потерян, бьёт ладонь" };
        const float k_ChangeSeconds = 2.5f;

        GuitarModeSwitcher m_Switcher;
        GuitarHandPlayer m_Hands;
        GuitarMusic m_Music;
        bool m_SettingsOpen;
        bool m_Diagnostics;
        string[] m_PatternLabels;
        string[] m_ChordLabels;
        string m_Hint;
        float m_HintAlpha;
        System.Action<Rect, int, bool> m_DrawModeSegment;

        void Awake()
        {
            m_Hands = GetComponent<GuitarHandPlayer>();
            m_Music = GetComponent<GuitarMusic>();
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
            if (m_Music == null)
                m_Music = GetComponent<GuitarMusic>();
            if (m_PatternLabels == null)
            {
                m_PatternLabels = new string[StrumPattern.All.Length];
                for (int i = 0; i < m_PatternLabels.Length; i++)
                    m_PatternLabels[i] = StrumPattern.All[i].Name;
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
            else if (playable && m_Hands != null && m_Music != null)
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
                var state = m_Hands != null ? m_Hands.StrummingHand : GuitarHandPlayer.HandState.NotSeen;
                bool easy = m_Music != null && m_Music.Mode == PlayMode.Easy;
                bool playing = m_Music != null && m_Music.IsPlaying;
                Color dot;
                string text;
                switch (state)
                {
                    case GuitarHandPlayer.HandState.Ready:
                        dot = GuitarUI.Good;
                        text = !easy ? "Можно играть" : playing ? "Играет · кулак = стоп" : "Взмахни — начнём";
                        break;
                    case GuitarHandPlayer.HandState.Paused:
                        dot = GuitarUI.Bad;
                        text = GuitarHandPlayer.Source == GuitarHandPlayer.MotionSource.Palm ? "Кулак: пауза" : "Палец опущен: пауза";
                        break;
                    default:
                        dot = playing ? GuitarUI.Accent : GuitarUI.TextDim;
                        text = GuitarHandPlayer.LeftHanded != GuitarHandPlayer.SwapHands ? "Покажи левую руку" : "Покажи правую руку";
                        break;
                }
                GuitarUI.Fill(status, status.height * 0.5f, GuitarUI.Panel);
                GuitarUI.Dot(new Vector2(status.x + 20f * pt, status.center.y), 10f * pt, dot);
                GuitarUI.Label(new Rect(status.x + 33f * pt, status.y, status.width - 40f * pt, status.height), text, 14f, GuitarUI.Text, true, TextAnchor.MiddleLeft);
                GuitarUI.Block(status);
            }
            return row.yMax;
        }

        /// <summary>
        /// The play mode card: the mode (automatic Easy, or your own strumming with or without
        /// help), and under it Easy's start/stop button and pattern with the slot playing now, or
        /// the metronome for your own strumming.
        /// </summary>
        float DrawRhythm(Rect card, float pt)
        {
            GuitarUI.Fill(card, 18f * pt, GuitarUI.Panel);
            GuitarUI.Block(card);
            float pad = 8f * pt;

            var choice = new Rect(card.x + pad, card.y + pad, card.width - pad * 2f, 32f * pt);
            int mode = GuitarUI.Segmented(choice, (int)m_Music.Mode, k_PlayModeLabels, 12f);
            if (mode != (int)m_Music.Mode)
                m_Music.Mode = (PlayMode)mode;

            var row = new Rect(card.x + pad, choice.yMax + 6f * pt, card.width - pad * 2f, card.yMax - pad - choice.yMax - 6f * pt);
            bool keepsTime = m_Music.TryGetPosition(out int beat, out float phase, out int slot);

            // The tempo on the right.
            float tempoWidth = 62f * pt;
            GuitarUI.Label(new Rect(row.xMax - tempoWidth, row.y, tempoWidth, row.height), $"{m_Music.Bpm:0} уд/мин", 11f, GuitarUI.TextDim, false, TextAnchor.MiddleRight);
            row.width -= tempoWidth + 6f * pt;

            switch (m_Music.Mode)
            {
                case PlayMode.Easy:
                {
                    // Start / stop, big and coloured, so it's clear whether the music runs.
                    bool playing = m_Music.IsPlaying;
                    var button = new Rect(row.x, row.y, 78f * pt, row.height);
                    GuitarUI.Fill(button, button.height * 0.5f, playing ? GuitarUI.Bad : GuitarUI.Good);
                    GuitarUI.Label(button, playing ? "■ Стоп" : "▶ Старт", 14f, Color.white, true);
                    if (GuitarUI.Tapped(button))
                    {
                        if (playing)
                            m_Music.Stop("кнопка");
                        else
                            m_Music.Play();
                    }
                    DrawPatternSlots(new Rect(button.xMax + 6f * pt, row.y, row.xMax - button.xMax - 6f * pt, row.height), keepsTime ? slot : -1, pt);
                    break;
                }
                case PlayMode.Assisted:
                {
                    // The metronome: four beats, the one now lit, and whether the last strike was nudged.
                    float size = Mathf.Min(row.height - 8f * pt, 22f * pt);
                    for (int i = 0; i < GuitarRhythmEngine.BeatsPerBar; i++)
                    {
                        var center = new Vector2(row.x + size * 0.5f + i * (size + 8f * pt), row.center.y);
                        bool now = keepsTime && i == beat;
                        float grow = now ? 1f + 0.35f * (1f - phase) : 1f;
                        GuitarUI.Dot(center, size * grow * (i == 0 ? 1f : 0.8f), now ? GuitarUI.Accent : GuitarUI.Track);
                    }
                    float x = row.x + 4f * (size + 8f * pt) + 4f * pt;
                    string last = Time.unscaledTime - m_Music.LastStrikeTime < 1.5f ? (m_Music.LastAssistSnapped ? "в долю" : "сразу") : "взмахивай в такт";
                    GuitarUI.Label(new Rect(x, row.y, row.xMax - x, row.height), last, 12f, GuitarUI.TextDim, false, TextAnchor.MiddleLeft);
                    break;
                }
                default:
                    GuitarUI.Label(row, "Каждый взмах — один удар, сразу", 12f, GuitarUI.TextDim);
                    break;
            }
            return card.yMax;
        }

        /// <summary>Easy's pattern, one cell per slot (↓ ↑ ✕, or · for a rest), the one playing now lit.</summary>
        void DrawPatternSlots(Rect area, int now, float pt)
        {
            var pattern = m_Music.CurrentPattern;
            float gap = 3f * pt;
            float width = (area.width - gap * (pattern.Length - 1)) / pattern.Length;
            for (int i = 0; i < pattern.Length; i++)
            {
                string symbol = "·";
                foreach (var hit in pattern.Hits)
                {
                    if (hit.Position == i)
                        symbol = hit.Kind == StrumKind.Down ? "↓" : hit.Kind == StrumKind.Up ? "↑" : "×";
                }
                var cell = new Rect(area.x + i * (width + gap), area.y + 2f * pt, width, area.height - 4f * pt);
                bool lit = i == now;
                bool onBeat = i % pattern.SlotsPerBeat == 0;
                GuitarUI.Fill(cell, Mathf.Min(8f * pt, width * 0.4f), lit ? GuitarUI.Accent : onBeat ? GuitarUI.Track : new Color(1f, 1f, 1f, 0.04f));
                GuitarUI.Label(cell, symbol, 15f, lit ? GuitarUI.OnAccent : GuitarUI.Text, true);
            }
        }

        /// <summary>
        /// The settings sheet: tempo, pattern and metronome; what strikes; how the chord is
        /// picked; hands (left-handed, swap, calibration); diagnostics. One column, or two side by
        /// side when one would run off the bottom of the screen (the phone held sideways).
        /// Returns its bottom.
        /// </summary>
        float DrawSettings(Rect area, float pt, Rect safe)
        {
            float pad = 14f * pt;
            int chordMode = m_Hands != null ? (int)m_Hands.ChordMode : (int)GuitarChordReader.Mode.Off;
            float oneInner = area.width - pad * 2f;
            float music = MusicHeight(pt) + 1f + SourceHeight(oneInner, pt);
            float rest = ChordHeight(chordMode, oneInner, pt) + 1f + HandsHeight(pt) + 1f + DiagnosticsHeight(pt);
            float oneHeight = pad + music + 1f + rest + pad * 0.5f;
            bool twoColumns = area.y + oneHeight > safe.yMax - k_Gap * pt && area.width >= 480f * pt;

            if (!twoColumns)
            {
                var sheet = new Rect(area.x, area.y, area.width, oneHeight);
                GuitarUI.Fill(sheet, 20f * pt, GuitarUI.PanelStrong);
                GuitarUI.Block(sheet);
                float x = sheet.x + pad, y = sheet.y + pad;
                y = DrawMusic(x, y, oneInner, pt);
                y = Separator(x, y, oneInner);
                y = DrawSource(x, y, oneInner, pt);
                y = Separator(x, y, oneInner);
                y = DrawChordMode(x, y, oneInner, chordMode, pt);
                y = Separator(x, y, oneInner);
                y = DrawHands(x, y, oneInner, pt);
                y = Separator(x, y, oneInner);
                DrawDiagnosticsSwitch(x, y, oneInner, pt);
                return sheet.yMax;
            }

            float inner = (area.width - pad * 3f) * 0.5f;
            float left = pad + MusicHeight(pt) + 1f + SourceHeight(inner, pt) + 1f + HandsHeight(pt);
            float right = pad + ChordHeight(chordMode, inner, pt) + 1f + DiagnosticsHeight(pt);
            var wide = new Rect(area.x, area.y, area.width, Mathf.Max(left, right) + pad * 0.5f);
            GuitarUI.Fill(wide, 20f * pt, GuitarUI.PanelStrong);
            GuitarUI.Block(wide);
            float leftX = wide.x + pad, rightX = leftX + inner + pad, top = wide.y + pad;
            float ly = DrawMusic(leftX, top, inner, pt);
            ly = Separator(leftX, ly, inner);
            ly = DrawSource(leftX, ly, inner, pt);
            ly = Separator(leftX, ly, inner);
            DrawHands(leftX, ly, inner, pt);
            float ry = DrawChordMode(rightX, top, inner, chordMode, pt);
            ry = Separator(rightX, ry, inner);
            DrawDiagnosticsSwitch(rightX, ry, inner, pt);
            return wide.yMax;
        }

        static float MusicHeight(float pt) => 4f * pt + 24f * pt + 34f * pt + 4f * pt + 32f * pt + 44f * pt;
        static float SourceHeight(float inner, float pt) => 12f * pt + 22f * pt + 32f * pt + 4f * pt + GuitarUI.TextHeight(k_SourceHelp[1], inner, 11f) + 10f * pt;
        static float HandsHeight(float pt) => 44f * pt + 44f * pt + 44f * pt;
        static float ChordHeight(int chordMode, float inner, float pt) => 12f * pt + 22f * pt + 34f * pt + 6f * pt + GuitarUI.TextHeight(k_ChordModeHelp[chordMode], inner, 11f) + 12f * pt;
        float DiagnosticsHeight(float pt) => m_Diagnostics ? 48f * pt + 44f * pt : 48f * pt;

        static Vector2 SwitchSize(float pt) => new Vector2(50f * pt, 30f * pt);

        /// <summary>A label with a note under it and a switch on the right, 44 points tall. Returns the switch's state.</summary>
        static bool SwitchRow(float x, float y, float inner, string label, string note, bool on, float pt)
        {
            var size = SwitchSize(pt);
            GuitarUI.Label(new Rect(x, y + 3f * pt, inner - size.x, 20f * pt), label, 13f, GuitarUI.Text, true, TextAnchor.MiddleLeft);
            if (note != null)
                GuitarUI.Label(new Rect(x, y + 22f * pt, inner - size.x - 8f * pt, 18f * pt), note, 11f, GuitarUI.TextDim, false, TextAnchor.MiddleLeft);
            return GuitarUI.Switch(new Rect(x + inner - size.x, y + (44f * pt - size.y) * 0.5f, size.x, size.y), on);
        }

        /// <summary>Tempo (60-160), the pattern for Easy, and the metronome.</summary>
        float DrawMusic(float x, float y, float inner, float pt)
        {
            y += 4f * pt;
            float bpm = m_Music != null ? m_Music.Bpm : 90f;
            GuitarUI.Label(new Rect(x, y, inner, 24f * pt), "Темп", 15f, GuitarUI.Text, true, TextAnchor.MiddleLeft);
            GuitarUI.Label(new Rect(x, y, inner, 24f * pt), $"{bpm:0} уд/мин", 12f, GuitarUI.Accent, true, TextAnchor.MiddleRight);
            y += 24f * pt;
            float chosen = GuitarUI.Slider(new Rect(x, y, inner, 34f * pt), bpm, GuitarRhythmEngine.MinBpm, GuitarRhythmEngine.MaxBpm);
            if (m_Music != null && Mathf.Abs(chosen - bpm) >= 0.5f)
                m_Music.Bpm = chosen;
            y += 34f * pt + 4f * pt;

            if (m_Music != null)
            {
                int pattern = GuitarUI.Segmented(new Rect(x, y, inner, 32f * pt), m_Music.Pattern, m_PatternLabels, 12f);
                if (pattern != m_Music.Pattern)
                    m_Music.Pattern = pattern;
            }
            y += 32f * pt;

            bool metronome = m_Music != null && m_Music.Metronome;
            bool on = SwitchRow(x, y, inner, "Метроном", "Щелчки на каждую долю", metronome, pt);
            if (m_Music != null && on != metronome)
                m_Music.Metronome = on;
            return y + 44f * pt;
        }

        /// <summary>What strikes: the palm or the index fingertip.</summary>
        float DrawSource(float x, float y, float inner, float pt)
        {
            y += 12f * pt;
            GuitarUI.Label(new Rect(x, y, inner, 22f * pt), "Бьёт", 15f, GuitarUI.Text, true, TextAnchor.MiddleLeft);
            y += 22f * pt;
            int source = (int)GuitarHandPlayer.Source;
            int chosen = GuitarUI.Segmented(new Rect(x, y, inner, 32f * pt), source, k_SourceLabels, 13f);
            if (chosen != source)
                GuitarHandPlayer.Source = (GuitarHandPlayer.MotionSource)chosen;
            y += 32f * pt + 4f * pt;
            float help = GuitarUI.TextHeight(k_SourceHelp[1], inner, 11f);
            GuitarUI.Label(new Rect(x, y, inner, help), k_SourceHelp[chosen], 11f, GuitarUI.TextDim, false, TextAnchor.UpperLeft, true);
            return y + help + 10f * pt;
        }

        /// <summary>Left-handed mode, swapping the hands' roles, and the calibration.</summary>
        float DrawHands(float x, float y, float inner, float pt)
        {
            bool lefty = SwitchRow(x, y, inner, "Левша", "Бой левой рукой, гитара зеркально", GuitarHandPlayer.LeftHanded, pt);
            if (lefty != GuitarHandPlayer.LeftHanded)
                GuitarHandPlayer.LeftHanded = lefty;
            y += 44f * pt;
            bool swap = SwitchRow(x, y, inner, "Поменять руки", "Если путаются: бой и аккорды местами", GuitarHandPlayer.SwapHands, pt);
            if (swap != GuitarHandPlayer.SwapHands)
                GuitarHandPlayer.SwapHands = swap;
            y += 44f * pt;

            if (m_Hands != null)
            {
                float resetWidth = 92f * pt;
                var calibrate = new Rect(x, y + 5f * pt, inner - resetWidth - 6f * pt, 34f * pt);
                if (GuitarUI.Button(calibrate, m_Hands.IsCalibrating ? "Отменить калибровку" : "Калибровка (10 с)", 13f, m_Hands.IsCalibrating, 12f * pt))
                {
                    if (m_Hands.IsCalibrating)
                        m_Hands.CancelCalibration();
                    else
                    {
                        m_Hands.StartCalibration();
                        m_SettingsOpen = false;
                    }
                }
                if (GuitarUI.Button(new Rect(calibrate.xMax + 6f * pt, calibrate.y, resetWidth, calibrate.height), "Сбросить", 12f, false, 12f * pt))
                    m_Hands.ResetCalibration();
            }
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
                if (!diagnostics)
                    GuitarDiagnosticsRecorder.Stop();
            }
            if (!m_Diagnostics)
                return;

            // For developers only: hand joints and events to a file, never video.
            bool recording = SwitchRow(x, y + 48f * pt, inner, "Запись жестов в файл", "Точки рук и события, без видео", GuitarDiagnosticsRecorder.IsRecording, pt);
            if (recording != GuitarDiagnosticsRecorder.IsRecording)
            {
                if (recording)
                    GuitarDiagnosticsRecorder.Start();
                else
                    GuitarDiagnosticsRecorder.Stop();
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
            string hint = m_SettingsOpen ? null : CurrentHint();
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

        /// <summary>
        /// What to tell the player now: the calibration's step, what just happened (started,
        /// stopped and why), what the camera mode needs, then how to play in this mode.
        /// </summary>
        string CurrentHint()
        {
            string calibration = m_Hands != null ? m_Hands.CalibrationHint : null;
            if (calibration != null)
                return calibration;
            if (m_Music != null && m_Music.LastChange != null && Time.unscaledTime - m_Music.LastChangeTime < k_ChangeSeconds)
                return m_Music.LastChange;
            string mode = m_Switcher != null ? m_Switcher.Hint() : null;
            if (mode != null || !AnyPlayable() || m_Music == null || m_Hands == null)
                return mode;

            bool lefty = GuitarHandPlayer.LeftHanded != GuitarHandPlayer.SwapHands;
            string strum = lefty ? "левой" : "правой", chord = lefty ? "Правой" : "Левой";
            if (m_Hands.StrummingHand == GuitarHandPlayer.HandState.NotSeen)
                return $"Покажи обе руки: {strum} бьёшь, {chord.ToLower()} выбираешь аккорд";
            bool gestures = m_Hands.ChordMode == GuitarChordReader.Mode.Gestures;
            switch (m_Music.Mode)
            {
                case PlayMode.Easy:
                    if (m_Music.IsPlaying)
                        return GuitarStrummer.HasPlayed && Time.unscaledTime - m_Music.LastChangeTime < 8f && gestures
                            ? $"{chord}: 1–4 пальца = Am, C, G, F. Стоп: кулак или ■"
                            : null;
                    return $"Взмахни {strum} рукой над струнами — бой начнётся сам";
                case PlayMode.Assisted:
                    return GuitarStrummer.HasPlayed ? null : "Сам: каждый взмах — удар, приложение подровняет его к доле";
                default:
                    return GuitarStrummer.HasPlayed ? null : "Сам: каждый взмах вниз или вверх — один удар";
            }
        }

        void DrawDiagnostics(Rect area, float pt)
        {
            string text = GuitarStrummer.Diagnostics + (m_Music != null ? "\n" + m_Music.DiagnosticText : "")
                + (GuitarDiagnosticsRecorder.IsRecording ? "\nзапись: " + GuitarDiagnosticsRecorder.FilePath : "");
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
                    string gesture = GuitarChordReader.GestureHint(i) ?? "кнопка";
                    GuitarUI.Label(new Rect(chip.x, chip.y + 34f * pt, chip.width, 16f * pt), gesture, 9.5f, selected ? GuitarUI.OnAccent : GuitarUI.TextDim);
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

        /// <summary>
        /// A ring on the point that strikes (the palm, bigger, or the index fingertip): green when
        /// it plays, red when paused (a fist, or the finger down).
        /// </summary>
        void DrawFingertip(float pt)
        {
            var state = m_Hands.StrummingHand;
            if (state == GuitarHandPlayer.HandState.NotSeen)
                return;
            var tip = m_Hands.StrikePoint;
            var center = new Vector2(tip.x, Screen.height - tip.y);
            float scale = m_Hands.StrikingWithPalm ? 1.6f : 1f;
            if (state == GuitarHandPlayer.HandState.Ready)
            {
                GuitarUI.Ring(center, 34f * pt * scale, GuitarUI.Good);
                GuitarUI.Dot(center, 9f * pt, GuitarUI.Good);
            }
            else
            {
                var red = GuitarUI.Bad;
                red.a = 0.75f;
                GuitarUI.Ring(center, 28f * pt * scale, red);
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
