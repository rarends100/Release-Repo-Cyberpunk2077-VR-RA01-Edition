using System.Diagnostics;
using System.Numerics;

namespace RA01.Visuals;

// RA01: M14-1b - PER-HAND CYBERWARE: Mantis Blades and Gorilla Arms pop out on the hand that strikes. The M14-1a recordings showed the game unfolds both arms
// together (blades slide out 12-31 cm, hinges open up to 52 degrees, plates open 36-52 degrees, within 0.1-0.5 s) and, once drawn, keeps them deployed.
// M14-1d - THE GAME'S OWN FLIPBOOK. The user's test: the old way (below) spun the blades round to the bottom of the arm. Why: it morphed straight from a
// folded photo to an open photo, and a morph always takes the SHORTEST way round - but the real blade slides out along the forearm and then swings up and
// OVER the top, a long way round, so the shortcut went the wrong way (like dragging a door's free edge straight across instead of swinging it on its hinge).
// The open photo was also stitched from each part's furthest moment, so the parts didn't line up. Now:
//  - the DRAW is filmed: when a new rig appears (the cyberware was just built onto V) every pose of its parts is kept for DrawMs - the game's whole fold-out
//    on both arms, frame by frame. The draw itself plays exactly as the game shows it;
//  - per side, FULLY OPEN is the one frame where that side's parts are furthest out together (one moment, so they line up);
//  - both arms REST at the draw's first frame - for Mantis Blades that is POSITION 2 (the user's call: drawn and ready, blade upright on the wrist; position 1,
//    fully concealed, is only when holstered, which the game handles). M14-1e: the M14-1d search for position 2 measured how far blade01 SLIDES - but it
//    only turns, so the two arms rested differently. Gone;
//  - each arm has a dimmer along the flipbook: 0 = folded, 1 = fully open. A strike (Actions) flips THAT arm forward to fully open in OpenMs, holds (M14-1g timing),
//    then flips back to its rest in CloseMs - always along the game's own path, blending only between two neighbouring frames, so it can't go the wrong way;
//  - M14-1e - THE POP: the user's test showed the TIP blade staying inside its housing on a strike. The log said why: the draw slides the telescoping tip
//    (blade03-05) out only about 13 cm, while the game's own strikes shoot it out 18-32 cm and open the three hinges (bladeHinge01-03) a further 12-32 degrees -
//    and the flipbook only holds the draw. So these six parts follow their own furthest pose seen (draw or strike), on the same dimmer as the swing: like a
//    spring-loaded tip inside a folding knife, it shoots out with the swing and goes back in with it. A tip only slides (no spin possible), so the left tip
//    uses the right tip's reach mirrored when that went further (the game strikes mostly with the right arm), and both pop the same length;
//  - M14-1g - THE USER'S TIMING (replaces M14-1f, which waited for the game's strike to settle - now our timing rules the blades outright). Each arm has
//    TWO dimmers, like a folding knife with a spring tip inside: the SWING (the blade swinging forward along the flipbook) and the POP (tip and hinges):
//    - a quick strike - a physical swing (that arm) or an RT tap shorter than TapMs (both arms) - is QuickMs in all (game: about 0.41 s; ours is longer so you
//      can see your blades in VR): out in OpenMs, held, back in CloseMs, swing and pop together;
//    - HOLDING RT (both arms): the blades swing forward and stay there as long as you hold, tip still in - the cocked spring. LETTING GO is the game's power
//      attack: the tip punches out and stays out through the lunge, PowerMs in all from the release (the game's lunge: 1.2-1.5 s), then all goes back to
//      position 2;
//  - any block (the physical guard or your own LT) fades to exactly the game's own pose (gameMix), so it looks like the vanilla block.
// The OLD WAY (M14-1c, two photos, per-part furthest pose) stays as the backup: the Dev row "Cyberware strike: old blend", and by itself for a side whose
// draw wasn't filmed (no movement seen on it).
// Runs in VRIK's pose handler after the recorder (so recordings stay the game's own) and before VRIK's arm work. Tuning.PerHandCyberware off = untouched.
// Cost - CPU: low (about 30 small turn and position blends per pose while Mantis Blades or Gorilla Arms are out, four dimmer steps; a 1.5 s copy of 30 parts per pose on each
// draw; nothing otherwise); memory: about 460 KB for the filmed draw; GPU: none.
internal static unsafe class CyberwarePose
{
    const int SlotBytes = 0x30, RotationAt = 0x10, MaxBones = 64, MaxFrames = 256;
    const long QuickMs = 750, PowerMs = 1350, TapMs = 250;           // RA01: M14-1g - a quick strike, a power attack (from the release), the longest RT press that is a tap
    const long OpenMs = 100, CloseMs = 250, DrawMs = 1500, FrameGapTicks = 80_000; // RA01: 8 ms between filmed frames
    static readonly int[] slots = new int[MaxBones];
    static readonly bool[] isLeft = new bool[MaxBones];
    static readonly Vector3[] foldedAt = new Vector3[MaxBones];
    static readonly Quaternion[] foldedTurn = new Quaternion[MaxBones];
    static readonly Vector3[] openAt = new Vector3[MaxBones];       // RA01: M14-1c (old way) - each part's furthest-out pose seen so far
    static readonly Quaternion[] openTurn = new Quaternion[MaxBones];
    static readonly float[] openScore = new float[MaxBones];
    static readonly Vector3[,] drawAt = new Vector3[MaxFrames, MaxBones];       // RA01: M14-1d - the filmed draw
    static readonly Quaternion[,] drawTurn = new Quaternion[MaxFrames, MaxBones];
    static readonly int[] endFrame = new int[2];                     // RA01: per side (0 left, 1 right): the fully open frame; 0 = not filmed
    static readonly byte[] pop = new byte[MaxBones];                 // RA01: M14-1e - 0 swings with the flipbook, 1 hinge (pops to its own furthest), 2 tip (pops, mirrored reach)
    static readonly int[] partner = new int[MaxBones];               // RA01: M14-1e - the same part on the other arm, -1 if none
    static readonly long[] swingUntil = new long[2], popUntil = new long[2]; // RA01: M14-1g - per side: Stopwatch ticks until which the swing / pop is held out
    static readonly float[] swing = new float[2], popOpen = new float[2];     // RA01: M14-1g - per side: the dimmers, 0 rest (position 2), 1 out
    static int boneCount, drawFrames;
    static bool foldedKnown, filming;
    static ulong[]? seenRig;
    static long lastTick, filmUntil, lastFrame, pressedAt;
    static bool triggerHeld;
    static volatile bool blocking;
    static float gameMix;                                            // RA01: 0 our pose, 1 exactly the game's own (while blocking)

    // RA01: Actions: a physical strike with the left (true) or right hand - a quick strike on that arm.
    // Cost - CPU: extremely low (a clock read and two stores); GPU: none.
    public static void Strike(bool left) => Out(left ? 0 : 1, QuickMs);

    // RA01: M14-1g - Actions, every look: is your own right trigger down (with Mantis Blades or Gorilla Arms out)? Down: both arms swing out and stay out.
    // Let go: a tap (shorter than TapMs) is a quick strike on both arms; a hold is the power attack - the tip punches out for PowerMs, then all goes back.
    // Cost - CPU: extremely low (a compare per look, a few stores on a press or release); GPU: none.
    public static void Trigger(bool down)
    {
        if (down == triggerHeld) return;
        triggerHeld = down;
        long now = Stopwatch.GetTimestamp();
        if (down) { pressedAt = now; swingUntil[0] = swingUntil[1] = long.MaxValue; return; }
        long ms = (now - pressedAt) * 1000 / Stopwatch.Frequency >= TapMs ? PowerMs : QuickMs;
        Out(0, ms); Out(1, ms);
    }

    // RA01: M14-1g - an arm's swing and pop held out so the whole strike, out and back in, lasts 'ms'.
    // Cost - CPU: extremely low (a clock read and two stores); GPU: none.
    static void Out(int side, long ms)
    {
        long until = Stopwatch.GetTimestamp() + (ms - CloseMs) * Stopwatch.Frequency / 1000;
        popUntil[side] = until;
        swingUntil[side] = triggerHeld ? long.MaxValue : until;     // RA01: a physical swing while RT is held doesn't end the hold
    }

    // RA01: Actions: a block is held (true) - the physical guard or your own LT - or let go: while it lasts the game's own pose shows on both arms.
    // Cost - CPU: extremely low (one store); GPU: none.
    public static void Block(bool on) => blocking = on;

    // RA01: VRIK: one pose of V (parent-relative bone slots). Learns and films on a new rig, then poses each side by its dimmer (see the class comment).
    // Cost - CPU: low (see the class comment); GPU: none.
    public static void Apply(byte* bones, int count)
    {
        ulong[] rig = VRIK.RigNames;
        if (!ReferenceEquals(seenRig, rig)) { seenRig = rig; Learn(rig, bones, count); }
        if (!foldedKnown) return;
        long now = Stopwatch.GetTimestamp();
        if (filming) { Film(bones, count, now); return; }            // RA01: the draw plays exactly as the game shows it
        if (!Core.Tuning.PerHandCyberware) return;

        float seconds = lastTick == 0 ? 0f : MathF.Min(0.1f, (now - lastTick) / (float)Stopwatch.Frequency);
        lastTick = now;
        bool old = Core.Tuning.OldCyberwareBlend;
        for (int side = 0; side < 2; side++)
        {
            swing[side] = Step(swing[side], now < swingUntil[side] ? 1f : 0f, seconds);
            popOpen[side] = Step(popOpen[side], now < popUntil[side] ? 1f : 0f, seconds);
        }
        gameMix = Step(gameMix, blocking ? 1f : 0f, seconds);

        for (int b = 0; b < boneCount; b++)
        {
            if (slots[b] >= count) continue;
            byte* slot = bones + slots[b] * SlotBytes;
            Vector3* at = (Vector3*)slot;
            Quaternion* turn = (Quaternion*)(slot + RotationAt);
            float score = Away(b, *at, *turn);                       // RA01: the old way keeps learning, from the game's own pose
            if (score > openScore[b]) { openScore[b] = score; openAt[b] = *at; openTurn[b] = *turn; }
            if (gameMix >= 1f) continue;                             // RA01: blocking: the game's own pose, untouched
            int side = isLeft[b] ? 0 : 1;
            float open = !old && pop[b] > 0 ? popOpen[side] : swing[side]; // RA01: M14-1g - the tip and hinges ride the pop, the rest the swing
            Vector3 ourAt;
            Quaternion ourTurn;
            if (!old && pop[b] > 0)                                  // RA01: M14-1e - the pop: straight from rest to its furthest (small turns, slides)
            {
                ourAt = Vector3.Lerp(foldedAt[b], PopAt(b), open);
                ourTurn = Quaternion.Slerp(foldedTurn[b], openTurn[b], open);
            }
            else if (!old && endFrame[side] > 0)
            {
                float p = open * endFrame[side];                     // RA01: where along the flipbook; blend the two neighbouring frames
                int i = Math.Min((int)p, endFrame[side]), j = Math.Min(i + 1, endFrame[side]);
                ourAt = Vector3.Lerp(drawAt[i, b], drawAt[j, b], p - i);
                ourTurn = Quaternion.Slerp(drawTurn[i, b], drawTurn[j, b], p - i);
            }
            else
            {
                ourAt = Vector3.Lerp(foldedAt[b], openAt[b], open);
                ourTurn = Quaternion.Slerp(foldedTurn[b], openTurn[b], open);
            }
            *at = Vector3.Lerp(ourAt, *at, gameMix);
            *turn = Quaternion.Normalize(Quaternion.Slerp(ourTurn, *turn, gameMix));
        }
    }

    // RA01: M14-1d - keeps one frame of the draw (every FrameGapTicks); when DrawMs is up (or the film is full), finds each side's fully open and rest moments.
    // Cost - CPU: low while filming (about 30 copies of 28 bytes per frame), one small pass over the film at the end; GPU: none.
    static void Film(byte* bones, int count, long now)
    {
        if (now >= filmUntil || drawFrames >= MaxFrames) { filming = false; Finish(); return; }
        if (now - lastFrame < FrameGapTicks) return;
        lastFrame = now;
        for (int b = 0; b < boneCount; b++)
        {
            byte* s = bones + Math.Min(slots[b], count - 1) * SlotBytes;
            drawAt[drawFrames, b] = *(Vector3*)s;
            drawTurn[drawFrames, b] = *(Quaternion*)(s + RotationAt);
        }
        drawFrames++;
    }

    // RA01: M14-1d - per side: fully open = the frame where that side's parts are furthest from folded TOGETHER. Both arms then flip from fully open (where the
    // draw left them) back to their rest (M14-1e: the draw's first frame - position 2). Logs what was found.
    // Cost - CPU: extremely low, once per draw (about 190 frames x 30 parts of small sums); GPU: none.
    static void Finish()
    {
        string text = $"RA01 per-hand cyberware: draw filmed, {drawFrames} frames;";
        for (int side = 0; side < 2; side++)
        {
            float best = 0f;
            endFrame[side] = 0;
            for (int f = 1; f < drawFrames; f++)
            {
                float total = 0f;
                for (int b = 0; b < boneCount; b++) if ((isLeft[b] ? 0 : 1) == side && pop[b] == 0) total += Away(b, drawAt[f, b], drawTurn[f, b]);
                if (total > best) { best = total; endFrame[side] = f; }
            }
            int end = endFrame[side];
            text += $" {(side == 0 ? "left" : "right")}: {(end > 0 ? $"fully open at frame {end}" : "no movement seen - old blend for this side")};";
        }
        swing[0] = swing[1] = 1f;                                    // RA01: from where the draw left them, back to rest
        lastTick = 0;
        Core.Log.Write(text + " both rest at the first frame (position 2); tips and hinges pop to their furthest seen");
    }

    // RA01: M14-1e - where a popping part goes: its own furthest position seen - for a tip, the other arm's tip reach mirrored when that went further.
    // Cost - CPU: extremely low (two distances); GPU: none.
    static Vector3 PopAt(int b)
    {
        int p = partner[b];
        if (pop[b] != 2 || p < 0) return openAt[b];
        Vector3 own = openAt[b] - foldedAt[b], other = openAt[p] - foldedAt[p];
        return other.Length() > own.Length() ? foldedAt[b] - other : openAt[b]; // RA01: the arms are mirror images: the other arm's slide, reversed
    }

    // RA01: M14-1c - how far a part's pose is from its folded pose: the turn between them in degrees plus the distance in centimetres.
    // Cost - CPU: extremely low (a dot product, an arc-cosine and a distance); GPU: none.
    static float Away(int b, Vector3 at, Quaternion turn)
    {
        float dot = MathF.Min(1f, MathF.Abs(Quaternion.Dot(foldedTurn[b], turn)));
        return 2f * MathF.Acos(dot) * (180f / MathF.PI) + Vector3.Distance(foldedAt[b], at) * 100f;
    }

    // RA01: moves a dimmer toward its target: opening (up) at the OpenMs pace, closing (down) at the CloseMs pace.
    // Cost - CPU: extremely low (a divide and a clamp); GPU: none.
    static float Step(float value, float target, float seconds) =>
        value < target ? MathF.Min(target, value + seconds * 1000f / OpenMs) : MathF.Max(target, value - seconds * 1000f / CloseMs);

    // RA01: a new rig: finds the cyberware parts in it, keeps this first pose as FOLDED (a draw starts folded) and starts filming the draw.
    // Cost - CPU: extremely low, once per new rig (about 70 name lookups and 30 small copies); GPU: none.
    static void Learn(ulong[] rig, byte* bones, int count)
    {
        boneCount = 0;
        foreach (string part in StrikeRecorder.Parts)
            foreach (string side in new[] { "l_", "r_" })
            {
                int slot = Array.IndexOf(rig, VRIK.NameCode(side + part));
                if (slot < 0 || slot >= count || boneCount >= MaxBones) continue;
                byte* s = bones + slot * SlotBytes;
                slots[boneCount] = slot;
                isLeft[boneCount] = side == "l_";
                pop[boneCount] = part is "blade03_JNT" or "blade04_JNT" or "blade05_JNT" ? (byte)2 : part.StartsWith("bladeHinge") ? (byte)1 : (byte)0;
                partner[boneCount] = -1;
                if (side == "r_" && boneCount > 0 && isLeft[boneCount - 1] && slots[boneCount - 1] == Array.IndexOf(rig, VRIK.NameCode("l_" + part)))
                { partner[boneCount] = boneCount - 1; partner[boneCount - 1] = boneCount; }
                foldedAt[boneCount] = *(Vector3*)s;
                foldedTurn[boneCount] = *(Quaternion*)(s + RotationAt);
                openAt[boneCount] = foldedAt[boneCount]; openTurn[boneCount] = foldedTurn[boneCount]; openScore[boneCount] = 0f;
                boneCount++;
            }
        foldedKnown = boneCount > 0;
        swing[0] = swing[1] = popOpen[0] = popOpen[1] = gameMix = 0f;
        swingUntil[0] = swingUntil[1] = popUntil[0] = popUntil[1] = lastTick = 0;
        triggerHeld = false;
        endFrame[0] = endFrame[1] = 0;
        drawFrames = 0;
        lastFrame = 0;
        filming = foldedKnown;
        filmUntil = Stopwatch.GetTimestamp() + DrawMs * Stopwatch.Frequency / 1000;
        if (foldedKnown) Core.Log.Write($"RA01 per-hand cyberware: folded pose learned for {boneCount} parts of a new rig ({rig.Length} bones) - filming the draw");
    }
}
