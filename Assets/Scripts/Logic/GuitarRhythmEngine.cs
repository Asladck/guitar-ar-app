using System;
using System.Collections.Generic;

namespace GuitarAR
{
    /// <summary>How strikes become music.</summary>
    public enum PlayMode
    {
        /// <summary>Automatic strumming: a swing starts a ready pattern that keeps its own tempo.</summary>
        Easy,
        /// <summary>Your own strumming, each swing nudged (at most 40 ms) onto the beat.</summary>
        Assisted,
        /// <summary>Your own strumming, played at once, as it is.</summary>
        Free,
    }

    public enum StrumKind { Down, Up, Mute }

    /// <summary>One stroke of a pattern: its slot in the bar, which way, how hard (0..1).</summary>
    public struct PatternHit
    {
        public int Position;
        public StrumKind Kind;
        public float Strength;

        public PatternHit(int position, StrumKind kind, float strength)
        {
            Position = position;
            Kind = kind;
            Strength = strength;
        }
    }

    /// <summary>A strumming pattern as data: one bar of slots, and the strokes on them.</summary>
    public class StrumPattern
    {
        public string Name;
        public int SlotsPerBeat;
        public int Length;
        public PatternHit[] Hits;

        public StrumPattern(string name, int slotsPerBeat, int length, params PatternHit[] hits)
        {
            Name = name;
            SlotsPerBeat = slotsPerBeat;
            Length = length;
            Hits = hits;
        }

        /// <summary>
        /// The patterns, all in 4/4. Простой and Поп are the beginner ones (eighths, counted
        /// 1 и 2 и 3 и 4 и); Шестёрка and Восьмёрка are the ones the app had before.
        /// </summary>
        public static readonly StrumPattern[] All =
        {
            // Down on every beat.
            new StrumPattern("Простой", 2, 8,
                new PatternHit(0, StrumKind.Down, 1f), new PatternHit(2, StrumKind.Down, 0.8f),
                new PatternHit(4, StrumKind.Down, 0.9f), new PatternHit(6, StrumKind.Down, 0.8f)),
            // ↓ _ ↓ ↑ _ ↑ ↓ ↑ on 1 и 2 и 3 и 4 и.
            new StrumPattern("Поп", 2, 8,
                new PatternHit(0, StrumKind.Down, 1f), new PatternHit(2, StrumKind.Down, 0.85f),
                new PatternHit(3, StrumKind.Up, 0.65f), new PatternHit(5, StrumKind.Up, 0.65f),
                new PatternHit(6, StrumKind.Down, 0.85f), new PatternHit(7, StrumKind.Up, 0.65f)),
            // ↓ _ ✕ ↑ _ ↑ ✕ ↑ (✕ = palm-muted down).
            new StrumPattern("Шестёрка", 2, 8,
                new PatternHit(0, StrumKind.Down, 1f), new PatternHit(2, StrumKind.Mute, 0.85f),
                new PatternHit(3, StrumKind.Up, 0.65f), new PatternHit(5, StrumKind.Up, 0.65f),
                new PatternHit(6, StrumKind.Mute, 0.85f), new PatternHit(7, StrumKind.Up, 0.65f)),
            // Triplets: ↓ _ ↓ | _ ↑ ↑ | _ ↑ ↓ | _ ↓ ↑.
            new StrumPattern("Восьмёрка", 3, 12,
                new PatternHit(0, StrumKind.Down, 1f), new PatternHit(2, StrumKind.Down, 0.8f),
                new PatternHit(4, StrumKind.Up, 0.65f), new PatternHit(5, StrumKind.Up, 0.65f),
                new PatternHit(7, StrumKind.Up, 0.65f), new PatternHit(8, StrumKind.Down, 0.85f),
                new PatternHit(10, StrumKind.Down, 0.8f), new PatternHit(11, StrumKind.Up, 0.65f)),
        };
    }

    /// <summary>A stroke or metronome click to play at a time on the audio clock.</summary>
    public struct ScheduledSound
    {
        public double Time;
        public bool IsClick;
        public bool Accent;      // A click on the bar's first beat.
        public StrumKind Kind;
        public float Strength;
    }

    /// <summary>
    /// The musical side, on the audio clock (AudioSettings.dspTime), never on camera frames:
    /// strikes and chords come in as events and sounds go out with the time they must play.
    /// Easy keeps its own tempo from a grid of slots, so a slow camera or a hitch can't change
    /// it: a slot that has already gone by is skipped, never played late. Assisted and Free
    /// play the player's own strikes. Sounds are handed out a short look-ahead before they're
    /// due, so a chord picked meanwhile is heard on the next stroke.
    /// </summary>
    public class GuitarRhythmEngine
    {
        public const int BeatsPerBar = 4;
        public const float MinBpm = 60f;
        public const float MaxBpm = 160f;

        public float Lookahead = 0.1f;      // Seconds ahead sounds are handed out...
        public float MaxLookahead = 0.3f;   // ...or 1.5 frames, up to this, when frames come slowly.
        public float MaxAssistWait = 0.04f; // The most Assisted holds a strike back to land on the grid.
        public float StartLead = 0.02f;     // Easy's first stroke comes this soon after the swing that starts it.

        float m_Bpm = 90f;
        PlayMode m_Mode = PlayMode.Easy;
        StrumPattern m_Pattern = StrumPattern.All[0];
        bool m_ClockRunning;
        long m_NextSlot;
        double m_NextTime;
        double m_LastUpdate = double.NaN;

        public bool Metronome;

        public float Bpm
        {
            get => m_Bpm;
            set => m_Bpm = Math.Max(MinBpm, Math.Min(MaxBpm, value));
        }

        public PlayMode Mode
        {
            get => m_Mode;
            set
            {
                if (value == m_Mode)
                    return;
                m_Mode = value;
                IsPlaying = false;
                m_ClockRunning = false;
            }
        }

        public StrumPattern Pattern
        {
            get => m_Pattern;
            set
            {
                if (value != null)
                    m_Pattern = value;
            }
        }

        /// <summary>Easy only: whether the pattern is playing.</summary>
        public bool IsPlaying { get; private set; }

        /// <summary>Slots in Easy follow the pattern; Assisted's grid is in eighths.</summary>
        int SlotsPerBeat => m_Mode == PlayMode.Easy ? m_Pattern.SlotsPerBeat : 2;

        public double SlotSeconds => 60.0 / m_Bpm / SlotsPerBeat;

        /// <summary>Easy: starts the pattern from its first slot, now.</summary>
        public void Start(double now)
        {
            if (m_Mode != PlayMode.Easy)
                return;
            IsPlaying = true;
            StartClock(now + StartLead);
        }

        /// <summary>Easy: stops; nothing more is handed out (cancelling what was is the audio side's job).</summary>
        public void Stop() => IsPlaying = false;

        void StartClock(double now)
        {
            m_ClockRunning = true;
            m_NextSlot = 0;
            m_NextTime = now;
        }

        /// <summary>
        /// A strike from the player. In Easy, the first one starts the pattern and the others do
        /// nothing (false). Otherwise it's one stroke, its time set by the mode.
        /// </summary>
        public bool OnStrike(StrikeDirection direction, float strength, double now, out ScheduledSound sound)
        {
            sound = new ScheduledSound();
            switch (m_Mode)
            {
                case PlayMode.Easy:
                    if (IsPlaying)
                        return false;
                    Start(now);
                    return false;

                case PlayMode.Assisted:
                    if (!m_ClockRunning)
                        StartClock(now);
                    sound.Time = AssistedTime(now);
                    break;

                default:
                    sound.Time = now;
                    break;
            }
            sound.Kind = direction == StrikeDirection.Down ? StrumKind.Down : StrumKind.Up;
            sound.Strength = strength;
            return true;
        }

        /// <summary>
        /// When Assisted plays a strike made now: on the next grid slot if that's at most
        /// <see cref="MaxAssistWait"/> away, else at once. Never earlier than now.
        /// </summary>
        public double AssistedTime(double now)
        {
            if (!m_ClockRunning)
                return now;
            double slot = SlotSeconds;
            double ahead = m_NextTime - now;  // The grid's next handed-out slot is at or after now.
            double next = ahead >= 0 ? m_NextTime - Math.Floor(ahead / slot) * slot : m_NextTime + Math.Ceiling(-ahead / slot) * slot;
            return next - now <= MaxAssistWait + 1e-9 ? next : now;
        }

        /// <summary>
        /// Called every frame: adds the sounds due before now + look-ahead to the list. Easy hands
        /// out its pattern while playing; both Easy and Assisted hand out metronome clicks when the
        /// metronome is on. Returns how many slots were skipped because they had already gone by.
        /// </summary>
        public int Update(double now, List<ScheduledSound> sounds)
        {
            bool easy = m_Mode == PlayMode.Easy;
            if (easy && !IsPlaying)
                return 0;
            if (m_Mode == PlayMode.Free)
            {
                m_ClockRunning = false;
                return 0;
            }
            if (!m_ClockRunning)
                StartClock(now);

            // Sounds must be handed out before the frame after next could, or they'd be late.
            double frame = double.IsNaN(m_LastUpdate) ? 0 : now - m_LastUpdate;
            m_LastUpdate = now;
            double lookahead = Math.Max(Lookahead, Math.Min(MaxLookahead, 1.5 * frame));

            int skipped = 0;
            int perBeat = SlotsPerBeat;
            while (m_NextTime < now + lookahead)
            {
                if (m_NextTime >= now - 0.002)
                {
                    long inBar = easy ? m_NextSlot % m_Pattern.Length : m_NextSlot % (perBeat * BeatsPerBar);
                    if (easy)
                    {
                        foreach (var hit in m_Pattern.Hits)
                        {
                            if (hit.Position == inBar)
                                sounds.Add(new ScheduledSound { Time = m_NextTime, Kind = hit.Kind, Strength = hit.Strength });
                        }
                    }
                    if (Metronome && inBar % perBeat == 0)
                        sounds.Add(new ScheduledSound { Time = m_NextTime, IsClick = true, Accent = inBar == 0 });
                }
                else
                {
                    skipped++;
                }
                m_NextSlot++;
                m_NextTime += SlotSeconds;
            }
            return skipped;
        }

        /// <summary>
        /// Where the music is now, for the metronome on screen: the beat in the bar (0..3), how far
        /// into it (0..1), and the slot in the pattern. False while nothing keeps time.
        /// </summary>
        public bool TryGetPosition(double now, out int beat, out float beatPhase, out int slotInBar)
        {
            beat = slotInBar = 0;
            beatPhase = 0f;
            if (!m_ClockRunning || m_Mode == PlayMode.Free || (m_Mode == PlayMode.Easy && !IsPlaying))
                return false;
            double slots = m_NextSlot - (m_NextTime - now) / SlotSeconds;
            if (slots < 0)
                return false;
            int perBeat = SlotsPerBeat;
            double beats = slots / perBeat;
            beat = (int)(Math.Floor(beats) % BeatsPerBar);
            beatPhase = (float)(beats - Math.Floor(beats));
            int barLength = m_Mode == PlayMode.Easy ? m_Pattern.Length : perBeat * BeatsPerBar;
            slotInBar = (int)(Math.Floor(slots) % barLength);
            return true;
        }
    }
}
