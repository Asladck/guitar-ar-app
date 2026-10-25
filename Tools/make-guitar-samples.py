#!/usr/bin/env python3
"""Makes the guitar's note recordings in Assets/Resources/GuitarSamples.

The source is the guitar of the University of Iowa Electronic Music Studios' Musical Instrument
Samples (a Raimundo 118 classical guitar, recorded in an anechoic chamber):
https://theremin.music.uiowa.edu/MISguitar.html. Their terms: the recordings "may be downloaded
and used for any projects, without restrictions".

Each source file is one string played up a chromatic run at one dynamic. This cuts every run into
notes, checks each note's pitch and retunes it to A = 440 Hz, and writes the first frets of each
string as {dynamic}_s{string}_f{fret}.wav (string 1 is the low E), which GuitarSamples.cs loads.

    python3 Tools/make-guitar-samples.py

Only the standard library is needed; the sources are downloaded with curl into
Library/GuitarSampleSources and kept there.
"""

import array
import math
import os
import re
import shutil
import struct
import subprocess
import sys
import wave

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE_DIR = os.path.join(ROOT, "Library", "GuitarSampleSources")
OUTPUT_DIR = os.path.join(ROOT, "Assets", "Resources", "GuitarSamples")
BASE_URL = "https://theremin.music.uiowa.edu/sound%20files/MIS/Piano_Other/guitar/"

OPEN_STRINGS = [40, 45, 50, 55, 59, 64]  # MIDI notes, low E first; as GuitarSynth.OpenStrings.
FRETS = range(0, 6)
DYNAMICS = ["mf", "ff"]
# The recordings that hold each string's first frets ("sul_E" is the high E).
RUNS = {
    1: ["sulE.E2B2"],
    2: ["sulA.A2B2", "sulA.C3B3"],
    3: ["sulD.D3B3"],
    4: ["sulG.G3B3", "sulG.C4B4"],
    5: ["sulB.B3", "sulB.C4B4"],
    6: ["sul_E.E4B4"],
}

RATE = 48000            # Unity's output rate (ProjectSettings/AudioManager), so notes play unresampled.
NOTE_SECONDS = 3.0      # Longest note kept; a strum cuts the ringing string anyway.
FADE_SECONDS = 0.25     # Fade at the end of a note that's still ringing.
PRE_ROLL = 0.002        # Seconds kept before the pluck, so its attack isn't clipped.
PEAK = 0.6              # Every note is scaled to this peak, as the synthesized notes are.
MAX_CENTS = 60          # A note further than this from its expected pitch is an error.
NAMES = {"C": 0, "C#": 1, "Db": 1, "D": 2, "D#": 3, "Eb": 3, "E": 4, "F": 5, "F#": 6, "Gb": 6,
         "G": 7, "G#": 8, "Ab": 8, "A": 9, "A#": 10, "Bb": 10, "B": 11}


def midi_of(name):
    match = re.fullmatch(r"([A-G][b#]?)(-?\d)", name)
    return NAMES[match.group(1)] + 12 * (int(match.group(2)) + 1)


def note_range(run):
    """The first and last MIDI notes of a run, from its name: sulA.C3B3 is C3 up to B3."""
    names = re.findall(r"[A-G][b#]?-?\d", run.split(".", 1)[1])
    return midi_of(names[0]), midi_of(names[-1])


def frequency(midi):
    return 440.0 * 2.0 ** ((midi - 69) / 12.0)


def note_name(midi):
    return ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"][midi % 12] + str(midi // 12 - 1)


def download(dynamic, run):
    name = f"Guitar.{dynamic}.{run}.mono.aif"
    path = os.path.join(SOURCE_DIR, name)
    if not os.path.exists(path):
        os.makedirs(SOURCE_DIR, exist_ok=True)
        print(f"downloading {name}")
        subprocess.run(["curl", "-fsSL", "--retry", "3", "-o", path + ".part", BASE_URL + name], check=True)
        os.replace(path + ".part", path)
    return convert(path)


def convert(path):
    """The recording as 16-bit mono WAV at RATE, made with macOS's afconvert or with ffmpeg
    (their resampling is far better and faster than anything done here). Without either, the
    original is read as it is."""
    out = path[:-4] + f".{RATE // 1000}k.wav"
    if os.path.exists(out):
        return out
    if shutil.which("afconvert"):
        command = ["afconvert", "-f", "WAVE", "-d", f"LEI16@{RATE}", "-c", "1", path, out + ".part"]
    elif shutil.which("ffmpeg"):
        command = ["ffmpeg", "-loglevel", "error", "-y", "-i", path, "-ac", "1", "-ar", str(RATE), "-c:a", "pcm_s16le", "-f", "wav", out + ".part"]
    else:
        print(f"  no afconvert or ffmpeg: using {os.path.basename(path)} at its own rate")
        return path
    subprocess.run(command, check=True)
    os.replace(out + ".part", out)
    return out


def read_extended(data):
    """An 80-bit IEEE extended float, as AIFF stores its sample rate."""
    exponent = ((data[0] & 0x7F) << 8) | data[1]
    mantissa = int.from_bytes(data[2:10], "big")
    value = mantissa * 2.0 ** (exponent - 16383 - 63)
    return -value if data[0] & 0x80 else value


def read_audio(path):
    """Mono samples from -1 to 1 and the sample rate, from an AIFF or WAV file."""
    if path.lower().endswith(".wav"):
        with wave.open(path, "rb") as w:
            channels, width, rate = w.getnchannels(), w.getsampwidth(), w.getframerate()
            raw, big_endian = w.readframes(w.getnframes()), False
    else:
        with open(path, "rb") as f:
            data = f.read()
        if data[:4] != b"FORM" or data[8:12] not in (b"AIFF", b"AIFC"):
            raise ValueError(f"{path} is not an AIFF file")
        big_endian = True
        raw = None
        pos = 12
        while pos + 8 <= len(data):
            chunk, size = data[pos:pos + 4], struct.unpack(">I", data[pos + 4:pos + 8])[0]
            body = data[pos + 8:pos + 8 + size]
            if chunk == b"COMM":
                channels, frames, bits = struct.unpack(">hIh", body[:8])
                rate = read_extended(body[8:18])
                width = bits // 8
                if data[8:12] == b"AIFC" and body[18:22] == b"sowt":
                    big_endian = False
                elif data[8:12] == b"AIFC" and body[18:22] != b"NONE":
                    raise ValueError(f"{path} is compressed")
            elif chunk == b"SSND":
                offset = struct.unpack(">I", body[:4])[0]
                raw = body[8 + offset:]
            pos += 8 + size + (size & 1)
        raw = raw[:frames * channels * width]

    if width == 2:
        values = array.array("h", raw)
        if big_endian != (sys.byteorder == "big"):
            values.byteswap()
        scale = 32768.0
    elif width == 3:
        order = "big" if big_endian else "little"
        values = [int.from_bytes(raw[i:i + 3], order, signed=True) for i in range(0, len(raw), 3)]
        scale = 8388608.0
    else:
        raise ValueError(f"{path}: {width * 8}-bit samples aren't supported")

    if channels == 1:
        samples = [v / scale for v in values]
    else:
        samples = [sum(values[i:i + channels]) / (scale * channels) for i in range(0, len(values), channels)]
    return remove_dc(samples), int(round(rate))


def remove_dc(samples):
    out, previous_in, previous_out = [0.0] * len(samples), 0.0, 0.0
    for i, x in enumerate(samples):
        previous_out = x - previous_in + 0.9995 * previous_out
        previous_in = x
        out[i] = previous_out
    return out


def find_onsets(samples, rate):
    """Where the level jumps well above what was ringing just before: (sample, level) pairs.
    Plucks, but also some noise and swells in a ringing note; cut_run sorts them out by pitch."""
    block = 256
    levels = [max(abs(x) for x in samples[i:i + block]) for i in range(0, len(samples) - block, block)]
    floor = 0.08 * max(levels)
    spacing = max(1, int(0.03 * rate / block))
    attack = max(1, int(0.03 * rate / block))
    onsets, last = [], -spacing
    for b in range(8, len(levels)):
        before = sum(levels[b - 8:b - 2]) / 6.0
        if levels[b] > floor and levels[b] > 2.0 * before and b - last >= spacing:
            # The pluck starts where the samples first climb above a fifth of the new level.
            start = (b - 1) * block
            threshold = 0.2 * levels[b]
            while start < (b + 1) * block and abs(samples[start]) < threshold:
                start += 1
            onsets.append((start, max(levels[b:b + attack])))
            last = b
    return onsets


def measure_pitch(samples, rate, start, low, high):
    """The fundamental (Hz) a moment after the pluck, by the YIN method, looking only between
    two notes; None if no clear pitch."""
    step = 2 if frequency(high) < 220 else 1  # Low notes: halve the rate to save time.
    begin = start + int(0.3 * rate)
    window = 2048
    span = samples[begin:begin + (window * 2 + 1200) * step]
    x = span if step == 1 else [0.5 * (span[i] + span[i + 1]) for i in range(0, len(span) - 1, 2)]
    sr = rate / step
    shortest = int(sr / frequency(high + 1))
    longest = int(sr / frequency(low - 1)) + 2
    if len(x) < window + longest + 2:
        return None

    head = x[:window]
    diffs = [0.0]
    for lag in range(1, longest + 2):
        shifted = x[lag:lag + window]
        diffs.append(sum((a - b) * (a - b) for a, b in zip(head, shifted)))
    normalized, running = [1.0], 0.0
    for lag in range(1, len(diffs)):
        running += diffs[lag]
        normalized.append(diffs[lag] * lag / running if running > 0 else 1.0)

    best = None
    for lag in range(max(2, shortest), longest + 1):
        if normalized[lag] < 0.2 and normalized[lag] <= normalized[lag + 1]:
            best = lag
            break
    if best is None:
        best = min(range(max(2, shortest), longest + 1), key=lambda lag: normalized[lag])
        if normalized[best] > 0.5:
            return None
    a, b, c = normalized[best - 1], normalized[best], normalized[best + 1]
    shift = 0.5 * (a - c) / (a - 2 * b + c) if a - 2 * b + c != 0 else 0.0
    return sr / (best + shift)


def resample(samples, ratio):
    """Reads the samples ratio times as fast (cubic interpolation): below 1 lowers the pitch."""
    count = int((len(samples) - 3) / ratio)
    out = [0.0] * count
    for i in range(count):
        position = 1.0 + i * ratio
        n = int(position)
        t = position - n
        p0, p1, p2, p3 = samples[n - 1], samples[n], samples[n + 1], samples[n + 2]
        out[i] = p1 + 0.5 * t * (p2 - p0 + t * (2 * p0 - 5 * p1 + 4 * p2 - p3 + t * (3 * (p1 - p2) + p3 - p0)))
    return out


def shape(note, rate):
    """Trims a note where it has died away, fades its end, and scales it to the common peak."""
    peak = max(abs(x) for x in note) or 1.0
    quiet = peak * 0.001  # -60 dB.
    block = 512
    for start in range(rate // 2, len(note) - block, block):
        if max(abs(x) for x in note[start:start + block]) < quiet:
            note = note[:start + block]
            break
    fade = max(1, min(int(FADE_SECONDS * rate), len(note) // 2))
    attack = int(0.001 * rate)
    gain = PEAK / peak
    for i in range(len(note)):
        g = gain
        if i < attack:
            g *= i / attack
        if i >= len(note) - fade:
            g *= (len(note) - i) / fade
        note[i] *= g
    return note


def write_wav(path, samples, rate):
    values = array.array("h", (max(-32767, min(32767, int(round(x * 32767)))) for x in samples))
    if sys.byteorder == "big":
        values.byteswap()
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(values.tobytes())


def quietest(samples, begin, end, block=480):
    """The lowest peak level of any short block between two points."""
    levels = [max(abs(x) for x in samples[i:i + block]) for i in range(begin, end - block, block)]
    return min(levels, default=0.0)


def cut_run(path, low, high):
    """The notes of one chromatic run: {midi: (samples, rate, start, end)}.

    Every jump in level is a candidate pluck, named by the pitch it rings at. A candidate with
    no clear pitch in the run's range, or the same pitch as the note before it, is noise or a
    swell in that note and stays part of it. The recording's overall tuning is allowed for, so
    a guitar tuned a little flat doesn't round notes down to the semitone below."""
    samples, rate = read_audio(path)
    candidates = find_onsets(samples, rate)
    heard = []
    for start, level in candidates:
        # A knock or squeak dies at once; a pluck still rings a moment later.
        ringing = max((abs(x) for x in samples[start + int(0.3 * rate):start + int(0.4 * rate)]), default=0.0)
        if ringing < 0.03 * level:
            heard.append(None)
            continue
        f = measure_pitch(samples, rate, start, low, high)
        heard.append(None if f is None else 69 + 12 * math.log2(f / 440.0))
    deviations = sorted(m - round(m) for m in heard if m is not None)
    tuning = deviations[len(deviations) // 2] if deviations else 0.0

    plucks = []  # [midi, start, level]
    for (start, level), m in zip(candidates, heard):
        if m is None:
            continue
        midi = int(round(m - tuning))
        if not low <= midi <= high:
            continue
        if plucks and plucks[-1][0] == midi:
            # The same note again. Something just before the pluck (a breath, the finger
            # touching the string) is weaker than the pluck and soon over: start at the pluck.
            previous = plucks[-1][1]
            if level > 1.5 * plucks[-1][2] and (start - previous < 0.35 * rate
                                                 or quietest(samples, previous + int(0.03 * rate), start) < 0.1 * level):
                plucks[-1][1:] = [start, level]
            continue
        plucks.append([midi, start, level])

    notes = {}
    for i, (midi, start, _) in enumerate(plucks):
        end = plucks[i + 1][1] - int(0.005 * rate) if i + 1 < len(plucks) else len(samples)
        notes.setdefault(midi, (samples, rate, start, end))
    found = sorted(notes)
    missing = [note_name(m) for m in range(low, high + 1) if m not in notes]
    print(f"  {os.path.basename(path)}: {len(candidates)} candidates, {len(found)} of {high - low + 1} notes, "
          f"tuned {tuning * 100:+.0f} cents{', missing ' + ' '.join(missing) if missing else ''}")
    return notes


def main():
    os.makedirs(OUTPUT_DIR, exist_ok=True)
    made = set()
    written = skipped = 0
    for dynamic in DYNAMICS:
        for string, runs in RUNS.items():
            notes = {}
            for run in runs:
                low, high = note_range(run)
                for midi, note in cut_run(download(dynamic, run), low, high).items():
                    notes.setdefault(midi, note)

            for fret in FRETS:
                midi = OPEN_STRINGS[string - 1] + fret
                label = f"{dynamic}_s{string}_f{fret}"
                if midi not in notes:
                    print(f"{label} {note_name(midi):4} MISSING")
                    skipped += 1
                    continue
                samples, rate, start, end = notes[midi]
                begin = max(0, start - int(PRE_ROLL * rate))
                limit = begin + int(NOTE_SECONDS * rate)
                found = measure_pitch(samples, rate, start, midi, midi)
                if found is None:
                    print(f"{label} {note_name(midi):4} no clear pitch")
                    skipped += 1
                    continue
                cents = 1200 * math.log2(found / frequency(midi))
                if abs(cents) > MAX_CENTS:
                    print(f"{label} {note_name(midi):4} off by {cents:+.0f} cents")
                    skipped += 1
                    continue

                # Retune, then cut the note out. Read a little extra so the cut lands right.
                ratio = 2.0 ** (-cents / 1200.0)
                source = samples[begin:min(end, int(begin + NOTE_SECONDS * rate / ratio) + 4)]
                note = resample(source, ratio) if abs(cents) > 1 else list(source)
                note = shape(note[:limit - begin], rate)
                write_wav(os.path.join(OUTPUT_DIR, label + ".wav"), note, rate)
                print(f"{label} {note_name(midi):4} {cents:+4.0f} cents  {len(note) / rate:.2f} s")
                made.add((string, fret))
                written += 1

    # A note that couldn't be cut is played from the other dynamic or a neighbouring fret,
    # retuned (GuitarSamples.cs). Only a fret with nothing close by is a real gap.
    gaps = [f"s{string}_f{fret}" for string in RUNS for fret in FRETS
            if not any((string, f) in made for f in range(fret - 2, fret + 3))]
    print(f"{written} notes written, {skipped} skipped (played from a neighbour instead)")
    if gaps:
        print("no recording within 2 frets for: " + " ".join(gaps))
        sys.exit(1)
    print(f"done: {OUTPUT_DIR}")


if __name__ == "__main__":
    main()
