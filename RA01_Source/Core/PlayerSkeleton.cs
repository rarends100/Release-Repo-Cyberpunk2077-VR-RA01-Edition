namespace RA01.Core;

// RA01: M7-6 - the permanent V-only doorway. PoseHook hands over every animated body's finished bone list; this class works out which one is V and passes on
// only hers, to whoever registered with SetHandler (Visuals). Male or female, first load or after a save load, no mod and no fingerprint.
// How V is recognised (proven in M7-5, when shrinking only this body lowered the first-person camera): V runs on her own player rig files, so the "skeleton"
// value the game hands us for her is one no NPC shares, while every NPC of a body type points at the same shared rig. The hunt, rechecked once a second:
// among bodies with at least MinBones bones, take the one alive longest (V loads first and never leaves); if at most MaxVBodies bodies use its skeleton
// value, that value is V's. After that, "is this V?" is one compare per body per frame.
// M13-3 - V is STICKY and HANDED OVER. Putting on arm cyberware (Mantis Blades, the launcher...) makes the game build V a brand-new body, and a new body is
// the youngest on site, not the oldest - so the old rule picked an older NPC (once a ripperdoc took over the VR controls). Now:
//  - once found, V stays V as long as her skeleton value is still animated and still hers alone, whatever is older;
//  - when her body stops, the new V is the unshared body with at least InGameMinBones bones that first moved close to that moment (HandoverBeforeMs before it
//    to HandoverAfterMs after) - the rebuilt V coming back through the gate; with several, the one with the most bones (V wears more than any NPC);
//  - only when no such body turns up in time (a save load rebuilds everything) does the old "oldest body" rule pick again.
// M13-5 - the handover is quick: V's body counts as stopped once it has missed VStaleMs of animation while the rest of the world kept moving (it used to wait
// for the 2-second forget, then the next once-a-second check - VRIK sat frozen 2-4 s on every cyberware draw), and the check runs every HuntEveryMs. A pause
// or loading screen stops every body at once, so it never counts as V stopping; if the old body moves again during the handover wait, it is simply kept.
// Built to survive game updates: nothing here is tied to one game version. PoseHook finds its spot by a byte pattern (and refuses to install if it isn't
// found exactly once); the hunt uses no offsets into the exe, no bone lengths and no pointers saved between launches. The only layout this relies on - the
// list's header (bones pointer at +0x0, count at +0xC) and 48-byte bone slots (position, rotation, size) - is checked on every candidate before it is trusted:
// slot 0 must look like a root bone (rotation of length 1, size 1), checked each time the game hands the list over. If an update changes that layout the check fails, nothing is ever handed on or written,
// and the log says why once - the game just runs as normal.
// Core boundary: this only identifies which bone list is the player's; it never decides anything about the body itself.
// Cost - CPU: low (per body per frame, a few hundred times a second: one short lock and a walk over at most 256 small entries, plus one compare; ten times a
// second (M13-5) a few more walks over the same entries, reading only this class's own table); GPU: none.
internal static unsafe class PlayerSkeleton
{
    const int SlotBytes = 0x30;         // RA01: each bone: position (16 bytes), rotation (16), size (16)
    const int RotationAt = 0x10;
    const int SizeAt = 0x20;
    const int MaxTracked = 256;         // RA01: M7-5 saw up to 177 bodies at once in a street crowd
    const int MinBones = 180;           // RA01: V's rig has 181-183 bones (619 in game once clothes and face are added); props, doors and cars have 64 or fewer
    const int MaxBones = 1024;          // RA01: sanity ceiling - no real body comes close
    const int MaxVBodies = 2;           // RA01: V's value may be shared by her first- and third-person bodies, never by a crowd
    const long ForgetAfterMs = 2000;    // RA01: a body not seen for this long has despawned (or V was rebuilt by a save load)
    const long HuntEveryMs = 100;        // RA01: M13-5 - was 1000
    const long VStaleMs = 250;           // RA01: M13-5 - V's body missing this long, with the world running, = stopped
    const long HandoverBeforeMs = 5000;  // RA01: M13-3 - the rebuilt body may start moving a little before the old one stops...
    const long HandoverAfterMs = 4000;   // RA01: ...or a little after; outside this window it isn't a handover

    static readonly nint[] lists = new nint[MaxTracked];   // RA01: 0 means an empty entry
    static readonly nint[] skeletons = new nint[MaxTracked];
    static readonly long[] firstSeen = new long[MaxTracked];
    static readonly long[] lastSeen = new long[MaxTracked];
    static readonly int[] boneCounts = new int[MaxTracked];   // RA01: read when the game hands the list over, so the hunt never touches a body that may have just despawned
    static readonly bool[] rootOk = new bool[MaxTracked];
    static readonly object gate = new();                   // RA01: the game animates bodies on several threads at once, so the table is only touched under this lock
    static long lastHuntMs;
    static nint vSkeleton;                                  // RA01: the hunt's answer; 0 until found. Read without the lock (a pointer-sized read is atomic on x64)
    static int vBones;                                      // RA01: for the status line only (M10-4: and InGame)
    static long vLastPoseMs;                                // RA01: M10-4 - when V's body was last handed over (Environment.TickCount64)
    const int InGameMinBones = 500;                         // RA01: M10-4 - in-game V has 619 bones; the start screen's menu model has 294
    const long InGameStaleMs = 500;                         // RA01: M10-4 - no pose for this long = the game stopped animating V (a loading screen)
    static bool layoutWarned;
    static nint lostSkeleton;                               // RA01: M13-3 - V's last skeleton value after her body stopped (0 = no handover pending)
    static int lostBones;
    static long lostMs;                                     // RA01: when that body was last animated
    static long lastAnyPoseMs, worldRunningSinceMs;          // RA01: M13-5 - the last time any body was handed over, and when the world last resumed after a stop
    static delegate*<nint, int, void> handler;             // RA01: set once by Visuals; gets (first bone slot, bone count) for V's bodies only

    // RA01: plugs the hunt into PoseHook's doorway. Call once, after PoseHook.Install.
    // Cost - CPU: extremely low (stores one pointer, once); GPU: none.
    public static void Install() => PoseHook.SetHandler(&OnPoseList);

    // RA01: Visuals signs up here to be handed V's bone slots, every frame, on the game's animation threads (so whatever it does must be quick and thread-safe).
    // Cost - CPU: extremely low (stores one pointer, once); GPU: none.
    public static void SetHandler(delegate*<nint, int, void> h) => handler = h;

    // RA01: M8-0 - the skeleton value currently picked as V's (0 if none), so Visuals can read V's rig (bone names, parents) from the same object.
    // RA01: Cost - CPU: extremely low (one pointer read); GPU: none.
    public static nint Current => vSkeleton;

    // RA01: M38-0 - V's animation float tracks for the pose being handed to Visuals right now (the pose's list at +0x18: pointer, count at +0x24, 4 bytes each -
    // RA01: exe+0x4A9A42 sizes it from the source pose's +0x5C). Holds e.g. the Monowire's "wire slide" numbers. Valid only during the V-pose call; 0 / 0 otherwise.
    // RA01: Cost - CPU: extremely low (two reads); GPU: none.
    public static nint TracksAt => vTracks;
    public static int TrackCount => vTrackCount;
    static nint vTracks;
    static int vTrackCount;

    // RA01: M10-4 - true while the player is actually in the world: V's full in-game body (not the start screen's menu model) has been animated within the last half
    // RA01: second. False on the start screen and on loading screens (the game stops animating V there). The HUD panel only shows while this is true.
    // RA01: M20-7 - a stand-in body (GameAnchor.OnStandIn - the Militech training avatar has 294 bones, like the menu model) also counts.
    // RA01: M20-9 - so does looking through any camera that isn't V's (GameAnchor.CameraNotV: a taken-over security camera - the game stops animating V then).
    // RA01: Cost - CPU: extremely low (a clock read and two compares); GPU: none.
    public static bool InGame => GameAnchor.CameraNotV || vSkeleton != 0 && (vBones >= InGameMinBones || GameAnchor.OnStandIn) && Environment.TickCount64 - Volatile.Read(ref vLastPoseMs) < InGameStaleMs;

    // RA01: Dev Only Tools status line.
    // Cost - CPU: extremely low (one short string, only while the menu shows it); GPU: none.
    public static string Status() => vSkeleton != 0 ? $"found - V's skeleton 0x{vSkeleton:X}, {vBones} bones" : layoutWarned ? "not found - the bone layout looks different (game update?), see log" : "searching";

    // RA01: called by PoseHook for every animated body, every frame.
    // Cost - CPU: low (see the class comment); GPU: none.
    static void OnPoseList(nint list, nint skeleton)
    {
        long now = Environment.TickCount64;
        string? report = null;
        lock (gate)
        {
            if (now - lastAnyPoseMs > VStaleMs) worldRunningSinceMs = now; // RA01: M13-5 - nothing was animated for a while (pause, loading) - the world just resumed
            lastAnyPoseMs = now;
            Track(list, skeleton, now);
            if (now - lastHuntMs >= HuntEveryMs) { lastHuntMs = now; report = Hunt(now); }
        }
        if (report is not null) Log.Write(report); // RA01: outside the lock so a slow log file never holds up the game's animation threads

        if (skeleton == 0) return;
        if (skeleton != vSkeleton) { WeaponProbe.Observe(list, skeleton, now); return; } // RA01: M13-1 - every other body goes past the (read-only) weapon probe
        Volatile.Write(ref vLastPoseMs, now); // RA01: M10-4 - V's body is being animated right now
        delegate*<nint, int, void> h = handler;
        if (h == null) return;
        byte* bones = *(byte**)list;
        int count = *(int*)(list + 0xC);
        int tracks = *(int*)(list + 0x24);                                           // RA01: M38-0 - the pose's float tracks, for VRIK's Monowire wire
        vTracks = tracks > 0 && tracks <= 1024 ? *(nint*)(list + 0x18) : 0;
        vTrackCount = vTracks != 0 ? tracks : 0;
        if (bones != null && count >= MinBones && count <= MaxBones) h((nint)bones, count);
        vTracks = 0; vTrackCount = 0;
    }

    // RA01: drops bodies not seen lately, then finds or adds this one and marks it seen.
    // Cost - CPU: extremely low (at most 256 entries, a few compares each, plus the seven-float root check below); GPU: none.
    static void Track(nint list, nint skeleton, long now)
    {
        int mine = -1, free = -1;
        for (int i = 0; i < MaxTracked; i++)
        {
            if (lists[i] != 0 && now - lastSeen[i] > ForgetAfterMs) lists[i] = 0;
            if (lists[i] == 0) { if (free < 0) free = i; continue; }
            if (lists[i] == list) mine = i;
        }
        if (mine < 0)
        {
            if (free < 0) return; // RA01: table full - an untracked body can never be picked as V
            mine = free;
            lists[mine] = list;
            firstSeen[mine] = now;
        }
        skeletons[mine] = skeleton;
        lastSeen[mine] = now;
        boneCounts[mine] = *(int*)(list + 0xC);
        rootOk[mine] = LooksLikeRoot(list);
    }

    // RA01: M14-0c - V straight from her own entity: among the bodies this class has seen animated lately, the one with the most bones whose skeleton value is
    // one of V's own (GameAnchor.IsVSkeleton) - her full body, not a smaller animated part of her. With no V right now (a loading screen) the last answer
    // is kept - nothing is animated then anyway. Returns a log line only when V changes.
    // Cost - CPU: extremely low (one walk over at most 256 entries with up to 8 compares each, ten times a second); GPU: none.
    static string? FromAnchor(long now)
    {
        if (!GameAnchor.HasV) return null;
        int best = -1;
        for (int i = 0; i < MaxTracked; i++)
        {
            if (lists[i] == 0 || now - lastSeen[i] > VStaleMs || !rootOk[i] || boneCounts[i] < MinBones || boneCounts[i] > MaxBones) continue;
            if (!GameAnchor.IsVSkeleton(skeletons[i])) continue;
            if (best < 0 || boneCounts[i] > boneCounts[best]) best = i;
        }
        if (best < 0) return null;
        lostSkeleton = 0;
        vBones = boneCounts[best];
        if (rigChecked != skeletons[best]) { rigChecked = skeletons[best]; WeaponProbe.CheckPlayerRig(skeletons[best], vBones); }
        if (skeletons[best] == vSkeleton) return null;
        nint was = vSkeleton;
        vSkeleton = skeletons[best];
        return $"RA01 player skeleton: V's body 0x{vSkeleton:X} ({vBones} bones) taken from the game's own record of V (was 0x{was:X})";
    }
    static nint rigChecked; // RA01: M14-0c - the skeleton WeaponProbe last checked as V's rig (once her bone count is known)

    // RA01: picks V (see the class comment): keep her while her body is alive, else hand over to her rebuilt body, else the old "oldest body" rule.
    // Returns a log line only when the answer changes.
    // Cost - CPU: extremely low (a few walks over at most 256 entries, ten times a second; reads only this class's own table, never game memory); GPU: none.
    static string? Hunt(long now)
    {
        string? note = null;
        if (GameAnchor.Working) return FromAnchor(now); // RA01: M14-0c - the game's own record of V decides; the hunt below is the backup
        if (vSkeleton != 0)
        {
            int mineUsers = Users(vSkeleton, out int mine);
            bool moving = now - Volatile.Read(ref vLastPoseMs) <= VStaleMs || now - worldRunningSinceMs <= VStaleMs; // RA01: M13-5
            if (mineUsers > 0 && mineUsers <= MaxVBodies && rootOk[mine] && moving) { vBones = boneCounts[mine]; return null; } // RA01: M13-3 - sticky: still her, still hers alone
            lostSkeleton = vSkeleton; lostBones = vBones; lostMs = Volatile.Read(ref vLastPoseMs);
            vSkeleton = 0;
            note = $"RA01 player skeleton: V's body 0x{lostSkeleton:X} ({lostBones} bones) stopped - looking for her rebuilt body";
        }

        if (lostSkeleton != 0)
        {
            if (note is null && Users(lostSkeleton, out int back) > 0 && now - lastSeen[back] <= VStaleMs && rootOk[back]) // RA01: M13-5 - the old body moved again
            {
                vSkeleton = lostSkeleton; vBones = boneCounts[back]; lostSkeleton = 0;
                Volatile.Write(ref vLastPoseMs, now);
                return $"RA01 player skeleton: V's body 0x{vSkeleton:X} moved again - kept";
            }
            int best = -1;
            for (int i = 0; i < MaxTracked; i++)
            {
                if (lists[i] == 0 || skeletons[i] == lostSkeleton || !rootOk[i] || boneCounts[i] < InGameMinBones || boneCounts[i] > MaxBones) continue;
                if (now - lastSeen[i] > VStaleMs) continue; // RA01: M13-5 - only a body that is moving right now
                if (firstSeen[i] < lostMs - HandoverBeforeMs || firstSeen[i] > lostMs + HandoverAfterMs) continue;
                if (Users(skeletons[i], out _) > MaxVBodies) continue;
                if (best < 0 || boneCounts[i] > boneCounts[best]) best = i;
            }
            if (best >= 0)
            {
                vSkeleton = skeletons[best]; vBones = boneCounts[best];
                string handed = $"RA01 player skeleton: V's body was rebuilt - handed over from 0x{lostSkeleton:X} ({lostBones} bones) to 0x{vSkeleton:X} ({vBones} bones), " +
                                $"which first moved {(firstSeen[best] - lostMs) / 1000.0:+0.0;-0.0} s from when the old one stopped";
                WeaponProbe.CheckPlayerRig(vSkeleton, vBones);
                lostSkeleton = 0;
                return note is null ? handed : note + "\n" + handed;
            }
            if (now - lostMs <= HandoverAfterMs + 1000) return note; // RA01: still inside the window - wait (VRIK simply pauses)
            lostSkeleton = 0;
            note = (note is null ? "" : note + "\n") + "RA01 player skeleton: no rebuilt body turned up in time (a save load?) - picking V the start-up way";
        }

        int oldest = -1;
        for (int i = 0; i < MaxTracked; i++)
        {
            if (lists[i] == 0 || boneCounts[i] < MinBones || boneCounts[i] > MaxBones) continue;
            if (oldest < 0 || firstSeen[i] < firstSeen[oldest]) oldest = i;
        }

        nint found = 0;
        int users = 0, bones = 0;
        string? why = null;
        if (oldest >= 0)
        {
            users = Users(skeletons[oldest], out _);
            bones = boneCounts[oldest];
            if (users > MaxVBodies) why = $"the oldest body's skeleton is shared by {users} bodies, so it isn't V's";
            else if (!rootOk[oldest]) why = "the oldest body's first bone doesn't look like a root - the bone layout may have changed in a game update, so nothing is handed on";
            else found = skeletons[oldest];
        }
        if (found == vSkeleton) return note;
        vSkeleton = found;
        vBones = bones;
        string line;
        if (found != 0)
        {
            line = $"RA01 player skeleton: found V - skeleton 0x{found:X}, {bones} bones, {users} bod{(users == 1 ? "y" : "ies")}, alive {(now - firstSeen[oldest]) / 1000.0:F1} s";
            if (bones >= InGameMinBones) WeaponProbe.CheckPlayerRig(found, bones);
        }
        else
        {
            if (why is not null && why.Contains("layout"))
            {
                if (layoutWarned) return note;
                layoutWarned = true;
            }
            line = $"RA01 player skeleton: lost V - {why ?? "no body with enough bones is alive"}";
        }
        return note is null ? line : note + "\n" + line;
    }

    // RA01: M13-3 - how many tracked bodies use this skeleton value, and (in 'last') the most recently animated of them.
    // Cost - CPU: extremely low (one walk over at most 256 entries); GPU: none.
    static int Users(nint skeleton, out int last)
    {
        int users = 0;
        last = -1;
        for (int i = 0; i < MaxTracked; i++)
        {
            if (lists[i] == 0 || skeletons[i] != skeleton) continue;
            users++;
            if (last < 0 || lastSeen[i] > lastSeen[last]) last = i;
        }
        return users;
    }

    // RA01: the update guard: a root bone's rotation is a unit quaternion (length 1) and its size is (1,1,1). If the game ever moves these fields, this fails.
    // M9-5 - the size must be even and 1 (M9-4's whole-body size was dropped, so nothing of ours ever changes her root's size).
    // Cost - CPU: extremely low (seven float reads and a few multiplies, per body per frame); GPU: none.
    static bool LooksLikeRoot(nint list)
    {
        byte* bones = *(byte**)list;
        if (bones == null) return false;
        float* q = (float*)(bones + RotationAt);
        float* s = (float*)(bones + SizeAt);
        float length = q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3];
        if (!(MathF.Abs(length - 1f) < 0.01f) || MathF.Abs(s[0] - s[1]) > 0.01f || MathF.Abs(s[0] - s[2]) > 0.01f) return false; // RA01: a turn of length 1, an even size
        return MathF.Abs(s[0] - 1f) < 0.01f; // RA01: M9-5 - her own size only (VRIK no longer resizes any bone)
    }
}
