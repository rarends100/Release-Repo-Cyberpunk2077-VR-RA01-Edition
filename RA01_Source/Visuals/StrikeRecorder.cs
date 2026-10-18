using System.Numerics;

namespace RA01.Visuals;

// RA01: M14-1a - STRIKE RECORDER (test, log only): films the game's own Mantis Blades and Gorilla Arms strike, frame by frame, so VRIK can later replay it one arm
// at a time (M14-1b: a right-hand strike pops only the right blade or knuckle plates). Like filming a pro's swing before teaching it.
// Actions (MeleeActions) calls Begin when you press the right trigger with that cyberware out; for the next RecordMs, every pose VRIK is handed is read
// BEFORE VRIK changes anything (so it is exactly the game's animation), for the cyberware's own bones only - their names come from the game's rig files
// (WolvenKit exports in the repo, cp2077_wolvenkit_json_data\cyberware): the blade rail, blades, hinges and piston for Mantis Blades; the plates, pistons,
// circle mechanism and muscles for Gorilla Arms - each on both sides (l_ and r_). Each bone's slot is a 48-byte entry: position (+0x0), turn (+0x10).
// M14-1a2 - also records the DRAW: the first pose of a new rig that holds these parts (the game has just built the cyberware onto V) starts a DrawMs
// recording by itself - its first frames are the folded pose (blades in, plates shut), which a strike recording never shows (drawn, they idle deployed).
// When the time is up, a background task writes, per bone that moved (more than MinDeg or MinCm from its first frame): its parent-relative rest, how far it
// went (degrees and centimetres), when it was furthest out, and a curve - how far along it was every CurveMs (0 = rest, 100 = furthest).
// Cost - CPU: low while recording (about 40 bones x 32 bytes copied per pose, for 1.5 s after a press or 2.5 s after a draw), extremely low otherwise (one compare per pose);
// the report is one background pass over at most MaxFrames x 64 bones; GPU: none.
internal static unsafe class StrikeRecorder
{
    const int SlotBytes = 0x30, RotationAt = 0x10, MaxBones = 64, MaxFrames = 256, CurveMs = 33;
    const long RecordMs = 1500, DrawMs = 2500, MinGapTicks = 50_000; // RA01: 5 ms between kept frames (V can be posed twice a frame)
    const float MinDeg = 2f, MinCm = 0.5f;
    internal static readonly string[] Parts = // RA01: M14-1b - CyberwarePose uses the same list
    {
        "wristSuspension01_JNT", "wristSuspension02_JNT", "wristSuspension03_JNT", "wristSuspension04_JNT", "wristSuspension02_rail_JNT", "innerWrist01_JNT",
        "wrist01_GRP", "blade01_JNT", "blade02_JNT", "blade03_JNT", "blade04_JNT", "blade05_JNT", "bladeHinge01_JNT", "bladeHinge02_JNT", "bladeHinge03_JNT",
        "bladePiston01_JNT",
        "plate_up_a", "plate_up_b", "plate_dn_a", "plate_dn_b", "plate_a", "plate_b", "muscle_a", "muscle_b", "circle_mechanism_JNT",
        "piston_wrist_aa_JNT", "piston_wrist_ab_JNT", "piston_wrist_ba_JNT", "piston_wrist_bb_JNT",
        "thumb_piston_a", "index_piston_a", "middle_piston_a", "ring_piston_a", "pinky_piston_a",
    };
    static readonly string[] names = new string[MaxBones];
    static readonly int[] slots = new int[MaxBones];
    static int boneCount;
    static ulong[]? seenRig;
    static readonly Vector3[,] positions = new Vector3[MaxFrames, MaxBones];
    static readonly Quaternion[,] turns = new Quaternion[MaxFrames, MaxBones];
    static readonly long[] times = new long[MaxFrames];
    static int frames;
    static long recordUntil, lastFrame;
    static volatile bool recording, reporting;
    static string what = "";

    // RA01: Actions: the right trigger was just pressed with Mantis Blades or Gorilla Arms out ('kind' goes in the log). Ignored while a recording or report runs.
    // Cost - CPU: extremely low (a few stores); GPU: none.
    public static void Begin(string kind) => Start(kind, RecordMs);

    // RA01: starts a recording of 'ms' milliseconds ('kind' goes in the log), unless one is already running or being reported.
    // Cost - CPU: extremely low (a few stores); GPU: none.
    static void Start(string kind, long ms)
    {
        if (recording || reporting) return;
        what = kind;
        frames = 0;
        lastFrame = 0;
        recordUntil = System.Diagnostics.Stopwatch.GetTimestamp() + ms * System.Diagnostics.Stopwatch.Frequency / 1000;
        recording = true;
    }

    // RA01: VRIK: one pose of V, before VRIK changes it. Keeps the tracked bones while recording; hands the report to a background task when the time is up.
    // Cost - CPU: low while recording (see the class comment); GPU: none.
    public static void OnPose(byte* bones, int count)
    {
        ulong[] rig = VRIK.RigNames;
        if (!ReferenceEquals(seenRig, rig)) { seenRig = rig; Map(rig); if (boneCount > 0) Start($"draw - a new rig of {rig.Length} bones", DrawMs); } // RA01: M14-1a2
        if (!recording) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now > recordUntil || frames >= MaxFrames)
        {
            recording = false;
            reporting = true;
            Task.Run(Report);
            return;
        }
        if (now - lastFrame < MinGapTicks) return;
        lastFrame = now;
        for (int b = 0; b < boneCount; b++)
        {
            if (slots[b] >= count) continue;
            byte* slot = bones + slots[b] * SlotBytes;
            positions[frames, b] = *(Vector3*)slot;
            turns[frames, b] = *(Quaternion*)(slot + RotationAt);
        }
        times[frames++] = now;
    }

    // RA01: finds the tracked parts in V's current rig by name code (both sides). Logs how many were found.
    // Cost - CPU: extremely low, once per rig (about 70 name lookups in a list of up to a thousand); GPU: none.
    static void Map(ulong[] rig)
    {
        boneCount = 0;
        if (recording) recording = false; // RA01: a recording of the old rig's slots is dropped
        foreach (string part in Parts)
            foreach (string side in new[] { "l_", "r_" })
            {
                int slot = Array.IndexOf(rig, VRIK.NameCode(side + part));
                if (slot < 0 || boneCount >= MaxBones) continue;
                names[boneCount] = side + part;
                slots[boneCount++] = slot;
            }
        Core.Log.Write($"RA01 strike recorder: {boneCount} cyberware bones found in V's rig ({rig.Length} bones)");
    }

    // RA01: the report (see the class comment): one line per bone that moved, sorted left side first. Runs on a background task.
    // Cost - CPU: low, once per recording (at most 256 frames x 64 bones of small sums); GPU: none.
    static void Report()
    {
        try
        {
            int n = frames;
            if (n < 2 || boneCount == 0) { Core.Log.Write($"RA01 strike recorder ({what}): nothing recorded ({n} frames, {boneCount} bones)"); return; }
            double ms(int f) => (times[f] - times[0]) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            var text = new System.Text.StringBuilder($"RA01 strike recorder ({what}): {n} frames over {ms(n - 1):F0} ms; bones that moved (rest = first frame, parent-relative):");
            int moved = 0;
            for (int b = 0; b < boneCount; b++)
            {
                Quaternion rest = turns[0, b];
                Vector3 restAt = positions[0, b];
                float[] deg = new float[n], cm = new float[n];
                float maxDeg = 0, maxCm = 0;
                int peak = 0;
                for (int f = 0; f < n; f++)
                {
                    float dot = MathF.Min(1f, MathF.Abs(Quaternion.Dot(rest, turns[f, b])));
                    deg[f] = 2f * MathF.Acos(dot) * (180f / MathF.PI);
                    cm[f] = Vector3.Distance(restAt, positions[f, b]) * 100f;
                    if (deg[f] + cm[f] > maxDeg + maxCm) peak = f;
                    maxDeg = MathF.Max(maxDeg, deg[f]); maxCm = MathF.Max(maxCm, cm[f]);
                }
                if (maxDeg < MinDeg && maxCm < MinCm) continue;
                moved++;
                string curve = "";
                double next = 0;
                for (int f = 0; f < n; f++)
                {
                    if (ms(f) < next) continue;
                    next += CurveMs;
                    float share = (maxDeg >= MinDeg ? deg[f] / maxDeg : cm[f] / maxCm) * 100f;
                    curve += $" {share:F0}";
                }
                text.Append($"{Environment.NewLine}RA01   {names[b],-30} rest pos ({restAt.X * 100:F1}, {restAt.Y * 100:F1}, {restAt.Z * 100:F1}) cm, turn ({rest.X:F3}, {rest.Y:F3}, {rest.Z:F3}, {rest.W:F3}); " +
                            $"furthest {maxDeg:F1} deg / {maxCm:F2} cm at {ms(peak):F0} ms; furthest turn ({turns[peak, b].X:F3}, {turns[peak, b].Y:F3}, {turns[peak, b].Z:F3}, {turns[peak, b].W:F3}); " +
                            $"curve % every {CurveMs} ms:{curve}");
            }
            if (moved == 0) text.Append(" none moved more than 2 degrees or 0.5 cm");
            Core.Log.Write(text.ToString());
        }
        catch (Exception e) { Core.Log.Write($"RA01 strike recorder report failed: {e.Message}"); }
        finally { reporting = false; }
    }
}
