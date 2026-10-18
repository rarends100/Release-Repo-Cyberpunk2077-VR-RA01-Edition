using System.Numerics;
using RA01.Actions;
using RA01.Core;
using static RA01.Core.Win32;

namespace RA01.Visuals;

// RA01: M8-0 - VRIK, step one: the bone map. Before any arm can be bent we need to know which of V's live bone slots is her shoulder, upper arm, forearm and hand
// (both sides), plus head, neck and upper spine, and which bone each one hangs from. Nothing here is written to the game - it only reads and logs.
// How: the game keeps V's rig in memory behind the same "skeleton" value PlayerSkeleton picks her out by. A rig holds a list of bone names - stored not as
// text but as 64-bit name codes made with the standard FNV-1a formula - and a list of parents (one small number per bone: which earlier bone it hangs
// from, -1 for the root). We make the name codes ourselves from the spellings in WolvenKit, search the rig for them like finding parts by barcode, and find
// the parent list by its shape. No slot numbers, offsets or lengths are hard-coded, so a game update that reorders or adds bones is still mapped correctly;
// if either list can't be found, VRIK stays off and the log says which step failed.
// M8-1 proved the two-beam crane solve on one arm (wrist landed 0.0 mm from its aim); M8-2 made both arms follow the controllers. Each elbow's pole
// "string" is tied down, outward and a little behind along her body's own fixed axes (X right, Y forward, Z up) - M8-3's shoulder-line directions swung by up
// to 45 degrees with the first-person animation and were dropped.
// M9-1 - the hand targets now come ready-made from HandTargets (Actions): placed from the finished game camera measured on V's body, so they are already on
// her body and already where the headset shows your hands. The M8 anchor, hand-offset sliders and straight-arm calibration are gone.
// Written: the collarbones' rest position and turn, the upper arm's and forearm's rotations, the hand's turn, one size on each upper arm (M9-6) and the
// turns of the six forearm twist helpers (M9-7) and V's back - Spine to Spine3 - aimed under your head or bent with its lean, and turned with it (M9-8, M11-1, M11-1c), and while physically crouched the hips' position and the legs' and feet's turns (M11-3), and the place and turn of any other bone hung off a forearm - the cyberware arms' sleeve and glove bones (M13-5) - never
// the root or anything else - and only while the bone map is trusted and the VRIK tab's "Arms follow controllers" is on; with it off, or a controller not
// tracked, that arm is left to the game's animation (and its size put back to 1).
// The search runs once per new V skeleton, on a background thread, entirely through ReadProcessMemory (a bad pointer fails one read, never crashes the game).
// Cost - CPU: low for a moment once per load (a few thousand small safe reads, a few milliseconds, off the game's threads), then extremely low (one pointer
// compare per V body per frame, plus both arm solves while "Arms follow controllers" is on - rated on SolveArms); GPU: none.
internal static unsafe class VRIK
{
    // RA01: the bones VRIK needs, spelled exactly as WolvenKit shows them in player_man_fpp_deformations / player_woman_base_deformations (both genders identical)
    static readonly string[] Names =
    {
        "Root", "Spine3", "Neck", "Head",
        "LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand",
        "RightShoulder", "RightArm", "RightForeArm", "RightHand",
    };
    const int HeadBone = 3, LeftShoulder = 4, LeftHand = 7, RightShoulder = 8, RightHand = 11;
    // RA01: M9-7 - the forearm twist helpers (deformation rig): three per arm along the forearm, there only to share the wrist's roll out along the skin. Optional -
    // RA01: if any is missing, the twist fix stays off and everything else still runs. [0..2] left, [3..5] right.
    // RA01: M9-8 - the two spine bones below Spine3 (Names[1]), for the chest turn. Optional like the twist helpers.
    static readonly string[] ChestNames = { "Spine", "Spine1", "Spine2" }; // RA01: M11-1 - Spine added: the whole back is shaped now
    // RA01: M11-3 - the hips and both legs, for matching V's crouch depth to yours (CrouchLegs). Optional like the twist helpers. [0] Hips, [1..3] left thigh/shin/foot, [4..6] right.
    static readonly string[] LegNames = { "Hips", "LeftUpLeg", "LeftLeg", "LeftFoot", "RightUpLeg", "RightLeg", "RightFoot" };
    // RA01: M13-1 - V's weapon hooks: the bones a held weapon hangs from (her animation skeleton has both). Optional; read-only for now.
    static readonly string[] WeaponNames = { "WeaponRight", "WeaponLeft" };
    static readonly string[] TwistNames = { "l_Wrist_0_JNT", "l_Wrist_1_JNT", "l_Wrist_2_JNT", "r_Wrist_0_JNT", "r_Wrist_1_JNT", "r_Wrist_2_JNT" };

    const int SlotBytes = 0x30;          // RA01: each live bone: position (16 bytes), rotation (16), size (16) - same layout PlayerSkeleton checks
    const int ObjectScanBytes = 0x400;   // RA01: how much of each rig object is looked through for list headers
    const int MinListLength = 100;       // RA01: every rig here has 152+ bones; shorter lists are something else
    const int MaxListLength = 4096;
    const float PoleOut = 0.6f, PoleBack = 0.3f; // RA01: M8-2 - the elbow's pole direction: straight down, plus this much outward and this much behind (in V's own measured directions)

    static readonly nint Self = GetCurrentProcess();
    static readonly ulong[] codes = Names.Select(NameCode).ToArray();
    static readonly int[] slot = new int[Names.Length];     // RA01: live slot of each bone in Names, filled by BuildMap
    static readonly int[] parentSlot = new int[Names.Length];
    static nint mappedSkeleton;          // RA01: the skeleton value the map was last built (or failed) for
    static nint searchingSkeleton;       // RA01: the skeleton value a background search is running for right now
    static volatile bool mapReady;       // RA01: set by the background search once slot[] is filled (so the one-off live report can run)
    static volatile bool mapTrusted;     // RA01: the family tree checked out and every slot exists in the live list - M8-1 will only ever write when this is true
    static bool liveReported;
    internal static ulong[] RigNames = Array.Empty<ulong>(); // RA01: M14-1a - V's current rig's bone name codes, slot by slot (StrikeRecorder finds its bones here)
    static short[] parentOf = Array.Empty<short>();   // RA01: M8-1 - the full parent list, so a bone's place on the body can be worked out from the feet up
    const long ReportEveryMs = 5000;    // RA01: M8-3 - how often the arms-following line is logged while the arms follow
    static long nextReportMs;
    const float PalmToWrist = 0.07f;    // RA01: M9-2 - must match HandTargets: your measured span is grip to grip, V's is compared wrist to wrist plus this on each side
    static bool fitReady;               // RA01: M9-2 - V's rest arm lengths and shoulder width, measured once per load (the first frame the arms follow)
    static readonly float[] restLength = new float[4]; // RA01: M9-2 - [0] left upper arm, [1] left forearm, [2] right upper arm, [3] right forearm, metres
    static float shoulderWidth;         // RA01: M9-2 - distance between V's two shoulder joints, metres
    static bool reportSolve = true;      // RA01: M8-2 - log one line of numbers for the first frame the arms follow, after each load
    static float[] restPose = Array.Empty<float>(); // RA01: M9-3 - V's rest pose, 12 floats per bone (position, turn, size), copied from her rig by BuildMap
    static bool restTrusted;             // RA01: M9-3 - the rest pose's arm lengths matched her live ones (MeasureFit), so shoulders and wrists may use it
    static int committedCalibration;     // RA01: M9-3 - the Tuning.ArmCalibrationCount last acted on
    static Quaternion pendingWristL, pendingWristR; // RA01: M9-3 - wrist corrections captured at the T-pose's widest moment, committed only if the calibration succeeds
    static bool pendingWrist;
    static Vector3 eyeSum;               // RA01: M9-3 - head bone positions added up during the "Put camera at V's eyes" second
    static int eyeCount;
    static readonly ulong[] twistCodes = TwistNames.Select(NameCode).ToArray();
    static readonly int[] twistSlot = new int[TwistNames.Length];               // RA01: M9-7 - live slot of each helper (-1 = not in the rig)
    // RA01: M18-4 - the finger joints, 14 a hand, left first: index/middle/ring/pinky joints 1-3, then thumb 1-2 (the M13-16 names, proven in V's rig)
    static readonly string[] FingerNames = Enumerable.Range(0, 2).SelectMany(h => new[] { "Index", "Middle", "Ring", "Pinky" }
        .SelectMany(f => new[] { 1, 2, 3 }.Select(j => $"{(h == 0 ? "Left" : "Right")}Hand{f}{j}")).Concat(new[] { $"{(h == 0 ? "Left" : "Right")}HandThumb1", $"{(h == 0 ? "Left" : "Right")}HandThumb2" })).ToArray();
    static readonly ulong[] fingerCodes = FingerNames.Select(NameCode).ToArray();
    static readonly int[] fingerSlot = new int[28];
    static readonly float[] twistShare = new float[TwistNames.Length];          // RA01: M9-7 - how much of the hand's roll each helper takes (0 at the elbow, 1 at the wrist)
    static readonly Quaternion[] twistRestRel = new Quaternion[TwistNames.Length]; // RA01: M9-7 - each helper's rest turn measured against the rest forearm
    static readonly float[] twistGiven = new float[TwistNames.Length];          // RA01: M9-7 - the roll each helper got last frame, degrees (log only)
    static readonly float[] handRoll = new float[2];                            // RA01: M9-7 - [0] left, [1] right hand roll against its rest pose, degrees (log only)
    static bool twistFound, twistReady;
    // RA01: M13-5 - forearm followers: every other bone hanging straight off a forearm (the cyberware arms' own sleeve and glove bones). See MoveFollowers.
    const float GloveShare = 0.9f;                   // RA01: a follower at least this far along the forearm (0 elbow, 1 wrist) moves fully with the hand
    static int[] followers = Array.Empty<int>();     // RA01: their live slots
    static byte[] followerSide = Array.Empty<byte>(); // RA01: 0 left, 1 right
    static float[] followerBase = Array.Empty<float>(), followerWritten = Array.Empty<float>(); // RA01: 7 floats each (offset x,y,z, turn x,y,z,w): the game's own, and ours
    static short[]? followersFor;                    // RA01: the parent list they were found in (a rebuilt V gets a new one)
    static readonly bool[] followerLogDue = new bool[2];
    static readonly Quaternion[] handBase = new Quaternion[2], handWritten = new Quaternion[2]; // RA01: the game's own hand turn this frame, and the one we wrote
    // RA01: M13-7 - wrist anchors: deeper bones (below a sleeve follower) where a cyberware branch reaches the wrist - see MoveAnchors.
    static int[] anchors = Array.Empty<int>();       // RA01: their live slots
    static int[] anchorFollower = Array.Empty<int>(); // RA01: index (in followers) of the sleeve bone their branch hangs from
    static int[][] anchorPath = Array.Empty<int[]>(); // RA01: the bones between that sleeve bone and the anchor (both left out), top down
    static float[] anchorBase = Array.Empty<float>(), anchorWritten = Array.Empty<float>(); // RA01: 7 floats each, as the followers'
    static readonly bool[] anchorsDue = new bool[2];
    // RA01: M13-7 - READ-ONLY Monowire wire report: both wire brackets, both first wire links, the right wire's second and last links.
    static readonly string[] WireNames = { "l_nanoWire01_jnt", "r_nanoWire01_jnt", "r_nanoWire02_jnt", "l_nanoWire02_jnt" };
    static readonly ulong[] wireCodes = WireNames.Select(NameCode).ToArray();
    static readonly int[] wireSlot = new int[4];
    static long nextWireReportMs;
    // RA01: M13-8 - the wire rule (CaptureWire's idle test; M38-1: LayWire lays the cables)
    const float WireAimMaxDeg = 40f;                // RA01: M13-9 - idle: a wire half leaves its bracket within this angle of the other bracket (M13-9b: 35 -> 40;
                                                    // RA01: the game's idle wire sits at 32-33 degrees, so 35 flickered)...
    const float WireAimLeaveDeg = 60f;              // RA01: M13-9b - ...and once idle, it only stops being idle past this angle (a whip flings it out to 150 degrees)
    const int WireLeaveFrames = 4;                  // RA01: M13-9b - or after this many frames in a row without reaching the other bracket
    const int WireReachMax = 25;                    // RA01: ...and reaches the other bracket's distance within this many links (a whip flings it far out instead)
    // RA01: M13-10 - per wire half, [0] = the right hand's wire (aimed at the left bracket), [1] = the left hand's (aimed at the right bracket)
    static readonly int[][] wireLinks = { Array.Empty<int>(), Array.Empty<int>() };       // RA01: each half's links, first to last (followed down the parent list)
    static int[] rightWire => wireLinks[0];
    static readonly Vector3[] wireEnd = new Vector3[2];              // RA01: where the game had the other bracket, in that half's first link's own frame
    static readonly int[] wireReach = new int[2];                    // RA01: the link where the game's half reaches it
    static readonly Quaternion[] wireBaseTurn = new Quaternion[2], wireWrittenTurn = new Quaternion[2]; // RA01: the first link's turn as the game gave it, and as we wrote it
    static readonly Vector3[][] wireBaseOffsets = { Array.Empty<Vector3>(), Array.Empty<Vector3>() };   // RA01: links 1..reach offsets as the game gave them
    static readonly bool[] wireWasIdle = new bool[2];
    static readonly int[] wireNotIdleFrames = new int[2];
    static readonly ulong[] chestCodes = ChestNames.Select(NameCode).ToArray();
    static readonly ulong[] legCodes = LegNames.Select(NameCode).ToArray();
    static readonly int[] legSlot = new int[7];   // RA01: M11-3 - live slots of LegNames
    static readonly ulong[] weaponCodes = WeaponNames.Select(NameCode).ToArray();
    static readonly int[] weaponSlot = new int[2];  // RA01: M13-1 - live slots of WeaponRight / WeaponLeft (-1 = not in the rig)
    static long nextWeaponReportMs;
    static bool legFound;
    static float standingHingeZ;                  // RA01: M11-3 - how high her lower back (Spine) sits on her body when standing - the most the crouch lift may restore
    static Vector3 hipsBase, hipsWritten;         // RA01: M11-3 - the hips offset the game gave and the one we wrote, so a frame the game skips never lifts twice
    static nint legsFor;                          // RA01: M11-3 - which body hipsWritten belongs to
    static float lastLift, lastKneeL, lastKneeR;  // RA01: M11-3 - for the log
    static long nextLegReportMs;
    const int SpineBones = 4;
    static readonly int[] chestSlot = new int[SpineBones];  // RA01: M11-1 - live slots of Spine, Spine1, Spine2, Spine3, bottom up
    static readonly Quaternion[] chestWritten = new Quaternion[SpineBones], chestBase = new Quaternion[SpineBones]; // RA01: M9-8 - what we wrote last frame and what it was built from
    static bool chestFound;
    static nint chestFor;                // RA01: M9-8 - which body chestWritten belongs to (V can have two), so one body's last write is never mistaken for another's
    static float chestTurn, lastNeck, leanForward, leanSide; // RA01: M9-8, M11-1 - degrees, for the log
    const float MaxLeanDeg = 35f;        // RA01: M11-1 - the furthest V's back bends to follow your head
    const float MaxAimDeg = 45f;         // RA01: M11-1c - the furthest one pass of "Upright spine" swings her back toward your head
    static float aimDeg = float.NaN;     // RA01: M11-1c - the last spine aim swing, degrees (log only; NaN = not aiming)
    const float ChestShare = 1f / 3f;    // RA01: M9-8 - the chest takes a third of the neck angle (a real neck turns about 60-70 degrees, the upper back about 30)
    const float MaxChestDeg = 25f;  // RA01: M9-7 - found: all six helpers are in the rig; ready: their shares were measured from the trusted rest pose
    const float EyeForward = 0.09f, EyeUp = 0.07f; // RA01: M9-3 - where the eyes are from the head bone (the top of the neck), metres along her forward and up

    // RA01: true once V's bone map is found and its family tree checks out - the gate every later write will sit behind.
    // RA01: Cost - CPU: extremely low (one bool read); GPU: none.
    public static bool MapTrusted => mapTrusted;

    // RA01: plugs VRIK into the V-only doorway. Call once, after PlayerSkeleton.Install.
    // Cost - CPU: extremely low (stores one pointer, once); GPU: none.
    public static void Install() => PlayerSkeleton.SetHandler(&OnVPose);

    // RA01: called by PlayerSkeleton for V's bodies only, every frame, on the game's animation threads - so it only compares a pointer, starts the one-off search
    // when V's skeleton changes (a load), and logs V's live bone positions once when the map is ready.
    // Cost - CPU: extremely low (one or two compares per frame; the one-off live report is a few dozen float reads); GPU: none.
    // M20-10 - VRIK tab "VRIK in cutscenes": true while V's scene tier (GameAnchor.SceneTier) is in the hands-off range - 4-5 (first-person and full
    // cutscenes, the default), 3-5 (dialogue scenes too), or never. Then VRIK leaves her body to the game: no arms, fingers, crouch, spine or cyberware folding;
    // the read-only measuring still runs. Logs each hand-over and take-back with the tier.
    // Cost - CPU: extremely low (a volatile read and two compares per V body per frame; a log line on a change); GPU: none.
    static bool HandsToGame()
    {
        int tier = GameAnchor.SceneTier;
        bool off = Tuning.VrikInScenes switch { 0 => tier >= 4, 1 => tier >= 3, _ => false };
        if (off != scenesOff)
        {
            scenesOff = off;
            Log.Write(off ? $"RA01 VRIK: scene tier {tier} - V's body handed to the game's animation" : $"RA01 VRIK: scene tier {tier} - VRIK drives V's body again");
        }
        return off;
    }
    static volatile bool scenesOff;

    static void OnVPose(nint bones, int count)
    {
        nint skeleton = PlayerSkeleton.Current;
        if (skeleton == 0) return;
        if (skeleton != mappedSkeleton)
        {
            if (Interlocked.CompareExchange(ref searchingSkeleton, skeleton, 0) != 0) return; // RA01: a search is already running
            mapReady = false;
            mapTrusted = false;
            reportSolve = true;
            fitReady = false;
            liveReported = false;
            int liveCount = count;
            Task.Run(() => BuildMap(skeleton, liveCount));
            return;
        }
        bool health = HealthDue();                                 // RA01: M45 - the arm health check's once-a-second turn (VRIK log on only)
        if (health) ArmHealth((byte*)bones, count, 0);             // RA01: M45 - the game's own pose, before anything below writes
        bool johnnyDue = mapTrusted && JohnnyLookDue();             // RA01: M75 look
        if (johnnyDue) JohnnyLook((byte*)bones, count, 0);          // RA01: M75 look - Johnny's hands as the game posed them
        if (mapReady) StrikeRecorder.OnPose((byte*)bones, count); // RA01: M14-1a - first, while the pose is still exactly the game's
        bool scene = HandsToGame();                                // RA01: M20-10 - a cutscene: V's body stays exactly as the game animates it
        bool leanOut = mapTrusted && !scene && JohnnyLeaningOut((byte*)bones, count); // RA01: M75 - read from the game's own pose, before the spine and arms move
        if (mapReady && !scene) CyberwarePose.Apply((byte*)bones, count);   // RA01: M14-1b - each arm's cyberware folded until that hand strikes, before the arm work
        if (mapReady && !liveReported) { liveReported = true; ReportLive((byte*)bones, count); }
        if (mapTrusted) MeasureSight((byte*)bones, count);      // RA01: M15-1 - LOG-ONLY: while the pose is still exactly the game's
        if (mapTrusted && Tuning.CameraToEyesDueMs != 0 && Environment.TickCount64 + 1000 > Tuning.CameraToEyesDueMs) MeasureEyes((byte*)bones, count);
        bool wireIdleR = mapTrusted && CaptureWire((byte*)bones, count, 0), wireIdleL = mapTrusted && CaptureWire((byte*)bones, count, 1); // RA01: M13-8, M13-10 - first, while the pose is still exactly the game's
        if (mapTrusted) PublishGamePose((byte*)bones, count); // RA01: M19-1/2 - the game's own head and wrists (on the wheel, when seated), before ours move them (before the legs and spine, so her head is the game's)
        if (mapTrusted && !scene && legFound && restTrusted) CrouchLegs((byte*)bones, count); // RA01: M11-3 - before the spine, so the back is aimed from the lifted hips
        if (mapTrusted && !scene && (Tuning.ChestFollowsHead || Tuning.UprightSpine || Tuning.LeanWithHead || leanOut)) ShapeSpine((byte*)bones, count); // RA01: M9-8, M11-1 - before the arms, so the shoulders they start from sit on the shaped back (M75-2: and Johnny leaning out - his torso turns)
        if (mapTrusted && !scene && Tuning.ArmsFollowControllers && HandTargets.TryGet(out Vector3 head, out Vector3 left, out bool leftOk, out Vector3 right, out bool rightOk, out Quaternion leftTurn, out Quaternion rightTurn))
            SolveArms((byte*)bones, count, head, left, leftOk, right, rightOk, leftTurn, rightTurn);
        weaponInLeft = mapTrusted && !scene && restTrusted && Tuning.ArmsFollowControllers && Tuning.LeftHanded
                       && Core.WeaponProbe.Held is Core.WeaponProbe.Kind.Gun or Core.WeaponProbe.Kind.Melee;   // RA01: M28-1 - left-hand mode with a weapon drawn
        if (weaponInLeft) WeaponToLeftHand((byte*)bones, count);      // RA01: M28-1 - after the arms (both hands are where yours are), before the fingers and the muzzle
        else if (hookMoved) { hookMoved = false; Log.Write("RA01 VRIK left-hand weapon: back to the game's own (right) hand"); }
        if (mapTrusted && !scene && restTrusted) OffHandFingers((byte*)bones, count); // RA01: M18-4 - the off hand opens near the weapon and grips it
        else if (mapTrusted && fitReady) { ResetArmSize((byte*)bones, count, LeftShoulder); ResetArmSize((byte*)bones, count, RightShoulder); } // RA01: M9-6 - arms not following: her own arm size
        if (mapTrusted) WireTracks();                      // RA01: M38-0 - finds the wire's float tracks once per rig (for LayWire)
        if (mapTrusted && !scene) { LayWire((byte*)bones, count, 0, wireIdleR); LayWire((byte*)bones, count, 1, wireIdleL); } // RA01: M38-1 - after the arms: each cable re-threaded to where her (your) other hand really is
        if (mapTrusted) PublishMuzzle((byte*)bones, count); // RA01: M13-47 - after the arms: the muzzle as it sits in her (your) hand this frame
        if (mapTrusted) WeaponReport((byte*)bones, count); // RA01: M13-1 - read-only, after the arms so it sees where her hands really ended up
        if (johnnyDue) JohnnyLook((byte*)bones, count, 1, scene); // RA01: M75 look - and as VRIK leaves them
        if (mapTrusted) WireReport((byte*)bones, count);   // RA01: M13-7 - read-only, only while Monowire is out
        if (health) ArmHealth((byte*)bones, count, 1);     // RA01: M45 - the same frame, as VRIK leaves her arms
    }

    // RA01: M75 LOOK (log only, Rob 2026-10-13): in Never Fade Away's car shootout Johnny leans out of the window and, in right-hand mode, his arms end up
    // crossed. Once a second while you're in Johnny's body (not the training) with a gun out, his hands and both weapon hooks are measured as the game
    // posed them (stage 0) and as VRIK leaves them (stage 1), in his body's axes (x right, y forward, z up), next to both controller targets and how far
    // your neck is turned - so the next step knows which arm the game uses there and how to tell that moment apart. Like photographing the crane arm
    // M75 look 2 - plus both shoulders (the crossing is likely his upper body turned to the window) and whether HIS body is seated in a vehicle.
    // before and after the operator takes over. At most MaxJohnnyLooks lines a session.
    // Cost - CPU: extremely low (eight short walks from the feet up and a log line, once a second, only then); GPU: none.
    static bool JohnnyLookDue()
    {
        long now = Environment.TickCount64;
        if (now < nextJohnnyLookMs || johnnyLooks >= MaxJohnnyLooks || !Core.GameAnchor.InJohnnysBody || Core.GameAnchor.InTrainingSim || Core.WeaponProbe.Held != Core.WeaponProbe.Kind.Gun) return false;
        nextJohnnyLookMs = now + 1000;
        return true;
    }

    // RA01: M75 look - stage 0 keeps the game's own places; stage 1 logs them beside VRIK's. Cost - CPU: extremely low (see JohnnyLookDue); GPU: none.
    static void JohnnyLook(byte* bones, int count, int stage, bool scene = false)
    {
        int[] at = { slot[LeftHand], slot[RightHand], weaponSlot[0], weaponSlot[1], slot[LeftShoulder], slot[RightShoulder] };
        foreach (int s in at) if (s < 0 || s >= count || parentOf.Length < count) return;
        float[] into = stage == 0 ? johnnyBefore : johnnyAfter;
        for (int i = 0; i < 6; i++)
        {
            BodyPlace(bones, at[i], out Vector3 p, out _);
            into[i * 3] = p.X; into[i * 3 + 1] = p.Y; into[i * 3 + 2] = p.Z;
        }
        if (stage == 0) return;
        johnnyLooks++;
        string P(float[] a, int i) => $"({a[i * 3]:F2}, {a[i * 3 + 1]:F2}, {a[i * 3 + 2]:F2})";
        string targets = HandTargets.TryGet(out _, out Vector3 lt, out bool lok, out Vector3 rt, out bool rok, out _, out _)
            ? $"left {(lok ? $"({lt.X:F2}, {lt.Y:F2}, {lt.Z:F2})" : "not tracked")}, right {(rok ? $"({rt.X:F2}, {rt.Y:F2}, {rt.Z:F2})" : "not tracked")}" : "none";
        string neck = HandTargets.TryGetNeckTurn(out float deg) ? $"{deg:F0} deg" : "?";
        string seat = Core.GameAnchor.TryActingVehicle(out nint car, out int carState) ? (car != 0 ? $"in a vehicle (state {carState})" : $"on foot (state {carState})") : "unknown";
        Log.Write($"RA01 VRIK Johnny look (M75): scene tier {Core.GameAnchor.SceneTier}{(scene ? " (handed to the game)" : "")}, left-hand mode {Tuning.LeftHanded}; " +
            $"Johnny {seat}; game's own: left hand {P(johnnyBefore, 0)}, right hand {P(johnnyBefore, 1)}, WeaponRight {P(johnnyBefore, 2)}, WeaponLeft {P(johnnyBefore, 3)}, " +
            $"left shoulder {P(johnnyBefore, 4)}, right shoulder {P(johnnyBefore, 5)}; after VRIK: left hand {P(johnnyAfter, 0)}, right hand {P(johnnyAfter, 1)}, WeaponRight {P(johnnyAfter, 2)}, " +
            $"left shoulder {P(johnnyAfter, 4)}, right shoulder {P(johnnyAfter, 5)}; controller targets {targets}; neck turned {neck}");
    }
    static readonly float[] johnnyBefore = new float[18], johnnyAfter = new float[18];
    static long nextJohnnyLookMs;
    static int johnnyLooks;
    const int MaxJohnnyLooks = 900;

    // RA01: M75 - JOHNNY LEANING OUT OF THE CAR (Never Fade Away's car shootout, right-hand mode): the game turns his body to fire behind the car, his left hand
    // grips the car and the gun arm reaches across his chest, so with your right controller on that arm his arms crossed (Rob 2026-10-13). Detected from
    // the game's own pose: Johnny's body (not the training) seated in a vehicle (GameAnchor.TryActingVehicle), a gun out, and his shoulders lifted out of
    // the seat - about 0.55 m up when sitting, 1.0 m leaning out (M75 look, 2026-10-14) - on above LeanOutOnZ, off below LeanOutOffZ, so it can't flicker.
    // M75-2 (Rob 00:24, replaces M75's arm swap): his TORSO is turned LeanOutTurnDeg (180) round instead (ShapeSpine's chest turn, spread over the four
    // spine bones), so his chest faces the shooting and his back the car; the arms stay on their own controllers, in both right- and left-hand mode.
    // Like turning the crane's cab round to face the load instead of making its arm reach backwards over the roof.
    // Cost - CPU: extremely low (a few reads and, only while Johnny is in a car with a gun, two short walks from the feet up per frame); GPU: none.
    static bool JohnnyLeaningOut(byte* bones, int count)
    {
        bool want = false;
        int ls = slot[LeftShoulder], rs = slot[RightShoulder];
        if (Core.GameAnchor.InJohnnysBody && !Core.GameAnchor.InTrainingSim && Core.WeaponProbe.Held == Core.WeaponProbe.Kind.Gun
            && ls >= 0 && ls < count && rs >= 0 && rs < count && parentOf.Length >= count && Core.GameAnchor.TryActingVehicle(out nint car, out _) && car != 0)
        {
            BodyPlace(bones, ls, out Vector3 a, out _);
            BodyPlace(bones, rs, out Vector3 b, out _);
            float height = (a.Z + b.Z) * 0.5f;
            want = height > (leaningOut ? LeanOutOffZ : LeanOutOnZ);
        }
        if (want == leaningOut) return want;
        leaningOut = want;
        Log.Write(want ? $"RA01 VRIK: Johnny leaning out of the car - his torso turned {LeanOutTurnDeg:F0} deg to face the shooting (M75-2)" : "RA01 VRIK: Johnny back in his seat - torso as the game has it (M75-2)");
        return want;
    }
    const float LeanOutOnZ = 0.80f, LeanOutOffZ = 0.70f, LeanOutTurnDeg = 180f;
    static bool leaningOut;

    // RA01: M45 - ARM HEALTH CHECK (log only, Rob approved 2026-10-11): hunting the stretched / wrung arm that a save load doesn't clear but a game restart does.
    // RA01: Once a second, and only while the VRIK log is switched on (F10 > Log control), V's two arms are measured twice in the same frame: as the game handed
    // RA01: them over (stage 0, before VRIK touches anything) and as VRIK leaves them (stage 1). Builder picture: a tape measure and a protractor on the crane arm
    // RA01: before and after the operator moves it - are the beams still their own length, is every part its own size, is the hand still bolted on straight?
    // RA01: Per arm: the upper arm and forearm bones' own lengths against the fit measured at load (bones never change length when they animate) and their
    // RA01: direction against the rest pose; every arm bone's size (only the upper arm may differ from 1, and only by the arm & hand size); how far the hand bends
    // RA01: off its forearm; each twist helper's roll against its share of the hand's; each forearm follower (cyberware sleeve bones) staying near its forearm.
    // RA01: Nothing is written. Lines: one baseline per stage the first time it runs, one when something first looks wrong (then at most every 5 s while it stays
    // RA01: wrong), one when it is all right again - each with both arms' numbers.
    // RA01: Cost - CPU: extremely low (once a second: about a dozen short walks up the arm and a few dozen float reads; nothing at all with the VRIK log off); GPU: none.
    const long HealthEveryMs = 1000, HealthRepeatMs = 5000;
    const float HealthLengthSlack = 0.01f, HealthSizeSlack = 0.02f, HealthAngleSlackDeg = 5f, HealthMaxBendDeg = 110f, HealthTwistSlackDeg = 60f, HealthFollowerReach = 0.2f;
    static long nextHealthMs;
    static readonly bool[] healthBad = new bool[2], healthStarted = new bool[2];
    static readonly long[] healthLoggedMs = new long[2];

    // RA01: M45 - true for one V body once a second, while the map and fit are ready and the VRIK log is on (two V bodies can arrive at once: only one wins).
    // Cost - CPU: extremely low (a bool read; once a second a clock read and one compare-and-swap); GPU: none.
    static bool HealthDue()
    {
        if (!mapTrusted || !fitReady || !Tuning.LogOn[Log.Vrik]) return false;
        long now = Environment.TickCount64, due = Interlocked.Read(ref nextHealthMs);
        return now >= due && Interlocked.CompareExchange(ref nextHealthMs, now + HealthEveryMs, due) == due;
    }

    // RA01: M45 - one stage of the check (0 = the game's own pose, 1 = after VRIK): both arms measured, one log line when the state calls for it.
    // Cost - CPU: extremely low (two ArmHealthSide calls and at most one log line, once a second); GPU: none.
    static void ArmHealth(byte* bones, int count, int stage)
    {
        if (parentOf.Length < count) return;
        for (int k = 0; k < Names.Length; k++) if (slot[k] >= count) return;
        var text = new System.Text.StringBuilder();
        bool bad = ArmHealthSide(bones, count, LeftShoulder, text) | ArmHealthSide(bones, count, RightShoulder, text);
        long now = Environment.TickCount64;
        string what = stage == 0 ? "game's own pose, before VRIK" : "after VRIK";
        if (bad && (!healthBad[stage] || now - healthLoggedMs[stage] >= HealthRepeatMs)) { healthLoggedMs[stage] = now; Log.Write($"RA01 VRIK arm health ({what}): PROBLEM -{text}"); }
        else if (!bad && healthBad[stage]) Log.Write($"RA01 VRIK arm health ({what}): all right again -{text}");
        else if (!healthStarted[stage]) Log.Write($"RA01 VRIK arm health ({what}): check running, once a second while the VRIK log is on - first reading{(bad ? " has a PROBLEM" : " all right")} -{text}");
        healthStarted[stage] = true;
        healthBad[stage] = bad;
    }

    // RA01: M45 - one arm's measurements, appended to 'text' (problems after "<-"); true if anything is out of bounds. See ArmHealth.
    // Cost - CPU: extremely low (one BodyPlace per twist helper, a few quaternion products, a pass over the followers); GPU: none.
    static bool ArmHealthSide(byte* bones, int count, int shoulder, System.Text.StringBuilder text)
    {
        int side = shoulder == LeftShoulder ? 0 : 1;
        float* pc = (float*)(bones + slot[shoulder] * SlotBytes), pa = (float*)(bones + slot[shoulder + 1] * SlotBytes);
        float* pf = (float*)(bones + slot[shoulder + 2] * SlotBytes), ph = (float*)(bones + slot[shoulder + 3] * SlotBytes);
        var problems = new System.Text.StringBuilder();

        // RA01: the beams: each bone's offset from its parent is the length of the bone before it
        float upper = Offset(pf).Length(), fore = Offset(ph).Length();
        if (MathF.Abs(upper - restLength[side * 2]) > HealthLengthSlack) problems.Append($" upper arm {upper:F3} m (should be {restLength[side * 2]:F3});");
        if (MathF.Abs(fore - restLength[side * 2 + 1]) > HealthLengthSlack) problems.Append($" forearm {fore:F3} m (should be {restLength[side * 2 + 1]:F3});");
        if (restTrusted)
            fixed (float* rest = restPose)
            {
                float upperOff = AngleDeg(Offset(pf), Offset(rest + slot[shoulder + 2] * 12)), foreOff = AngleDeg(Offset(ph), Offset(rest + slot[shoulder + 3] * 12));
                if (upperOff > HealthAngleSlackDeg) problems.Append($" elbow moved {upperOff:F0} deg off the upper arm's line;");
                if (foreOff > HealthAngleSlackDeg) problems.Append($" wrist moved {foreOff:F0} deg off the forearm's line;");
            }

        // RA01: the sizes: only the upper arm may differ from 1, by the arm & hand size; every size must be the same in all three directions
        float allowed = ArmSize(restLength[0] + restLength[1] + restLength[2] + restLength[3]);
        if (OddSize(pc, 1f)) problems.Append($" collarbone size {pc[8]:F2}/{pc[9]:F2}/{pc[10]:F2};");
        if (OddSize(pa, allowed)) problems.Append($" upper arm size {pa[8]:F2}/{pa[9]:F2}/{pa[10]:F2} (arm & hand size {allowed:F2});");
        if (OddSize(pf, 1f)) problems.Append($" forearm size {pf[8]:F2}/{pf[9]:F2}/{pf[10]:F2};");
        if (OddSize(ph, 1f)) problems.Append($" hand size {ph[8]:F2}/{ph[9]:F2}/{ph[10]:F2};");

        // RA01: the hand on its forearm: split its turn away from the rest pose into roll (screwdriver turn about the forearm) and bend (the hinge)
        Vector3 axis = fore > 1e-4f ? Offset(ph) / fore : Vector3.UnitX;
        float roll = float.NaN, bend = float.NaN, twistOff = 0f;
        if (restTrusted)
        {
            SplitTurn(Quaternion.Normalize(Turn(ph) * Quaternion.Conjugate(RestTurn(slot[shoulder + 3]))), axis, out roll, out bend);
            if (bend > HealthMaxBendDeg) problems.Append($" hand bent {bend:F0} deg off the forearm;");
            if (twistReady)
            {
                BodyPlace(bones, slot[shoulder + 2], out _, out Quaternion foreTurn);
                for (int t = side * 3; t < side * 3 + 3; t++)
                {
                    if (twistSlot[t] < 0 || twistSlot[t] >= count) continue;
                    float* pt = (float*)(bones + twistSlot[t] * SlotBytes);
                    BodyPlace(bones, twistSlot[t], out _, out Quaternion helperTurn);
                    SplitTurn(Quaternion.Normalize(Quaternion.Conjugate(foreTurn) * helperTurn * Quaternion.Conjugate(twistRestRel[t])), axis, out float helperRoll, out _);
                    float off = MathF.Abs(WrapDeg(helperRoll - roll * twistShare[t]));
                    twistOff = MathF.Max(twistOff, off);
                    if (off > HealthTwistSlackDeg) problems.Append($" {TwistNames[t]} rolled {helperRoll:F0} deg (its share of the hand's is {roll * twistShare[t]:F0});");
                    if (OddSize(pt, 1f)) problems.Append($" {TwistNames[t]} size {pt[8]:F2};");
                }
            }
        }

        // RA01: the forearm followers (cyberware sleeve bones): each should stay near its forearm and keep its own size
        int followersSeen = 0;
        if (ReferenceEquals(followersFor, parentOf))
            for (int k = 0; k < followers.Length; k++)
            {
                if (followerSide[k] != side || followers[k] >= count) continue;
                followersSeen++;
                float* p = (float*)(bones + followers[k] * SlotBytes);
                Vector3 off = Offset(p);
                float along = Vector3.Dot(off, axis), away = (off - axis * along).Length();
                if (away > HealthFollowerReach || along < -HealthFollowerReach || along > fore + HealthFollowerReach) problems.Append($" follower slot {followers[k]} {away:F2} m off the forearm, {along:F2} m along;");
                if (OddSize(p, 1f)) problems.Append($" follower slot {followers[k]} size {p[8]:F2};");
            }

        text.Append($" {(side == 0 ? "left" : "right")}: upper {upper:F3} fore {fore:F3} m, sizes {pc[8]:F2}/{pa[8]:F2}/{pf[8]:F2}/{ph[8]:F2}, hand roll {roll:F0} bend {bend:F0} deg, " +
                    $"twist helpers off by up to {twistOff:F0} deg, {followersSeen} followers{(problems.Length > 0 ? $" <-{problems}" : "")};");
        return problems.Length > 0;
    }

    // RA01: M45 - a bone's size is odd if it isn't the same in all three directions, or is neither 1 nor 'allowed'. Cost - CPU: extremely low (a few compares); GPU: none.
    static bool OddSize(float* p, float allowed) =>
        MathF.Abs(p[8] - p[9]) > HealthSizeSlack || MathF.Abs(p[8] - p[10]) > HealthSizeSlack || (MathF.Abs(p[8] - 1f) > HealthSizeSlack && MathF.Abs(p[8] - allowed) > HealthSizeSlack);

    // RA01: M45 - splits a turn into its roll about 'axis' (the screwdriver part, -180..180) and the rest (the hinge bend, 0..180), in degrees - the same split as
    // ApplyTwist. Cost - CPU: extremely low (an atan2, an acos and one quaternion product); GPU: none.
    static void SplitTurn(Quaternion d, Vector3 axis, out float rollDeg, out float bendDeg)
    {
        if (d.W < 0) d = new Quaternion(-d.X, -d.Y, -d.Z, -d.W);
        float along = Vector3.Dot(new Vector3(d.X, d.Y, d.Z), axis);
        rollDeg = 2f * MathF.Atan2(along, d.W) * 180f / MathF.PI;
        Quaternion twist = new Quaternion(axis * along, d.W);
        twist = twist.LengthSquared() > 1e-12f ? Quaternion.Normalize(twist) : Quaternion.Identity;
        Quaternion swing = d * Quaternion.Conjugate(twist);
        bendDeg = 2f * MathF.Acos(Math.Clamp(MathF.Abs(swing.W), 0f, 1f)) * 180f / MathF.PI;
    }

    // RA01: M45 - the angle between two directions in degrees (0 if either is too short), and an angle folded into -180..180. Cost - CPU: extremely low; GPU: none.
    static float AngleDeg(Vector3 a, Vector3 b) =>
        a.LengthSquared() < 1e-8f || b.LengthSquared() < 1e-8f ? 0f : MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f)) * 180f / MathF.PI;
    static float WrapDeg(float a) => a - 360f * MathF.Round(a / 360f);

    // RA01: M11-1 - shapes V's back each frame (Spine, Spine1, Spine2, Spine3, bottom up), replacing M9-8's chest-only turn:
    //  1. start: the game's pose (or what we built from it last frame, so turns never pile up if the game skips refreshing a bone);
    //  2. chest turn (M9-8, "Chest follows head"): a third of your neck angle, capped at 25 degrees, spread over the four bones;
    //  3. lean (VRIK tab "Lean with your head", only while "Upright spine" is off): your head's lean from standing straight (HandTargets.TryGetLean) turned
    //     into angles - her back is a crane boom hinged at her lower back, tipped until its top reaches the point above it: angle = atan2(lean, boom height),
    //     capped at MaxLeanDeg, spread over the four bones;
    //  4. aim (VRIK tab "Upright spine", M11-1c/d): AimSpine replaces steps 1 and 3 with a straight back swung under your head - see there. Aiming at your
    //     real head already leans with it.
    // Each bone gets its turn about her body's own axes (right, forward, up) at its own joint, seen from its parent: local = (parent^-1 x turn x parent) x start.
    // M11-1/M11-1b's "copy her standing pose" upright is gone: the first-person animation walks her hips well behind the camera, so a standing-pose back
    // put her shoulders 10-20 cm behind your head (arms stretched past full length, chest pulled forward) and swaying with every hip step.
    // Cost - CPU: extremely low (a dozen short walks from the feet up and a few quaternion products per V body per frame); GPU: none.
    static void ShapeSpine(byte* bones, int count)
    {
        if (!chestFound || parentOf.Length < count) return;
        if (!HandTargets.TryGetNeckTurn(out float neck)) neck = 0f;
        lastNeck = neck;
        chestTurn = Tuning.ChestFollowsHead ? Math.Clamp(neck * ChestShare, -MaxChestDeg, MaxChestDeg) : 0f;
        Vector3 leanAxis = Vector3.UnitZ;                          // RA01: M75-4 - Johnny leaning out: the turn is about HIS SPINE's own line, not straight up -
        if (leaningOut && slot[2] >= 0 && slot[2] < count)         // RA01: his upper body leans out of the window, and turning it about "up" swung the leaned chest
        {                                                          // RA01: over to the car's other side, a metre from your hands ("arms feel far shorter", Rob 00:37)
            BodyPlace(bones, chestSlot[0], out Vector3 low, out _);
            BodyPlace(bones, slot[2], out Vector3 high, out _);
            Vector3 line = high - low;
            if (line.LengthSquared() > 0.01f) leanAxis = Vector3.Normalize(line);
        }
        bool upright = Tuning.UprightSpine && restTrusted;
        leanForward = leanSide = 0f;
        if (!upright && Tuning.LeanWithHead && HandTargets.TryGetLean(out float right, out float forward))
        {
            BodyPlace(bones, chestSlot[0], out Vector3 hinge, out _);
            float boom = MathF.Max(CameraBodyProbe.GameSpot.Z + Tuning.CameraUpCm / 100f - hinge.Z, 0.3f); // RA01: from her lower back up to where your head sits standing
            leanForward = Math.Clamp(MathF.Atan2(forward, boom) * (180f / MathF.PI), -MaxLeanDeg, MaxLeanDeg);
            leanSide = Math.Clamp(MathF.Atan2(right, boom) * (180f / MathF.PI), -MaxLeanDeg, MaxLeanDeg);
        }
        const float ToRadians = MathF.PI / 180f / SpineBones;
        // RA01: her body's axes: x right, y forward, z up. Forward lean tips the top toward +y (a turn about -x), side lean toward +x (a turn about +y), the chest turn is about up.
        Quaternion each = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, chestTurn * ToRadians)
                        * (leaningOut ? Quaternion.CreateFromAxisAngle(leanAxis, LeanOutTurnDeg * ToRadians) : Quaternion.Identity) // RA01: M75-2/4 - his chest turned round to face the shooting
                        * Quaternion.CreateFromAxisAngle(Vector3.UnitY, leanSide * ToRadians)
                        * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -leanForward * ToRadians);
        if (upright) { aimDeg = AimSpine(bones, count, each); return; }
        aimDeg = float.NaN;
        for (int k = 0; k < SpineBones; k++)
        {
            int b = chestSlot[k];
            if (b >= count) return;
            float* p = (float*)(bones + b * SlotBytes);
            Quaternion live = Turn(p);
            Quaternion from = (nint)bones == chestFor && live == chestWritten[k] ? chestBase[k] : live;
            BodyPlace(bones, parentOf[b], out _, out Quaternion parentTurn);
            Quaternion turned = Quaternion.Normalize(Quaternion.Conjugate(parentTurn) * each * parentTurn * from);
            WriteTurn(p, turned);
            chestWritten[k] = turned; chestBase[k] = from;
        }
        chestFor = (nint)bones;
    }

    // RA01: M11-1c/M11-1d - "Upright spine": her back is a straight mast, hinged at her lower back (Spine), with a cable from its top (the base of her neck) to your head.
    //  1. the mast: her standing-pose back and neck (Spine, Spine1, Spine2, Spine3, Neck and the bone above it) welded into one rigid piece - every bone takes
    //     its standing-pose turn on her body (RestPlace), so the game's weapon S-bend and forward-tipped neck are gone, not just tilted (M11-1c's mistake);
    //  2. the anchor: your head's spot on her body (HandTargets.TryGetHeadSpot - your real head with "Lean with your head", else where it sits standing straight),
    //     dropped to her head bone (EyeForward/EyeUp, the same offsets "Put camera at V's eyes" uses) and then down her standing-pose neck;
    //  3. the swing: the mast's line from hinge to neck base (standing pose) turned onto the line from her live hinge to the anchor (TurnBetween, the same tool
    //     the arms use), capped at MaxAimDeg so a strange reading can never fold her in half. One rigid piece turned once lands exactly - no second pass.
    // The hips keep walking as animated; they only carry the hinge, never the mast's angle, so the stride's sway no longer reaches her shoulders.
    // The chest turn (only the twist part of "each" - the lean is already in the anchor) is stacked up the four spine bones as before; the neck keeps the full twist.
    // Each bone: local = parent^-1 x (twist^k x swing x standing turn), with parent its live, already-written parent. chestWritten is updated so switching
    // Upright spine off starts cleanly from the game's pose. Returns the swing, degrees (log only).
    // Cost - CPU: extremely low (six bones, each two short walks from the feet up and three quaternion products, per V body per frame); GPU: none.
    static float AimSpine(byte* bones, int count, Quaternion each)
    {
        if (!HandTargets.TryGetHeadSpot(Tuning.LeanWithHead, out Vector3 eyes)) return float.NaN;
        int neckTop = parentSlot[HeadBone];
        if (neckTop < 0 || slot[2] >= count || neckTop >= count) return float.NaN;
        RestPlace(chestSlot[0], out Vector3 restHinge, out _);
        RestPlace(slot[2], out Vector3 restNeck, out _);
        RestPlace(slot[HeadBone], out Vector3 restHead, out _);
        Vector3 anchor = eyes - new Vector3(0, EyeForward, EyeUp) - (restHead - restNeck); // RA01: where the base of her neck belongs
        BodyPlace(bones, chestSlot[0], out Vector3 hinge, out _);
        Vector3 want = anchor - hinge, mast = restNeck - restHinge;
        if (want.Length() < 0.1f || mast.Length() < 0.1f) return float.NaN; // RA01: a nonsense reading - leave the back to the game this frame
        Quaternion swing = TurnBetween(Vector3.Normalize(mast), Vector3.Normalize(want));
        float deg = 2f * MathF.Acos(Math.Clamp(MathF.Abs(swing.W), 0f, 1f)) * (180f / MathF.PI);
        if (deg > MaxAimDeg) swing = Quaternion.Slerp(Quaternion.Identity, swing, MaxAimDeg / deg);
        Quaternion twist = Quaternion.Identity;
        for (int k = 0; k < SpineBones + 2; k++)
        {
            int b = k < SpineBones ? chestSlot[k] : k == SpineBones ? slot[2] : neckTop;
            if (b >= count) return deg;
            if (k < SpineBones) twist = Quaternion.Normalize(each * twist);
            float* p = (float*)(bones + b * SlotBytes);
            RestPlace(b, out _, out Quaternion standing);
            BodyPlace(bones, parentOf[b], out _, out Quaternion parentTurn);
            Quaternion turned = Quaternion.Normalize(Quaternion.Conjugate(parentTurn) * twist * swing * standing);
            WriteTurn(p, turned);
            if (k < SpineBones) { chestWritten[k] = turned; chestBase[k] = turned; }
        }
        chestFor = (nint)bones;
        return deg;
    }

    // RA01: M11-3 - V's crouch matched to yours. While physical crouch has V crouched (Actions.PhysicalCrouch.Crouched), the game's crouch pose sinks her to its
    // own fixed depth, below your view (which stays at your real height). So:
    //  1. mark the feet: where each ankle sits on her body now, and which way each foot turns - the anchors that must not move;
    //  2. the lift: her back is the upright spine's rigid mast (standing-pose length, hip hinge to neck base). For its top to reach your head's anchor (the same
    //     point AimSpine aims at), the hinge must sit one mast-length below it, less what the mast leans sideways: lift = anchor height - sqrt(mast^2 - lean^2)
    //     - hinge height. Never below 0, never above her standing hinge height (remembered while standing) - she can't rise past standing or float;
    //  3. raise the hips by the lift (everything above rides along);
    //  4. bend each leg back down to its marked ankle with the arms' two-beam crane solve (law of cosines: thigh and shin are the beams, the hip-to-ankle distance
    //     sets the knee angle), the knee's pole string pulled forward so knees bend the natural way;
    //  5. put each foot back to its marked turn, so the soles stay flat.
    // Standing (or physical crouch off), nothing is written and the game's own pose stands. The hips' offset is remembered (hipsBase/hipsWritten) so a frame the
    // game doesn't refresh is never lifted twice.
    // Cost - CPU: extremely low (a handful of short walks from the feet up and two small crane solves, only while physically crouched); GPU: none.
    static void CrouchLegs(byte* bones, int count)
    {
        foreach (int b in legSlot) if (b >= count) return;
        if (parentOf.Length < count) return;
        BodyPlace(bones, chestSlot[0], out Vector3 hinge, out _);
        if (!Actions.PhysicalCrouch.Crouched) { if (hinge.Z > standingHingeZ && hinge.Z < 1.6f) standingHingeZ = hinge.Z; lastLift = 0; return; }
        if (standingHingeZ <= 0 || !HandTargets.TryGetHeadSpot(Tuning.LeanWithHead, out Vector3 eyes)) return;

        // RA01: step 1 - the feet, before anything moves
        BodyPlace(bones, legSlot[3], out Vector3 footL, out Quaternion footTurnL);
        BodyPlace(bones, legSlot[6], out Vector3 footR, out Quaternion footTurnR);

        // RA01: step 2 - the lift
        RestPlace(chestSlot[0], out Vector3 restHinge, out _);
        RestPlace(slot[2], out Vector3 restNeck, out _);
        RestPlace(slot[HeadBone], out Vector3 restHead, out _);
        Vector3 anchor = eyes - new Vector3(0, EyeForward, EyeUp) - (restHead - restNeck);
        float mast = Vector3.Distance(restNeck, restHinge);
        float leanX = anchor.X - hinge.X, leanY = anchor.Y - hinge.Y;
        float upright = MathF.Sqrt(MathF.Max(mast * mast - leanX * leanX - leanY * leanY, 0f));
        float lift = Math.Clamp(anchor.Z - upright - hinge.Z, 0f, MathF.Max(standingHingeZ - hinge.Z, 0f));
        lastLift = lift;
        if (lift < 0.005f) return;

        // RA01: step 3 - raise the hips: the lift, turned into the hips' parent's own axes
        float* ph = (float*)(bones + legSlot[0] * SlotBytes);
        Vector3 live = Offset(ph);
        Vector3 basis = (nint)bones == legsFor && live == hipsWritten ? hipsBase : live;
        BodyPlace(bones, parentOf[legSlot[0]], out _, out Quaternion hipsParent, out float parentScale);
        Vector3 raised = basis + Vector3.Transform(new Vector3(0, 0, lift), Quaternion.Conjugate(hipsParent)) / MathF.Max(parentScale, 0.01f);
        ph[0] = raised.X; ph[1] = raised.Y; ph[2] = raised.Z;
        hipsBase = basis; hipsWritten = raised; legsFor = (nint)bones;

        // RA01: steps 4 and 5 - each leg back down to its ankle, each foot back to its turn
        lastKneeL = SolveLeg(bones, 1, footL, footTurnL, -1f);
        lastKneeR = SolveLeg(bones, 4, footR, footTurnR, 1f);

        long now = Environment.TickCount64;
        if (now >= nextLegReportMs)
        {
            nextLegReportMs = now + ReportEveryMs;
            Log.Write($"RA01 VRIK crouch: lifted V {lift * 100:F0} cm to meet your height (most allowed {(standingHingeZ - hinge.Z) * 100:F0} cm); knees bent {lastKneeL:F0} / {lastKneeR:F0} deg (left / right)");
        }
    }

    // RA01: M11-3 - one leg's two-beam crane solve (the same steps as SolveArm): thigh joint s, knee e, ankle w; the ankle is pulled back to 'target', the knee bends
    // toward the pole (forward, a little outward on its own side), then the foot is given back its marked turn on her body. Returns the knee's bend in degrees.
    // Cost - CPU: extremely low (three short walks from the feet up, a few square roots and quaternion products); GPU: none.
    static float SolveLeg(byte* bones, int first, Vector3 target, Quaternion footTurn, float side)
    {
        float* pu = (float*)(bones + legSlot[first] * SlotBytes);
        float* pl = (float*)(bones + legSlot[first + 1] * SlotBytes);
        float* pf = (float*)(bones + legSlot[first + 2] * SlotBytes);
        BodyPlace(bones, parentOf[legSlot[first]], out Vector3 hipPos, out Quaternion hipTurn);
        Quaternion upTurn = hipTurn * Turn(pu);
        Vector3 s = hipPos + Vector3.Transform(Offset(pu), hipTurn);
        Quaternion lowTurn = upTurn * Turn(pl);
        Vector3 e0 = s + Vector3.Transform(Offset(pl), upTurn);
        Vector3 w0 = e0 + Vector3.Transform(Offset(pf), lowTurn);

        float a = Vector3.Distance(s, e0), b = Vector3.Distance(e0, w0);
        Vector3 toTarget = target - s;
        float d = toTarget.Length();
        if (a < 0.05f || b < 0.05f || d < 0.01f) return 0;
        Vector3 dir = toTarget / d;
        d = Math.Clamp(d, MathF.Abs(a - b) + 0.001f, a + b - 0.001f);
        float cosHip = Math.Clamp((a * a + d * d - b * b) / (2 * a * d), -1f, 1f);
        float sinHip = MathF.Sqrt(1 - cosHip * cosHip);
        Vector3 pole = Vector3.UnitY + Vector3.UnitX * (0.15f * side);
        Vector3 bend = pole - dir * Vector3.Dot(pole, dir);
        if (bend.LengthSquared() < 1e-6f) bend = (e0 - s) - dir * Vector3.Dot(e0 - s, dir);
        if (bend.LengthSquared() < 1e-8f) return 0;
        bend = Vector3.Normalize(bend);
        Vector3 e = s + dir * (a * cosHip) + bend * (a * sinHip);
        Vector3 w = s + dir * d;

        Quaternion swingUp = TurnBetween(Vector3.Normalize(e0 - s), Vector3.Normalize(e - s));
        Quaternion newUpTurn = Quaternion.Normalize(swingUp * upTurn);
        Vector3 lowNow = Vector3.Transform(Vector3.Normalize(w0 - e0), swingUp);
        Quaternion swingLow = TurnBetween(lowNow, Vector3.Normalize(w - e));
        Quaternion newLowTurn = Quaternion.Normalize(swingLow * swingUp * lowTurn);
        WriteTurn(pu, Quaternion.Normalize(Quaternion.Conjugate(hipTurn) * newUpTurn));
        WriteTurn(pl, Quaternion.Normalize(Quaternion.Conjugate(newUpTurn) * newLowTurn));
        WriteTurn(pf, Quaternion.Normalize(Quaternion.Conjugate(newLowTurn) * footTurn)); // RA01: step 5 - the foot keeps its turn on her body
        float knee = Vector3.Dot(Vector3.Normalize(e - s), Vector3.Normalize(w - e));
        return MathF.Acos(Math.Clamp(knee, -1f, 1f)) * (180f / MathF.PI);
    }

    // RA01: M15-1 - GUN CALIBRATION TEST (log only). Aiming down the sights, the game's OWN animation lines up whatever sight is fitted with its own camera's centre
    // line - the way a gun is zeroed. VRIK hasn't moved anything yet here, so this is that game pose. Once LT has been held SettleMs: the muzzle bone's +Y
    // (the line bullets use now) is compared with the game camera's forward line, measured in the GUN's own axes - how far the barrel points left/right
    // and up/down of the true sight line - plus how far below the sight line the muzzle sits (the sight height). One line a second while aiming, with
    // the gun's name and its skeleton's bone count (the same model = the same rig). Nothing is changed.
    // Cost - CPU: extremely low (one walk up to WeaponRight, a few turns; a log line once a second, only while aiming a gun); GPU: none.
    static void MeasureSight(byte* bones, int count)
    {
        long now = Environment.TickCount64;
        bool aiming = Core.WeaponProbe.Held == Core.WeaponProbe.Kind.Gun && Core.ControllerMapping.LeftTriggerSent;
        if (!aiming) { aimSince = 0; return; }
        if (aimSince == 0) aimSince = now;
        if (now - aimSince < SettleMs || now < nextSightLog) return;
        string key = Core.GunCalibration.CurrentKey();
        if (!Core.GunCalibration.Needed(key)) return;                                  // RA01: M15-2 - a calibrated gun is never measured again
        int hook = weaponSlot[0];
        if (hook < 0 || hook >= count || parentOf.Length < count || !Core.WeaponProbe.TryGetMuzzle(out Vector3 onGun, out Quaternion gunTurn, out int gunBones, out _)
            || !Core.CameraBodyProbe.GameLook(out Vector3 camSpot, out Quaternion camTurn)) return;
        nextSightLog = now + 250;                                    // RA01: M15-2 - four samples a second while measuring
        BodyPlace(bones, hook, out Vector3 hookPlace, out Quaternion hookTurn);
        Vector3 muzzle = hookPlace + Vector3.Transform(onGun, hookTurn);
        Quaternion muzzleTurn = Quaternion.Normalize(hookTurn * gunTurn);
        Vector3 sight = Vector3.Transform(Vector3.UnitY, camTurn);                      // RA01: the game camera's centre line, on V's body
        Vector3 inGun = Vector3.Transform(sight, Quaternion.Conjugate(muzzleTurn));     // RA01: ... in the muzzle bone's own axes (+Y = where bullets go now)
        float right = MathF.Atan2(inGun.X, inGun.Y) * (180f / MathF.PI);
        float up = MathF.Atan2(inGun.Z, MathF.Sqrt(inGun.X * inGun.X + inGun.Y * inGun.Y)) * (180f / MathF.PI);
        Vector3 off = muzzle - camSpot;
        Vector3 across = off - sight * Vector3.Dot(off, sight);                          // RA01: the muzzle's offset from the sight line, square to it
        float barrelVsSight = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Transform(Vector3.UnitY, muzzleTurn), sight), -1f, 1f)) * (180f / MathF.PI);
        string scope = Core.GameAnchor.TryScope(out bool hasScope, out Vector4 scopeAt)
            ? (hasScope ? $"scope fitted, offset ({scopeAt.X * 100:F1}, {scopeAt.Y * 100:F1}, {scopeAt.Z * 100:F1}) cm" : "no scope (iron sights)") : "scope unreadable";
        Core.GunCalibration.Measure(key, Vector3.Transform(-across, Quaternion.Conjugate(muzzleTurn)), Vector3.Dot(off, sight), barrelVsSight); // RA01: M15-2 - muzzle to sight line, gun axes; M16-A2 - and how far ahead of the eye
        Log.Write($"RA01 gun calibration (measuring): {Core.WeaponProbe.HeldName} (gun rig {gunBones} bones, {scope}) - the sight line is {right:F2} deg right, {up:F2} deg up of the barrel bone " +
                  $"({barrelVsSight:F2} deg apart); muzzle {across.Length() * 100:F1} cm from the sight line ({Vector3.Dot(across, Vector3.Transform(Vector3.UnitX, camTurn)) * 100:F1} cm right, " +
                  $"{Vector3.Dot(across, Vector3.Transform(Vector3.UnitZ, camTurn)) * 100:F1} cm up), {Vector3.Dot(off, sight) * 100:F0} cm ahead of the game camera");
    }
    const long SettleMs = 600;                                   // RA01: M15-1 - the game's aim-down-sights animation has settled by then
    static long aimSince, nextSightLog;

    // RA01: M19-1/2 - where the game's own animation put V's head and two wrists this frame, on her body (seated: the wrists are on the real steering wheel's
    // rim, or the bike's grips), to Core's mailbox (VehicleState.PublishGamePose) - read before anything of ours moves them.
    // Cost - CPU: extremely low (three walks from the feet, about 10 links each, and a short lock, once per frame); GPU: none.
    static void PublishGamePose(byte* bones, int count)
    {
        int h = slot[HeadBone], l = slot[LeftHand], r = slot[RightHand];
        if (h < 0 || l < 0 || r < 0 || h >= count || l >= count || r >= count || parentOf.Length < count) return;
        BodyPlace(bones, h, out Vector3 head, out _);
        BodyPlace(bones, l, out Vector3 left, out _);
        BodyPlace(bones, r, out Vector3 right, out _);
        Core.VehicleState.PublishGamePose(head, left, right);
    }

    // RA01: M28-1 - LEFT-HAND WEAPONS (VR Controls tab "Left-hand mode": guns and melee weapons). The game only animates weapons in V's RIGHT hand: every gun and blade
    // hangs from one hook bone, WeaponRight, under her right hand. So each frame, after the arms are posed, that hook is moved into her LEFT hand:
    //  1. how the weapon sits in her right hand this frame (the game's grip, with its recoil and reload motion): the hook measured from the right hand;
    //  2. that grip MIRRORED across her centre line - a right glove checked in a mirror becomes a left glove - so the barrel still points ahead, the top
    //     stays up, and it sits in the left palm as a left-handed shooter holds it (a turn, not a flipped copy: the gun itself is never inside-out);
    //     the rig's left hand may not be an exact mirror of the right in its own axes, so a fix measured once from her rest pose squares that up;
    //  3. placed on her left hand, and written back into the hook as an offset and turn from its parent bone.
    // The weapon follows its hook, so it appears in her left hand; shots and the laser follow the muzzle wherever it is (PublishMuzzle runs after this).
    // If the game skips refreshing the hook one frame, the grip it last gave is used again, so the move never stacks on itself.
    // Builder picture: unbolting a tool holder from the right side of a workbench and bolting it on the left - same holder, mirrored mounting.
    // Cost - CPU: extremely low (four short walks from the feet up, a few quaternion products and one bone write per frame, only with a weapon in the left hand); GPU: none.
    static void WeaponToLeftHand(byte* bones, int count)
    {
        int w = weaponSlot[0], r = slot[RightHand], l = slot[LeftHand];
        if (w < 0 || w >= count || r < 0 || r >= count || l < 0 || l >= count || parentOf.Length < count) return;
        int parent = parentOf[w];
        if (parent < 0 || parent >= count) return;
        if (!mirrorKnown)
        {
            RestPlace(r, out _, out Quaternion rightRest);
            RestPlace(l, out _, out Quaternion leftRest);
            Quaternion fixNow = Quaternion.Normalize(Quaternion.Conjugate(Quaternion.Conjugate(Mirror(rightRest)) * leftRest)); // RA01: how far the left hand's axes are from a true mirror
            mirrorFix[0] = fixNow.X; mirrorFix[1] = fixNow.Y; mirrorFix[2] = fixNow.Z; mirrorFix[3] = fixNow.W;
            mirrorKnown = true;
            Log.Write($"RA01 VRIK left-hand weapon: the rig's hands are {2f * MathF.Acos(Math.Clamp(MathF.Abs(fixNow.W), 0f, 1f)) * (180f / MathF.PI):F0} deg from a true mirror (squared up)");
        }
        float* p = (float*)(bones + w * SlotBytes);
        bool skipped = hookMoved && hookWritten[0] == p[0] && hookWritten[1] == p[1] && hookWritten[2] == p[2] && hookWritten[3] == p[4] && hookWritten[4] == p[5] && hookWritten[5] == p[6] && hookWritten[6] == p[7];
        if (skipped) { p[0] = hookGame[0]; p[1] = hookGame[1]; p[2] = hookGame[2]; p[4] = hookGame[3]; p[5] = hookGame[4]; p[6] = hookGame[5]; p[7] = hookGame[6]; } // RA01: the game's last grip, not ours
        else { hookGame[0] = p[0]; hookGame[1] = p[1]; hookGame[2] = p[2]; hookGame[3] = p[4]; hookGame[4] = p[5]; hookGame[5] = p[6]; hookGame[6] = p[7]; }

        BodyPlace(bones, r, out Vector3 rPos, out Quaternion rTurn, out float rScale);   // RA01: 1. the grip in the right hand
        BodyPlace(bones, w, out Vector3 gPos, out Quaternion gTurn);
        Quaternion undoR = Quaternion.Conjugate(rTurn);
        Vector3 inRight = Vector3.Transform(gPos - rPos, undoR) / MathF.Max(rScale, 1e-3f);
        Quaternion turnInRight = Quaternion.Normalize(undoR * gTurn);
        Quaternion fix = new(mirrorFix[0], mirrorFix[1], mirrorFix[2], mirrorFix[3]);
        Vector3 inLeft = Vector3.Transform(new Vector3(-inRight.X, inRight.Y, inRight.Z), fix); // RA01: 2. mirrored, squared up to the left hand's axes
        Quaternion turnInLeft = Quaternion.Normalize(fix * Mirror(turnInRight));
        BodyPlace(bones, l, out Vector3 lPos, out Quaternion lTurn, out float lScale);   // RA01: 3. on the left hand
        Vector3 wantPos = lPos + Vector3.Transform(inLeft * lScale, lTurn);
        Quaternion wantTurn = Quaternion.Normalize(lTurn * turnInLeft);
        BodyPlace(bones, parent, out Vector3 pPos, out Quaternion pTurn, out float pScale);
        Quaternion undoP = Quaternion.Conjugate(pTurn);
        Vector3 local = Vector3.Transform(wantPos - pPos, undoP) / MathF.Max(pScale, 1e-3f);
        Quaternion localTurn = Quaternion.Normalize(undoP * wantTurn);
        if (!(local.LengthSquared() < 25f) || float.IsNaN(localTurn.W)) return;          // RA01: a bad reading (NaN, or metres away) - leave the game's hook
        p[0] = local.X; p[1] = local.Y; p[2] = local.Z;
        WriteTurn(p, localTurn);
        hookWritten[0] = p[0]; hookWritten[1] = p[1]; hookWritten[2] = p[2]; hookWritten[3] = p[4]; hookWritten[4] = p[5]; hookWritten[5] = p[6]; hookWritten[6] = p[7];
        if (!hookMoved) { hookMoved = true; Log.Write($"RA01 VRIK left-hand weapon: {Core.WeaponProbe.HeldName} moved to V's left hand ({inRight.Length() * 100:F1} cm from the hand, as in her right)"); }
    }
    static volatile bool weaponInLeft;                  // RA01: M28-1 - this frame the weapon is in her left hand (her left fingers grip, her right relax)
    static bool hookMoved, mirrorKnown;
    static readonly float[] mirrorFix = { 0f, 0f, 0f, 1f }; // RA01: the rest-pose squaring-up turn (x, y, z, w) - an array, not a stored Quaternion (the ILC quirk)
    static readonly float[] hookGame = new float[7], hookWritten = new float[7]; // RA01: the hook's offset (x, y, z) and turn (x, y, z, w) as the game gave it, and as we wrote it - arrays (the ILC quirk)

    // RA01: M28-1 - a turn seen in a mirror standing along V's centre line (her body's X = right flips): same pin mirrored, opposite way round.
    // Cost - CPU: extremely low (two sign flips); GPU: none.
    static Quaternion Mirror(Quaternion q) => new(q.X, -q.Y, -q.Z, q.W);

    // RA01: M13-47 - hands the gun's muzzle, as it sits on V's body this frame, to Core's mailbox (WeaponProbe.PublishMuzzleOnBody) for Actions' aiming.
    // The same tool-tip-from-the-belt-hook sum as WeaponReport below: muzzle on V = the hook's place + the hook's turn applied to the muzzle's place
    // on the gun. The hook is always WeaponRight (the game hangs every weapon there); in left-hand mode WeaponToLeftHand has already moved it into her left hand.
    // Cost - CPU: extremely low (one walk from the feet to the hand, about 10 links, and a short lock, once per frame while a gun is animated); GPU: none.
    static void PublishMuzzle(byte* bones, int count)
    {
        int hook = weaponSlot[0];                                    // RA01: M28-1 - always WeaponRight: in left-hand mode VRIK moves that hook itself into her left hand
        if (hook < 0 || hook >= count || parentOf.Length < count || !WeaponProbe.TryGetMuzzle(out Vector3 onGun, out Quaternion gunTurn, out _, out _)) return;
        BodyPlace(bones, hook, out Vector3 hookPlace, out Quaternion hookTurn);
        WeaponProbe.PublishMuzzleOnBody(hookPlace + Vector3.Transform(onGun, hookTurn), Quaternion.Normalize(hookTurn * gunTurn));
        int l = fingerSlot[3], r = fingerSlot[17];                   // RA01: M27-6f - LeftHandMiddle1 / RightHandMiddle1: the knuckle where the fingers close round a held thing
        if (l < 0 || l >= count || r < 0 || r >= count) return;
        int lw = slot[LeftHand], rw = slot[RightHand];
        if (lw < 0 || lw >= count || rw < 0 || rw >= count) return;
        BodyPlace(bones, l, out Vector3 lGrip, out _);
        BodyPlace(bones, r, out Vector3 rGrip, out _);
        BodyPlace(bones, lw, out Vector3 lWrist, out _);
        BodyPlace(bones, rw, out Vector3 rWrist, out _);
        WeaponProbe.PublishGripsOnBody(lGrip, rGrip, lWrist, rWrist);  // RA01: M27-6f - the same body axes as the muzzle, so a carried magazine can sit in her DRAWN hand; M27-6m - and her wrists
    }

    // RA01: M13-1 - READ-ONLY weapon probe report, every ReportEveryMs: where V's two weapon hooks sit on her body next to her hands, and - if a gun is being animated
    // (Core.WeaponProbe) - its muzzle hung off WeaponRight: muzzle place = WeaponRight's place + WeaponRight's turn applied to the muzzle's place on the gun,
    // like measuring a tool's tip from the belt hook it hangs on. The barrel line is logged as the direction from WeaponRight to the muzzle, plus the muzzle bone's
    // own three axes on her body (one of them is the barrel), next to the right controller's target, so the next step knows which way "forward" is.
    // Cost - CPU: extremely low (four short walks from the feet up and a log line, once every 5 s); GPU: none.
    static void WeaponReport(byte* bones, int count)
    {
        long now = Environment.TickCount64;
        if (now < nextWeaponReportMs) return;
        nextWeaponReportMs = now + (WeaponProbe.Held == WeaponProbe.Kind.Gun ? 1000 : ReportEveryMs); // RA01: M13-2 - every second while a gun is out
        if (weaponSlot[0] < 0 || weaponSlot[0] >= count || parentOf.Length < count) return;
        BodyPlace(bones, weaponSlot[0], out Vector3 wr, out Quaternion wrTurn);
        BodyPlace(bones, slot[RightHand], out Vector3 rh, out _);
        string left = "";
        if (weaponSlot[1] >= 0 && weaponSlot[1] < count)
        {
            BodyPlace(bones, weaponSlot[1], out Vector3 wl, out _);
            BodyPlace(bones, slot[LeftHand], out Vector3 lh, out _);
            left = $"; WeaponLeft at ({wl.X:F3}, {wl.Y:F3}, {wl.Z:F3}), {Vector3.Distance(wl, lh) * 100:F0} cm from her left hand";
        }
        string gun = "no gun animated";
        if (WeaponProbe.TryGetMuzzle(out Vector3 mPos, out Quaternion mTurn, out int gunBones, out nint gunSkeleton))
        {
            Vector3 muzzle = wr + Vector3.Transform(mPos, wrTurn);
            Quaternion onBody = Quaternion.Normalize(wrTurn * mTurn);
            Vector3 line = muzzle - wr;
            Vector3 ax = Vector3.Transform(Vector3.UnitX, onBody), ay = Vector3.Transform(Vector3.UnitY, onBody), az = Vector3.Transform(Vector3.UnitZ, onBody);
            Vector3 dir = line.LengthSquared() > 1e-6f ? Vector3.Normalize(line) : Vector3.Zero;
            gun = $"gun 0x{gunSkeleton:X} ({gunBones} bones): muzzle on the gun ({mPos.X:F3}, {mPos.Y:F3}, {mPos.Z:F3}) -> on V ({muzzle.X:F3}, {muzzle.Y:F3}, {muzzle.Z:F3}), {line.Length() * 100:F0} cm from WeaponRight along ({dir.X:F2}, {dir.Y:F2}, {dir.Z:F2}); " +
                  $"muzzle axes on V: x ({ax.X:F2}, {ax.Y:F2}, {ax.Z:F2}) y ({ay.X:F2}, {ay.Y:F2}, {ay.Z:F2}) z ({az.X:F2}, {az.Y:F2}, {az.Z:F2})";
        }
        string target = HandTargets.TryGet(out _, out _, out _, out Vector3 rt, out bool rightOk, out _, out _) && rightOk ? $"({rt.X:F3}, {rt.Y:F3}, {rt.Z:F3})" : "not tracked";
        Log.Write($"RA01 VRIK weapon probe: WeaponRight at ({wr.X:F3}, {wr.Y:F3}, {wr.Z:F3}), {Vector3.Distance(wr, rh) * 100:F0} cm from her right hand{left}; right controller target {target}; {gun}");
    }

    // RA01: M9-6 - puts one upper arm's size back to 1, in case the game keeps a size we wrote while the arms were following.
    // Cost - CPU: extremely low (a compare and three float writes); GPU: none.
    static void ResetArmSize(byte* bones, int count, int shoulder)
    {
        if (slot[shoulder + 1] >= count) return;
        float* p = (float*)(bones + slot[shoulder + 1] * SlotBytes);
        p[8] = 1f; p[9] = 1f; p[10] = 1f;
    }

    // RA01: M9-6 - how big V's arms and hands are drawn (1 = her own). Auto: the size that makes her wrist-to-wrist span equal yours - her shoulder width stays hers,
    // only the two arms stretch or shrink to make up the difference (like fitting longer beams to the same crane base). Manual: the VRIK tab's percentage.
    // Cost - CPU: extremely low (a subtract and a divide); GPU: none.
    static float ArmSize(float armsTotal)
    {
        if (!Tuning.ArmSizeAuto) return Math.Clamp(Tuning.ArmSizePercent, Tuning.MinArmSizePercent, Tuning.MaxArmSizePercent) / 100f;
        if (Tuning.UserArmSpan <= 0 || armsTotal < 0.5f) return 1f;
        return Math.Clamp((Tuning.UserArmSpan - 2 * PalmToWrist - shoulderWidth) / armsTotal, Tuning.MinArmSizePercent / 100f, Tuning.MaxArmSizePercent / 100f);
    }

    // RA01: M9-1 - bends each arm onto its target (already on V's body, from HandTargets). The elbow's pole "string" is tied down, outward and a little behind along
    // her body's fixed axes (X right, Y forward, Z up). Logs one line of numbers after each load, then every ReportEveryMs.
    // M9-3 - in order, each frame:
    //  1. both collarbones get their REST turn AND (M9-6) their rest POSITION (VRIK tab "Rest-pose shoulders"). The first-person animation slides them in toward
    //     the middle of her chest (joints 0.16 m apart against 0.36 m standing, sometimes on her centre line and 25 cm behind the camera), which pinched the
    //     chest skin into a point; fixing only the turn was like squaring a bracket that's still bolted in the wrong holes. Her spine still animates;
    //  2. M9-6 - one size on each upper arm (ArmSize), which the game carries on to the forearm and hand. Nothing else is resized: M9-4's whole-body size (on her
    //     root) came apart because the engine does not carry a root size through every bone (her camera rose while her head bone sank to 1.08 m);
    //  3. your reach is scaled about the centre of your head by her (sized) wrist span against yours, so her arm is straight exactly when yours is;
    //  4. each arm is bent onto its target, and her hand bone is turned to your controller's turn times that hand's fixed correction (captured in the T-pose).
    // Cost - CPU: extremely low (two arm solves per V body per frame, each rated on SolveArm, plus two quaternion products per wrist); GPU: none.
    static void SolveArms(byte* bones, int count, Vector3 head, Vector3 leftTarget, bool leftOk, Vector3 rightTarget, bool rightOk, Quaternion leftTurn, Quaternion rightTurn)
    {
        for (int k = 0; k < Names.Length; k++) if (slot[k] >= count) return;
        if (parentOf.Length < count) return;
        if (!fitReady) MeasureFit(bones);
        float armsTotal = restLength[0] + restLength[1] + restLength[2] + restLength[3];
        if (Tuning.ArmCalibrationCount != committedCalibration) CommitCalibration(armsTotal);

        if (restTrusted && Tuning.RestShoulders) // RA01: step 1 - rest-pose collarbones, position and turn
        {
            RestShoulder((float*)(bones + slot[LeftShoulder] * SlotBytes), slot[LeftShoulder]);
            RestShoulder((float*)(bones + slot[RightShoulder] * SlotBytes), slot[RightShoulder]);
        }

        float armSize = ArmSize(armsTotal); // RA01: step 2 - written by SolveArm onto each upper arm

        float scale = 1f; // RA01: step 3
        if (Tuning.UserArmSpan > 0 && armsTotal > 0.5f)
            scale = Math.Clamp((shoulderWidth + armSize * armsTotal) / (Tuning.UserArmSpan - 2 * PalmToWrist), 0.6f, 1.4f);
        if (scale != 1f) // RA01: M19-3 - not a hand glued to the steering wheel: its target is exactly on the rim
        {
            if (!PhysicalSteering.PinnedHand(true)) leftTarget = head + (leftTarget - head) * scale;
            if (!PhysicalSteering.PinnedHand(false)) rightTarget = head + (rightTarget - head) * scale;
        }

        Vector3 bodyRight = leaningOut ? -Vector3.UnitX : Vector3.UnitX, bodyForward = leaningOut ? -Vector3.UnitY : Vector3.UnitY, up = Vector3.UnitZ; // RA01: M75-3 - Johnny leaning
        // RA01: out: his torso is turned 180 (M75-2), so the elbows' pole strings turn with it - "outward and a little behind" from HIS turned chest, not from the hips -
        // RA01: else each elbow was pulled across his front and the shoulders wrung round (Rob 00:36)
        string? note = null;
        if (rightOk)
        {
            note = SolveArm(bones, RightShoulder, armSize, rightTarget, -up + bodyRight * PoleOut - bodyForward * PoleBack, out Vector3 e, out Vector3 w, out Quaternion fore);
            Wrist(bones, RightShoulder, rightTurn, fore, e, w, new Quaternion(Tuning.WristRX, Tuning.WristRY, Tuning.WristRZ, Tuning.WristRW), ref pendingWristR);
        }
        if (leftOk)
        {
            SolveArm(bones, LeftShoulder, armSize, leftTarget, -up - bodyRight * PoleOut - bodyForward * PoleBack, out Vector3 e, out Vector3 w, out Quaternion fore);
            Wrist(bones, LeftShoulder, leftTurn, fore, e, w, new Quaternion(Tuning.WristLX, Tuning.WristLY, Tuning.WristLZ, Tuning.WristLW), ref pendingWristL);
        }
        if (HandTargets.WidestNow && restTrusted && leftOk && rightOk) pendingWrist = true;

        long now = Environment.TickCount64;
        if (reportSolve || now >= nextReportMs)
        {
            reportSolve = false;
            nextReportMs = now + ReportEveryMs;
            Log.Write($"RA01 VRIK arms following: head centre ({head.X:F3}, {head.Y:F3}, {head.Z:F3}), reach x{scale:F2} (your span {Tuning.UserArmSpan:F2} m), arm & hand size {armSize * 100:F0}%{(Tuning.ArmSizeAuto ? " auto" : "")}, rest shoulders {(restTrusted && Tuning.RestShoulders ? "on" : "off")}, wrists {(Tuning.WristRW != 0 || Tuning.WristRX != 0 ? "on" : "not calibrated")}; " +
                      $"right target {(rightOk ? $"({rightTarget.X:F3}, {rightTarget.Y:F3}, {rightTarget.Z:F3})" : "not tracked")}, left target {(leftOk ? $"({leftTarget.X:F3}, {leftTarget.Y:F3}, {leftTarget.Z:F3})" : "not tracked")}; {note}; " +
                      (twistReady && Tuning.ForearmTwist ? $"forearm twist: left hand roll {handRoll[0]:F0} deg -> {twistGiven[0]:F0}/{twistGiven[1]:F0}/{twistGiven[2]:F0}, right hand roll {handRoll[1]:F0} deg -> {twistGiven[3]:F0}/{twistGiven[4]:F0}/{twistGiven[5]:F0}" : $"forearm twist {(Tuning.ForearmTwist ? "not ready" : "off")}") +
                      $"; neck {lastNeck:F0} deg, chest {(Tuning.ChestFollowsHead && chestFound ? $"{chestTurn:F0} deg" : "off")}, spine {(Tuning.UprightSpine && restTrusted ? (float.IsNaN(aimDeg) ? "upright (no head reading)" : $"aimed at your head, swung {aimDeg:F0} deg") : "game's")}, lean {(!Tuning.LeanWithHead ? "off" : Tuning.UprightSpine && restTrusted ? "by the aim" : $"{leanForward:F0} deg forward, {leanSide:F0} deg right")}");
        }
    }

    // RA01: M9-3 - one wrist. With a correction (captured in the T-pose), her hand bone's turn on her body = your controller's turn on her body, then the correction;
    // stored relative to her (just bent) forearm. Without one, the hand keeps the game's angle.
    // While the T-pose window finds a new widest span, a fresh correction is worked out: the hand as it hangs in her rest pose (a T-pose drawing, palm down),
    // swung by the smallest turn that takes her rest forearm's direction onto her forearm's direction now - "her hand, straight along her arm, palm down" - with
    // your controller's turn undone. It is only kept if the calibration then succeeds (CommitCalibration).
    // M9-7 - then shares the hand's roll out along the forearm's twist helpers (ApplyTwist, VRIK tab "Forearm twist").
    // Cost - CPU: extremely low (a few quaternion products, plus ApplyTwist; two rest-pose walks only during the 2-second T-pose window); GPU: none.
    static void Wrist(byte* bones, int shoulder, Quaternion controller, Quaternion foreTurn, Vector3 elbow, Vector3 wrist, Quaternion correction, ref Quaternion pending)
    {
        if (HandTargets.WidestNow && restTrusted && foreTurn != Quaternion.Identity)
        {
            RestPlace(slot[shoulder + 2], out Vector3 restElbow, out _);
            RestPlace(slot[shoulder + 3], out Vector3 restWrist, out Quaternion restHand);
            Vector3 restDir = restWrist - restElbow, nowDir = wrist - elbow;
            if (restDir.LengthSquared() > 1e-6f && nowDir.LengthSquared() > 1e-6f)
            {
                Quaternion straight = Quaternion.Normalize(TurnBetween(Vector3.Normalize(restDir), Vector3.Normalize(nowDir)) * restHand);
                pending = Quaternion.Normalize(Quaternion.Conjugate(controller) * straight);
            }
        }
        if (correction.LengthSquared() < 0.5f || foreTurn == Quaternion.Identity) return;
        Quaternion hand = Quaternion.Normalize(controller * Quaternion.Normalize(correction));
        Quaternion handLocal = Quaternion.Normalize(Quaternion.Conjugate(foreTurn) * hand);
        int side = shoulder == LeftShoulder ? 0 : 1;
        float* ph = (float*)(bones + slot[shoulder + 3] * SlotBytes);
        Quaternion gameHand = Turn(ph);
        if (gameHand != handWritten[side]) handBase[side] = Quaternion.Normalize(gameHand); // RA01: M13-5 - the game's own turn (not last frame's write)
        WriteTurn(ph, handLocal);
        handWritten[side] = handLocal;
        if (twistReady && Tuning.ForearmTwist) ApplyTwist(bones, shoulder, foreTurn, handLocal); // RA01: M9-7
        MoveFollowers(bones, shoulder, Quaternion.Normalize(handLocal * Quaternion.Conjugate(handBase[side]))); // RA01: M13-5
    }

    // RA01: M9-3 - a T-pose calibration just succeeded: keep the wrist corrections captured at its widest moment (your span itself is already saved by HandTargets
    // and turned into the arm size and reach scale every frame), and put the camera at her eyes again.
    // Cost - CPU: extremely low (a divide and a few stores, once per calibration); GPU: none.
    static void CommitCalibration(float armsTotal)
    {
        committedCalibration = Tuning.ArmCalibrationCount;
        float armSize = ArmSize(armsTotal);
        float hers = shoulderWidth + armSize * armsTotal, yours = Tuning.UserArmSpan - 2 * PalmToWrist;
        Tuning.CameraToEyesDueMs = Environment.TickCount64 + 1500; // RA01: M9-4 - half a second to settle, then one second of measuring
        if (pendingWrist)
        {
            Tuning.WristLX = pendingWristL.X; Tuning.WristLY = pendingWristL.Y; Tuning.WristLZ = pendingWristL.Z; Tuning.WristLW = pendingWristL.W;
            Tuning.WristRX = pendingWristR.X; Tuning.WristRY = pendingWristR.Y; Tuning.WristRZ = pendingWristR.Z; Tuning.WristRW = pendingWristR.W;
        }
        Tuning.ArmCalibrationResult = $"done - span {Tuning.UserArmSpan:F2} m, arms & hands {armSize * 100:F0}%, reach x{Math.Clamp(hers / Math.Max(yours, 0.5f), 0.6f, 1.4f):F2}, camera to eyes next, wrists {(pendingWrist ? "lined up" : "NOT captured (rest pose missing)")}";
        Log.Write($"RA01 VRIK calibration: {Tuning.ArmCalibrationResult} (V's wrist span {hers:F3} m, yours {yours:F3} m)");
        pendingWrist = false;
    }

    // RA01: M9-3 - "Put camera at V's eyes": for one second, add up where her head bone is on her body; then her eyes are that average plus EyeForward/EyeUp, and the
    // camera shift that moves the game's own camera spot (from Core) there becomes the two camera sliders. Averaging stops the animation's head bob moving the camera.
    // Cost - CPU: extremely low (one short walk from the feet up per frame, only during that second); GPU: none.
    static void MeasureEyes(byte* bones, int count)
    {
        if (slot[HeadBone] >= count || parentOf.Length < count) return;
        BodyPlace(bones, slot[HeadBone], out Vector3 headBone, out _, out _);
        eyeSum += headBone; eyeCount++;
        if (Environment.TickCount64 < Tuning.CameraToEyesDueMs) return;
        Tuning.CameraToEyesDueMs = 0;
        Vector3 eyes = eyeSum / Math.Max(eyeCount, 1) + new Vector3(0, EyeForward, EyeUp);
        Vector3 shift = eyes - CameraBodyProbe.GameSpot;
        eyeSum = Vector3.Zero; eyeCount = 0;
        Tuning.CameraForwardCm = Math.Clamp(MathF.Round(shift.Y * 100f), -Tuning.MaxCameraShiftCm, Tuning.MaxCameraShiftCm);
        Tuning.CameraUpCm = Math.Clamp(MathF.Round(shift.Z * 100f), -Tuning.MaxCameraShiftCm, Tuning.MaxCameraShiftCm);
        Tuning.CameraToEyesResult = $"done - camera moved {Tuning.CameraForwardCm:F0} cm forward, {Tuning.CameraUpCm:F0} cm up";
        Log.Write($"RA01 VRIK camera to eyes: {Tuning.CameraToEyesResult} (her eyes at ({eyes.X:F3}, {eyes.Y:F3}, {eyes.Z:F3}), game camera at ({CameraBodyProbe.GameSpot.X:F3}, {CameraBodyProbe.GameSpot.Y:F3}, {CameraBodyProbe.GameSpot.Z:F3}), sideways {shift.X * 100:F1} cm not applied)");
    }

    // RA01: the crane solve for one arm (walkthrough steps 1-5), the same maths M8-1 proved on the right arm, now for either side and any target:
    // 1. where the shoulder joint (s), elbow (e0) and wrist (w0) are now, stacked up from the feet;
    // 2. the two beams (a = upper arm, b = forearm) and the distance d to the target, clamped to what the arm can reach;
    // 3. law of cosines for the angle at the shoulder;
    // 4. the triangle swings toward the pole direction (the "string" tied down, out and behind);
    // 5. turn the upper arm onto its new line, then the forearm, and store both as turns relative to their parents.
    // 'shoulder' is the Names index of the arm's collarbone (LeftShoulder or RightShoulder); upper arm, forearm and hand follow it in Names. Returns a short
    // note of the numbers for the one-off log.
    // M9-3 - bone sizes are carried through the stacking, so the solver's beams are her arm as drawn. M9-6 - 'armSize' is written onto the upper arm first,
    // so both beams (and the hand) come out that much longer. Hands back
    // where the elbow and wrist end up and the forearm's new turn on her body (for the wrist); identity if unsolved.
    // Cost - CPU: extremely low (one walk of about 8 links, a triangle solve, two square roots, a few 16-byte writes); GPU: none.
    static string? SolveArm(byte* bones, int shoulder, float armSize, Vector3 target, Vector3 poleDir, out Vector3 elbow, out Vector3 wrist, out Quaternion foreOut)
    {
        elbow = wrist = default;
        foreOut = Quaternion.Identity;
        float* pa = (float*)(bones + slot[shoulder + 1] * SlotBytes);
        float* pf = (float*)(bones + slot[shoulder + 2] * SlotBytes);
        float* ph = (float*)(bones + slot[shoulder + 3] * SlotBytes);
        pa[8] = armSize; pa[9] = armSize; pa[10] = armSize; // RA01: M9-6 - the arm & hand size, on the upper arm only (the game carries it to the forearm and hand)

        // RA01: step 1 - where everything is now
        BodyPlace(bones, slot[shoulder], out Vector3 shoulderPos, out Quaternion shoulderTurn, out float shoulderScale);
        float armScale = shoulderScale * armSize;
        Quaternion armTurn = shoulderTurn * Turn(pa);
        Vector3 s = shoulderPos + Vector3.Transform(Offset(pa) * shoulderScale, shoulderTurn);
        Quaternion foreTurn = armTurn * Turn(pf);
        Vector3 e0 = s + Vector3.Transform(Offset(pf) * armScale, armTurn);
        Vector3 w0 = e0 + Vector3.Transform(Offset(ph) * armScale, foreTurn);

        // RA01: step 2 - the beams and the reach
        float a = Vector3.Distance(s, e0), b = Vector3.Distance(e0, w0);
        if (a < 0.01f || b < 0.01f) return null;
        Vector3 toTarget = target - s;
        float d = toTarget.Length();
        if (d < 0.001f) return null;
        Vector3 dir = toTarget / d;
        d = Math.Clamp(d, MathF.Abs(a - b) + 0.001f, a + b - 0.001f); // RA01: too far -> straight arm pointing at it; too close -> folded as far as it goes

        // RA01: step 3 - law of cosines for the shoulder angle
        float cosShoulder = Math.Clamp((a * a + d * d - b * b) / (2 * a * d), -1f, 1f);
        float sinShoulder = MathF.Sqrt(1 - cosShoulder * cosShoulder);

        // RA01: step 4 - the swing: the part of the pole direction that sticks out sideways from the shoulder-to-target line; if the pole lines up with that line,
        // RA01: fall back to where the game's elbow already is
        Vector3 bend = poleDir - dir * Vector3.Dot(poleDir, dir);
        if (bend.LengthSquared() < 1e-6f) bend = (e0 - s) - dir * Vector3.Dot(e0 - s, dir);
        if (bend.LengthSquared() < 1e-8f) return null;
        bend = Vector3.Normalize(bend);
        Vector3 e = s + dir * (a * cosShoulder) + bend * (a * sinShoulder);
        Vector3 w = s + dir * d;

        // RA01: step 5 - turn each beam onto its new line, then store the turns relative to each parent (parent's turn undone, then ours)
        Quaternion swingArm = TurnBetween(Vector3.Normalize(e0 - s), Vector3.Normalize(e - s));
        Quaternion newArmTurn = Quaternion.Normalize(swingArm * armTurn);
        Vector3 foreNow = Vector3.Transform(Vector3.Normalize(w0 - e0), swingArm);
        Quaternion swingFore = TurnBetween(foreNow, Vector3.Normalize(w - e));
        Quaternion newForeTurn = Quaternion.Normalize(swingFore * swingArm * foreTurn);
        WriteTurn(pa, Quaternion.Normalize(Quaternion.Conjugate(shoulderTurn) * newArmTurn));
        WriteTurn(pf, Quaternion.Normalize(Quaternion.Conjugate(newArmTurn) * newForeTurn));
        elbow = e; wrist = w; foreOut = newForeTurn;

        return $"right arm: shoulder joint ({s.X:F3}, {s.Y:F3}, {s.Z:F3}), target ({target.X:F3}, {target.Y:F3}, {target.Z:F3}), reach needed {Vector3.Distance(s, target):F3} m of {a + b:F3} m";
    }

    // RA01: M9-2 - once per load: V's four arm-bone lengths (each bone's offset from its parent is the length of the bone before it) and her shoulder width.
    // Cost - CPU: extremely low (two short walks from the feet up and four lengths, once per load); GPU: none.
    static void MeasureFit(byte* bones)
    {
        restLength[0] = Offset((float*)(bones + slot[LeftShoulder + 2] * SlotBytes)).Length();
        restLength[1] = Offset((float*)(bones + slot[LeftShoulder + 3] * SlotBytes)).Length();
        restLength[2] = Offset((float*)(bones + slot[RightShoulder + 2] * SlotBytes)).Length();
        restLength[3] = Offset((float*)(bones + slot[RightShoulder + 3] * SlotBytes)).Length();
        // RA01: M9-3 - the rest pose is only trusted if its four arm lengths match her live ones (bones don't change length when they animate), within 5 mm
        restTrusted = restPose.Length >= parentOf.Length * 12 && restPose.Length > 0;
        if (restTrusted)
            fixed (float* rest = restPose)
            {
                int[] check = { slot[LeftShoulder + 2], slot[LeftShoulder + 3], slot[RightShoulder + 2], slot[RightShoulder + 3] };
                for (int i = 0; i < 4; i++) restTrusted &= MathF.Abs(Offset(rest + check[i] * 12).Length() - restLength[i]) < 0.005f;
            }
        Log.Write($"RA01 VRIK rest pose: {(restTrusted ? "matches her live arm lengths - rest shoulders and wrist calibration are on" : "does NOT match (or missing) - shoulders and wrists stay on the game's animation")}");
        // RA01: M9-4 - shoulder width from the REST pose: the first-person animation pinches her shoulders inward (0.163 m against 0.370 m standing), which made the
        // RA01: M9-3 calibration think her span was far shorter than it is. The live measurement is only the fallback.
        Vector3 leftJoint, rightJoint;
        if (restTrusted) { RestPlace(slot[LeftShoulder + 1], out leftJoint, out _); RestPlace(slot[RightShoulder + 1], out rightJoint, out _); }
        else { BodyPlace(bones, slot[LeftShoulder + 1], out leftJoint, out _); BodyPlace(bones, slot[RightShoulder + 1], out rightJoint, out _); }
        shoulderWidth = Vector3.Distance(leftJoint, rightJoint);
        MeasureTwist();
        fitReady = true;
        Log.Write($"RA01 VRIK arm fit: V's arms {restLength[0]:F3}+{restLength[1]:F3} (left) and {restLength[2]:F3}+{restLength[3]:F3} (right) m, shoulders {shoulderWidth:F3} m apart, wrist-to-wrist span {shoulderWidth + restLength[0] + restLength[1] + restLength[2] + restLength[3]:F3} m");
    }

    // RA01: M9-7 - once per load: where each twist helper sits along its forearm in the REST pose (its share of the roll: 0 at the elbow, 1 at the wrist, like marks
    // along a rope fixed at the elbow and turned at the wrist), and its rest turn against the rest forearm. Only with a trusted rest pose and parents that
    // sit in the live list. Logged once, with each helper's parent.
    // Cost - CPU: extremely low (a dozen short rest-pose walks, once per load); GPU: none.
    static void MeasureTwist()
    {
        twistReady = false;
        if (!twistFound || !restTrusted) { if (twistFound) Log.Write("RA01 VRIK forearm twist: rest pose not trusted - the twist fix stays off"); return; }
        var text = new System.Text.StringBuilder("RA01 VRIK forearm twist helpers:");
        for (int t = 0; t < TwistNames.Length; t++)
        {
            int shoulder = t < 3 ? LeftShoulder : RightShoulder;
            if (twistSlot[t] >= parentOf.Length) return;
            RestPlace(slot[shoulder + 2], out Vector3 elbow, out Quaternion foreRest);
            RestPlace(slot[shoulder + 3], out Vector3 wrist, out _);
            RestPlace(twistSlot[t], out Vector3 at, out Quaternion helperRest);
            Vector3 along = wrist - elbow;
            twistShare[t] = along.LengthSquared() > 1e-6f ? Math.Clamp(Vector3.Dot(at - elbow, along) / along.LengthSquared(), 0f, 1f) : 0.5f;
            twistRestRel[t] = Quaternion.Normalize(Quaternion.Conjugate(foreRest) * helperRest);
            int parent = parentOf[twistSlot[t]];
            string parentName = NameAtSlot(parent) ?? (Array.IndexOf(twistSlot, parent) is int h && h >= 0 ? TwistNames[h] : $"slot {parent}");
            text.Append($"{Environment.NewLine}RA01   {TwistNames[t],-14} slot {twistSlot[t],3}, parent {parentName}, share {twistShare[t]:F2}, {Vector3.Distance(at, elbow):F3} m from the elbow");
        }
        twistReady = true;
        Log.Write(text.ToString());
    }

    // RA01: M9-7 - shares the hand's roll out along one forearm (the three helpers). Two steps:
    //  1. split the hand's turn (against its rest pose, in the forearm's own frame) into swing (the hinge bend) and twist (the screwdriver roll about the
    //     forearm's long axis) and keep only the roll angle - the part of the turn's pin that lies along the forearm;
    //  2. each helper = the forearm's turn, then its share of that roll about the same axis, then its own rest turn against the forearm; stored against its
    //     live parent (whatever that is), in order, so a helper hanging from another helper sees its parent already written.
    // Cost - CPU: extremely low (one atan2 and, per helper, a walk of about 10 links and three quaternion products); GPU: none.
    static void ApplyTwist(byte* bones, int shoulder, Quaternion foreTurn, Quaternion handLocal)
    {
        int side = shoulder == LeftShoulder ? 0 : 1;
        float* ph = (float*)(bones + slot[shoulder + 3] * SlotBytes);
        Vector3 axis = Offset(ph);                                  // RA01: the forearm's long axis in its own frame = where the hand is bolted on
        if (axis.LengthSquared() < 1e-6f) return;
        axis = Vector3.Normalize(axis);
        Quaternion d = Quaternion.Normalize(handLocal * Quaternion.Conjugate(RestTurn(slot[shoulder + 3]))); // RA01: the hand's turn away from its rest pose
        if (d.W < 0) d = new Quaternion(-d.X, -d.Y, -d.Z, -d.W); // RA01: the short way round, so the roll stays within half a turn either way
        float roll = 2f * MathF.Atan2(Vector3.Dot(new Vector3(d.X, d.Y, d.Z), axis), d.W);
        handRoll[side] = roll * 180f / MathF.PI;
        for (int t = side * 3; t < side * 3 + 3; t++)
        {
            float* p = (float*)(bones + twistSlot[t] * SlotBytes);
            Quaternion want = Quaternion.Normalize(foreTurn * Quaternion.CreateFromAxisAngle(axis, roll * twistShare[t]) * twistRestRel[t]);
            BodyPlace(bones, parentOf[twistSlot[t]], out _, out Quaternion parentTurn);
            WriteTurn(p, Quaternion.Normalize(Quaternion.Conjugate(parentTurn) * want));
            twistGiven[t] = handRoll[side] * twistShare[t];
        }
    }

    // RA01: M13-5 - the cyberware arms (Mantis Blades, Gorilla Arms, Monowire, the launcher) bring their own forearm and wrist bones, hung straight off V's forearm -
    // not off her hand - so when VRIK turned her hand they stayed where the game's animation left them, and the knuckles came apart from the palm. Picture a
    // sleeve over the forearm and a glove over the hand:
    //  - 'd' is the extra turn VRIK just gave the hand (in the forearm's own frame); its "roll" is the screwdriver part about the forearm's long axis;
    //  - each follower's share is how far along the forearm it hangs (0 at the elbow, 1 at the wrist), measured on its own offset every frame;
    //  - a SLEEVE bone (share below GloveShare) is turned that share of the roll about the forearm's axis, like marks along a rope twisted at one end;
    //  - a GLOVE bone (at the wrist end) gets the hand's whole extra turn, swung about the wrist point, so it stays bolted to the palm.
    // Only the change is added on top of the game's own pose, so the cyberware's animation (blades opening, the wire, the fists) still plays. Works for any rig,
    // male or female, without knowing a single bone name; on V's normal arms there is usually nothing extra to move. If the game ever leaves a bone as we wrote
    // it, the copy of its own value is kept, so the change is never stacked twice.
    // Cost - CPU: low (per arm per frame, one atan2 and for each follower - a few dozen on a cyberware arm - a quaternion product and a vector turn); GPU: none.
    static void MoveFollowers(byte* bones, int shoulder, Quaternion d)
    {
        if (!ReferenceEquals(followersFor, parentOf)) FindFollowers();
        int side = shoulder == LeftShoulder ? 0 : 1;
        Vector3 wristAt = Offset((float*)(bones + slot[shoulder + 3] * SlotBytes)); // RA01: where the hand is bolted on, in the forearm's frame
        float reach = wristAt.Length();
        if (reach < 1e-4f || followers.Length == 0) return;
        Vector3 axis = wristAt / reach;
        if (d.W < 0) d = new Quaternion(-d.X, -d.Y, -d.Z, -d.W);
        float roll = 2f * MathF.Atan2(Vector3.Dot(new Vector3(d.X, d.Y, d.Z), axis), d.W);
        var text = followerLogDue[side] ? new System.Text.StringBuilder($"RA01 VRIK forearm followers ({(side == 0 ? "left" : "right")} arm):") : null;
        for (int k = 0; k < followers.Length; k++)
        {
            if (followerSide[k] != side) continue;
            float* p = (float*)(bones + followers[k] * SlotBytes);
            int b = k * 7;
            bool fresh = false;
            for (int j = 0; j < 7 && !fresh; j++) fresh = p[j < 3 ? j : j + 1] != followerWritten[b + j];
            if (fresh) for (int j = 0; j < 7; j++) followerBase[b + j] = p[j < 3 ? j : j + 1]; // RA01: the game refreshed it - keep its own value
            Vector3 off = new(followerBase[b], followerBase[b + 1], followerBase[b + 2]);
            Quaternion turn = new(followerBase[b + 3], followerBase[b + 4], followerBase[b + 5], followerBase[b + 6]);
            float share = Math.Clamp(Vector3.Dot(off, axis) / reach, 0f, 1f);
            bool glove = share >= GloveShare;
            Quaternion r = glove ? d : Quaternion.CreateFromAxisAngle(axis, roll * share);
            Vector3 moved = glove ? wristAt + Vector3.Transform(off - wristAt, d) : Vector3.Transform(off, r);
            Quaternion turned = Quaternion.Normalize(r * turn);
            p[0] = moved.X; p[1] = moved.Y; p[2] = moved.Z;
            WriteTurn(p, turned);
            followerWritten[b] = p[0]; followerWritten[b + 1] = p[1]; followerWritten[b + 2] = p[2];
            followerWritten[b + 3] = p[4]; followerWritten[b + 4] = p[5]; followerWritten[b + 5] = p[6]; followerWritten[b + 6] = p[7];
            text?.Append($"{Environment.NewLine}RA01   slot {followers[k],4}: share {share:F2} - {(glove ? "glove (moves with the hand)" : "sleeve (shares the roll)")}");
        }
        if (anchorsDue[side]) FindAnchors(bones, side, wristAt, axis, reach); // RA01: M13-7 - once per rig per arm, on the first frame
        MoveAnchors(bones, side, d, wristAt);
        if (text is null) return;
        followerLogDue[side] = false;
        Log.Write(text.ToString());
    }

    // RA01: M13-7 - a cyberware branch can be bolted on near the elbow and only reach the wrist further down (the Mantis Blades' wrist rail: its first link sits a
    // fifth of the way along, its second at the wrist, and the whole blade hand hangs under that). The sleeve rule alone gave that branch a fifth of your roll
    // and none of your wrist's bend. So, once per rig, each sleeve bone's family is searched down for the first bone on every branch that reaches the wrist
    // (GloveShare or more, measured in the forearm's frame on this frame's pose): that is a WRIST ANCHOR. The anchors are logged.
    // Cost - CPU: low, once per rig (one pass over the parent list per sleeve bone, a short stack per candidate); GPU: none.
    static void FindAnchors(byte* bones, int side, Vector3 wristAt, Vector3 axis, float reach)
    {
        anchorsDue[side] = false;
        var slots = new List<int>(anchors); var owners = new List<int>(anchorFollower); var paths = new List<int[]>(anchorPath);
        var text = new System.Text.StringBuilder($"RA01 VRIK wrist anchors ({(side == 0 ? "left" : "right")} arm):");
        int* chain = stackalloc int[64];
        for (int k = 0; k < followers.Length; k++)
        {
            if (followerSide[k] != side) continue;
            int b = k * 7;
            if (Math.Clamp(Vector3.Dot(new Vector3(followerBase[b], followerBase[b + 1], followerBase[b + 2]), axis) / reach, 0f, 1f) >= GloveShare) continue; // RA01: a glove already
            if (Carries(followers[k], wireSlot[0]) || Carries(followers[k], wireSlot[1])) continue; // RA01: M13-8 - the Monowire's wire has its own rule (M38-1: LayWire)
            for (int i = 0; i < parentOf.Length; i++)
            {
                int n = 0;
                bool under = false;
                for (int x = i; x >= 0 && n < 64; x = parentOf[x]) { if (x == followers[k]) { under = true; break; } chain[n++] = x; }
                if (!under || n == 0) continue;                         // RA01: chain = i up to (not including) the sleeve bone
                bool first = true;                                      // RA01: no bone above i on this branch reaches the wrist already
                Vector3 pos = new(followerBase[b], followerBase[b + 1], followerBase[b + 2]);
                Quaternion turn = new(followerBase[b + 3], followerBase[b + 4], followerBase[b + 5], followerBase[b + 6]);
                for (int j = n - 1; j >= 0 && first; j--)
                {
                    float* p = (float*)(bones + chain[j] * SlotBytes);
                    pos += Vector3.Transform(Offset(p), turn);
                    turn = Quaternion.Normalize(turn * Turn(p));
                    if (Vector3.Dot(pos, axis) / reach >= GloveShare && j > 0) first = false;
                }
                if (!first || Vector3.Dot(pos, axis) / reach < GloveShare) continue;
                var path = new int[n - 1];
                for (int j = 0; j < n - 1; j++) path[j] = chain[n - 1 - j];  // RA01: top down, without i itself
                slots.Add(i); owners.Add(k); paths.Add(path);
                text.Append($"{Environment.NewLine}RA01   slot {i,4}: under sleeve slot {followers[k]}, {n} link(s) down, reaches {Vector3.Dot(pos, axis) / reach:F2} of the forearm");
            }
        }
        int added = slots.Count - anchors.Length;
        anchors = slots.ToArray(); anchorFollower = owners.ToArray(); anchorPath = paths.ToArray();
        var keepBase = anchorBase; var keepWritten = anchorWritten;
        anchorBase = new float[anchors.Length * 7]; anchorWritten = new float[anchors.Length * 7];
        Array.Copy(keepBase, anchorBase, keepBase.Length); Array.Copy(keepWritten, anchorWritten, keepWritten.Length);
        Array.Fill(anchorWritten, float.NaN, keepWritten.Length, anchorWritten.Length - keepWritten.Length);
        if (added > 0) Log.Write(text.ToString());
    }

    // RA01: M13-7 - each wrist anchor gets the hand's whole extra turn 'd', pivoting about the wrist point - the same as a glove bone - but it hangs below other
    // bones, so its new place is worked out in the forearm's frame first: where the game had it (its branch stacked from the game's own values), swung by 'd'
    // about the wrist; then turned back into a local offset and turn under its parent as that parent is now (after the sleeve roll above it). Everything
    // hanging under the anchor - a whole blade hand, its fingers and blades - follows by itself.
    // Cost - CPU: low (per anchor per frame, two stacks of a few links and a few quaternion products); GPU: none.
    static void MoveAnchors(byte* bones, int side, Quaternion d, Vector3 wristAt)
    {
        for (int a = 0; a < anchors.Length; a++)
        {
            int k = anchorFollower[a];
            if (followerSide[k] != side) continue;
            float* p = (float*)(bones + anchors[a] * SlotBytes);
            int b = a * 7, fb = k * 7;
            bool fresh = false;
            for (int j = 0; j < 7 && !fresh; j++) fresh = p[j < 3 ? j : j + 1] != anchorWritten[b + j];
            if (fresh) for (int j = 0; j < 7; j++) anchorBase[b + j] = p[j < 3 ? j : j + 1];
            float* f = (float*)(bones + followers[k] * SlotBytes);
            Vector3 gamePos = new(followerBase[fb], followerBase[fb + 1], followerBase[fb + 2]);   // RA01: the branch as the game had it...
            Quaternion gameTurn = new(followerBase[fb + 3], followerBase[fb + 4], followerBase[fb + 5], followerBase[fb + 6]);
            Vector3 nowPos = Offset(f);                                                            // RA01: ...and as it is now
            Quaternion nowTurn = Turn(f);
            foreach (int x in anchorPath[a])
            {
                float* q = (float*)(bones + x * SlotBytes);
                gamePos += Vector3.Transform(Offset(q), gameTurn); gameTurn = Quaternion.Normalize(gameTurn * Turn(q));
                nowPos += Vector3.Transform(Offset(q), nowTurn); nowTurn = Quaternion.Normalize(nowTurn * Turn(q));
            }
            gamePos += Vector3.Transform(new Vector3(anchorBase[b], anchorBase[b + 1], anchorBase[b + 2]), gameTurn);
            gameTurn = Quaternion.Normalize(gameTurn * new Quaternion(anchorBase[b + 3], anchorBase[b + 4], anchorBase[b + 5], anchorBase[b + 6]));
            Vector3 wantPos = wristAt + Vector3.Transform(gamePos - wristAt, d);
            Quaternion wantTurn = Quaternion.Normalize(d * gameTurn);
            Quaternion undo = Quaternion.Conjugate(nowTurn);
            Vector3 local = Vector3.Transform(wantPos - nowPos, undo);
            WriteTurn(p, Quaternion.Normalize(undo * wantTurn));
            p[0] = local.X; p[1] = local.Y; p[2] = local.Z;
            anchorWritten[b] = p[0]; anchorWritten[b + 1] = p[1]; anchorWritten[b + 2] = p[2];
            anchorWritten[b + 3] = p[4]; anchorWritten[b + 4] = p[5]; anchorWritten[b + 5] = p[6]; anchorWritten[b + 6] = p[7];
        }
    }

    // RA01: M13-8 - true if 'bone' hangs somewhere under 'top' (or is it).
    // Cost - CPU: extremely low (a walk up the parent list, at most 64 links); GPU: none.
    static bool Carries(int top, int bone)
    {
        for (int x = bone, n = 0; x >= 0 && n < 64 && x < parentOf.Length; x = parentOf[x], n++) if (x == top) return true;
        return false;
    }

    // RA01: M13-8 - the straight-wire rule, part 1, on the game's own pose (before VRIK moves anything). The Monowire has one wire per hand: a chain of 11 cm links hung
    // from a bracket on each forearm. At rest the game lays a half from each bracket toward the other one and parks the rest far away - aimed at where ITS
    // animation puts V's other hand, so once VRIK moves the hands a half points off into space (M13-10: your diagram and screenshot - the visible half was the
    // left hand's, which M13-8 never touched). For one half ('side': 0 = the right hand's, aimed at the left bracket; 1 = the left hand's, aimed at the right):
    // is it idle (it leaves its bracket within WireAimMaxDeg of the other bracket and reaches it within WireReachMax links; once idle, it lets go only past
    // WireAimLeaveDeg or after WireLeaveFrames frames without reach - a whip flings it far out)? If so, remember where the other bracket is in this half's first
    // link's own frame (like marking on a tape measure where the other hand held it), which link reaches it, and the game's own link values.
    // Cost - CPU: low, only with Monowire out (one walk per link up to the reach link, about 15 per half); GPU: none.
    static bool CaptureWire(byte* bones, int count, int side)
    {
        if (wireSlot[0] < 0 || wireSlot[1] < 0 || wireSlot[0] >= count || wireSlot[1] >= count || parentOf.Length < count) return false;
        int start = wireSlot[side == 0 ? 1 : 0], second = wireSlot[side == 0 ? 2 : 3], target = wireSlot[side == 0 ? 0 : 1];
        if (second < 0 || second >= count) return false;
        int[] links = wireLinks[side];
        if (links.Length < 3 || links[0] != start || links[^1] >= count) { Links(side, start, second); links = wireLinks[side]; if (links.Length < 3) return false; }
        float* first = (float*)(bones + links[0] * SlotBytes);
        Vector3[] baseOffsets = wireBaseOffsets[side];
        if (Turn(first) == wireWrittenTurn[side] && baseOffsets.Length > 0) // RA01: the game didn't refresh this half - put its own values back before measuring
        {
            WriteTurn(first, wireBaseTurn[side]);
            for (int k = 1; k < baseOffsets.Length; k++) { float* q = (float*)(bones + links[k] * SlotBytes); q[0] = baseOffsets[k].X; q[1] = baseOffsets[k].Y; q[2] = baseOffsets[k].Z; }
        }
        BodyPlace(bones, links[0], out Vector3 r1, out Quaternion r1Turn);
        BodyPlace(bones, target, out Vector3 l1, out _);
        Vector3 span = l1 - r1;
        float gap = span.Length();
        bool idle = false;
        int reachLink = -1;
        if (gap > 0.02f)
        {
            Vector3 line = span / gap;
            float stray = 0f, aim = 180f;
            for (int k = 1; k < links.Length && reachLink < 0; k++)
            {
                BodyPlace(bones, links[k], out Vector3 at, out _);
                if (k == 1 && (at - r1).LengthSquared() > 1e-8f) aim = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(at - r1), line), -1f, 1f)) * 180f / MathF.PI;
                float along = Vector3.Dot(at - r1, line);
                stray = MathF.Max(stray, (at - r1 - line * along).Length());
                if (along >= gap) reachLink = k;
            }
            bool reaches = reachLink > 0 && reachLink <= WireReachMax;
            if (!wireWasIdle[side]) { idle = reaches && aim <= WireAimMaxDeg; wireNotIdleFrames[side] = 0; }
            else // RA01: M13-9b - with some play, so the edge never flickers
            {
                wireNotIdleFrames[side] = reaches && aim <= WireAimLeaveDeg ? 0 : wireNotIdleFrames[side] + 1;
                idle = aim <= WireAimLeaveDeg && wireNotIdleFrames[side] < WireLeaveFrames;
            }
            long now = Environment.TickCount64;
            if (WireLogDue(side, now)) // RA01: M13-9 - the game's OWN wire halves, before VRIK, once a second each
                Log.Write($"RA01 VRIK wire (game's own pose, {(side == 0 ? "right" : "left")} half): brackets {gap * 100:F0} cm apart, first link leaves {aim:F0} deg off the line to the other bracket, " +
                          $"{(reachLink > 0 ? $"reaches it at link {reachLink}" : "never reaches it")}, strays up to {stray * 100:F0} cm -> {(idle ? "idle" : "not idle")}");
        }
        if (idle != wireWasIdle[side]) { wireWasIdle[side] = idle; Log.Write($"RA01 VRIK wire ({(side == 0 ? "right" : "left")} half): {(idle ? $"idle - held straight to the other hand (the game's half reaches its bracket at link {reachLink})" : "whipping (or put away) - left to the game")}"); }
        if (!idle) return false;
        wireEnd[side] = Vector3.Transform(span, Quaternion.Conjugate(r1Turn));
        wireReach[side] = reachLink;
        wireBaseTurn[side] = Turn(first);
        int n = Math.Max(reachLink, 0) + 1;
        if (baseOffsets.Length != n) wireBaseOffsets[side] = baseOffsets = new Vector3[n];
        for (int k = 1; k <= reachLink; k++) baseOffsets[k] = Offset((float*)(bones + links[k] * SlotBytes));
        return true;
    }

    // RA01: M13-10 - one "game's own pose" log line per half each second.
    // RA01: Cost - CPU: extremely low (a clock compare); GPU: none.
    static readonly long[] nextWireLogMs = new long[2];
    static bool WireLogDue(int side, long now)
    {
        if (now < nextWireLogMs[side]) return false;
        nextWireLogMs[side] = now + 1000;
        return true;
    }

    // RA01: M38-1 - LAY THE CABLE (replaces M13-8's StraightenWire; Rob approved 2026-10-11). How the Monowire's wire is really drawn (M38-0 probe and the WolvenKit
    // exports, see claude/RA01_M38_...md):
    //  - Each hand has a cable mesh (whip1_l / whip1_r) skinned to its own chain of 46 links (l_/r_nanoWire01..46_jnt), hung from a bracket on the forearm.
    //    Its material (w_monowire.mi: SpanCount 45, SplineLength 5 m) bends the glowing tube along those links on the graphics card - the links ARE the shape.
    //  - HOW MUCH of the 5 m cable shows is not the links but an animation number, the float track left/rightWireSlideFactor (1 = all of it). The links past
    //    that point are simply not drawn.
    //  - Idle, the game's animation pins the LEFT cable's tip to the right hand (track l_ik_attachToRightWeapon = 1, slide 1.0 - the string you see between
    //    the hands) and leaves the RIGHT cable as a short loose strand (slide 0.186, no pin). During whips the pins and slides change and the game flings them.
    //  - The game does that pinning BEFORE VRIK moves your arms, so the tip stays where the game's animation had the other hand - the "broken in half" look.
    // So, after the arms, for one cable ('side': 0 = the right hand's, 1 = the left hand's): while the game has it pinned to the other hand (its attach
    // track >= 0.5; the loose right strand also while the left one is pinned - the game's resting pose) and no attack is flying (melee states 11-21), it is re-threaded straight from its bracket (on its own forearm, 5-8 cm behind the hand)
    // into the OTHER FIST (its weapon point WeaponLeft / WeaponRight - the very point the game pins to). The two cables, each wrist to the other fist, cross
    // in an X - Rob's chosen "VR look" (M38-1c, 2026-10-11; M38-1b's bracket-to-bracket single string looked odd and hid the whip-out). The instant the
    // game lets go of the pin (a whip, a swing, putting it away) the cable is the game's again, so swings fling it out as normal. M13-8's geometric idle
    // guess is used only if the float tracks could not be found. M38-1d - holding or charging RT (melee states 6-7) keeps the cable laid on purpose (Rob:
    // "on charge up let's keep it hanging on"); it is let go the moment the attack fires (11-21) or the game drops its pin (a whip), so attacks fly out.
    // Weapons tab "Monowire look" (Tuning.MonowireX): X (each cable wrist to the other fist) or Straight (the right strand folded away by HideWire, only the
    // left cable from the left wrist into the right fist):
    //  1. the visible link count k = slide x 45 (from the cable's own slide track; the reach link if the track is unknown);
    //  2. the first link is turned (the smallest turn, so it keeps its roll) until its chain axis (+X - every link sits 11.1 cm along its parent's X) points
    //     at the other fist;
    //  3. every following link gets no turn of its own and sits gap/k along X from the one before - so links 0..k are evenly spaced eyelets ending exactly
    //     in the other fist, however near or far apart your hands are; links past k are parked there (hidden: past the slide they aren't drawn).
    // Builder picture: instead of swinging a bent rod, the cable is re-threaded through evenly spaced eyelets from one wrist into the other fist.
    // Anything else (a whip, the cable put away, a cutscene) is left exactly as the game animates it.
    // DEV "Wire test (Monowire)" (Tuning.WireTest, kept on purpose for future checks): the RIGHT cable is laid by this same code, but aimed straight UP
    // from its bracket, the length of the gap to the left hand - if it points up in the headset, these link writes reach the screen.
    // Cost - CPU: low, only with the Monowire out and a cable pinned (one walk up the arm, a turn and about 46 small writes per cable); GPU: none.
    static void LayWire(byte* bones, int count, int side, bool idle)
    {
        int[] links = wireLinks[side];
        if (links.Length < 3 || links[^1] >= count || parentOf.Length < count) return;
        // RA01: M45-1 - only this body's own wire: the link list is kept between bodies (slot numbers), so on a body without the Monowire (Mantis Blades, fists,
        // RA01: a gun) - or one whose wire starts elsewhere - those numbers are other bones, and laying "the cable" through them bent the blades and arms.
        if (wireSlot[0] < 0 || wireSlot[1] < 0 || links[0] != wireSlot[side == 0 ? 1 : 0]) return;
        float* tracks = (float*)PlayerSkeleton.TracksAt;
        int n = PlayerSkeleton.TrackCount;
        int slideAt = wireTrack[side == 0 ? 1 : 0], pinAt = wireTrack[side == 0 ? 6 : 4];   // RA01: right: rightWireSlideFactor / r_ik_attachToLeftWeapon; left: the mirror
        bool tracksOk = tracks != null && slideAt >= 0 && slideAt < n && pinAt >= 0 && pinAt < n;
        bool swinging = Core.GameAnchor.TryMeleeState(out int melee) && melee >= MeleeAttackFirst && melee <= MeleeAttackLast;   // RA01: M38-1c - never while an attack flies (RT charge-up keeps it, Rob 2026-10-11)
        int restAt = wireTrack[4];                                                            // RA01: M38-1c - l_ik_attachToRightWeapon: the game's "wire resting between the hands" pin
        bool resting = tracksOk && restAt >= 0 && restAt < n && tracks[restAt] >= 0.5f;       // RA01: the loose right strand is laid with it, so both cables make the X
        bool pinned = tracksOk && (tracks[pinAt] >= 0.5f || resting) && !swinging;
        bool fallback = !tracksOk && side == 0 && idle && !swinging;                          // RA01: M38-1c - the old idle guess only if the tracks are unknown
        bool test = Tuning.WireTest && side == 0 && (pinned || fallback);
        bool lay = pinned || fallback || test;
        if (lay != wireLaid[side]) { wireLaid[side] = lay; Log.Write($"RA01 VRIK wire ({(side == 0 ? "right" : "left")} cable): {(lay ? $"laid wrist to fist ({(pinned ? "the game pins it there" : "idle, tracks unknown")}{(test ? ", DEV wire test: pointed UP" : "")})" : "left to the game (swing, whip or put away)")}"); }
        if (!lay) return;
        if (side == 0 && !Tuning.MonowireX && !test) { HideWire(bones, links); return; }   // RA01: M38-1d - "Straight": only the left cable shows, as in the flat game
        int other = weaponSlot[side == 0 ? 1 : 0];                                           // RA01: M38-1c - into the other FIST (its weapon point - where the game pins it)
        if (other < 0 || other >= count) other = slot[side == 0 ? LeftHand : RightHand];     // RA01: no weapon point - the other hand bone
        if (other < 0 || other >= count) return;
        BodyPlace(bones, links[0], out Vector3 start, out Quaternion firstTurn);
        BodyPlace(bones, other, out Vector3 end, out _);
        Vector3 span = end - start;
        if (test) span = Vector3.UnitZ * span.Length();                                       // RA01: DEV wire test - straight up (game space: z is up)
        float gap = span.Length();
        if (gap < 0.02f) return;
        int last = links.Length - 1;
        int k = tracksOk ? (int)MathF.Round(Math.Clamp(tracks[slideAt], 0f, 1f) * WireSpans) : wireReach[side];
        k = Math.Clamp(k, 1, last);
        float* first = (float*)(bones + links[0] * SlotBytes);
        Vector3 aim = Vector3.Normalize(Vector3.Transform(span / gap, Quaternion.Conjugate(firstTurn)));   // RA01: the target, seen from the first link's own frame
        Quaternion turned = Quaternion.Normalize(Turn(first) * TurnBetween(Vector3.UnitX, aim));
        WriteTurn(first, turned);
        wireWrittenTurn[side] = turned;
        float step = gap / k;
        for (int i = 1; i <= last; i++)
        {
            float* q = (float*)(bones + links[i] * SlotBytes);
            q[0] = i <= k ? step : 0.0005f; q[1] = 0f; q[2] = 0f;                              // RA01: eyelets up to k, then parked (a hair apart, never zero length)
            WriteTurn(q, Quaternion.Identity);
        }
    }
    const int WireSpans = 45;                         // RA01: M38-1 - the cable material's SpanCount: slide 1.0 = 45 link gaps = 5 m
    static readonly bool[] wireLaid = new bool[2];    // RA01: M38-1 - for the on/off log line
    const int MeleeAttackFirst = 11, MeleeAttackLast = 21;   // RA01: M38-1c - gamePSMMeleeWeapon ComboAttack..EquipAttack: a swing, the cable is the game's

    // RA01: M38-1d - "Straight" look: the right hand's short resting strand is folded away into its bracket (its first link stays, every link after it sits a hair
    // from the one before, unturned), so only the left cable shows - one string from the left wrist into the right fist, the flat game's look.
    // Cost - CPU: extremely low (about 46 small writes); GPU: none.
    static void HideWire(byte* bones, int[] links)
    {
        for (int i = 1; i < links.Length; i++)
        {
            float* q = (float*)(bones + links[i] * SlotBytes);
            q[0] = 0.0005f; q[1] = 0f; q[2] = 0f;
            WriteTurn(q, Quaternion.Identity);
        }
    }

    // RA01: M13-8, M13-10 - follows one wire half down the parent list once per rig: its first link, then each link's single child.
    // Cost - CPU: extremely low (a pass over the parent list per link, about 46, once per rig); GPU: none.
    static void Links(int side, int start, int second)
    {
        var links = new List<int> { start };
        for (int next = second; next >= 0 && links.Count < 64; )
        {
            links.Add(next);
            int child = -1;
            for (int i = next + 1; i < parentOf.Length; i++) if (parentOf[i] == next) { child = i; break; }
            next = child;
        }
        wireLinks[side] = links.ToArray();
        wireBaseOffsets[side] = Array.Empty<Vector3>();
    }

    // RA01: M13-7 - READ-ONLY, once a second while V's rig has the Monowire: where the game puts the two wire brackets on her body, how far apart they are, which way
    // the right wire leaves its bracket compared with the line to the left bracket, how many of its 11 cm links it takes to reach the left bracket's distance,
    // how far those links stray from that straight line, and how far its last link is. Idle against a whip attack shows how the game shapes the wire, so the
    // rule that keeps it straight between your two hands can be built on facts.
    // Cost - CPU: low, only with Monowire out (once a second, up to 50 short walks from the feet up and one log line); GPU: none.
    static void WireReport(byte* bones, int count)
    {
        if (wireSlot[0] < 0 || wireSlot[1] < 0 || wireSlot[2] < 0 || wireSlot[0] >= count || wireSlot[1] >= count || parentOf.Length < count) return;
        long now = Environment.TickCount64;
        if (now < nextWireReportMs) return;
        nextWireReportMs = now + 1000;
        if (rightWire.Length == 0 || rightWire[0] != wireSlot[1] || rightWire[^1] >= count) { if (wireSlot[2] < 0) return; Links(0, wireSlot[1], wireSlot[2]); }
        if (rightWire.Length < 2) return;
        BodyPlace(bones, wireSlot[0], out Vector3 l1, out _);
        BodyPlace(bones, wireSlot[1], out Vector3 r1, out _);
        BodyPlace(bones, slot[RightHand], out Vector3 rh, out _);
        BodyPlace(bones, slot[LeftHand], out Vector3 lh, out _);
        Vector3 span = l1 - r1;
        float gap = span.Length();
        if (gap < 1e-4f) return;
        Vector3 line = span / gap;
        BodyPlace(bones, rightWire[1], out Vector3 r2, out _);
        float leave = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(r2 - r1), line), -1f, 1f)) * 180f / MathF.PI;
        int reachLink = -1;
        float stray = 0f, lastDist = 0f;
        for (int k = 1; k < rightWire.Length; k++)
        {
            BodyPlace(bones, rightWire[k], out Vector3 at, out _);
            float along = Vector3.Dot(at - r1, line);
            if (reachLink < 0) stray = MathF.Max(stray, (at - r1 - line * along).Length());
            if (reachLink < 0 && along >= gap) reachLink = k;
            lastDist = Vector3.Distance(at, r1);
        }
        string left = "";
        if (wireSlot[3] >= 0 && wireSlot[3] < count)
        {
            BodyPlace(bones, wireSlot[3], out Vector3 l2, out _);
            left = $", left wire leaves {MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(l2 - l1), -line), -1f, 1f)) * 180f / MathF.PI:F0} deg off the line back to the right bracket";
        }
        Log.Write($"RA01 VRIK wire report: brackets {gap * 100:F0} cm apart (hands {Vector3.Distance(lh, rh) * 100:F0} cm; right bracket {Vector3.Distance(r1, rh) * 100:F0} cm from her right hand, " +
                  $"left {Vector3.Distance(l1, lh) * 100:F0} cm from her left); right wire leaves {leave:F0} deg off the line to the left bracket, " +
                  $"{(reachLink > 0 ? $"reaches it at link {reachLink} of {rightWire.Length - 1}, straying up to {stray * 100:F1} cm" : $"never reaches it (strays up to {stray * 100:F1} cm)")}, last link {lastDist * 100:F0} cm out{left}");
    }

    // RA01: M38-0 - finds the Monowire wire's animation float tracks in V's pose, once per rig (retried every 5 s if not found). The cable mesh (whip1_l/_r,
    // material w_monowire.mi: SpanCount 45, SplineLength 5 m) bends along the 46 links, but HOW MUCH of it shows is the float tracks leftWireSlideFactor /
    // rightWireSlideFactor (1 = all 5 m), and the game's pins are the l_/r_ik_attachTo... weights. Builder picture: the links say which way the tape measure
    // runs, the slide number says how far it is pulled out. The names are found as a list of name codes, as long as the track list, hung off the skeleton;
    // LayWire (M38-1) reads the values by these slots. Rob's 2026-10-11 run: 445 tracks, wire tracks at 23..30; idle = left slide 1.0 pinned to the right
    // hand, right slide 0.186 loose.
    // Cost - CPU: low once per rig (a scan of up to ~130 small objects), then extremely low (a clock compare); GPU: none.
    static void WireTracks()
    {
        if (wireSlot[0] < 0 || wireSlot[1] < 0) return;
        int n = PlayerSkeleton.TrackCount;
        nint skeleton = PlayerSkeleton.Current;
        if (n <= 0 || (skeleton == wireTracksSkeleton && wireTrackCount == n && (wireTrack[1] >= 0 || Environment.TickCount64 < nextWireTracksMs))) return;
        nextWireTracksMs = Environment.TickCount64 + 5000;
        wireTracksSkeleton = skeleton; wireTrackCount = n;
        ulong[] names = FindTrackNames(skeleton, n);
        string where = "";
        for (int k = 0; k < WireTrackNames.Length; k++)
        {
            wireTrack[k] = names.Length == n ? Array.IndexOf(names, NameCode(WireTrackNames[k])) : -1;
            where += (k > 0 ? ", " : "") + WireTrackNames[k] + " " + wireTrack[k];
        }
        Log.Write(names.Length == n ? $"RA01 VRIK wire tracks: {n} float tracks; wire tracks at {where}"
                                    : $"RA01 VRIK wire tracks: {n} float tracks, but their name list was NOT found - the cables are laid only when idle (retry in 5 s)");
    }
    static readonly string[] WireTrackNames = { "leftWireSlideFactor", "rightWireSlideFactor", "l_ik_attachToTarget", "l_ik_attachToLeftWeapon", "l_ik_attachToRightWeapon",
                                                "r_ik_attachToTarget", "r_ik_attachToLeftWeapon", "r_ik_attachToRightWeapon" };
    static readonly int[] wireTrack = { -1, -1, -1, -1, -1, -1, -1, -1 };
    static nint wireTracksSkeleton;
    static int wireTrackCount;
    static long nextWireTracksMs;

    // RA01: M38-0 - finds the float-track name list for 'count' tracks: like FindLists, a pointer whose list length (at +12) is 'count' on the skeleton or one level
    // down, whose entries include the name code of "rightWireSlideFactor". Empty if not found. Safe reads only.
    // Cost - CPU: low, once per rig (up to ~130 reads of 1 KB plus one list read per candidate); GPU: none.
    static ulong[] FindTrackNames(nint skeleton, int count)
    {
        ulong want = NameCode("rightWireSlideFactor");
        var level1 = new List<nint> { skeleton };
        var level2 = new List<nint>();
        byte* buf = stackalloc byte[ObjectScanBytes];
        foreach (var level in new[] { level1, level2 })
            foreach (nint obj in level)
            {
                if (!Read(obj, buf, ObjectScanBytes)) continue;
                for (int o = 0; o + 16 <= ObjectScanBytes; o += 8)
                {
                    nint ptr = *(nint*)(buf + o);
                    if (!LooksLikePointer(ptr)) continue;
                    if (level == level1 && level2.Count < 128) level2.Add(ptr);
                    if (*(int*)(buf + o + 12) != count) continue;
                    var names = new ulong[count];
                    fixed (ulong* p = names) if (!Read(ptr, p, count * 8)) continue;
                    if (Array.IndexOf(names, want) >= 0) return names;
                }
            }
        return Array.Empty<ulong>();
    }

    // RA01: M13-5 - once per rig: every bone whose parent is a forearm, other than the hand and the M9-7 twist helpers (they have their own handling).
    // Cost - CPU: extremely low (one pass over the parent list, once per rebuilt V); GPU: none.
    static void FindFollowers()
    {
        followersFor = parentOf;
        var found = new List<int>();
        var sides = new List<byte>();
        for (int side = 0; side < 2; side++)
        {
            int shoulder = side == 0 ? LeftShoulder : RightShoulder;
            int fore = slot[shoulder + 2], hand = slot[shoulder + 3];
            for (int i = 0; i < parentOf.Length; i++)
                if (parentOf[i] == fore && i != hand && Array.IndexOf(twistSlot, i) < 0) { found.Add(i); sides.Add((byte)side); }
        }
        followers = found.ToArray();
        followerSide = sides.ToArray();
        followerBase = new float[followers.Length * 7];
        followerWritten = new float[followers.Length * 7];
        Array.Fill(followerWritten, float.NaN); // RA01: never equal to anything, so the first frame always takes the game's own values
        followerLogDue[0] = followerLogDue[1] = followers.Length > 0;
        anchors = Array.Empty<int>(); anchorFollower = Array.Empty<int>(); anchorPath = Array.Empty<int[]>();
        anchorBase = Array.Empty<float>(); anchorWritten = Array.Empty<float>();
        anchorsDue[0] = anchorsDue[1] = followers.Length > 0; // RA01: M13-7
        handWritten[0] = handWritten[1] = new Quaternion(float.NaN, 0, 0, 0);
        Log.Write($"RA01 VRIK forearm followers: {sides.Count(x => x == 0)} on the left forearm, {sides.Count(x => x == 1)} on the right ({parentOf.Length}-bone rig)");
    }

    // RA01: M27-1 - and then gesture mode (Actions\GestureMode.cs): its own curl for each finger (a dimmer per finger now; the wheel and the grip set all five alike).
    // RA01: M19-2 - now BOTH hands, each with its own dimmer: the steering wheel (PhysicalSteering.TryHandCurl) first, else the off hand on a weapon.
    // RA01: M18-4 - THE OFF HAND'S FINGERS, a dimmer between two lamps: OPEN (the rig's flat hand with a slight natural curl, OpenCurl of the way to the fist) and
    // RA01: CLOSED (the game's OWN fist - a made-up hinge bend gave a claw and a backwards middle finger in M13-16). Actions (TwoHandGrip.TryOffHandCurl) says how
    // RA01: closed: nothing when the hand is out of grab reach (the game's own fingers stay), 0..1 with the grip squeeze in reach, 1 while holding the weapon.
    // RA01: The dimmer slides CurlEase of the way each frame, so the hand never snaps. The game's fist is copied the first time V makes one (bare fists drawn:
    // RA01: both hands' middle and ring fingers bent past FistDeg) and kept in RA01_FistPose.txt beside the log, so later sessions have it from the start;
    // RA01: until then nothing is bent.
    // RA01: Cost - CPU: low (14 joints, a slerp and a write each, per frame while the off hand is in reach; a few angles per frame while fists are out until the
    // RA01: fist is copied; a small file read once and a write once); GPU: none.
    const float OpenCurl = 0.12f, CurlEase = 0.3f, FistDeg = 80f;
    static readonly Quaternion[] fist = new Quaternion[28];
    static int fistState;                 // RA01: 0 = not loaded yet, 1 = looked for (none yet), 2 = known
    static readonly float[] curlNow = { -1f, -1f, -1f, -1f, -1f, -1f, -1f, -1f, -1f, -1f }; // RA01: M27-1 - one dimmer per FINGER (left hand 0-4, right 5-9: index, middle, ring, pinky, thumb; was one per hand, M19-2) - -1 = not driving
    static readonly float[] curlWant = new float[5];
    static readonly string FistFile = Path.Combine(Core.ModFolders.Settings, "RA01_FistPose.txt"); // RA01: M20-4 - the launch folder's Settings folder

    // RA01: M18-4 - one frame of the off hand's fingers (see above): copies the fist first if it isn't known yet; otherwise blends the 14 joints of the off hand.
    // Cost - CPU: low (14 slerps and writes while in reach); GPU: none.
    static void OffHandFingers(byte* bones, int count)
    {
        if (fistState == 0) LoadFist();
        if (fistState != 2) { CopyFist(bones, count); return; }
        for (int h = 0; h < 2; h++)                      // RA01: M19-2 - each hand: 0 left (finger joints 0-13), 1 right (14-27)
        {
            float want = -1f;
            bool gesture = false;
            if (PhysicalSteering.TryHandCurl(h == 0, out float wheel)) want = wheel;                                   // RA01: M19-2 - on / near the steering wheel
            else if (weaponInLeft && h == 0) want = 1f;                                                                 // RA01: M28-1 - left-hand mode: her left hand grips the weapon
            else if (TwoHandGrip.TryOffHandCurl(out bool leftHand, out float grip) && leftHand == (h == 0)) want = grip;  // RA01: M18-4 - the off hand on a weapon
            else if (weaponInLeft && h == 1) want = 0f;                                                                 // RA01: M28-1 - ...and her emptied right hand relaxes open
            else gesture = GestureMode.TryFingers(h == 0, curlWant);                                                     // RA01: M27-1 - gesture mode: each finger its own
            if (!gesture) for (int f = 0; f < 5; f++) curlWant[f] = want;                                                // RA01: the wheel and the grip bend all fingers alike
            if (!gesture && want < 0f) { for (int f = 0; f < 5; f++) curlNow[h * 5 + f] = -1f; continue; }
            for (int f = 0; f < 5; f++)
            {
                int d = h * 5 + f;
                curlNow[d] = curlNow[d] < 0f ? curlWant[f] : curlNow[d] + (curlWant[f] - curlNow[d]) * CurlEase;
            }
            for (int b = h * 14; b < h * 14 + 14; b++)
            {
                int s = fingerSlot[b];
                if (s < 0 || s >= count || (s + 1) * 12 > restPose.Length) continue;
                int finger = Math.Min((b - h * 14) / 3, 4);                                                            // RA01: M27-1 - joints 0-11 = four fingers of 3, 12-13 = the thumb
                Quaternion rest = RestTurn(s), open = Quaternion.Slerp(rest, fist[b], OpenCurl);
                WriteTurn((float*)(bones + s * SlotBytes), Quaternion.Normalize(Quaternion.Slerp(open, fist[b], curlNow[h * 5 + finger])));
            }
        }
    }

    // RA01: M18-4 - while bare fists are out: when both hands' middle and ring fingers (knuckle and middle joint) are bent past FistDeg from the flat rest pose, the
    // game is making a full fist - copy all 28 joints and save them (on a background thread).
    // Cost - CPU: extremely low (a weapon check, and 8 angles while fists are out); GPU: none.
    static void CopyFist(byte* bones, int count)
    {
        if (Core.WeaponProbe.Held != Core.WeaponProbe.Kind.Fists) return;
        float sum = 0; int n = 0;
        foreach (int h in new[] { 0, 14 })
            foreach (int f in new[] { 3, 6 })              // RA01: middle and ring fingers
                for (int j = 0; j < 2; j++)                // RA01: knuckle and middle joint
                {
                    int s = fingerSlot[h + f + j];
                    if (s < 0 || s >= count || (s + 1) * 12 > restPose.Length) return;
                    Quaternion d = Quaternion.Normalize(Quaternion.Conjugate(RestTurn(s)) * Turn((float*)(bones + s * SlotBytes)));
                    sum += 2f * MathF.Acos(Math.Clamp(MathF.Abs(d.W), 0f, 1f)) * (180f / MathF.PI); n++;
                }
        if (n == 0 || sum / n < FistDeg) return;
        for (int b = 0; b < 28; b++)
        {
            int s = fingerSlot[b];
            if (s < 0 || s >= count) return;
            fist[b] = Quaternion.Normalize(Turn((float*)(bones + s * SlotBytes)));
        }
        fistState = 2;
        Core.Banner.Show("HANDS LEARNED");                                    // RA01: M33-2 - V's fist copied from the game: once ever (it is saved)
        var copy = (Quaternion[])fist.Clone();
        Task.Run(() =>
        {
            try { File.WriteAllLines(FistFile, copy.Select(q => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{q.X} {q.Y} {q.Z} {q.W}"))); Log.Write("RA01 VRIK fingers: copied the game's own fist (saved to RA01_FistPose.txt) - the off hand can now grip"); }
            catch (Exception e) { Log.Write($"RA01 VRIK fingers: copied the game's fist, but couldn't save it ({e.Message})"); }
        });
    }

    // RA01: M18-4 - RA01_FistPose.txt: 28 lines "x y z w" in FingerNames order. Read once; missing or broken = not known (logged, with what to do).
    // Cost - CPU: extremely low (one small file read, once); GPU: none.
    static void LoadFist()
    {
        fistState = 1;
        try
        {
            if (File.Exists(FistFile))
            {
                string[] lines = File.ReadAllLines(FistFile);
                if (lines.Length >= 28)
                {
                    for (int b = 0; b < 28; b++)
                    {
                        float[] q = lines[b].Split(' ').Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                        fist[b] = new Quaternion(q[0], q[1], q[2], q[3]);
                    }
                    fistState = 2;
                    Log.Write("RA01 VRIK fingers: the game's fist loaded from RA01_FistPose.txt");
                    return;
                }
            }
        }
        catch (Exception e) { Log.Write($"RA01 VRIK fingers: RA01_FistPose.txt could not be read ({e.Message})"); }
        Log.Write("RA01 VRIK fingers: no fist copied yet - draw bare fists for a second and the off hand will be able to grip");
    }

    // RA01: M9-3 - one bone's rest turn relative to its parent.
    // Cost - CPU: extremely low (four reads); GPU: none.
    static Quaternion RestTurn(int s)
    {
        fixed (float* rest = restPose) return Quaternion.Normalize(Turn(rest + s * 12));
    }

    // RA01: M8-1 - where a bone sits on V's body and which way it is turned: start at the feet and, link by link down the family tree, add each bone's offset
    // (turned by everything above it) and stack its turn on top - the building-blocks-on-a-crane picture from the walkthrough.
    // M9-3 - also stacks each bone's size (a bone drawn bigger pushes everything below it further out), and hands back the size carried to this bone's children.
    // Cost - CPU: extremely low (about 8 links for the shoulder, a quaternion multiply and a vector turn each); GPU: none.
    static void BodyPlace(byte* bones, int s, out Vector3 pos, out Quaternion turn, out float scale)
    {
        int* chain = stackalloc int[64];
        int n = 0;
        for (int x = s; x >= 0 && n < 64; x = parentOf[x]) chain[n++] = x;
        pos = Vector3.Zero;
        turn = Quaternion.Identity;
        scale = 1f;
        for (int i = n - 1; i >= 0; i--)
        {
            float* p = (float*)(bones + chain[i] * SlotBytes);
            pos += Vector3.Transform(Offset(p) * scale, turn);
            turn = Quaternion.Normalize(turn * Turn(p));
            scale *= p[8];
        }
    }
    static void BodyPlace(byte* bones, int s, out Vector3 pos, out Quaternion turn) => BodyPlace(bones, s, out pos, out turn, out _);

    // RA01: M9-3 - the same stacking, but through V's REST pose (the drawing she was built from) instead of her live pose.
    // Cost - CPU: extremely low (about 10 links); GPU: none.
    static void RestPlace(int s, out Vector3 pos, out Quaternion turn)
    {
        pos = Vector3.Zero;
        turn = Quaternion.Identity;
        int* chain = stackalloc int[64];
        int n = 0;
        for (int x = s; x >= 0 && n < 64; x = parentOf[x]) chain[n++] = x;
        fixed (float* rest = restPose)
            for (int i = n - 1; i >= 0; i--)
            {
                float* p = rest + chain[i] * 12;
                pos += Vector3.Transform(Offset(p), turn);
                turn = Quaternion.Normalize(turn * Turn(p));
            }
    }

    // RA01: M9-6 - puts one collarbone back to its rest pose: its offset from its parent (where it is bolted on) and its turn (which way it points).
    // Cost - CPU: extremely low (seven float reads and writes); GPU: none.
    static void RestShoulder(float* p, int s)
    {
        fixed (float* rest = restPose)
        {
            float* r = rest + s * 12;
            p[0] = r[0]; p[1] = r[1]; p[2] = r[2];
            WriteTurn(p, Quaternion.Normalize(Turn(r)));
        }
    }

    // RA01: M8-1 - the smallest turn that swings arrow "from" onto arrow "to": the hinge pin is their cross product, and how far comes from their dot product
    // (both arrows length 1). Opposite arrows have no single pin, so any pin at right angles is used.
    // Cost - CPU: extremely low (a cross product, a dot product and a normalise); GPU: none.
    static Quaternion TurnBetween(Vector3 from, Vector3 to)
    {
        float dot = Vector3.Dot(from, to);
        if (dot < -0.9999f)
        {
            Vector3 pin = Vector3.Cross(Vector3.UnitX, from);
            if (pin.LengthSquared() < 1e-6f) pin = Vector3.Cross(Vector3.UnitY, from);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(pin), MathF.PI);
        }
        Vector3 c = Vector3.Cross(from, to);
        return Quaternion.Normalize(new Quaternion(c.X, c.Y, c.Z, 1 + dot));
    }

    // RA01: M8-1 - a bone slot's offset from its parent (first 12 bytes), its turn (x, y, z, w at +0x10), and writing a new turn back.
    // Cost - CPU: extremely low (a few float reads or writes); GPU: none.
    static Vector3 Offset(float* p) => new(p[0], p[1], p[2]);
    static Quaternion Turn(float* p) => new(p[4], p[5], p[6], p[7]);
    static void WriteTurn(float* p, Quaternion q) { p[4] = q.X; p[5] = q.Y; p[6] = q.Z; p[7] = q.W; }

    // RA01: the one-off search (background thread): find the name list and parent list in V's rig, look up each bone, check the arm chains, log the result.
    // Cost - CPU: low, once per load (see the class comment); GPU: none.
    static void BuildMap(nint skeleton, int liveCount)
    {
        try
        {
            FindLists(skeleton, out nint namesAt, out int nameCount, out nint parentsAt, out int parentCount, out nint restAt);
            if (namesAt == 0) { Log.Write($"RA01 VRIK bone map: FAILED - no bone-name list found in V's rig (skeleton 0x{skeleton:X}); VRIK stays off"); return; }

            var nameList = new ulong[nameCount];
            fixed (ulong* p = nameList) if (!Read(namesAt, p, nameCount * 8)) { Log.Write("RA01 VRIK bone map: FAILED - the name list could not be read"); return; }
            var missing = new List<string>();
            for (int k = 0; k < Names.Length; k++)
            {
                slot[k] = Array.IndexOf(nameList, codes[k]);
                if (slot[k] < 0) missing.Add(Names[k]);
            }
            if (missing.Count > 0) { Log.Write($"RA01 VRIK bone map: FAILED - not in V's rig: {string.Join(", ", missing)}; VRIK stays off"); return; }
            for (int k = 0; k < ChestNames.Length; k++) chestSlot[k] = Array.IndexOf(nameList, chestCodes[k]); // RA01: M9-8, M11-1
            chestSlot[SpineBones - 1] = slot[1]; // RA01: Spine3 is already in Names
            chestFound = Array.TrueForAll(chestSlot, x => x >= 0);
            if (!chestFound) Log.Write("RA01 VRIK spine: Spine/Spine1/Spine2 not in V's rig - upright spine, lean and chest turn stay off");
            for (int k = 0; k < WeaponNames.Length; k++) weaponSlot[k] = Array.IndexOf(nameList, weaponCodes[k]); // RA01: M13-1
            for (int k = 0; k < WireNames.Length; k++) wireSlot[k] = Array.IndexOf(nameList, wireCodes[k]); // RA01: M13-7
            Log.Write($"RA01 VRIK weapon hooks: WeaponRight {(weaponSlot[0] >= 0 ? $"slot {weaponSlot[0]}" : "NOT in V's rig")}, WeaponLeft {(weaponSlot[1] >= 0 ? $"slot {weaponSlot[1]}" : "NOT in V's rig")}");
            for (int k = 0; k < LegNames.Length; k++) legSlot[k] = Array.IndexOf(nameList, legCodes[k]); // RA01: M11-3
            legFound = Array.TrueForAll(legSlot, x => x >= 0);
            standingHingeZ = 0; hipsWritten = new Vector3(float.NaN);
            if (!legFound) Log.Write("RA01 VRIK legs: Hips/legs not all in V's rig - crouch depth matching stays off");
            for (int b = 0; b < 28; b++) fingerSlot[b] = Array.IndexOf(nameList, fingerCodes[b]); // RA01: M18-4
            for (int t = 0; t < TwistNames.Length; t++) twistSlot[t] = Array.IndexOf(nameList, twistCodes[t]); // RA01: M9-7
            twistFound = Array.TrueForAll(twistSlot, x => x >= 0);
            twistReady = false;
            if (!twistFound) Log.Write($"RA01 VRIK forearm twist: helpers not in V's rig ({string.Join(", ", TwistNames.Where((_, t) => twistSlot[t] < 0))}) - the twist fix stays off");

            bool haveParents = parentsAt != 0 && parentCount == nameCount;
            var parents = new short[haveParents ? parentCount : 0];
            if (haveParents) fixed (short* p = parents) haveParents = Read(parentsAt, p, parentCount * 2);
            for (int k = 0; k < Names.Length; k++) parentSlot[k] = haveParents ? parents[slot[k]] : -2;

            // RA01: the family tree WolvenKit's rig says we should see: hand -> forearm -> upper arm -> shoulder, on both sides
            bool chainsOk = haveParents;
            for (int k = LeftShoulder + 1; haveParents && k <= LeftHand; k++) chainsOk &= parentSlot[k] == slot[k - 1];
            for (int k = RightShoulder + 1; haveParents && k <= RightHand; k++) chainsOk &= parentSlot[k] == slot[k - 1];

            var text = new System.Text.StringBuilder($"RA01 VRIK bone map: name list {nameCount} bones (V's live list has {liveCount}), parent list {(haveParents ? "found" : "NOT found")}, arm chains {(chainsOk ? "OK" : haveParents ? "WRONG" : "unchecked")}");
            for (int k = 0; k < Names.Length; k++)
            {
                string parentName = parentSlot[k] switch { -2 => "?", -1 => "none", _ => NameAtSlot(parentSlot[k]) ?? $"slot {parentSlot[k]}" };
                text.Append($"{Environment.NewLine}RA01   {Names[k],-14} slot {slot[k],3}, parent {parentName}");
            }
            Log.Write(text.ToString());
            var rest = new float[restAt != 0 ? nameCount * 12 : 0];
            if (restAt != 0) fixed (float* p = rest) if (!Read(restAt, p, nameCount * 48)) rest = Array.Empty<float>();
            restPose = rest;                  // RA01: M9-3 - a copy, so nothing later reads the rig's memory again; checked in MeasureFit before use
            restTrusted = false;
            Log.Write($"RA01 VRIK rest pose: {(rest.Length > 0 ? $"found ({nameCount} bones) - checked against the live bone lengths on the first frame" : "NOT found - shoulders and wrists stay on the game's animation")}");
            if (chainsOk) parentOf = parents; // RA01: M8-1 - kept for BodyPlace; set before mapTrusted so no frame ever sees trusted-but-empty
            mapTrusted = chainsOk && nameCount <= liveCount;
            if (!mapTrusted) Log.Write("RA01 VRIK bone map: not trusted (see above) - VRIK will stay off; the live positions are still logged once for checking");
            RigNames = nameList; // RA01: M14-1a
            mapReady = true;
        }
        catch (Exception e) { Log.Write($"RA01 VRIK bone map: FAILED - {e.Message}"); }
        finally
        {
            mappedSkeleton = skeleton;
            Interlocked.Exchange(ref searchingSkeleton, 0);
        }
    }

    // RA01: looks through V's rig object, and every object it points to, for list headers (the game's lists are: pointer to the entries, then capacity and length
    // as two 4-byte numbers). A name list is recognised by starting with Root's name code and containing RightForeArm's; a parent list by starting at -1
    // and every entry pointing to an earlier bone.
    // A parent list is only used if it is exactly as long as the name list, so a list belonging to some other rig can't be paired with it by mistake.
    // Cost - CPU: low, once per load (up to 129 objects of 1 KB each, read safely, plus a quick test of each list found); GPU: none.
    static void FindLists(nint skeleton, out nint namesAt, out int nameCount, out nint parentsAt, out int parentCount, out nint restAt)
    {
        var names = new List<(nint at, int length)>();
        var parents = new List<(nint at, int length)>();
        var rests = new List<(nint at, int length)>();
        var level1 = new List<nint> { skeleton };
        var level2 = new List<nint>();
        byte* buf = stackalloc byte[ObjectScanBytes];
        foreach (var level in new[] { level1, level2 })
        {
            foreach (nint obj in level)
            {
                if (!Read(obj, buf, ObjectScanBytes)) continue;
                for (int o = 0; o + 16 <= ObjectScanBytes; o += 8)
                {
                    nint ptr = *(nint*)(buf + o);
                    if (!LooksLikePointer(ptr)) continue;
                    if (level == level1 && level2.Count < 128) level2.Add(ptr);
                    int length = *(int*)(buf + o + 12);
                    if (length < MinListLength || length > MaxListLength) continue;
                    if (IsNameList(ptr, length)) names.Add((ptr, length));
                    else if (IsParentList(ptr, length)) parents.Add((ptr, length));
                    else if (IsRestList(ptr, length)) rests.Add((ptr, length));
                }
            }
            if (names.Count > 0 && parents.Exists(p => p.length == names[0].length) && rests.Exists(p => p.length == names[0].length)) break; // RA01: all three found on this level - no need to go deeper
        }
        namesAt = names.Count > 0 ? names[0].at : 0;
        nameCount = names.Count > 0 ? names[0].length : 0;
        int n = nameCount;
        var match = parents.Find(p => p.length == n);
        parentsAt = match.at;
        parentCount = match.length;
        restAt = rests.Find(p => p.length == n).at; // RA01: M9-3 - 0 if no rest-pose list of the same length was found
    }

    // RA01: M9-3 - a rest-pose list: one 48-byte bone transform per bone (position, turn, size), recognised by its first two entries having a turn of length 1 and a
    // size of exactly (1, 1, 1). Whether it really is V's rest pose is checked later against her live bone lengths (MeasureFit) before anything uses it.
    // Cost - CPU: extremely low (one 96-byte safe read); GPU: none.
    static bool IsRestList(nint at, int length)
    {
        float* f = stackalloc float[24];
        if (!Read(at, f, 96)) return false;
        for (int e = 0; e < 2; e++)
        {
            float* t = f + e * 12;
            float q = t[4] * t[4] + t[5] * t[5] + t[6] * t[6] + t[7] * t[7];
            if (!(q > 0.99f && q < 1.01f) || MathF.Abs(t[8] - 1f) > 1e-3f || MathF.Abs(t[9] - 1f) > 1e-3f || MathF.Abs(t[10] - 1f) > 1e-3f) return false;
        }
        return true;
    }

    // RA01: Cost - CPU: extremely low (one 8-byte read, then at most a few KB read once if the first entry matches); GPU: none.
    static bool IsNameList(nint at, int length)
    {
        ulong first;
        if (!Read(at, &first, 8) || first != codes[0]) return false; // RA01: codes[0] is Root
        var list = new ulong[length];
        fixed (ulong* p = list) if (!Read(at, p, length * 8)) return false;
        return Array.IndexOf(list, codes[10]) >= 0; // RA01: codes[10] is RightForeArm
    }

    // RA01: Cost - CPU: extremely low (one read of at most 8 KB and one pass over it); GPU: none.
    static bool IsParentList(nint at, int length)
    {
        var list = new short[length];
        fixed (short* p = list) if (!Read(at, p, length * 2)) return false;
        if (list[0] != -1) return false;
        for (int i = 1; i < length; i++) if (list[i] < 0 || list[i] >= i) return false;
        return true;
    }

    // RA01: the one-off live check: each mapped bone's position relative to its parent and that distance (its bone length) - the forearm should read about
    // 0.29 m - plus the first 26 live slots, to see whether the live list starts in the rig file's order (Root, Hips, Spine...).
    // Cost - CPU: extremely low, once per load (a few dozen float reads and one log write); GPU: none.
    static void ReportLive(byte* bones, int count)
    {
        var text = new System.Text.StringBuilder($"RA01 VRIK live check ({count} live bones):");
        for (int k = 0; k < Names.Length; k++)
        {
            if (slot[k] < 0 || slot[k] >= count) { text.Append($"{Environment.NewLine}RA01   {Names[k],-14} slot {slot[k]} is outside the live list"); continue; }
            float* p = (float*)(bones + slot[k] * SlotBytes);
            text.Append($"{Environment.NewLine}RA01   {Names[k],-14} offset from parent ({p[0]:F4}, {p[1]:F4}, {p[2]:F4}) = {MathF.Sqrt(p[0] * p[0] + p[1] * p[1] + p[2] * p[2]):F4} m");
        }
        for (int i = 0; i < Math.Min(26, count); i++)
        {
            float* p = (float*)(bones + i * SlotBytes);
            text.Append($"{Environment.NewLine}RA01   live slot {i,2}: ({p[0]:F4}, {p[1]:F4}, {p[2]:F4}) = {MathF.Sqrt(p[0] * p[0] + p[1] * p[1] + p[2] * p[2]):F4} m");
        }
        Log.Write(text.ToString());
    }

    // RA01: which of our named bones sits at a slot, for readable log lines.
    // Cost - CPU: extremely low (a walk over 12 numbers); GPU: none.
    static string? NameAtSlot(int s)
    {
        for (int k = 0; k < Names.Length; k++) if (slot[k] == s) return Names[k];
        return null;
    }

    // RA01: the game's name code: FNV-1a 64-bit over the name's bytes - start from a fixed number, then for each letter mix it in and multiply by a fixed prime.
    // Cost - CPU: extremely low (a dozen letters, done once per name at start-up); GPU: none.
    internal static ulong NameCode(string name)
    {
        ulong h = 0xCBF29CE484222325;
        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(name)) { h ^= b; h *= 0x100000001B3; }
        return h;
    }

    // RA01: a plausible heap address: above the first 64 KB, inside user space, 8-byte aligned.
    // Cost - CPU: extremely low (three compares); GPU: none.
    static bool LooksLikePointer(nint p) => p > 0x10000 && p < 0x7FFF_FFFF_FFFF && (p & 7) == 0;

    // RA01: safe read - a bad address fails this one call instead of crashing the game.
    // Cost - CPU: extremely low (one ReadProcessMemory call); GPU: none.
    static bool Read(nint address, void* buffer, int bytes)
    {
        nuint got;
        return ReadProcessMemory(Self, address, buffer, (nuint)bytes, &got) && got == (nuint)bytes;
    }
}
