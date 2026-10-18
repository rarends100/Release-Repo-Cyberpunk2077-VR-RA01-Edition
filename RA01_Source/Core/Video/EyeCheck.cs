namespace RA01.Core;

// RA01: M25-1 - FRAME LAG AUTO-FIX (Video tab "Frame lag auto-fix", on by default). The mod pairs each game picture with the head pose it was drawn from by
// COUNTING (CameraHook: picture number N goes with camera build N + a gap), and the count can come out one off after a menu, a loading screen or a stutter.
// One off swaps the eyes: each eye sees the picture drawn for the other one - things look doubled, or the depth inside out. "Frame lag" was the hand fix.
// THE CHECK: about every CheckMs, a few thin bands of rows are read back from the newest left-eye and right-eye pictures (taken one frame apart, while
// the head is still), and slid against each other in short blocks to find how far each block is shifted. Seen with two eyes, a thing appears further
// RIGHT in the left eye's picture than in the right eye's - the nearer, the bigger the shift - and never the other way. So blocks shifted the right way say
// "paired correctly"; blocks clearly shifted the wrong way say "swapped". Far-away things (shift about 0), blank walls and sky (nothing to match) don't vote.
// Only when SwappedNeeded of the last Remember looks say "swapped" (and few say "correct") does it change Frame lag by one - toward the lower value, so
// the least delay - and logs why; then it waits SettleMs before judging again. Your hand choice of Frame lag still works; this only fixes a swap.
// It can't see being two off (both eyes still get their own side, only a frame late - a slight wobble when you turn); that needs the render-thread hunt.
// Builder picture: closing one eye, then the other, and watching a pencil held up near your face jump - if it jumps the wrong way, your glasses are on backwards.
// M26-1 - also not while exact picture pairing is working (the pictures already carry their true pose; Frame lag isn't used).
// Not while: off, 2D, a menu, the F10 window, the flat view at PC screens (both eyes the same on purpose), or the head turning between the two pictures.
// Cost - CPU: low (twice a second: a tiny submit, then about half a million additions on a pool thread, under a millisecond); GPU: extremely low (a few rows).
internal static unsafe class EyeCheck
{
    const long CheckMs = 500;               // RA01: how often a look is taken
    const long SettleMs = 3000;             // RA01: after a change (or the start of play) - no judging this long
    const int Remember = 8;                 // RA01: the last looks kept
    const int SwappedNeeded = 5;            // RA01: this many of them must say "swapped", and at most one "correct", for a change
    const int Rows = 4;                     // RA01: rows per band, averaged into one line
    const float MaxTurnDeg = 0.3f;          // RA01: the head may turn at most this much between the two pictures (a turn shifts the whole picture)
    const int Block = 48;                   // RA01: pixels per block that is matched
    const int MinVote = 3;                  // RA01: a block must be shifted at least this many pixels to vote
    static readonly float[] BandAt = { 0.55f, 0.7f, 0.85f };   // RA01: where the bands sit, down the picture (lower = nearer ground, bigger shifts)

    static long nextLook, settleUntil;
    static ulong pending;                   // RA01: the strip copy waited for (0 = none)
    static int busy;                        // RA01: 1 while a pool thread is matching
    static readonly int[] verdicts = new int[Remember];  // RA01: +1 correct, -1 swapped, 0 unsure (newest at verdictNext - 1)
    static int verdictNext, looks, correct, swapped, unsure;
    static long nextReport;
    static bool wasActive;

    // RA01: once per headset frame on the OpenXR thread (XrSession, after the pictures are shown): starts a look when one is due, picks up the finished copy.
    // Never throws (any problem: that look is skipped).
    // Cost - CPU: extremely low on most frames (a few compares); see the class comment for a look; GPU: none here.
    public static void Tick()
    {
        try
        {
            long now = Environment.TickCount64;
            bool active = Tuning.FrameLagAuto && !RenderCameraProbe.Working && Tuning.Stereo && !MenuSense.ShowsMenu && !SettingsMenu.Open
                          && !(Tuning.FlatAtDevices && BlackboardProbe.AtDeviceScreen) && CameraHook.Building(System.Diagnostics.Stopwatch.GetTimestamp());
            if (active && !wasActive) { settleUntil = now + SettleMs; Array.Clear(verdicts); }   // RA01: play (re)started: let the pairing settle first
            wasActive = active;
            Report(now);
            if (pending != 0)
            {
                if (!FrameCopy.StripsDone(pending)) return;
                pending = 0;
                if (active && Interlocked.CompareExchange(ref busy, 1, 0) == 0) Task.Run(Match);
                return;
            }
            if (!active || now < nextLook || now < settleUntil || Volatile.Read(ref busy) != 0) return;
            nextLook = now + CheckMs;
            Look();
        }
        catch (Exception e) { Log.Write($"RA01 eye check: skipped a look ({e.Message})"); }
    }

    // RA01: finds the newest left-eye and right-eye pictures grabbed one frame apart with the head still, and asks FrameCopy to read their bands. The eyes are
    // the ones you SEE them with (Swap eyes counted), so the check judges what reaches your eyes.
    // Cost - CPU: extremely low (a 6-mirror snapshot, a few pose lookups, one small submit); GPU: extremely low.
    static void Look()
    {
        Span<long> times = stackalloc long[FrameCopy.Mirrors], stamps = stackalloc long[FrameCopy.Mirrors];
        FrameCopy.Snapshot(times, stamps);
        int seenLeft = Tuning.SwapEyes ? 2 : 1;                                  // RA01: the tag of the pictures your left eye is shown
        int bestL = -1, bestR = -1;
        long bestTime = 0, frame = System.Diagnostics.Stopwatch.Frequency / 50; // RA01: one frame apart = under 20 ms
        for (int i = 0; i < times.Length; i++)
        {
            if (times[i] == 0 || !HeadPose.PoseOf(stamps[i], out Xr.Posef pi, out int ei) || ei == 0) continue;
            for (int j = 0; j < times.Length; j++)
            {
                if (j == i || times[j] == 0 || Math.Abs(times[i] - times[j]) > frame || !HeadPose.PoseOf(stamps[j], out Xr.Posef pj, out int ej) || ej == 0 || ej == ei) continue;
                if (ei != seenLeft || Math.Max(times[i], times[j]) <= bestTime || TurnDeg(pi, pj) > MaxTurnDeg) continue;
                bestL = i; bestR = j; bestTime = Math.Max(times[i], times[j]);
            }
        }
        if (bestL < 0) return;                                                    // RA01: no still pair right now (turning): try again next time
        int height = FrameCopy.Height;
        Span<int> tops = stackalloc int[BandAt.Length];
        for (int k = 0; k < tops.Length; k++) tops[k] = Math.Clamp((int)(height * BandAt[k]), 0, height - Rows);
        pending = FrameCopy.CopyStrips(times[bestL], times[bestR], tops, Rows);
    }

    // RA01: the angle between two head turns, in degrees. Cost - CPU: extremely low (a dot product and an arccosine); GPU: none.
    static float TurnDeg(in Xr.Posef a, in Xr.Posef b)
    {
        float dot = MathF.Abs(a.Orientation.X * b.Orientation.X + a.Orientation.Y * b.Orientation.Y + a.Orientation.Z * b.Orientation.Z + a.Orientation.W * b.Orientation.W);
        return 2f * MathF.Acos(Math.Min(1f, dot)) * (180f / MathF.PI);
    }

    // RA01: on a pool thread: each band of both pictures averaged into one line of brightness, then block by block the shift that matches best. Votes counted,
    // the look's verdict stored, and maybe Frame lag changed (Decide). The read-back memory is not written again until the next look is submitted (after this).
    // Cost - CPU: low (3 bands x about 25 blocks x up to 250 shifts x 48 pixels, about half a million additions, under a millisecond); GPU: none.
    static void Match()
    {
        try
        {
            int width = FrameCopy.StripWidth, pitch = FrameCopy.StripPitch, bandBytes = FrameCopy.StripBandBytes;
            byte* pixels = FrameCopy.StripPixels;
            if (pixels == null || width < 4 * Block) return;
            int range = Math.Max(16, width / 20);                                 // RA01: the biggest shift looked for: a hand 30 cm away at a Quest 3's 50%
            float[] left = new float[width], right = new float[width];
            int good = 0, bad = 0;
            for (int k = 0; k < BandAt.Length; k++)
            {
                Line(pixels + k * bandBytes, pitch, width, left);
                Line(pixels + (FrameCopy.MaxStripBands + k) * bandBytes, pitch, width, right);
                for (int x0 = range; x0 + Block + range <= width; x0 += Block)
                {
                    int shift = BestShift(left, right, x0, range);
                    if (shift >= MinVote) good++;
                    else if (shift <= -MinVote) bad++;
                }
            }
            int verdict = good + bad < 4 ? 0 : good >= 2 * bad ? 1 : bad >= 2 * good ? -1 : 0;
            Decide(verdict, good, bad);
        }
        catch (Exception e) { Log.Write($"RA01 eye check: a match failed ({e.Message})"); }
        finally { Volatile.Write(ref busy, 0); }
    }

    // RA01: one band's rows averaged into a line of brightness (red + 2 green + blue; byte order doesn't matter for this sum).
    // Cost - CPU: extremely low (Rows x width additions); GPU: none.
    static void Line(byte* band, int pitch, int width, float[] line)
    {
        for (int x = 0; x < width; x++)
        {
            float sum = 0;
            for (int r = 0; r < Rows; r++) { byte* p = band + r * pitch + x * 4; sum += p[0] + 2 * p[1] + p[2]; }
            line[x] = sum;
        }
    }

    // RA01: the shift d (pixels) at which right[x - d] matches left[x] best over this block; 0 if the block has too little detail or no clear best match.
    // Positive = the block sits further right in the left eye's picture (what two eyes see). Cost - CPU: low (range x 2 x Block subtractions); GPU: none.
    static int BestShift(float[] left, float[] right, int x0, int range)
    {
        float detail = 0;
        for (int x = x0 + 1; x < x0 + Block; x++) detail += MathF.Abs(left[x] - left[x - 1]);
        if (detail < Block * 40f) return 0;                                       // RA01: a flat wall, sky, darkness: nothing to match by
        float best = float.MaxValue, second = float.MaxValue;
        int bestD = 0;
        for (int d = -range; d <= range; d++)
        {
            float cost = 0;
            for (int x = x0; x < x0 + Block; x++) cost += MathF.Abs(left[x] - right[x - d]);
            if (cost < best) { if (Math.Abs(d - bestD) > 2) second = best; best = cost; bestD = d; }
            else if (cost < second && Math.Abs(d - bestD) > 2) second = cost;
        }
        return best < 0.7f * second ? bestD : 0;                                   // RA01: only a clear winner (a repeating pattern has two)
    }

    // RA01: stores a look's verdict and, if the recent looks clearly say "swapped", moves Frame lag by one (logged). Pool thread; Tuning.FrameLag is a plain int.
    // Cost - CPU: extremely low (a few counts and compares); GPU: none.
    static void Decide(int verdict, int good, int bad)
    {
        lock (verdicts)
        {
            verdicts[verdictNext] = verdict;
            verdictNext = (verdictNext + 1) % Remember;
            looks++;
            if (verdict > 0) correct++; else if (verdict < 0) swapped++; else unsure++;
            int saySwapped = 0, sayCorrect = 0;
            foreach (int v in verdicts) { if (v < 0) saySwapped++; else if (v > 0) sayCorrect++; }
            if (saySwapped < SwappedNeeded || sayCorrect > 1) return;
            int was = Tuning.FrameLag, lag = was > 0 ? was - 1 : was + 1;          // RA01: toward the lower value (less delay); from 0 the only way is up
            Tuning.FrameLag = lag;
            Array.Clear(verdicts);
            settleUntil = Environment.TickCount64 + SettleMs;
            Log.Write($"RA01 eye check: the eyes looked SWAPPED in {saySwapped} of the last {Remember} looks (this one: {bad} blocks swapped, {good} correct) - frame lag {was} -> {lag}");
        }
    }

    // RA01: every 30 s of play, one line: how the looks went. Cost - CPU: extremely low (a compare; a log line every 30 s); GPU: none.
    static void Report(long now)
    {
        if (now < nextReport) return;
        nextReport = now + 30000;
        lock (verdicts)
        {
            if (looks == 0) return;
            Log.Write($"RA01 eye check: {looks} looks in the last 30 s - {correct} correct, {swapped} swapped, {unsure} unsure (frame lag {Tuning.FrameLag})");
            looks = correct = swapped = unsure = 0;
        }
    }
}
