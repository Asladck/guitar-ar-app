using System;
using System.Collections.Generic;
using GuitarAR;

/// <summary>
/// Checks for the plain C# logic in Assets/Scripts/Logic, on simulated hand tracking: still
/// hands, swings at different frame rates, lost hands, gestures, the rhythm engine's timing.
/// </summary>
static class LogicTests
{
    static int s_Failed, s_Passed;

    static void Check(bool ok, string what)
    {
        if (ok)
            s_Passed++;
        else
        {
            s_Failed++;
            Console.WriteLine("FAIL: " + what);
        }
    }

    static int Main()
    {
        var tests = new List<KeyValuePair<string, Action>>
        {
            Test("Неподвижная рука не даёт ударов", StillHandMakesNoStrikes),
            Test("Один взмах — один удар (15/30/60 кадр/с)", OneSwingOneStrike),
            Test("Пересечение между кадрами находится (8 кадр/с)", CrossingBetweenFrames),
            Test("Потеря и возврат руки не дают ложного удара", LostHandNoFalseStrike),
            Test("Перерыв в данных без Lost тоже сбрасывает историю", GapResetsHistory),
            Test("Медленный дрейф и короткий взмах отклоняются", SlowAndShortRejected),
            Test("90 мс не мешает восьмым на 160 BPM", MinIntervalAllowsMaxTempo),
            Test("Дрожание у края зоны не даёт ударов", JitterAtBandEdge),
            Test("Easy: ровный темп при любом FPS, повторные взмахи не добавляют ударов", EasyKeepsTempo),
            Test("Easy: провал кадров не сдвигает сетку и не играет в прошлом", EasySkipsPastSlots),
            Test("Easy: остановка прекращает выдачу нот", EasyStopCancels),
            Test("Assisted: ожидание не больше 40 мс и никогда в прошлом", AssistedSnapping),
            Test("Assisted: звучат только удары рисунка", AssistedFollowsPattern),
            Test("Free: удар сразу", FreePlaysNow),
            Test("Паттерны: поп = ↓ _ ↓ ↑ _ ↑ ↓ ↑", PopPatternData),
            Test("Жесты по углам суставов, в любом повороте руки", GesturesByAngles),
            Test("Аккорд переключается только после удержания 150 мс", GestureHold),
            Test("Роли рук: зеркало, левша, ручная смена", HandRoles),
            Test("One Euro сглаживает дрожание и не отстаёт на взмахе", OneEuroFilter),
            Test("Калибровка подбирает чувствительность", Calibration),
        };
        foreach (var test in tests)
        {
            int before = s_Failed;
            try
            {
                test.Value();
            }
            catch (Exception e)
            {
                s_Failed++;
                Console.WriteLine("FAIL: exception " + e);
            }
            Console.WriteLine((s_Failed == before ? "ok   " : "FAIL ") + test.Key);
        }
        Console.WriteLine($"{s_Passed} checks passed, {s_Failed} failed");
        return s_Failed == 0 ? 0 : 1;
    }

    static KeyValuePair<string, Action> Test(string name, Action action) => new KeyValuePair<string, Action>(name, action);

    // ---- Simulated tracking ----

    static readonly Random s_Random = new Random(12345);

    static float Gauss(float sigma)
    {
        double u1 = 1.0 - s_Random.NextDouble(), u2 = s_Random.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2)) * sigma;
    }

    /// <summary>
    /// Feeds a movement d(t) through the One Euro filter and the detector, sampled at fps with
    /// timing jitter and position noise, as the app does. Returns the strikes.
    /// </summary>
    static List<Strike> Run(Func<double, float> d, double seconds, double fps, float noise, StrikeSettings settings = null,
        GuitarStrikeDetector detector = null, double start = 0)
    {
        detector = detector ?? new GuitarStrikeDetector(settings ?? new StrikeSettings());
        var filter = new GuitarOneEuroFilter(1f, 6f, 5f);
        var strikes = new List<Strike>();
        for (double t = start; t < start + seconds; t += 1.0 / fps * (0.9 + 0.2 * s_Random.NextDouble()))
        {
            filter.Filter(d(t) + Gauss(noise), 0.2f + Gauss(noise), t, out float fd, out float fv);
            if (detector.Process(t, fd, fv))
                strikes.Add(detector.LastStrike);
        }
        return strikes;
    }

    /// <summary>A comfortable swing: from -a to +a (down) or back, with a smooth (cosine) speed.</summary>
    static float Swing(double t, double t0, double duration, float a, bool down)
    {
        double k = Math.Max(0, Math.Min(1, (t - t0) / duration));
        float x = (float)(-Math.Cos(Math.PI * k)) * a;
        return down ? x : -x;
    }

    // ---- Strikes ----

    static void StillHandMakesNoStrikes()
    {
        foreach (float place in new[] { -0.2f, -0.03f, 0f, 0.02f, 0.25f })
            Check(Run(t => place, 10, 30, 0.006f).Count == 0, $"рука стоит в {place}");
    }

    static void OneSwingOneStrike()
    {
        foreach (double fps in new[] { 15.0, 30.0, 60.0 })
        {
            // Hold, one down swing of 0.15 s, hold.
            var strikes = Run(t => Swing(t, 0.5, 0.15, 0.15f, true), 1.5, fps, 0.004f);
            Check(strikes.Count == 1, $"{fps} кадр/с: ударов {strikes.Count}, нужен 1");
            Check(strikes.Count > 0 && strikes[0].Direction == StrikeDirection.Down, $"{fps} кадр/с: направление вниз");
            // Down then up.
            strikes = Run(t => t < 1 ? Swing(t, 0.5, 0.15, 0.15f, true) : Swing(t, 1.2, 0.15, 0.15f, false), 2, fps, 0.004f);
            Check(strikes.Count == 2 && strikes[0].Direction == StrikeDirection.Down && strikes[1].Direction == StrikeDirection.Up,
                $"{fps} кадр/с: вниз-вверх даёт {strikes.Count} удар(а)");
        }
    }

    static void CrossingBetweenFrames()
    {
        // At 8 fps a 0.1 s swing lands on at most one frame mid-way: the line is jumped over.
        int found = 0;
        for (int trial = 0; trial < 20; trial++)
        {
            double offset = trial * 0.0061;
            var detector = new GuitarStrikeDetector(new StrikeSettings());
            var strikes = new List<Strike>();
            for (double t = offset; t < 1.5; t += 0.125)
            {
                float d = Swing(t, 0.6, 0.1, 0.15f, true);
                if (detector.Process(t, d, 0.2f))
                    strikes.Add(detector.LastStrike);
            }
            if (strikes.Count == 1)
                found++;
            Check(strikes.Count <= 1, "не больше одного удара на взмах");
        }
        Check(found == 20, $"найдено {found} из 20 перепрыгнутых пересечений");
    }

    static void LostHandNoFalseStrike()
    {
        var detector = new GuitarStrikeDetector(new StrikeSettings());
        int strikes = 0;
        double t = 0;
        for (; t < 1; t += 1 / 30.0)
            strikes += detector.Process(t, -0.2f + Gauss(0.004f), 0.2f) ? 1 : 0;
        detector.Lost("рука пропала");
        t += 0.3;
        // Comes back on the other side, and stays there.
        for (double end = t + 1; t < end; t += 1 / 30.0)
            strikes += detector.Process(t, 0.2f + Gauss(0.004f), 0.2f) ? 1 : 0;
        Check(strikes == 0, $"ложных ударов {strikes}");
        // A real swing after recovery still works.
        double swing = t + 0.2;
        for (double end = t + 1; t < end; t += 1 / 30.0)
            strikes += detector.Process(t, -Swing(t, swing, 0.15, 0.2f, true), 0.2f) ? 1 : 0;
        Check(strikes == 1, $"после возврата настоящий взмах: {strikes}");
    }

    static void GapResetsHistory()
    {
        var detector = new GuitarStrikeDetector(new StrikeSettings());
        int strikes = 0;
        strikes += detector.Process(0.0, -0.2f, 0.2f) ? 1 : 0;
        strikes += detector.Process(0.033, -0.2f, 0.2f) ? 1 : 0;
        strikes += detector.Process(0.066, -0.2f, 0.2f) ? 1 : 0;
        // Missing for 0.4 s, then on the other side.
        strikes += detector.Process(0.466, 0.2f, 0.2f) ? 1 : 0;
        strikes += detector.Process(0.5, 0.2f, 0.2f) ? 1 : 0;
        Check(strikes == 0, "после перерыва не должно быть удара");
    }

    static void SlowAndShortRejected()
    {
        // Drifting across over 3 s.
        var detector = new GuitarStrikeDetector(new StrikeSettings());
        var strikes = Run(t => (float)(-0.2 + 0.4 * t / 3.0), 3, 30, 0.003f, null, detector);
        Check(strikes.Count == 0, "медленный дрейф: " + strikes.Count);
        Check(detector.LastRejection == "медленно" || detector.LastRejection == "стоит у струн", "причина: " + detector.LastRejection);
        // A small quick twitch over the line.
        detector = new GuitarStrikeDetector(new StrikeSettings());
        strikes = Run(t => Swing(t, 0.5, 0.08, 0.05f, true), 1.5, 30, 0.002f, null, detector);
        Check(strikes.Count == 0, "короткий взмах: " + strikes.Count + (strikes.Count > 0 ? $" ход {strikes[0].Travel:0.000}" : ""));
    }

    static void MinIntervalAllowsMaxTempo()
    {
        var settings = new StrikeSettings();
        double eighth = 60.0 / GuitarRhythmEngine.MaxBpm / 2;
        Check(settings.MinInterval < eighth, $"интервал {settings.MinInterval} < восьмой {eighth:0.000}");
        Check(settings.MinInterval < eighth / 2 + 0.001, "и даже шестнадцатые проходят");
        // Down-up strumming in eighths at 160 BPM for 3 s: each crossing a strike.
        var strikes = Run(t => t < 0.5 ? -0.15f : (float)(-0.15 * Math.Cos(Math.PI * (t - 0.5) / eighth)), 3.5, 30, 0.003f);
        int expected = (int)(3.0 / eighth);
        Check(Math.Abs(strikes.Count - expected) <= 1, $"ударов {strikes.Count}, взмахов {expected}");
        for (int i = 1; i < strikes.Count; i++)
            Check(strikes[i].Direction != strikes[i - 1].Direction, "вниз и вверх чередуются");
    }

    static void JitterAtBandEdge()
    {
        var settings = new StrikeSettings();
        foreach (float place in new[] { -settings.Band, settings.Band })
            Check(Run(t => place, 10, 60, settings.Band * 0.3f, settings).Count == 0, "дрожание у края " + place);
    }

    // ---- Rhythm ----

    static List<ScheduledSound> RunEngine(GuitarRhythmEngine engine, double from, double to, Func<double> frame, List<ScheduledSound> sounds = null)
    {
        sounds = sounds ?? new List<ScheduledSound>();
        for (double now = from; now < to; now += frame())
            engine.Update(now, sounds);
        return sounds;
    }

    static void EasyKeepsTempo()
    {
        foreach (int fps in new[] { 10, 30, 60 })
        {
            var engine = new GuitarRhythmEngine { Mode = PlayMode.Easy, Bpm = 90, Pattern = StrumPattern.All[1] };
            Check(!engine.OnStrike(StrikeDirection.Down, 1, 1.0, out _), "первый взмах запускает, а не играет сам");
            Check(engine.IsPlaying, "играет");
            var sounds = new List<ScheduledSound>();
            double last = 0;
            for (double now = 1.0; now < 9.0; now += 1.0 / fps * (0.5 + s_Random.NextDouble()))
            {
                last = now;
                engine.Update(now, sounds);
                // Swinging along all the time: no extra strokes.
                engine.OnStrike(StrikeDirection.Up, 1, now, out _);
            }
            double slot = 60.0 / 90 / 2;
            double startAt = 1.0 + engine.StartLead;
            bool onGrid = true;
            foreach (var s in sounds)
            {
                double slots = (s.Time - startAt) / slot;
                onGrid &= Math.Abs(slots - Math.Round(slots)) < 1e-6;
            }
            Check(onGrid, $"{fps} кадр/с: все удары точно на сетке");
            // 8 s at 90 BPM = 12 bars (minus the start), 6 strokes a bar.
            // Slots from the start to the end plus the look-ahead, and the pattern's strokes on them.
            int expected = 0;
            for (int k = 0; startAt + k * slot < last + engine.Lookahead + (fps == 10 ? 0.125 : 0); k++)
                expected += Array.FindAll(engine.Pattern.Hits, h => h.Position == k % 8).Length;
            int bars = expected / 6;
            Check(Math.Abs(sounds.Count - expected) <= 2, $"{fps} кадр/с: ударов {sounds.Count} на {bars} тактов");
        }
    }

    static void EasySkipsPastSlots()
    {
        var engine = new GuitarRhythmEngine { Mode = PlayMode.Easy, Bpm = 120, Pattern = StrumPattern.All[0] };
        engine.Start(0);
        var sounds = new List<ScheduledSound>();
        double now = 0;
        for (; now < 1; now += 1 / 60.0)
            engine.Update(now, sounds);
        now += 0.6;  // The app froze for 0.6 s.
        int skipped = 0;
        int before = sounds.Count;
        for (double end = now + 1; now < end; now += 1 / 60.0)
            skipped += engine.Update(now, sounds);
        Check(skipped > 0, "пропущенные доли не играются");
        bool future = true;
        double frozenUntil = 1 + 0.6;
        for (int i = before; i < sounds.Count; i++)
        {
            future &= sounds[i].Time >= frozenUntil - 0.02;
            double beats = (sounds[i].Time - engine.StartLead) / 0.5;
            future &= Math.Abs(beats - Math.Round(beats)) < 1e-6;
        }
        Check(future, "после провала: только будущие удары на прежней сетке");
    }

    static void EasyStopCancels()
    {
        var engine = new GuitarRhythmEngine { Mode = PlayMode.Easy, Bpm = 90 };
        engine.OnStrike(StrikeDirection.Down, 1, 0, out _);
        var sounds = RunEngine(engine, 0, 2, () => 1 / 30.0);
        engine.Stop();
        int count = sounds.Count;
        RunEngine(engine, 2, 4, () => 1 / 30.0, sounds);
        Check(sounds.Count == count && !engine.IsPlaying, "после стопа ничего не выдаётся");
        foreach (var s in sounds)
            Check(s.Time < 2 + engine.MaxLookahead + 1e-6, "выдано не дальше упреждения");
        // A new deliberate swing starts it again.
        engine.OnStrike(StrikeDirection.Down, 1, 5, out _);
        Check(engine.IsPlaying, "новый взмах запускает снова");
    }

    static void AssistedSnapping()
    {
        var engine = new GuitarRhythmEngine { Mode = PlayMode.Assisted, Bpm = 120, Metronome = true, Pattern = StrumPattern.All[0] };
        var clicks = RunEngine(engine, 0, 1, () => 1 / 60.0);
        Check(clicks.Count > 0 && clicks.TrueForAll(s => s.IsClick), "метроном щёлкает");
        // Простой: downs on the beats (0.5 s at 120). The first down starts the bar at 2.0.
        Check(engine.OnStrike(StrikeDirection.Down, 1, 2.0, out var first) && first.Time == 2.0, "первый взмах вниз начинает такт сразу");
        RunEngine(engine, 2.0, 2.5 - 0.02, () => 1 / 60.0);
        foreach (double offset in new[] { 0.0, 0.01, 0.03, 0.039, 0.045, 0.1 })
        {
            // A down strike `offset` before the second beat (2.5 s).
            var e = new GuitarRhythmEngine { Mode = PlayMode.Assisted, Bpm = 120, Pattern = StrumPattern.All[0] };
            e.OnStrike(StrikeDirection.Down, 1, 2.0, out _);
            double now = 2.5 - offset;
            RunEngine(e, 2.0, now, () => 1 / 60.0);
            Check(e.OnStrike(StrikeDirection.Down, 1, now, out var sound), $"за {offset * 1000:0} мс до доли: играет");
            double wait = sound.Time - now;
            Check(wait >= 0, $"за {offset * 1000:0} мс до доли: не в прошлом");
            Check(wait <= e.MaxAssistWait + 1e-6, $"за {offset * 1000:0} мс до доли: ждёт {wait * 1000:0} мс");
            bool snapped = offset <= e.MaxAssistWait + 1e-9;
            Check(snapped ? Math.Abs(sound.Time - 2.5) < 1e-6 : wait == 0, $"за {offset * 1000:0} мс до доли: {(snapped ? "на долю" : "сразу")}");
        }
        Check(true, "");
    }

    static void AssistedFollowsPattern()
    {
        // Поп: ↓ _ ↓ ↑ _ ↑ ↓ ↑ in eighths (0.25 s at 120).
        var e = new GuitarRhythmEngine { Mode = PlayMode.Assisted, Bpm = 120, Pattern = StrumPattern.All[1] };
        Check(!e.OnStrike(StrikeDirection.Up, 1, 1.0, out _) && e.LastRejection == "начни с ↓", "начинать надо с ↓");
        Check(e.OnStrike(StrikeDirection.Down, 1, 1.0, out _), "↓ на 1");
        // Down-up all the time, on every eighth: only the pattern's strokes sound.
        var played = new System.Text.StringBuilder("D");
        for (int k = 1; k < 16; k++)
        {
            double now = 1.0 + k * 0.25;
            RunEngine(e, now - 0.25, now, () => 1 / 60.0);
            var dir = k % 2 == 0 ? StrikeDirection.Down : StrikeDirection.Up;
            played.Append(e.OnStrike(dir, 1, now, out var s) ? (s.Kind == StrumKind.Up ? "U" : "D") : "_");
        }
        Check(played.ToString() == "D_DU_UDUD_DU_UDU", "звучит только рисунок: " + played);
        // The wrong way on a stroke's slot plays nothing either.
        var w = new GuitarRhythmEngine { Mode = PlayMode.Assisted, Bpm = 120, Pattern = StrumPattern.All[1] };
        w.OnStrike(StrikeDirection.Down, 1, 1.0, out _);
        RunEngine(w, 1.0, 1.75, () => 1 / 60.0);
        Check(!w.OnStrike(StrikeDirection.Down, 1, 1.75, out _) && w.LastRejection == "не по рисунку", "↓ там, где в рисунке ↑, не звучит");
        // A rest between strokes: a swing near the 200 ms rest slot is ignored too.
        var r = new GuitarRhythmEngine { Mode = PlayMode.Assisted, Bpm = 120, Pattern = StrumPattern.All[0] };
        r.OnStrike(StrikeDirection.Down, 1, 1.0, out _);
        RunEngine(r, 1.0, 1.3, () => 1 / 60.0);
        Check(!r.OnStrike(StrikeDirection.Down, 1, 1.3, out _), "Простой: взмах между четвертями не звучит");
        // A second swing on a slot already played does nothing.
        var f = new GuitarRhythmEngine { Mode = PlayMode.Assisted, Bpm = 120, Pattern = StrumPattern.All[1] };
        f.OnStrike(StrikeDirection.Down, 1, 1.0, out _);
        Check(!f.OnStrike(StrikeDirection.Down, 1, 1.05, out _), "дубль на той же доле не звучит");
        // Шестёрка: the down on slot 2 is a muted stroke.
        var g = new GuitarRhythmEngine { Mode = PlayMode.Assisted, Bpm = 120, Pattern = StrumPattern.All[2] };
        g.OnStrike(StrikeDirection.Down, 1, 1.0, out _);
        RunEngine(g, 1.0, 1.5, () => 1 / 60.0);
        Check(g.OnStrike(StrikeDirection.Down, 1, 1.5, out var mute) && mute.Kind == StrumKind.Mute, "взмах вниз на ✕ звучит глушением");
        // After a pause the bar starts over.
        Check(g.OnStrike(StrikeDirection.Down, 1, 9.0, out var again) && again.Time == 9.0, "после паузы такт заново");
    }

    static void FreePlaysNow()
    {
        var engine = new GuitarRhythmEngine { Mode = PlayMode.Free };
        Check(engine.OnStrike(StrikeDirection.Up, 0.5f, 3.21, out var sound) && sound.Time == 3.21 && sound.Kind == StrumKind.Up, "сразу и вверх");
        Check(RunEngine(engine, 0, 2, () => 1 / 30.0).Count == 0, "сам ничего не играет");
    }

    static void PopPatternData()
    {
        var pop = StrumPattern.All[1];
        var grid = new char[pop.Length];
        for (int i = 0; i < grid.Length; i++)
            grid[i] = '_';
        foreach (var hit in pop.Hits)
            grid[hit.Position] = hit.Kind == StrumKind.Down ? 'D' : 'U';
        Check(new string(grid) == "D_DU_UDU", "поп: " + new string(grid));
        var simple = StrumPattern.All[0];
        Check(simple.Hits.Length == 4 && Array.TrueForAll(simple.Hits, h => h.Kind == StrumKind.Down && h.Position % 2 == 0), "простой: вниз на каждую четверть");
    }

    // ---- Gestures ----

    /// <summary>
    /// A hand facing the camera, fingers pointing along angle (degrees, 90 = up the screen), with
    /// the chosen fingers straight and the others curled into the palm.
    /// </summary>
    static GuitarHandPose Hand(bool[] straight, float angle, float size = 100f, float noise = 0f)
    {
        var pose = new GuitarHandPose();
        double a = angle * Math.PI / 180;
        float ux = (float)Math.Cos(a), uy = (float)Math.Sin(a);   // Along the fingers.
        float px = -uy, py = ux;                                  // Across the hand.
        Action<HandJoint, float, float> set = (j, along, side) =>
            pose.Set(j, 500 + (along * ux + side * px) * size + Gauss(noise), 500 + (along * uy + side * py) * size + Gauss(noise), 0.9f);
        set(HandJoint.Wrist, 0, 0);
        set(HandJoint.ThumbCMC, 0.25f, -0.35f);
        set(HandJoint.ThumbMP, 0.45f, -0.55f);
        set(HandJoint.ThumbIP, 0.6f, -0.65f);
        set(HandJoint.ThumbTip, 0.75f, -0.7f);
        float[] across = { -0.3f, -0.05f, 0.18f, 0.38f };
        float[] mcpAlong = { 0.95f, 1f, 0.95f, 0.85f };
        float[] length = { 0.75f, 0.85f, 0.8f, 0.6f };
        for (int f = 0; f < 4; f++)
        {
            var mcp = (HandJoint)((int)HandJoint.IndexMCP + 4 * f);
            float m = mcpAlong[f], l = length[f], c = across[f];
            set(mcp, m, c);
            if (straight[f])
            {
                set(mcp + 1, m + 0.4f * l, c);
                set(mcp + 2, m + 0.7f * l, c);
                set(mcp + 3, m + l, c);
            }
            else
            {
                // Curled: up, then back down towards the palm.
                set(mcp + 1, m + 0.25f * l, c);
                set(mcp + 2, m + 0.1f * l, c);
                set(mcp + 3, m - 0.15f * l, c);
            }
        }
        return pose;
    }

    static void GesturesByAngles()
    {
        var cases = new[]
        {
            new { Fingers = new[] { true, false, false, false }, Gesture = HandGesture.OneFinger },
            new { Fingers = new[] { true, true, false, false }, Gesture = HandGesture.TwoFingers },
            new { Fingers = new[] { true, true, true, false }, Gesture = HandGesture.ThreeFingers },
            new { Fingers = new[] { true, true, true, true }, Gesture = HandGesture.FourFingers },
            new { Fingers = new[] { false, false, false, false }, Gesture = HandGesture.Fist },
            new { Fingers = new[] { false, true, false, false }, Gesture = HandGesture.Unknown },
            new { Fingers = new[] { true, false, false, true }, Gesture = HandGesture.Unknown },
        };
        foreach (var c in cases)
        {
            // Pointing up, sideways, down and at a slant; small and large on screen.
            foreach (float angle in new[] { 90f, 0f, 180f, -90f, 35f })
            {
                foreach (float size in new[] { 40f, 150f })
                {
                    var got = GuitarHandGestures.Read(Hand(c.Fingers, angle, size, size * 0.01f));
                    Check(got == c.Gesture, $"{string.Join(",", c.Fingers)} под {angle}°, размер {size}: {got}, нужен {c.Gesture}");
                }
            }
        }
        // A finger half raised is unknown, not a chord.
        var half = Hand(new[] { true, true, false, false }, 90f);
        half.Set(HandJoint.MiddleTip, half.X[(int)HandJoint.MiddleDIP] + 30, half.Y[(int)HandJoint.MiddleDIP] - 10, 0.9f);
        Check(GuitarHandGestures.Read(half) == HandGesture.Unknown, "полусогнутый палец — неизвестный жест");
        // An unseen joint makes it unknown too.
        var hidden = Hand(new[] { true, true, true, false }, 90f);
        hidden.Confidence[(int)HandJoint.RingTip] = 0.1f;
        Check(GuitarHandGestures.Read(hidden) == HandGesture.Unknown, "невидимый палец — неизвестный жест");
    }

    static void GestureHold()
    {
        var hold = new GuitarGestureHold(0.15f);
        double t = 0;
        Check(hold.Update(HandGesture.TwoFingers, t) == HandGesture.Unknown, "сразу не переключает");
        t += 0.1;
        Check(hold.Update(HandGesture.TwoFingers, t) == HandGesture.Unknown, "через 100 мс ещё нет");
        t += 0.06;
        Check(hold.Update(HandGesture.TwoFingers, t) == HandGesture.TwoFingers && hold.Confirmed == HandGesture.TwoFingers, "через 160 мс да");
        // Passing through other gestures on the way to a new one.
        foreach (var g in new[] { HandGesture.ThreeFingers, HandGesture.Unknown, HandGesture.FourFingers, HandGesture.ThreeFingers, HandGesture.OneFinger })
        {
            t += 0.05;
            hold.Update(g, t);
        }
        Check(hold.Confirmed == HandGesture.TwoFingers, "переходные жесты не переключают: " + hold.Confirmed);
        hold.Lost();
        Check(hold.Confirmed == HandGesture.TwoFingers, "при потере руки аккорд остаётся");
    }

    static void HandRoles()
    {
        float[] along = { 0.2f, 0.9f };
        GuitarHandRoles.Assign(2, new[] { 1, -1 }, along, false, false, false, out int strum, out int chord);
        Check(strum == 0 && chord == 1, "задняя камера, правша: правая бьёт");
        GuitarHandRoles.Assign(2, new[] { -1, 1 }, along, true, false, false, out strum, out chord);
        Check(strum == 0 && chord == 1, "фронтальная (зеркало): правая для Vision — левая игрока");
        GuitarHandRoles.Assign(2, new[] { 1, -1 }, along, false, true, false, out strum, out chord);
        Check(strum == 1 && chord == 0, "левша: бьёт левая");
        GuitarHandRoles.Assign(2, new[] { 1, -1 }, along, false, true, true, out strum, out chord);
        Check(strum == 0 && chord == 1, "левша + ручная смена = снова правая");
        GuitarHandRoles.Assign(2, new[] { 0, 0 }, new[] { 0.9f, 0.1f }, false, false, false, out strum, out chord);
        Check(strum == 1 && chord == 0, "без руки от трекера: у корпуса бьёт, у грифа аккорд");
        GuitarHandRoles.Assign(1, new[] { -1 }, along, false, false, false, out strum, out chord);
        Check(strum == -1 && chord == 0, "видна только левая");
    }

    static void OneEuroFilter()
    {
        var filter = new GuitarOneEuroFilter(1f, 6f, 5f);
        float sumIn = 0, sumOut = 0;
        float previous = 0;
        for (int i = 0; i < 300; i++)
        {
            float raw = Gauss(0.01f);
            filter.Filter(raw, 0, i / 30.0, out float x, out _);
            if (i > 10)
            {
                sumIn += raw * raw;
                sumOut += (x - previous) * (x - previous);
            }
            previous = x;
        }
        Check(sumOut < sumIn * 0.25f, $"дрожание: было {Math.Sqrt(sumIn / 289):0.0000}, шаги стали {Math.Sqrt(sumOut / 289):0.0000}");
        // A swing: how far behind is the filtered point when the real one crosses the middle?
        filter.Reset();
        float lag = 0;
        for (double t = 0; t < 1; t += 1 / 30.0)
        {
            float d = Swing(t, 0.4, 0.15, 0.15f, true);
            filter.Filter(d, 0, t, out float x, out _);
            if (t >= 0.475 && t < 0.475 + 1 / 30.0)
                lag = d - x;
        }
        Check(lag < 0.06f, $"отставание на взмахе {lag:0.000} длины гитары");
    }

    static void Calibration()
    {
        var defaults = new StrikeSettings();
        var calibrator = new GuitarCalibrator();
        double t = 0;
        // Both hands shown, the strumming one roughly still.
        for (; t < 1.5; t += 1 / 30.0)
            calibrator.Sample(t, true, 0.05f + Gauss(0.006f), true, defaults);
        Check(calibrator.Current == GuitarCalibrator.Phase.Swing, "после показа рук — взмахи");
        Check(calibrator.Noise > 0.003f && calibrator.Noise < 0.012f, $"шум {calibrator.Noise:0.0000}");
        // Swings of ±0.12 around 0.05, each 0.25 s, with pauses.
        float center = 0.05f, a = 0.12f;
        for (int k = 0; k < 6 && calibrator.Current == GuitarCalibrator.Phase.Swing; k++)
        {
            double t0 = t;
            bool down = k % 2 == 0;
            for (; t < t0 + 0.5 && calibrator.Current == GuitarCalibrator.Phase.Swing; t += 1 / 30.0)
                calibrator.Sample(t, true, center + Swing(t, t0 + 0.1, 0.25, a, down) + Gauss(0.006f), true, defaults);
        }
        Check(calibrator.Current == GuitarCalibrator.Phase.Done, "калибровка закончена: " + calibrator.Current);
        var s = calibrator.Result;
        if (s == null)
            return;
        Console.WriteLine($"      калибровка: полоса {s.Band:0.000}, ход {s.MinTravel:0.000}, скорость {s.MinSpeed:0.00}, линия {s.LineOffset:0.000}");
        Check(Math.Abs(s.LineOffset - center) < 0.03f, "линия посередине взмахов");
        Check(s.Band >= 3 * 0.006f, "полоса шире дрожания");
        // The same player's swings are found with these settings, and still hands aren't.
        var strikes = Run(x => center + (x < 1 ? Swing(x, 0.5, 0.25, a, true) : Swing(x, 1.3, 0.25, a, false)), 2.2, 30, 0.006f, s);
        Check(strikes.Count == 2, "с калибровкой: ударов " + strikes.Count);
        Check(Run(x => center + 0.1f, 5, 30, 0.006f, s).Count == 0, "с калибровкой: стоящая рука молчит");
    }
}
