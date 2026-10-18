using System.Numerics;
using static RA01.Core.Win32;

namespace RA01.Core;

// RA01: M13-1 - READ-ONLY weapon probe. A gun in Cyberpunk is animated like a little body of its own: it has its own rig (WolvenKit: the Lexington's
// w_handgun__militech_lexington__base1.rig) whose barrel tip is a bone named "fx_muzzle" (the game's "Muzzle" slot - muzzle flash and light hang off it).
// PoseHook already hands over every animated thing's bone list; PlayerSkeleton passes every one that isn't V's on to Observe here.
// How a gun is recognised: each new skeleton value is looked through once, on a background thread, for a list of bone-name codes (FNV-1a, the same codes
// VRIK uses) containing fx_muzzle's code - like checking a parts list for a "barrel tip" part. A rig with one is a gun; anything else is remembered as
// "not a gun" and never searched again. For a gun, its parent list is kept too, so each frame the muzzle's place on the gun can be stacked up from the gun's
// root the same way VRIK stacks V's bones from her feet.
// Published: the most recently animated gun's muzzle position and turn, measured on the gun itself (TryGetMuzzle) - VRIK hangs it off V's WeaponRight bone.
// Logged: each gun skeleton appearing (first animated) and disappearing (not animated for ForgetAfterMs) - draw and holster in a quiet place and the one that
// comes and goes with your draws is yours (NPC guns have skeletons too).
// M13-2 - also recognises arm cyberware by a bone only its rig has (WolvenKit exports, same for male and female V): Mantis Blades "right_blade_eff",
// Monowire "r_nanoWire01_jnt" - and publishes what V seems to be holding (Held), for Actions' crosshair rule. With several guns animated at once (each
// draw also wakes smaller copies - the third-person gun or parts), the one with the most bones is V's. Any other small skeleton that comes and goes is
// logged with its bone count (first MaxOtherLines), so melee weapons can be told apart next (their rigs have no muzzle).
// Gorilla Arms and the Projectile Launch System have no skeleton of their own (their meshes are skinned to V's body), so they never show up here.
// M13-3 - fixes and logging: a gun whose parent list sits one object deeper than its name list was given up on (the search stopped at the first level) -
// now it keeps looking. A gun whose live bone count doesn't match its lists is logged once (it can't be placed). "Other skeleton" lines are only written
// in game (not the start screen) and only for gun-sized rigs (MinLogBones-MaxLogBones). CheckPlayerRig logs which cyberware bones V's own rig carries -
// arm cyberware rebuilds V's body, so the blades may live in her rig rather than in one of their own.
// Nothing is ever written to the game; every rig read goes through ReadProcessMemory (a bad pointer fails one read, never crashes the game).
// M27-6a - THE MAGAZINE BONES (WolvenKit rigs, see the project doc "Gun Rig Magazine Bones"): a gun's rig has a "mag_slot" bone at its magazine well,
// and the magazine is a little skeleton of its own (mag_plug, then "magazine" - the one in the gun - and "magazine_reload" / "reload_magazine" - the spare
// the game's reload animation brings in). The gun search also notes mag_slot (TryGetMagSlot: where it sits from the muzzle), and magazine skeletons are
// recognised by those two names (logged when first animated, with where the magazine bone sits on its plug).
// M27-6d - WRITES (the only ones): V's own gun's mag_slot, for the visible magazine while Actions' physical reload has one out (HoldMagazine), and the Dev
// tab pull test below. Nothing else is ever written.
// M27-6b - the first exception to read-only, a test: while the Dev tab row "Magazine pull test" is on (Tuning.DevMagazinePull, not saved), each magazine
// skeleton's "magazine" bone is written PullTestM further out of its plug (straight away from the plug, the way it slides out of the well) - three floats
// in the game's own bone list for this frame, the same list VRIK writes V's bones into. Off: nothing is written. It answers "does the magazine mesh follow
// its bone?" before we build the real thing.
// M27-6b2 - Rob's test: nothing moved, and no magazine skeleton was ever logged - the magazine has no animated skeleton of its own; it rides on the gun's
// mag_slot bone like a trailer on a hitch. So the test now moves THE HITCH: V's gun's mag_slot bone, PullTestM straight down the gun (the muzzle bone's
// -Z - Actions' log found that is the gun's down on every gun so far), written from where it was when the test started.
// Cost - CPU: low (per non-V body per frame a short locked table lookup; per gun per frame a walk of about ten links; once per new skeleton value a background
// search of a few hundred small reads, a few milliseconds); GPU: none.
internal static unsafe class WeaponProbe
{
    const int SlotBytes = 0x30;              // RA01: each bone: position (16 bytes), rotation (16), size (16) - the same layout as V's
    const int MinBones = 2, MaxBones = 300;  // RA01: guns are small rigs; bodies (180+) and empty lists are skipped
    const int ObjectScanBytes = 0x400;       // RA01: how much of each rig object is looked through for list headers (as VRIK)
    const int MaxTable = 512;                // RA01: skeleton values remembered (guns and not-guns)
    const long ForgetAfterMs = 2000;         // RA01: a gun not animated for this long has been holstered or despawned
    const long SweepEveryMs = 1000;
    const long ReusedGapMs = 250;            // RA01: M60 - a skeleton unseen this long that comes back with a different bone count is a new object at an old address
    static readonly ulong MuzzleCode = NameCode("fx_muzzle");
    // RA01: M62 - barrel tips of the four gun rigs with no fx_muzzle (Rob's WolvenKit rig exports): the Igla and Testera double barrels' "fx_muzzle_right", the
    // RA01: Kang Tao Zhuo's "fx_muzzle_right_01", and the Militech Achilles' (Widow Maker) split barrel "l_front_barrel" / "r_front_barrel" on "barrel".
    static readonly ulong MuzzleRightCode = NameCode("fx_muzzle_right"), MuzzleRight1Code = NameCode("fx_muzzle_right_01");
    static readonly ulong LeftFrontCode = NameCode("l_front_barrel"), RightFrontCode = NameCode("r_front_barrel"), BarrelCode = NameCode("barrel");
    static readonly string[] TipNames = { "", "fx_muzzle_right", "fx_muzzle_right_01", "front barrels (Achilles)" };
    static readonly ulong MantisCode = NameCode("right_blade_eff");     // RA01: M13-2 - a0_003_wa/ma__mantisblade_skeleton.rig
    static readonly ulong MonowireCode = NameCode("r_nanoWire01_jnt");  // RA01: M13-2 - a0_002_wa/ma__monowire_whip.rig
    static readonly ulong GorillaCode = NameCode("r_circle_mechanism_JNT"); // RA01: M13-6 - a0_005_wa/ma__strongarms.rig (in no other arm rig)
    static readonly ulong LauncherCode = NameCode("launcher_base");        // RA01: M13-7 - a0_006_wa/ma__launcher.rig (left arm only)
    static readonly ulong MagSlotCode = NameCode("mag_slot");             // RA01: M27-6a - a gun rig's magazine well
    // RA01: M27-6m - bones some gun rigs have where the OFF hand holds the gun (Rob's WolvenKit rigs): the Mk.31 HMG's "left_grip" / "front_left_handle",
    // RA01: the Tactician's pump "foreend", the Carnage's pump "handle". First found wins (that order).
    static readonly string[] GripBoneNames = { "left_grip", "front_left_handle", "foreend", "handle" };
    static readonly ulong[] GripBoneCodes = GripBoneNames.Select(NameCode).ToArray();
    static readonly ulong MagazineCode = NameCode("magazine");            // RA01: M27-6a - a magazine rig's in-gun magazine
    static readonly ulong MagSpareCode = NameCode("magazine_reload");     // RA01: M27-6a - ...and its spare (most guns)
    static readonly ulong MagSpareCode2 = NameCode("reload_magazine");    // RA01: M27-6a - ...the other spelling (handgun, Achilles, revolvers)
    const float PullTestM = 0.10f;                                         // RA01: M27-6b - how far the test pulls the magazine out
    const int MaxMagLines = 30;                                            // RA01: M27-6a - magazine skeleton lines per session
    static int magLines;
    static Vector3 magSlotPos;                                             // RA01: M27-6a - V's gun's mag_slot, on the gun (as muzzlePos)
    static long magSlotAt;
    static Vector3 gripBonePos;                                            // RA01: M27-6m - V's gun's off-hand grip bone, on the gun (as muzzlePos)
    static long gripBoneAt;
    static string gripBoneName = "";
    static readonly ulong MeleeCode = NameCode("blade_slot");              // RA01: M13-9 - every melee weapon's grip rig (w_katana__grip1, w_fists__grip1...: Root, base, Trajectory, blade_slot)
    const int MaxOtherLines = 60;
    const int MinLogBones = 8, MaxLogBones = 60;   // RA01: M13-3 - only rigs this size get an "other skeleton" line (guns, blades, small props - not crowds)
    const int MaxPlayerBones = 1024;               // RA01: M13-3 - V's rig, for CheckPlayerRig
    const long FreshMs = 300;                // RA01: M13-2 - a gun reading older than this may be replaced by a smaller gun's
    public enum Kind { None, Gun, MantisBlades, Monowire, GorillaArms, ProjectileLauncher, Melee, Fists } // RA01: M13-34 - Fists only come from the game's events (the skeleton reading calls them Melee)
    static readonly nint Self = GetCurrentProcess();

    struct Entry
    {
        public nint Skeleton; public int State; public int Muzzle; public short[]? Parents; public long FirstSeen, LastSeen; public int Bones; public bool Announced, Warned;
        public int MagSlot, Mag, MagSpare;     // RA01: M27-6a - a gun: its mag_slot bone (-1 none); a magazine: its magazine and spare bones
        public int Grip;                       // RA01: M27-6m - a gun: its off-hand grip bone (-1 none; which one: GripBoneNames[GripName])
        public int GripName;
        public int TipHow, TipB, TipBarrel;    // RA01: M62 - which barrel tip (TipNames; 0 = fx_muzzle); Achilles (3): the right front barrel and the "barrel" bone
        public bool HaveBase; public float BX, BY, BZ, DX, DY, DZ; // RA01: M27-6b - the magazine bone's place when the pull test started, and which way is "out"
        public bool HoldBase; public int HoldMode; public float HX, HY, HZ, SX, SY, SZ, OX, OY, OZ; // RA01: M27-6d - mag_slot's own place and size, the mode last written, the grab offset (gun axes)
        public bool HaveLast; public float LX, LY, LZ, FX, FY, FZ, GX, GY, GZ; public long DropAt; // RA01: M27-6e - the last place written (parent axes); a fall's start and its down (parent axes)
    }
    // RA01: State: 0 = being searched, 1 = something else, 2 = a gun, 3 = Mantis Blades, 4 = Monowire, 5 = a melee weapon (M13-9), 6 = a magazine (M27-6a)
    static readonly Entry[] table = new Entry[MaxTable];
    static int used;
    static readonly object gate = new();
    static long nextSweepMs;
    static Vector3 muzzlePos;                // RA01: the latest gun's muzzle, on the gun (metres, the gun's own axes)
    static Quaternion muzzleTurn;
    static long muzzleAt;                    // RA01: Environment.TickCount64 of that reading (0 = none yet)
    static int muzzleBones;
    static nint muzzleSkeleton;
    static long mantisAt, monowireAt;        // RA01: M13-2 - when each cyberware rig was last animated
    static int otherLines;
    static long meleeAt;                     // RA01: M13-9 - when a melee weapon was last animated, and how far its blade_slot sits from the grip
    static long meleeSince;                  // RA01: M13-13 - when the melee rig started moving this time (after FreshMs or more of stillness)
    const long MeleeSettleMs = 1000;         // RA01: M13-13 - the melee rig must keep moving this long to outrank a gun: a gun bash borrows the same rig for 0.5-0.7 s
    static long gunDrawnAt, meleeDrawnAt;    // RA01: M13-20 - when a gun / a melee weapon last appeared ("drawn" in the log): the newer one is the one in V's hand
    static float meleeReach;
    static nint armsSkeleton;                // RA01: M13-6 - the V body CheckPlayerRig last read, and which arm cyberware its rig carries
    static Kind armsKind;

    // RA01: M13-2 - what V seems to be holding right now: a gun (animated within ForgetAfterMs), else Mantis Blades or Monowire, else nothing we know.
    // RA01: M13-6 - arm cyberware first, read from V's own body: drawing any of it rebuilds V with its bones merged into her rig (the logs: Mantis Blades 765 bones,
    // RA01: Gorilla Arms 767, Monowire 713, against 619), so "her current rig carries the blades' marker bone" means the blades are out right now.
    // RA01: M13-13 - a holstered gun keeps animating on V's hip (one was animated 113 s straight through fists and Monowire), so the gun rule used to outrank bare
    // RA01: fists and punches became gun bashes. Now a melee rig that has kept moving for MeleeSettleMs wins over the gun; arm cyberware still comes first (the
    // RA01: Monowire moves the same grip rig).
    // RA01: M13-20 - but a put-away katana keeps animating too: with a gun drawn after it, the old rule flipped to "melee" every second or so and raising the
    // RA01: free hand next to the aiming gun hand made the two-hands guard (LT held). Now the melee rig only beats a gun if it was DRAWN after that gun - like two
    // RA01: tools on a workbench, the one you picked up last is the one in your hand.
    // RA01: Cost - CPU: extremely low (a short lock and a few compares); GPU: none.
    static Kind SkeletonHeld
    {
        get
        {
            long now = Environment.TickCount64;
            lock (gate)
            {
                if (armsKind != Kind.None && armsSkeleton == PlayerSkeleton.Current) return armsKind;
                if (meleeAt != 0 && now - meleeAt < FreshMs && now - meleeSince >= MeleeSettleMs && meleeDrawnAt > gunDrawnAt) return Kind.Melee; // RA01: M13-13, M13-20 - drawn after the gun and out for a second: beats it
                if (muzzleAt != 0 && now - muzzleAt < ForgetAfterMs) return Kind.Gun;
                if (meleeAt != 0 && now - meleeAt < ForgetAfterMs) return Kind.Melee; // RA01: M13-9
                if (mantisAt != 0 && now - mantisAt < ForgetAfterMs) return Kind.MantisBlades;
                if (monowireAt != 0 && now - monowireAt < ForgetAfterMs) return Kind.Monowire;
                return Kind.None;
            }
        }
    }

    // RA01: M13-32 - what V holds, now from the GAME'S OWN DATA first (the user's call: the skeleton reading becomes the backup). The game sends a
    // RA01: M13-33 - what V holds, for Actions. The game's own weapon events are in charge; the skeleton reading only fills the gaps. In order:
    // RA01:  1. Dev tab "Weapon reading: skeleton only" (Tuning.SkeletonWeaponOnly) on: the skeleton reading alone, as before M13-32.
    // RA01:  2. The game last told V she drew something (fists, a bat, a knife, cyberware or a gun): that is what she holds - however still her bones are
    // RA01:     (a melee rig stops animating when V stands still, which made the skeleton reading forget it after 2 s and switched melee off). Two exceptions:
    // RA01:     a. the skeleton saw a gun appear more than EventLeadMs after that event - a gun redrawn that only the NPC copy was told about - go to step 3;
    // RA01:     b. the event named a gun and the skeleton has seen no gun for HolsterMs since - she put it away: nothing. Guns animate the whole time
    // RA01:        they are out, so this is safe for them; fists and melee weapons never get dropped this way.
    // RA01:  3. Otherwise (no event for V yet, the last one said "empty hands", or exception a): nothing seen means nothing held; something seen is named by
    // RA01:     the newest event close to the skeleton's change (WeaponEventProbe.Pick); failing that, a gun keeps V's last gun's name; failing that, the
    // RA01:     skeleton reading's own answer. Like a hotel: the front desk's check-in (event) counts even when the guest sits still; the doorman (skeleton)
    // RA01:     only matters for a guest the desk never heard about, or one whose gun has been out of sight for a while.
    // RA01: HeldName is the exact type in plain English, for the log ("" when only the skeleton reading answered).
    // RA01: Cost - CPU: extremely low (the skeleton reading's compares plus two short locks and a few compares); GPU: none.
    public static Kind Held
    {
        get
        {
            Kind skeleton = SkeletonHeld;
            long now = Environment.TickCount64;
            if (skeleton != lastSkeleton) { lastSkeleton = skeleton; skeletonChangedAt = now; } // RA01: "hands empty" counts as a change too, so a put-away-and-redrawn weapon is a new change
            if (Tuning.SkeletonWeaponOnly) { HeldName = ""; return skeleton; }                    // RA01: step 1
            if (!Tuning.OldWeaponReading && GameAnchor.TryRightHandWeapon(out nint weapon) && weapon != 0
                && WeaponEventProbe.KindOf(weapon, out Kind slotKind, out string slotName))        // RA01: M14-5 - step 0: the game's own right-hand slot
            {
                HeldName = slotName;
                Report(slotKind, slotName, "V's right-hand slot");
                return slotKind;
            }
            bool told = WeaponEventProbe.LatestForV(out Kind said, out string saidName, out long saidAt);
            bool gunSeenSince = skeleton == Kind.Gun && skeletonChangedAt - saidAt > EventLeadMs     // RA01: exception a - M14-5c: only if one of V's own guns was
                && WeaponEventProbe.Pick(skeletonChangedAt, out Kind seenKind, out _) && seenKind == Kind.Gun; // RA01: announced (an enemy's gun animating nearby no longer counts)
            if (told && said != Kind.None && !gunSeenSince)                                        // RA01: step 2
            {
                bool holstered = said == Kind.Gun && skeleton == Kind.None && skeletonChangedAt > saidAt && now - skeletonChangedAt >= HolsterMs; // RA01: exception b
                HeldName = holstered ? "" : saidName;
                Report(holstered ? Kind.None : said, HeldName, "the game's weapon events");
                return holstered ? Kind.None : said;
            }
            HeldName = "";                                                                          // RA01: step 3
            if (skeleton == Kind.None) return skeleton;
            if (WeaponEventProbe.Pick(skeletonChangedAt, out Kind kind, out string name)) { HeldName = name; Report(kind, name, "events near a skeleton change"); return kind; }
            if (told && said == Kind.Gun && skeleton == Kind.Gun) { HeldName = saidName; Report(said, saidName, "the last gun event"); return said; }
            Report(skeleton, "", "skeletons");
            return skeleton;
        }
    }
    public static volatile string HeldName = "";

    // RA01: M14-5 - one log line whenever the answer (or where it came from) changes - so a wrong switch shows which reading made it.
    // Cost - CPU: extremely low (two compares; a log line on a change); GPU: none.
    static void Report(Kind kind, string name, string source)
    {
        if (kind == reportedKind && ReferenceEquals(source, reportedSource)) return;
        reportedKind = kind; reportedSource = source;
        Log.Write($"RA01 what V holds: {kind}{(name.Length > 0 ? $" ({name})" : "")} - from {source}");
    }
    static Kind reportedKind = (Kind)(-1);
    static string reportedSource = "";

    // RA01: M13-47 - true while V holds a Smart weapon (the game's own data says so: WeaponEventProbe tags its name). Smart guns lock on by themselves, so they
    // RA01: keep the head-aimed crosshair.
    // RA01: Cost - CPU: extremely low (one string compare); GPU: none.
    public static bool HeldSmart => HeldName.EndsWith(WeaponEventProbe.SmartTag, StringComparison.Ordinal);

    // RA01: M27-6j - V's right-hand gun's audio set, the model as the game's sounds name it ("unity", "sor22", "sidewinder"...; "" if not known). For Actions'
    // RA01: reload sounds. Cost - CPU: extremely low (a slot read and a dictionary look-up; one database read the first time per gun); GPU: none.
    public static string HeldAudioSet => GameAnchor.TryRightHandWeapon(out nint weapon) && weapon != 0 ? WeaponEventProbe.AudioSetOf(weapon) : "";
    static Kind lastSkeleton;
    static long skeletonChangedAt;
    const long EventLeadMs = 3000;           // RA01: M13-33 - V's own event comes about 0.3 s before the skeleton notices; a gun seen later than this was not announced to V
    const long HolsterMs = 5000;             // RA01: M13-33 - an announced gun unseen this long has been put away

    // RA01: the latest gun's muzzle on the gun itself, if a gun was animated within the last ForgetAfterMs.
    // Cost - CPU: extremely low (a short lock and a few copies); GPU: none.
    public static bool TryGetMuzzle(out Vector3 position, out Quaternion turn, out int bones, out nint skeleton)
    {
        lock (gate)
        {
            position = muzzlePos; turn = muzzleTurn; bones = muzzleBones; skeleton = muzzleSkeleton;
            return muzzleAt != 0 && Environment.TickCount64 - muzzleAt < ForgetAfterMs;
        }
    }

    // RA01: M13-47 - the MUZZLE MAILBOX. VRIK (Visuals) drops in, every frame, where the gun's muzzle sits on V's body and which way it is turned - measured
    // from the hand that holds it, after her arms have followed your controllers. Actions reads it (Visuals never talks to Actions directly). Body
    // axes: X right, Y forward, Z up, from her feet - the same as CameraBodyProbe.OnBody.
    // Cost - CPU: extremely low (a short lock and a few copies, once per frame); GPU: none.
    public static void PublishMuzzleOnBody(Vector3 position, Quaternion turn)
    {
        lock (gate) { bodyMuzzle = position; bodyMuzzleTurn = turn; bodyMuzzleAt = System.Diagnostics.Stopwatch.GetTimestamp(); }
    }

    // RA01: M13-47 - the latest muzzle on V's body; false if none came in the last 100 ms (no gun, or VRIK not running).
    // Cost - CPU: extremely low (a short lock and a few copies); GPU: none.
    public static bool TryReadMuzzleOnBody(out Vector3 position, out Quaternion turn)
    {
        lock (gate)
        {
            position = bodyMuzzle; turn = bodyMuzzleTurn;
            return bodyMuzzleAt != 0 && System.Diagnostics.Stopwatch.GetTimestamp() - bodyMuzzleAt < System.Diagnostics.Stopwatch.Frequency / 10;
        }
    }
    static Vector3 bodyMuzzle;
    static Quaternion bodyMuzzleTurn;
    static long bodyMuzzleAt;

    // RA01: M27-6f - THE GRIP MAILBOX: VRIK drops in, every frame a gun is out, where each of V's hands closes (the middle finger's knuckle) on her body, in the
    // same axes as the muzzle above - so the magazine carried from the hip can be put in her DRAWN fingers, not where the controller's grip point maps to
    // (which sat in her wrist: Rob 22:49). Cost - CPU: extremely low (a short lock and six stores, once per frame); GPU: none.
    // M27-6m - and each hand's own bone (the wrist), so Actions can tell how far the palm sits from the wrist (the two-handed grip puts the PALM on the gun).
    public static void PublishGripsOnBody(Vector3 left, Vector3 right, Vector3 leftWrist, Vector3 rightWrist)
    {
        lock (gate)
        {
            bodyGrips[0] = left.X; bodyGrips[1] = left.Y; bodyGrips[2] = left.Z; bodyGrips[3] = right.X; bodyGrips[4] = right.Y; bodyGrips[5] = right.Z;
            bodyGrips[6] = leftWrist.X; bodyGrips[7] = leftWrist.Y; bodyGrips[8] = leftWrist.Z; bodyGrips[9] = rightWrist.X; bodyGrips[10] = rightWrist.Y; bodyGrips[11] = rightWrist.Z;
            bodyGripsAt = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    // RA01: M27-6m - for Actions: the way from a hand's wrist to where it closes (its middle knuckle), on V's body, as VRIK last drew it. False if stale.
    // Cost - CPU: extremely low (a short lock and a subtraction); GPU: none.
    public static bool TryReadPalmOffset(bool leftHand, out Vector3 wristToPalm)
    {
        lock (gate)
        {
            int k = leftHand ? 0 : 3, w = leftHand ? 6 : 9;
            wristToPalm = new Vector3(bodyGrips[k] - bodyGrips[w], bodyGrips[k + 1] - bodyGrips[w + 1], bodyGrips[k + 2] - bodyGrips[w + 2]);
            return bodyGripsAt != 0 && System.Diagnostics.Stopwatch.GetTimestamp() - bodyGripsAt < System.Diagnostics.Stopwatch.Frequency / 10 && wristToPalm.Length() < 0.25f;
        }
    }
    // RA01: M27-6s - for Actions: a hand's own bone (the wrist) on V's body as VRIK last DREW it. VRIK draws the hands at your targets scaled about your head
    // (to fit her arm length), so anything measured on her drawn body (the muzzle, the magazine well) must be taken against her drawn wrist, not against
    // your unscaled targets. False if stale. Cost - CPU: extremely low (a short lock and three copies); GPU: none.
    public static bool TryReadWrist(bool leftHand, out Vector3 wrist)
    {
        lock (gate)
        {
            int w = leftHand ? 6 : 9;
            wrist = new Vector3(bodyGrips[w], bodyGrips[w + 1], bodyGrips[w + 2]);
            return bodyGripsAt != 0 && System.Diagnostics.Stopwatch.GetTimestamp() - bodyGripsAt < System.Diagnostics.Stopwatch.Frequency / 10;
        }
    }
    static readonly float[] bodyGrips = new float[12];  // RA01: knuckles left x, y, z, right x, y, z; then wrists the same - an array, not Vector3 fields (the ILC quirk)
    static long bodyGripsAt;

    // RA01: called by PlayerSkeleton for every animated body that isn't V's, every frame, on the game's animation threads.
    // M60 - the game reuses a thrown-away object's memory: Rob's log 16:46:54 - V's Widow Maker came back at the address the light machine gun had, and its
    // 46-bone notes didn't fit the rifle's 25 bones, so no muzzle and no two-handed grip until the next draw. Like a parts drawer still labelled for the
    // old gun: a skeleton unseen for ReusedGapMs that comes back with a different bone count gets its notes thrown out and is looked up fresh. A skeleton
    // that changes count while it keeps animating (no gap) is not looked at again, so crowds cost nothing extra.
    // Cost - CPU: low (see the class comment; M60 adds one compare per skeleton per frame, and a background look-up only when an address is reused); GPU: none.
    public static void Observe(nint list, nint skeleton, long now)
    {
        if (skeleton == 0) return;
        int count = *(int*)(list + 0xC);
        if (count < MinBones || count > MaxBones) return;
        byte* bones = *(byte**)list;
        if (bones == null) return;
        string? report = null;
        lock (gate)
        {
            int i = Find(skeleton);
            if (i < 0)
            {
                if (used >= MaxTable) Forget(now);
                if (used >= MaxTable) return;
                i = used++;
                table[i] = new Entry { Skeleton = skeleton, State = 0, FirstSeen = now };
                Task.Run(() => Search(skeleton));
            }
            ref Entry e = ref table[i];
            if (e.State != 0 && count != e.Bones && now - e.LastSeen >= ReusedGapMs) // RA01: M60 - the game reused this address for another object: look again
            {
                if (e.State >= 2) report = $"RA01 weapon probe: skeleton 0x{skeleton:X} came back with {count} bones (was {e.Bones}) - the game reused it, looked up again (M60)";
                e = new Entry { Skeleton = skeleton, State = 0, FirstSeen = now };
                Task.Run(() => Search(skeleton));
            }
            e.LastSeen = now; e.Bones = count;
            if (!e.Announced && e.State != 0)
            {
                e.Announced = true; e.FirstSeen = now;
                if (e.State == 2) gunDrawnAt = now; else if (e.State == 5) meleeDrawnAt = now; // RA01: M13-20 - which was picked up last
                report = e.State switch
                {
                    2 => $"RA01 weapon probe: gun skeleton 0x{skeleton:X} drawn ({count} bones, fx_muzzle is bone {e.Muzzle}{MuzzleReach(bones, e, count)})",
                    3 => $"RA01 weapon probe: Mantis Blades skeleton 0x{skeleton:X} animated ({count} bones)",
                    4 => $"RA01 weapon probe: Monowire skeleton 0x{skeleton:X} animated ({count} bones)",
                    5 => $"RA01 weapon probe: melee weapon skeleton 0x{skeleton:X} drawn ({count} bones, blade_slot {Reach(bones, e, count):F0} cm from the grip)",
                    6 => PlayerSkeleton.InGame && magLines++ < MaxMagLines ? MagLine(bones, e, count) : null,  // RA01: M27-6a
                    _ => count >= MinLogBones && count <= MaxLogBones && PlayerSkeleton.InGame && otherLines++ < MaxOtherLines
                        ? $"RA01 weapon probe: other skeleton 0x{skeleton:X} animated ({count} bones) - not a gun or known cyberware" : null,
                };
            }
            if (e.State == 2 && e.Parents != null && !e.Warned && (e.Parents.Length != count || e.Muzzle >= count))
            {
                e.Warned = true; // RA01: M13-3 - once per gun: its lists don't fit its live bones, so its muzzle can't be placed
                report = $"RA01 weapon probe: gun skeleton 0x{skeleton:X} has fx_muzzle but its lists ({e.Parents.Length} bones) don't match its live bones ({count}) - ignored";
            }
            bool vGun = GameAnchor.IsVWeaponSkeleton(skeleton, out bool vGunKnown);            // RA01: M15-2 - V's own gun, from her right-hand slot
            bool muzzleRule = !Tuning.MuzzleAnyGun && vGunKnown ? vGun                             // RA01: M15-2 - only V's own gun gives the muzzle (an enemy's never)
                : (count >= muzzleBones || skeleton == muzzleSkeleton || now - muzzleAt > FreshMs)  // RA01: the old rule (M13-2): the biggest gun animated right now...
                  && (Tuning.MuzzleAnyGun || e.State != 2 || e.Parents == null || FitsHeld(bones, e, count)); // RA01: M24-5 - ...of the size V's gun type has
            if (e.State == 2 && e.Parents != null && e.Muzzle < count && e.Parents.Length == count && muzzleRule)
            {
                Tip(bones, e, out muzzlePos, out muzzleTurn); // RA01: M13-2 - the biggest gun animated right now wins (V's own, not its smaller copies)
                muzzleAt = now; muzzleBones = count; muzzleSkeleton = skeleton;
                if (e.MagSlot >= 0 && e.MagSlot < count) { Stack(bones, e.MagSlot, e.Parents, out magSlotPos, out _); magSlotAt = now; } // RA01: M27-6a
                if (e.Grip > 0 && e.Grip < count) { Stack(bones, e.Grip, e.Parents, out gripBonePos, out _); gripBoneAt = now; gripBoneName = GripBoneNames[e.GripName]; } // RA01: M27-6m
                if (!HoldMagazine(bones, ref e, count)) PullSlotTest(bones, ref e, count); // RA01: M27-6d - the visible magazine first; M27-6b2 - the Dev tab test (only while on)
            }
            else if (e.State == 6) PullTest(bones, ref e, count);                 // RA01: M27-6b - writes only while the Dev tab test is on
            else if (e.State == 3) mantisAt = now;
            else if (e.State == 4) monowireAt = now;
            else if (e.State == 5) { if (now - meleeAt > FreshMs) meleeSince = now; meleeAt = now; meleeReach = Reach(bones, e, count); } // RA01: M13-9, M13-13 - note when it started moving
            if (now >= nextSweepMs) { nextSweepMs = now + SweepEveryMs; report = Sweep(now) ?? report; }
        }
        if (report != null) Log.Write(report); // RA01: outside the lock, so a slow log file never holds the animation threads
    }

    // RA01: M62 - a gun's barrel tip on the gun: its muzzle bone as always (Stack). The Militech Achilles (Widow Maker) has no muzzle bone, only a barrel split
    // in two (left and right front barrels, 5 cm either side, swinging open when it charges): the tip is halfway between them, like the middle of a
    // tuning fork's two prongs. Their own axes are turned a quarter-turn from a muzzle bone's, so the barrel's direction is taken from the part the
    // "barrel" bone plugs into instead - in Rob's rig exports a normal muzzle bone's direction is exactly its plug's direction (the Kolac's fx_muzzle).
    // Cost - CPU: extremely low (one ten-link stack; the Achilles two more); GPU: none.
    static void Tip(byte* bones, in Entry e, out Vector3 pos, out Quaternion turn)
    {
        Stack(bones, e.Muzzle, e.Parents!, out pos, out turn);
        if (e.TipHow != 3) return;
        Stack(bones, e.TipB, e.Parents!, out Vector3 other, out _);
        pos = (pos + other) * 0.5f;
        int plug = e.Parents![e.TipBarrel];
        if (plug >= 0) Stack(bones, plug, e.Parents, out _, out turn); else turn = Quaternion.Identity;
    }

    // RA01: the muzzle's place on the gun: start at the gun's root and, link by link down its family tree, add each bone's offset (turned by everything above it)
    // and stack its turn - the same building-blocks walk as VRIK's BodyPlace.
    // Cost - CPU: extremely low (about ten links, a quaternion multiply and a vector turn each); GPU: none.
    static void Stack(byte* bones, int bone, short[] parents, out Vector3 pos, out Quaternion turn)
    {
        int* chain = stackalloc int[64];
        int n = 0;
        for (int x = bone; x >= 0 && n < 64; x = parents[x]) chain[n++] = x;
        pos = Vector3.Zero; turn = Quaternion.Identity;
        for (int k = n - 1; k >= 0; k--)
        {
            float* p = (float*)(bones + chain[k] * SlotBytes);
            pos += Vector3.Transform(new Vector3(p[0], p[1], p[2]), turn);
            turn = Quaternion.Normalize(turn * new Quaternion(p[4], p[5], p[6], p[7]));
        }
    }

    // RA01: M24-5 - THE SIZE CHECK for the fallback (V's own gun skeleton not readable): does this gun's length (grip to muzzle) fit the type the game says V
    // holds? Handguns and revolvers are under 35 cm (the rigs read 18-28), SMGs under 45 (about 30), everything else - rifles, shotguns, snipers, HMGs -
    // 35 or more (50-113). So an NPC's 63 cm rifle can't stand in for V's pistol (Rob's test, 2026-10-07 18:35). Unknown type: any size. Logged once per gun.
    // Cost - CPU: extremely low (one ten-link stack and a few string checks per gun per frame, only while V's own gun can't be read); GPU: none.
    static bool FitsHeld(byte* bones, Entry e, int count)
    {
        if (e.Muzzle >= count || e.Parents!.Length != count) return false;
        string held = HeldName;
        if (held.Length == 0) return true;
        Tip(bones, e, out Vector3 tip, out _);
        float reach = tip.Length();
        bool small = held.Contains("Handgun", StringComparison.OrdinalIgnoreCase) || held.Contains("Revolver", StringComparison.OrdinalIgnoreCase) || held.Contains("Pistol", StringComparison.OrdinalIgnoreCase);
        bool smg = held.Contains("SMG", StringComparison.OrdinalIgnoreCase) || held.Contains("Submachine", StringComparison.OrdinalIgnoreCase);
        bool fits = small ? reach < 0.35f : smg ? reach < 0.45f : reach >= 0.35f;
        if (!fits && e.Skeleton != lastMisfit) { lastMisfit = e.Skeleton; Log.Write($"RA01 weapon probe: gun 0x{e.Skeleton:X} ({reach * 100f:F0} cm) skipped for the muzzle - V holds {held}"); }
        return fits;
    }
    static nint lastMisfit;

    // RA01: M13-4 - for the "drawn" log line: how far the barrel tip is from the gun's root (its grip end), like a tape measure laid along the gun - the
    // WolvenKit rigs read 28 cm (revolver), 30 (SMG), 50 (sniper), 55 (rifle), 56 (shotgun), 113 (HMG), so the number tells you which gun the line is.
    // Empty when the gun's lists don't fit its live bones (that case gets its own warning line).
    // Cost - CPU: extremely low (one ten-link stack, once per draw); GPU: none.
    static string MuzzleReach(byte* bones, Entry e, int count)
    {
        if (e.Parents == null || e.Parents.Length != count || e.Muzzle >= count) return "";
        Tip(bones, e, out Vector3 tip, out _);
        return $", muzzle {tip.Length() * 100f:F0} cm out{(e.TipHow != 0 ? $" - no fx_muzzle, tip from {TipNames[e.TipHow]} (M62)" : "")}";
    }

    // RA01: M13-9 - how far a melee weapon's blade_slot sits from its grip, in cm (0 when its lists don't fit its live bones). Fists read about 0, blades far more.
    // Cost - CPU: extremely low (one short stack); GPU: none.
    static float Reach(byte* bones, Entry e, int count)
    {
        if (e.Parents == null || e.Parents.Length != count || e.Muzzle < 0 || e.Muzzle >= count) return 0f;
        Stack(bones, e.Muzzle, e.Parents, out Vector3 tip, out _);
        return tip.Length() * 100f;
    }

    // RA01: M13-9 - the blade reach of the melee weapon being held right now (cm), for Actions' block poses.
    // RA01: Cost - CPU: extremely low (a short lock and a copy); GPU: none.
    public static float MeleeReachCm { get { lock (gate) return meleeReach; } }

    // RA01: once a second: guns not animated lately are logged as holstered and their "announced" flag reset, so the next draw is logged again.
    // Cost - CPU: extremely low (a walk over at most 512 entries); GPU: none.
    static string? Sweep(long now)
    {
        string? report = null;
        for (int i = 0; i < used; i++)
        {
            ref Entry e = ref table[i];
            if (e.State < 2 || !e.Announced || now - e.LastSeen < ForgetAfterMs) continue;
            e.Announced = false;
            string what = e.State switch { 2 => "gun", 3 => "Mantis Blades", 5 => "melee weapon", 6 => "magazine", _ => "Monowire" }; // RA01: M27-6a - magazines too
            report = $"RA01 weapon probe: {what} skeleton 0x{e.Skeleton:X} put away or gone (animated for {(e.LastSeen - e.FirstSeen) / 1000.0:F1} s)";
        }
        return report;
    }

    // RA01: frees entries not seen for a long while (a crowd of NPCs came and went), keeping the table small.
    // Cost - CPU: extremely low (one pass over the table, only when it is full); GPU: none.
    static void Forget(long now)
    {
        int keep = 0;
        for (int i = 0; i < used; i++) if (now - table[i].LastSeen < 60000 || table[i].State == 0) table[keep++] = table[i];
        used = keep;
    }

    // RA01: Cost - CPU: extremely low (a walk over at most 512 entries); GPU: none.
    static int Find(nint skeleton)
    {
        for (int i = 0; i < used; i++) if (table[i].Skeleton == skeleton) return i;
        return -1;
    }

    // RA01: background: looks through the rig object and every object it points to (two levels, as VRIK's FindLists) for a bone-name list containing fx_muzzle,
    // and a parent list of the same length (-1 first, every entry pointing to an earlier bone). Found = a gun; otherwise remembered as not a gun.
    // M13-3 - the parent list is looked for on both levels (it was given up on after the first); a muzzle without one is logged.
    // Cost - CPU: low, once per new skeleton value (up to 129 objects of 1 KB, read safely, plus a quick test of each list found); GPU: none.
    static void Search(nint skeleton)
    {
        int muzzle = -1, blade = -1, length = 0, kind = 1, magSlot = -1, mag = -1, magSpare = -1, grip = -1, gripName = 0, tipHow = 0, tipB = -1, tipBarrel = -1;
        short[]? parents = null;
        try
        {
            var heads = Heads(skeleton, MaxBones);
            foreach (var (at, n) in heads)
            {
                ulong[]? names = Names(at, n);
                if (names is null) continue;
                int m = Array.IndexOf(names, MuzzleCode), how = 0, b2 = -1, bb = -1;
                if (m < 0 && (m = Array.IndexOf(names, MuzzleRightCode)) >= 0) how = 1;        // RA01: M62 - the rigs with no fx_muzzle, in this order
                if (m < 0 && (m = Array.IndexOf(names, MuzzleRight1Code)) >= 0) how = 2;
                if (m < 0 && Array.IndexOf(names, LeftFrontCode) is int lf && lf >= 0 && Array.IndexOf(names, RightFrontCode) is int rf && rf >= 0
                    && Array.IndexOf(names, BarrelCode) is int br && br >= 0) { m = lf; b2 = rf; bb = br; how = 3; }
                if (m >= 0)
                {
                    muzzle = m; length = n; tipHow = how; tipB = b2; tipBarrel = bb; magSlot = Array.IndexOf(names, MagSlotCode);       // RA01: M27-6a - and its magazine well
                    for (int g = 0; g < GripBoneCodes.Length && grip < 0; g++) { grip = Array.IndexOf(names, GripBoneCodes[g]); gripName = g; } // RA01: M27-6m - and an off-hand grip bone
                    break;
                }
                int spare = Array.IndexOf(names, MagSpareCode);
                if (spare < 0) spare = Array.IndexOf(names, MagSpareCode2);
                if (kind == 1 && n <= 40 && spare >= 0 && Array.IndexOf(names, MagazineCode) is int mg && mg >= 0) { kind = 6; mag = mg; magSpare = spare; continue; } // RA01: M27-6a
                if (kind == 1 && n <= 8 && Array.IndexOf(names, MeleeCode) is int b && b >= 0) { kind = 5; blade = b; length = n; } // RA01: M13-9 - the melee grip rig
                else if (kind == 1 && Array.IndexOf(names, MantisCode) >= 0) kind = 3; // RA01: M13-2
                else if (kind == 1 && Array.IndexOf(names, MonowireCode) >= 0) kind = 4;
            }
            if (muzzle >= 0 || blade >= 0) // RA01: M13-9 - a melee grip needs its parent list too (to measure where blade_slot sits)
            {
                foreach (var (at, n) in heads)
                {
                    if (n != length) continue;
                    var list = new short[n];
                    bool ok;
                    fixed (short* p = list) ok = Read(at, p, n * 2);
                    if (!ok || list[0] != -1) continue;
                    bool tree = true;
                    for (int i = 1; i < n && tree; i++) tree = list[i] >= 0 && list[i] < i;
                    if (tree) { parents = list; break; }
                }
                if (parents is null && muzzle >= 0) Log.Write($"RA01 weapon probe: skeleton 0x{skeleton:X} has fx_muzzle ({length} bones) but no parent list was found - not used as a gun");
            }
        }
        catch { }
        lock (gate)
        {
            int i = Find(skeleton);
            if (i < 0) return;
            table[i].State = muzzle >= 0 && parents != null ? 2 : kind; // RA01: M13-9 - a melee grip stays a melee weapon (5) even without a parent list
            table[i].Muzzle = muzzle >= 0 ? muzzle : blade;            // RA01: for a melee weapon, the blade_slot bone
            table[i].Parents = parents;
            table[i].MagSlot = magSlot; table[i].Mag = mag; table[i].MagSpare = magSpare; // RA01: M27-6a
            table[i].Grip = grip; table[i].GripName = gripName;           // RA01: M27-6m
            table[i].TipHow = tipHow; table[i].TipB = tipB; table[i].TipBarrel = tipBarrel; // RA01: M62
        }
    }

    // RA01: M27-6a - V's gun's mag_slot (its magazine well) measured from its muzzle, in the muzzle bone's own axes - so Actions can hang it off the muzzle it
    // already places in the room. False if V's gun has no mag_slot or none was read lately.
    // Cost - CPU: extremely low (a short lock, one turn); GPU: none.
    public static bool TryGetMagSlot(out Vector3 fromMuzzle)
    {
        lock (gate)
        {
            fromMuzzle = Vector3.Transform(magSlotPos - muzzlePos, Quaternion.Conjugate(muzzleTurn));
            long now = Environment.TickCount64;
            return magSlotAt != 0 && magSlotAt == muzzleAt && now - magSlotAt < ForgetAfterMs;
        }
    }

    // RA01: M27-6m - V's gun's off-hand grip bone (see GripBoneNames) measured from its muzzle, in the muzzle bone's own axes, and its name. False if V's gun
    // has none or none was read lately. Cost - CPU: extremely low (a short lock, one turn); GPU: none.
    public static bool TryGetGripBone(out Vector3 fromMuzzle, out string name)
    {
        lock (gate)
        {
            fromMuzzle = Vector3.Transform(gripBonePos - muzzlePos, Quaternion.Conjugate(muzzleTurn));
            name = gripBoneName;
            return gripBoneAt != 0 && gripBoneAt == muzzleAt && Environment.TickCount64 - gripBoneAt < ForgetAfterMs;
        }
    }

    // RA01: M27-6a - the log line for a magazine skeleton: its bones and where the magazine bone sits on its plug (cm, the plug's own axes).
    // Cost - CPU: extremely low (a few reads, once per magazine); GPU: none.
    static string MagLine(byte* bones, Entry e, int count)
    {
        string at = "";
        if (e.Mag >= 0 && e.Mag < count) { float* p = (float*)(bones + e.Mag * SlotBytes); at = $", magazine bone {e.Mag} at ({p[0] * 100:F1}, {p[1] * 100:F1}, {p[2] * 100:F1}) cm on its plug"; }
        return $"RA01 weapon probe: magazine skeleton 0x{e.Skeleton:X} animated ({count} bones{at}, spare bone {e.MagSpare})";
    }

    // RA01: M27-6d - THE VISIBLE MAGAZINE (Rob 19:40: the pull test moved the magazine - "it was great"). Actions\PhysicalReload.cs says what the magazine should do
    // RA01: (SetMagazineHold): 0 in the gun (nothing written), 1 follow the off hand keeping the offset it had when grabbed (the old one being pulled out), 2 hidden
    // RA01: (dropped - its size written as nothing), 3 in the off hand (a new one from the hip, its middle in the palm). Every frame, on V's own gun only, the magazine's
    // RA01: hitch (mag_slot) is written - like the pull test, but the place comes from your real off hand:
    // RA01:  your hand (the room) -> from your head -> the game's camera axes -> the game world (the same head-to-camera pairing the laser uses, run backwards) ->
    // RA01:  the gun's own frame (the muzzle is known both in the world and on the gun, so the gun's root is too) -> mag_slot's parent's frame.
    // RA01: The magazine keeps the gun's angle (only its place moves). A jump of more than MaxHoldReach from its own place is ignored (a bad reading).
    // RA01: M27-6e - Rob 20:26: mode 4 FALLS (an X drop or letting go: FallM down over FallMs, then gone); mode 3 sits CarryAhead toward the fingers (it was in the wrist).
// RA01: Builder picture: a trailer on a hitch - move the hitch ball and the trailer comes along; take the ball off and there's nothing to see.
    // RA01: Cost - CPU: extremely low (a few vector turns and three-six float writes per frame, only while a magazine is out); GPU: none.
    static volatile int holdMode, holdHand;
    const float MaxHoldReach = 1.5f, HoldMagDrop = 0.08f;                  // RA01: metres; the palm holds the magazine this far below its hitch (as Actions' magazine point)
    const float CarryAhead = 0.07f;                                         // RA01: M27-6e - a carried magazine sits this far ahead of the controller's grip point, toward the fingers (Rob: it sat in the wrist)
    const float FallM = 0.20f; const long FallMs = 350;                     // RA01: M27-6e - a dropped magazine falls this far, speeding up, over this long, then is gone (Rob: 15-20 cm, so it is seen dropping)

    // RA01: M27-6d - Actions: what the magazine does now (see HoldMagazine), and which hand is the off hand (0 left, 1 right). Cost - CPU: extremely low (two stores); GPU: none.
    public static void SetMagazineHold(int mode, int offHand) { holdHand = offHand; holdMode = mode; }

    // RA01: M27-6d - writes V's gun's mag_slot for the visible magazine (see SetMagazineHold). True if it wrote (then the Dev pull test stays out of it).
    // Cost - CPU: extremely low (see above); GPU: none.
    static bool HoldMagazine(byte* bones, ref Entry e, int count)
    {
        int mode = holdMode;
        if (mode == 0 || e.MagSlot <= 0 || e.MagSlot >= count || e.Parents == null) { e.HoldBase = false; e.HoldMode = 0; e.HaveLast = false; return false; }
        float* p = (float*)(bones + e.MagSlot * SlotBytes);
        if (!e.HoldBase)
        {
            if (!float.IsFinite(p[0] + p[1] + p[2] + p[8] + p[9] + p[10])) return false;
            e.HX = p[0]; e.HY = p[1]; e.HZ = p[2]; e.SX = p[8]; e.SY = p[9]; e.SZ = p[10]; e.HoldBase = true;
        }
        if (mode == 2) { p[0] = e.HX; p[1] = e.HY; p[2] = e.HZ; p[8] = p[9] = p[10] = 0f; e.HoldMode = 2; return true; }
        Stack(bones, e.Parents[e.MagSlot], e.Parents, out Vector3 parentPos, out Quaternion parentTurn);
        if (mode == 4) return Fall(p, ref e, parentTurn);                                         // RA01: M27-6e - dropped: it falls, then it is gone
        p[8] = e.SX; p[9] = e.SY; p[10] = e.SZ;
        Vector3 hand, down;
        if (!(mode == 3 && GripOnGun(out hand, out down)) && !HandOnGun(e, mode == 3 ? CarryAhead : 0f, out hand, out down, out _)) { p[0] = e.HX; p[1] = e.HY; p[2] = e.HZ; return true; } // RA01: M27-6f - carried: her drawn fingers first
        Quaternion back = Quaternion.Conjugate(parentTurn);
        Vector3 own = parentPos + Vector3.Transform(new Vector3(e.HX, e.HY, e.HZ), parentTurn);   // RA01: the hitch's own place, on the gun
        Vector3 target = hand - down * HoldMagDrop;                                                 // RA01: palm in the middle -> hitch above it
        if (e.HoldMode != mode)
        {
            Vector3 o = mode == 1 ? own - target : Vector3.Zero;                                      // RA01: pulled: keep where it was against the hand, so it doesn't jump
            e.OX = o.X; e.OY = o.Y; e.OZ = o.Z; e.HoldMode = mode;
        }
        target += new Vector3(e.OX, e.OY, e.OZ);
        if (!((target - own).Length() < MaxHoldReach)) { p[0] = e.HX; p[1] = e.HY; p[2] = e.HZ; return true; }
        Vector3 local = Vector3.Transform(target - parentPos, back);
        p[0] = local.X; p[1] = local.Y; p[2] = local.Z;
        e.LX = local.X; e.LY = local.Y; e.LZ = local.Z; e.HaveLast = true;
        return true;
    }

    // RA01: M27-6e - THE FALL: from where the magazine was (in the hand, or in the gun for an X drop) it drops FallM straight down the world (gravity - the gun's
    // down if the world can't be read), speeding up like a real drop (distance grows with time squared), over FallMs; then its size is written as nothing.
    // Cost - CPU: extremely low (a couple of vector turns and six float writes per frame while falling); GPU: none.
    static bool Fall(float* p, ref Entry e, Quaternion parentTurn)
    {
        long now = Environment.TickCount64;
        if (e.HoldMode != 4)
        {
            Vector3 start = e.HaveLast && e.HoldMode is 1 or 3 ? new Vector3(e.LX, e.LY, e.LZ) : new Vector3(e.HX, e.HY, e.HZ);
            Vector3 down = RootTurn(out Quaternion rootTurn) ? Vector3.Transform(-Vector3.UnitZ, Quaternion.Conjugate(rootTurn)) // RA01: the world's down (z up) on the gun
                                                            : Vector3.Transform(-Vector3.UnitZ, muzzleTurn);                   // RA01: no reading: the gun's down
            down = Vector3.Transform(down, Quaternion.Conjugate(parentTurn));
            e.FX = start.X; e.FY = start.Y; e.FZ = start.Z; e.GX = down.X; e.GY = down.Y; e.GZ = down.Z; e.DropAt = now; e.HoldMode = 4;
        }
        float t = (now - e.DropAt) / (float)FallMs;
        if (t >= 1f) { p[0] = e.HX; p[1] = e.HY; p[2] = e.HZ; p[8] = p[9] = p[10] = 0f; return true; }
        float d = FallM * t * t;
        p[0] = e.FX + e.GX * d; p[1] = e.FY + e.GY * d; p[2] = e.FZ + e.GZ * d;
        p[8] = e.SX; p[9] = e.SY; p[10] = e.SZ;
        return true;
    }

    // RA01: M27-6e - V's gun's root turn in the world (from the muzzle, known both in the world and on the gun). Cost - CPU: extremely low (a mailbox read, one turn); GPU: none.
    static bool RootTurn(out Quaternion rootTurn)
    {
        rootTurn = Quaternion.Identity;
        if (!AimProbe.TryMuzzleInWorld(out _, out Quaternion muzzleWorldTurn, out _)) return false;
        rootTurn = Quaternion.Normalize(muzzleWorldTurn * Quaternion.Conjugate(muzzleTurn));
        return true;
    }

    // RA01: M27-6f - V's DRAWN off hand (its middle knuckle, from VRIK's grip mailbox) and the gun's down, in V's gun's own frame - both the knuckle and the muzzle
    // are measured on her body the same frame, so no head or camera sums are involved: gun root on her body = the muzzle on her body, minus the muzzle's place
    // on the gun turned by (body muzzle turn x the muzzle's own turn undone); then the knuckle is taken into that root's frame. False if either is stale (no
    // VRIK, arms not following) - then the controller's mapping (HandOnGun) is used as before.
    // Cost - CPU: extremely low (two short locks and three vector turns); GPU: none.
    static bool GripOnGun(out Vector3 hand, out Vector3 down)
    {
        hand = down = default;
        if (!TryReadMuzzleOnBody(out Vector3 bm, out Quaternion bq)) return false;
        Vector3 grip;
        lock (gate)
        {
            if (bodyGripsAt == 0 || System.Diagnostics.Stopwatch.GetTimestamp() - bodyGripsAt > System.Diagnostics.Stopwatch.Frequency / 10) return false;
            int o = holdHand == 0 ? 0 : 3;
            grip = new Vector3(bodyGrips[o], bodyGrips[o + 1], bodyGrips[o + 2]);
        }
        Quaternion rootTurn = Quaternion.Normalize(bq * Quaternion.Conjugate(muzzleTurn));
        Vector3 rootPos = bm - Vector3.Transform(muzzlePos, rootTurn);
        hand = Vector3.Transform(grip - rootPos, Quaternion.Conjugate(rootTurn));
        down = Vector3.Transform(-Vector3.UnitZ, muzzleTurn);
        return float.IsFinite(hand.X + hand.Y + hand.Z) && hand.Length() < MaxHoldReach;
    }

    // RA01: M27-6d - your off hand and the gun's down, in V's gun's own frame (see HoldMagazine). False if a reading is missing. Uses muzzlePos / muzzleTurn,
    // stacked for this gun just before. Cost - CPU: extremely low (a few vector turns and two mailbox reads); GPU: none.
    static bool HandOnGun(in Entry e, float ahead, out Vector3 hand, out Vector3 down, out Quaternion rootTurnOut)
    {
        hand = down = default; rootTurnOut = Quaternion.Identity;
        if (!Controllers.TryReadFrame(out Controllers.Frame f) || !f.HeadValid || !(holdHand == 0 ? f.LeftValid : f.RightValid)) return false;
        if (!AimProbe.TryMuzzleInWorld(out Vector3 muzzleWorld, out Quaternion muzzleWorldTurn, out CameraBodyProbe.OnBody view)) return false;
        Xr.Posef h = holdHand == 0 ? f.Left : f.Right;
        Quaternion headTurn = Quaternion.Normalize(new Quaternion(f.Head.Orientation.X, f.Head.Orientation.Y, f.Head.Orientation.Z, f.Head.Orientation.W));
        Quaternion handTurn = Quaternion.Normalize(new Quaternion(h.Orientation.X, h.Orientation.Y, h.Orientation.Z, h.Orientation.W));
        Vector3 handAt = new Vector3(h.Position.X, h.Position.Y, h.Position.Z) + Vector3.Transform(new Vector3(0f, 0f, -ahead), handTurn); // RA01: M27-6e - toward the fingers (OpenXR grip: -Z)
        Vector3 fromHead = Vector3.Transform(handAt - new Vector3(f.Head.Position.X, f.Head.Position.Y, f.Head.Position.Z), Quaternion.Conjugate(headTurn));
        Vector3 world = view.WorldHead + Vector3.Transform(new Vector3(fromHead.X, -fromHead.Z, fromHead.Y), view.WorldTurn); // RA01: OpenXR (x right, y up, -z ahead) -> camera (x right, y ahead, z up)
        Quaternion rootTurn = Quaternion.Normalize(muzzleWorldTurn * Quaternion.Conjugate(muzzleTurn));
        rootTurnOut = rootTurn;
        Vector3 rootPos = muzzleWorld - Vector3.Transform(muzzlePos, rootTurn);
        hand = Vector3.Transform(world - rootPos, Quaternion.Conjugate(rootTurn));
        down = Vector3.Transform(-Vector3.UnitZ, muzzleTurn);                // RA01: the gun's down: the muzzle bone's -Z (found on every gun so far, M27-5d)
        return float.IsFinite(hand.X + hand.Y + hand.Z);
    }

    // RA01: M27-6b2 - the magazine pull test on V's gun: while Tuning.DevMagazinePull is on, its mag_slot bone is held PullTestM down the gun (the muzzle bone's
    // -Z, turned into mag_slot's parent's axes) from where it was when the test started; off, that place is forgotten and nothing is written. Logged once
    // each time it starts. Cost - CPU: extremely low (two short stacks and three float writes per frame, only while the test is on); GPU: none.
    static void PullSlotTest(byte* bones, ref Entry e, int count)
    {
        if (!Tuning.DevMagazinePull || e.MagSlot <= 0 || e.MagSlot >= count || e.Parents == null) { e.HaveBase = false; return; }
        float* p = (float*)(bones + e.MagSlot * SlotBytes);
        if (!e.HaveBase)
        {
            Vector3 b = new(p[0], p[1], p[2]);
            if (!float.IsFinite(b.X + b.Y + b.Z) || b.Length() > 2f) return;
            Tip(bones, e, out _, out Quaternion muzzleOnGun);
            Stack(bones, e.Parents[e.MagSlot], e.Parents, out _, out Quaternion parentOnGun);
            Vector3 d = Vector3.Transform(Vector3.Transform(-Vector3.UnitZ, muzzleOnGun), Quaternion.Conjugate(parentOnGun));
            e.BX = b.X; e.BY = b.Y; e.BZ = b.Z; e.DX = d.X; e.DY = d.Y; e.DZ = d.Z; e.HaveBase = true;
            Log.Write($"RA01 magazine pull test: moving mag_slot (bone {e.MagSlot}) of V's gun 0x{e.Skeleton:X} {PullTestM * 100:F0} cm down the gun - look at the magazine");
        }
        p[0] = e.BX + e.DX * PullTestM; p[1] = e.BY + e.DY * PullTestM; p[2] = e.BZ + e.DZ * PullTestM;
    }

    // RA01: M27-6b - the magazine pull test (see the class comment): while Tuning.DevMagazinePull is on, the magazine bone is held PullTestM further out of its
    // plug than where it was when the test started; off, its starting place is forgotten and nothing is written. "Out" is straight away from the plug (the
    // bone's own offset, reversed - it sits up inside the well), or the plug's -Z if it sits on the plug itself. Logged once per magazine each time it starts.
    // Cost - CPU: extremely low (three float writes per magazine per frame, only while the test is on); GPU: none.
    static void PullTest(byte* bones, ref Entry e, int count)
    {
        if (!Tuning.DevMagazinePull || e.Mag < 0 || e.Mag >= count) { e.HaveBase = false; return; }
        float* p = (float*)(bones + e.Mag * SlotBytes);
        if (!e.HaveBase)
        {
            Vector3 b = new(p[0], p[1], p[2]);
            if (!float.IsFinite(b.X + b.Y + b.Z) || b.Length() > 1f) return;
            Vector3 d = b.Length() > 0.005f ? -Vector3.Normalize(b) : -Vector3.UnitZ;
            e.BX = b.X; e.BY = b.Y; e.BZ = b.Z; e.DX = d.X; e.DY = d.Y; e.DZ = d.Z; e.HaveBase = true;
            Log.Write($"RA01 magazine pull test: moving magazine bone {e.Mag} of 0x{e.Skeleton:X} {PullTestM * 100:F0} cm along ({d.X:F2}, {d.Y:F2}, {d.Z:F2}) - look at the gun");
        }
        p[0] = e.BX + e.DX * PullTestM; p[1] = e.BY + e.DY * PullTestM; p[2] = e.BZ + e.DZ * PullTestM;
    }

    // RA01: M13-3 - logs, once, which cyberware bones V's own (rebuilt) rig carries. Called by PlayerSkeleton when it finds or hands over V. Background thread.
    // Cost - CPU: low, once per V body (the same safe two-level search as a gun's, lists up to MaxPlayerBones names); GPU: none.
    public static void CheckPlayerRig(nint skeleton, int liveBones) => Task.Run(() =>
    {
        try
        {
            bool mantis = false, monowire = false, gorilla = false, launcher = false, muzzle = false;
            int longest = 0;
            foreach (var (at, n) in Heads(skeleton, MaxPlayerBones))
            {
                ulong[]? names = Names(at, n);
                if (names is null) continue;
                longest = Math.Max(longest, n);
                mantis |= Array.IndexOf(names, MantisCode) >= 0;
                monowire |= Array.IndexOf(names, MonowireCode) >= 0;
                gorilla |= Array.IndexOf(names, GorillaCode) >= 0;
                launcher |= Array.IndexOf(names, LauncherCode) >= 0;
                muzzle |= Array.IndexOf(names, MuzzleCode) >= 0;
            }
            lock (gate) { armsSkeleton = skeleton; armsKind = mantis ? Kind.MantisBlades : monowire ? Kind.Monowire : gorilla ? Kind.GorillaArms : launcher ? Kind.ProjectileLauncher : Kind.None; } // RA01: M13-6
            Log.Write($"RA01 weapon probe: V's rig 0x{skeleton:X} ({liveBones} live bones, longest name list {longest}) - Mantis Blades bones {(mantis ? "YES" : "no")}, " +
                      $"Monowire bones {(monowire ? "YES" : "no")}, Gorilla Arms bones {(gorilla ? "YES" : "no")}, launcher bones {(launcher ? "YES" : "no")}, fx_muzzle {(muzzle ? "YES" : "no")}");
        }
        catch { }
    });

    // RA01: M13-3 - every list header (pointer, capacity, length) found in the rig object and the objects it points to (two levels, as VRIK's FindLists).
    // RA01: Cost - CPU: low (up to 129 safe reads of 1 KB); GPU: none.
    static List<(nint at, int length)> Heads(nint skeleton, int maxLength)
    {
        var heads = new List<(nint at, int length)>();
        var level2 = new List<nint>();
        byte* buf = stackalloc byte[ObjectScanBytes];
        for (int level = 0; level < 2; level++)
        {
            foreach (nint obj in level == 0 ? new List<nint> { skeleton } : level2)
            {
                if (!Read(obj, buf, ObjectScanBytes)) continue;
                for (int o = 0; o + 16 <= ObjectScanBytes; o += 8)
                {
                    nint ptr = *(nint*)(buf + o);
                    if (ptr <= 0x10000 || ptr >= 0x7FFF_FFFF_FFFF || (ptr & 7) != 0) continue;
                    if (level == 0 && level2.Count < 128) level2.Add(ptr);
                    int n = *(int*)(buf + o + 12);
                    if (n >= MinBones && n <= maxLength) heads.Add((ptr, n));
                }
            }
        }
        return heads;
    }

    // RA01: M13-3 - the n name codes at 'at', or null if they can't be read.
    // Cost - CPU: extremely low (one safe read of n x 8 bytes); GPU: none.
    static ulong[]? Names(nint at, int n)
    {
        var names = new ulong[n];
        fixed (ulong* p = names) return Read(at, p, n * 8) ? names : null;
    }

    // RA01: the game's name code: FNV-1a 64-bit over the name's bytes (as VRIK.NameCode).
    // Cost - CPU: extremely low (a few letters, once at start-up); GPU: none.
    static ulong NameCode(string name)
    {
        ulong h = 0xCBF29CE484222325;
        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(name)) { h ^= b; h *= 0x100000001B3; }
        return h;
    }

    // RA01: safe read - a bad address fails this one call instead of crashing the game.
    // Cost - CPU: extremely low (one ReadProcessMemory call); GPU: none.
    static bool Read(nint address, void* buffer, int bytes)
    {
        nuint got;
        return ReadProcessMemory(Self, address, buffer, (nuint)bytes, &got) && got == (nuint)bytes;
    }
}
