using System.Numerics;
using RA01.Core;

namespace RA01.Actions;

// RA01: M18-0 - TWO-HANDED GRIP: with a gun drawn, reach your off hand (left; right in left-handed mode) to anywhere along the gun and squeeze its grip - that
// spot is now held. Let go of the grip to release.
// While held:
//  - the gun turns like a real rifle in two hands: the spot you grabbed is kept on the line from your gun hand to your off hand. At the grab, the spot is
//    noted in the gun hand's own axes (a fixed point on the gun); each frame the gun hand is turned by the smallest turn that swings that point onto the line
//    to your off hand - like a pipe clamped in two vices: move one vice and the pipe swings about the other. At the grab the point is already on the line, so
//    the gun never jumps. The roll (twist about the barrel) stays your gun hand's.
//  - V's off hand stays on the grabbed spot (and keeps the turn it had against the gun), so it looks held even if your real hand drifts a little.
// LB / RB: neither reaches the game while the off hand is reaching (within ReachMetres of the gun) or holding it, so a grab never fires a game action
// (ControllerMapping.HoldBackShoulders). Away from the gun they work as always.
// M18-1 - SHORT GUNS: how hard the off hand steers depends on how far from the gun hand it grabbed (MinLever to FullLever, never under MinSteer). A pistol
// grabbed 5 cm from the gun hand is a very short lever - a spirit level 5 cm long reads every bump as a big tilt - so there it mostly steadies, a rifle's
// fore-grip 30 cm out steers fully.
// M18-1 - VIRTUAL STOCK: a shoulder pocket on your body (PocketDown below your eyes, PocketSide toward the gun hand, turning with your head's heading only).
// When the back of the gun (StockLength behind the gun hand, along the barrel) comes within BedIn of it, the stock beds in; it lets go past BedOut. While
// bedded, the barrel is swung toward the line from the pocket through your off hand (holding with both hands) or through your gun hand (one hand) -
// Tuning.StockStrength of the way (0 = pure hand aim, 1 = pure stock line). The pocket rides your body, not your wrist, so wrist tremor is spread over the
// long shoulder-to-hand lever and the muzzle wobbles far less - the reason a rifle butt against the shoulder steadies a real shot. Weapons tab "Virtual stock".
// M18-3 - pushing the gun hard into the shoulder: the pivot slides back along the barrel (never a short, swinging line), and the stock only lets go when
// the shoulder ends up beside the gun's line - the butt can't slide off.
// M18-4 - THE OFF HAND'S FINGERS (Weapons tab "Off hand opens and grips"): left to the game until the hand is within grab reach of the weapon; then open,
// closing with the grip squeeze, and a fist while holding (TryOffHandCurl; VRIK bends the fingers).
// M18-5 - the stock beds in WHERE THE BUTT IS (the M18-1 pocket sat 17 cm right of your head - you hold a gun near your cheek - so bedding in dragged the
// butt sideways and it released and re-bedded over and over: the "slide off"). The zone it starts looking in is 12 cm to the side, 17 cm down and 6.5 cm
// back from your eyes; once bedded, that spot is held on your body (it turns with your heading) and the stock lets go only past BedOut off it.
// The barrel's direction in the gun hand's axes is learned each frame from the muzzle VRIK placed last frame against the turn handed out last frame.
// M18-2 - MELEE WEAPONS (katana, machete, bat, pipe, hammer... - WeaponProbe.Kind.Melee; not fists or cyberware) can be held with both hands too, with
// their own Weapons tab row "Two-handed melee". You may grab anywhere on the weapon: the reach zone is simply within MeleeReach of the weapon hand. The
// same swing and short-lever rule apply; there is no virtual stock. LB/RB are held back while holding, and while the off hand is in reach and starting to
// squeeze (past Starting) - not the whole time a melee weapon is out.
// M27-6e - a squeeze in the magazine slot is first a possible MAGAZINE PULL (PhysicalReload.Deciding): the grip waits until that is decided - pulled down = the
// magazine (no grab), anything else or held still = the grip takes hold then (Rob 20:26: grabbing both at once swung the gun with the hand, so the magazine
// hardly moved against it).
// Guns need Physical aiming and the Weapons tab's "Two-handed grip" (or "Virtual stock") on. Called from HandTargets.TryGet (VRIK, every frame), with
// the hand targets on V's body; the weapon kind comes from MeleeActions' look (SetKind), so this never asks WeaponProbe itself.
// Cost - CPU: extremely low (a mailbox read and a distance to a line per frame; while holding, two quaternion turns and an arc); GPU: none.
internal static class TwoHandGrip
{
    const float ReachMetres = 0.16f, BehindHand = 0.15f, PastMuzzle = 0.05f; // RA01: the grab zone: within 16 cm of the gun (M69: was 12), from 15 cm behind the gun hand to 5 cm past the muzzle
    // RA01: M69 - TWO ZONES (Rob 01:20: the off hand only opened once it was already inside the gun's model, and the virtual stock's swing made grabs miss): V's
    // RA01: off hand starts opening within OpenMetres of the barrel line, like reaching for a foregrip; squeezing grabs (and LB/RB are held back) only within
    // RA01: ReachMetres, which also got 4 cm wider to cover the stock's small swing of the drawn gun.
    const float OpenMetres = 0.25f;
    const float GrabSqueeze = 0.5f, LetGoSqueeze = 0.35f;                    // RA01: grip squeeze to grab, and below which it lets go (a little gap, so it doesn't flutter)
    const long HoldBackMs = 150;                                              // RA01: LB/RB stay held back this long after the hand leaves the zone
    const float MinLever = 0.05f, FullLever = 0.30f, MinSteer = 0.3f;        // RA01: M18-1 - off-hand steering grows from MinSteer (lever MinLever or less) to full (FullLever)
    const float PocketDown = 0.17f, PocketSide = 0.12f, PocketBack = 0.065f, StockLength = 0.25f, BedIn = 0.12f, BedOut = 0.20f, StockFacing = 0.5f; // RA01: M18-1/M18-5 - the virtual stock
    static float heldRight, heldForward, heldUp; // RA01: M18-5 - where the butt bedded in, on your body, from your head's centre along your heading's axes (three plain
                                                 // RA01: numbers: ILC 8.0.31 failed "Code generation failed" reading the parts of a stored Vector3 in Stock)
    const float MinLine = 0.30f, StockBehindMin = 0.0f, StockBehindMax = 0.55f; // RA01: M18-3 - the shortest pivot line, and the gun's line behind the hand that the shoulder may sit on
    static bool bedded, haveBarrel;
    static Vector3 barrelInHand = Vector3.UnitY;  // RA01: M18-1 - the barrel's direction in the gun hand's own axes (learned)
    static Quaternion lastGunTurn = Quaternion.Identity;
    static volatile bool gunOut, meleeOut;
    static volatile float handCurl = -1f;   // RA01: M18-4 - how closed V's off hand should be (0 open .. 1 fist), -1 = leave it to the game
    const float MeleeReach = 0.9f, Starting = 0.2f;                          // RA01: M18-2 - a melee weapon: anywhere within 90 cm of the weapon hand; a squeeze past 20% counts as reaching
    static bool holding, gripWasDown, decidingWas; // RA01: M27-6e - decidingWas: PhysicalReload was deciding last frame

    // RA01: M20-3 - for PhysicalSteering: the off hand holds the weapon (then it can't grab the steering wheel). Cost - CPU: extremely low (a read); GPU: none.
    public static bool Holding => holding;
    static Vector3 grabInHand;                 // RA01: the grabbed spot, in the gun hand's own axes (from the gun hand's wrist)
    static Quaternion offTurnInHand;           // RA01: the off hand's turn against the gun hand, at the grab
    static long reachingUntil;

    // RA01: MeleeActions, every look: what V holds (only "is it a gun" is kept). Lets go at once when the gun is gone.
    // Cost - CPU: extremely low (a compare and a store); GPU: none.
    public static void SetKind(WeaponProbe.Kind kind) { gunOut = kind == WeaponProbe.Kind.Gun; meleeOut = kind == WeaponProbe.Kind.Melee; }

    // RA01: M18-4 - for VRIK: how closed V's off hand should be - 0 open (it is within grab reach of the weapon), up to 1 with the grip squeezed, 1 (a fist) while
    // holding. False = out of reach, nothing to hold, or the setting off: leave her fingers to the game. 'leftHand' = it is her left hand (not left-handed).
    // Cost - CPU: extremely low (a read); GPU: none.
    public static bool TryOffHandCurl(out bool leftHand, out float curl)
    {
        curl = handCurl;
        leftHand = !Tuning.LeftHanded;
        return curl >= 0f;
    }

    // RA01: HandTargets: adjusts this frame's hand targets (V's body axes) as the class comment says. Never throws.
    // Cost - CPU: extremely low (see the class comment); GPU: none.
    // M18-1 - 'head' and 'headTurn' are the centre of your head on V's body and its turn there (the shoulder pocket hangs off them).
    public static void Adjust(Vector3 head, Quaternion headTurn, ref Vector3 left, ref Quaternion leftTurn, bool leftOk, ref Vector3 right, ref Quaternion rightTurn, bool rightOk)
    {
        bool lefty = Tuning.LeftHanded;
        bool handsReady = leftOk && rightOk && !SettingsMenu.Open && !MenuSense.ShowsMenu;
        bool gunReady = Tuning.PhysicalAiming && gunOut && handsReady;
        bool melee = meleeOut && handsReady && Tuning.TwoHandMelee;                  // RA01: M18-2
        bool camera = GameAnchor.LookingThroughCamera;                               // RA01: M58 - a taken-over camera: V's gun is back at the console, nothing to grab
        bool allowed = ((gunReady && Tuning.TwoHandGrip) || melee) && !PhysicalSteering.OffHandOnWheel && !camera; // RA01: M20-3 - not while that hand holds the steering wheel
        float squeeze = lefty ? Controllers.RightGrip : Controllers.LeftGrip;
        bool gripDown = squeeze > (gripWasDown ? LetGoSqueeze : GrabSqueeze);
        bool pressed = gripDown && !gripWasDown;
        gripWasDown = gripDown;
        long now = Environment.TickCount64;

        ref Vector3 gunHand = ref lefty ? ref left : ref right;
        ref Quaternion gunTurn = ref lefty ? ref leftTurn : ref rightTurn;
        ref Vector3 offHand = ref lefty ? ref right : ref left;
        ref Quaternion offTurn = ref lefty ? ref rightTurn : ref leftTurn;

        LearnBarrel(gunReady);
        if (!allowed)
        {
            handCurl = -1f;                                                          // RA01: M18-4 - no grab possible: the game's own fingers
            if (holding) Release(camera ? "looking through a camera" : "no weapon / grip off");
            if (camera) ControllerMapping.HoldBackShoulders(false, "looking through a camera (M58 - LB scans)"); else HoldBack(false);
            if (gunReady) Stock(head, headTurn, gunHand, ref gunTurn, gunHand, lefty);
            else if (bedded) { bedded = false; Log.Write("RA01 virtual stock: released (no gun)"); }
            lastGunTurn = gunTurn;
            return;
        }
        // RA01: M27-6t - Rob 06:51: the grab goes by YOUR PHYSICAL HAND - the controller's grip point (the middle of your palm, where you hold it), not the wrist
        // RA01: the arm targets use (7 cm further back) and not V's drawn hand. Out of reach by palm = no grab, wherever V's hand is drawn.
        Vector3 palm = Controllers.TryReadFrame(out Controllers.Frame frame) && (lefty ? frame.RightValid : frame.LeftValid)
                       && HandTargets.TryRoomToBody(frame, lefty ? At(frame.Right) : At(frame.Left), out Vector3 palmOnBody) ? palmOnBody : offHand;
        bool near = melee ? Vector3.Distance(palm, gunHand) <= MeleeReach : Near(palm, gunHand, ReachMetres);
        bool opening = melee ? near : Near(palm, gunHand, OpenMetres);                 // RA01: M69 - the hand opens on the way in, before the grab zone
        // RA01: M27-10 - Rob 08:34: the HMG can be grabbed anywhere on its barrel line (as every gun) OR anywhere on its own handle (the left_grip bone, near the
        // RA01: back of the gun, off the barrel line). HMG only - every other gun and melee are unchanged. One log line per squeeze that misses, for diagnosis.
        if (gunReady && !melee && !near && WeaponProbe.HeldName.StartsWith("Heavy machine gun", StringComparison.Ordinal))
        {
            float gap = HmgHandleGap(palm, gunHand, lefty);
            near = gap <= ReachMetres;
            if (pressed && !near && !holding)
                Log.Write(gap == float.MaxValue ? "RA01 two-handed grip: HMG squeezed - its handle bone isn't read right now"
                                                : $"RA01 two-handed grip: HMG squeezed, your palm is {gap * 100:F0} cm from its handle - needs under {ReachMetres * 100:F0}");
        }
        if (near && (!melee || squeeze > Starting)) reachingUntil = now + HoldBackMs;   // RA01: M18-2 - with a melee weapon, only a starting squeeze counts as reaching

        bool reloadBusy = PhysicalReload.OffHandBusy;                                // RA01: M27-5 - the off hand holds a magazine: no grab, and let go
        bool deciding = PhysicalReload.Deciding;                                     // RA01: M27-6e - "magazine pull or two-handed grip?" not decided yet: wait
        bool decided = decidingWas && !deciding && gripDown;                          // RA01: M27-6e - it turned out to be the grip: take hold now (the grip is still squeezed)
        decidingWas = deciding;
        if (!holding && (pressed || decided) && near && !reloadBusy && !deciding)
        {
            Quaternion undo = Quaternion.Conjugate(gunTurn);
            grabInHand = Vector3.Transform(offHand - gunHand, undo);
            offTurnInHand = Quaternion.Normalize(undo * offTurn);
            holding = true;
            Log.Write($"RA01 two-handed grip: grabbed the {(melee ? "melee weapon" : "gun")} {grabInHand.Length() * 100:F0} cm from your weapon hand");
        }
        else if (holding && !gripDown) Release("grip let go");
        else if (holding && reloadBusy) Release("the off hand pulled the magazine");    // RA01: M27-5
        else if (holding && deciding) Release("waiting - magazine pull or two-handed grip?");  // RA01: M27-6e - grabbed a moment before PhysicalReload saw the squeeze (another thread): let go; it takes hold again if it is the grip
        HoldBack(holding || now < reachingUntil);
        handCurl = !Tuning.OffHandFingers ? -1f : holding ? 1f : near || opening ? Math.Clamp(squeeze, 0f, 1f) : -1f; // RA01: M18-4 - open in reach, closing with the squeeze, a fist while holding; M69 - from OpenMetres
        Vector3 realOff = offHand;
        if (holding)
        {
            // RA01: swing the gun hand so the grabbed spot lies on the line to your off hand (the smallest turn between the two directions), as much as the lever allows
            Vector3 spotDir = Vector3.Transform(grabInHand, gunTurn), handsDir = offHand - gunHand;
            float spotLen = spotDir.Length(), handsLen = handsDir.Length();
            if (spotLen >= 0.02f && handsLen >= 0.02f)
            {
                float steer = Math.Clamp((grabInHand.Length() - MinLever) / (FullLever - MinLever), MinSteer, 1f); // RA01: M18-1 - short guns steer less
                Quaternion swing = Quaternion.Slerp(Quaternion.Identity, Arc(spotDir / spotLen, handsDir / handsLen), steer);
                gunTurn = Quaternion.Normalize(swing * gunTurn);
            }
        }
        if (gunReady) Stock(head, headTurn, gunHand, ref gunTurn, holding ? realOff : gunHand, lefty); // RA01: M18-1 - the virtual stock (guns only), after the two-hand swing
        if (holding)
        {
            offHand = gunHand + Vector3.Transform(grabInHand, gunTurn);          // RA01: V's off hand sits on the grabbed spot
            offTurn = Quaternion.Normalize(gunTurn * offTurnInHand);
        }
        lastGunTurn = gunTurn;
    }

    // RA01: M18-1 - the barrel's direction in the gun hand's own axes: the muzzle VRIK placed last frame, turned back by the gun hand turn handed out last frame.
    // Cost - CPU: extremely low (a mailbox read and two turns); GPU: none.
    static void LearnBarrel(bool gunReady)
    {
        if (!gunReady || !WeaponProbe.TryReadMuzzleOnBody(out _, out Quaternion muzzleTurn)) { haveBarrel = false; return; }
        if (lastGunTurn.LengthSquared() > 0.5f)
        {
            Vector3 b = Vector3.Transform(Vector3.Transform(Vector3.UnitY, muzzleTurn), Quaternion.Conjugate(lastGunTurn));
            if (b.LengthSquared() > 0.5f) { barrelInHand = Vector3.Normalize(b); haveBarrel = true; }
        }
    }

    // RA01: M18-1 - the virtual stock (see the class comment): beds in when the back of the gun reaches the shoulder pocket, and swings the barrel toward the
    // line from the pocket through 'anchor' (your off hand when holding with both hands, else your gun hand), StockStrength of the way. Logs bed in / out.
    // Cost - CPU: extremely low (a few vector sums, one arc and one slerp per frame); GPU: none.
    // M18-5 - not optimised by the native compiler: ILC 8.0.31 failed "Code generation failed" on this method (the M13-11 kind of toolchain fault); the cost of
    // running these few sums unoptimised is negligible.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoOptimization)]
    static void Stock(Vector3 head, Quaternion headTurn, Vector3 gunHand, ref Quaternion gunTurn, Vector3 anchor, bool lefty)
    {
        if (!Tuning.VirtualStock || !haveBarrel) { if (bedded) { bedded = false; Log.Write("RA01 virtual stock: released"); } return; }
        Vector3 facing = Vector3.Transform(Vector3.UnitY, headTurn);
        facing.Z = 0f;                                                                   // RA01: your heading only (V's body axes: x right, y forward, z up)
        if (facing.LengthSquared() < 0.01f) return;
        facing = Vector3.Normalize(facing);
        Vector3 rightSide = Vector3.Cross(facing, Vector3.UnitZ);
        Vector3 barrel = Vector3.Transform(barrelInHand, gunTurn), butt = gunHand - barrel * StockLength;
        bool forward = Vector3.Dot(barrel, facing) > StockFacing;
        if (!bedded && !TryBedIn(head, facing, rightSide, butt, forward, lefty)) return;
        Vector3 pocket = head + rightSide * heldRight + facing * heldForward + Vector3.UnitZ * heldUp;
        float side = SideGap(pocket, gunHand, barrel);                                  // RA01: M18-3 - how far the held pocket is OFF the gun's line behind the hand
        if (side > BedOut || !forward) { Unbed(forward, side); return; }
        // RA01: M18-3 - pushed in, the hand comes closer to the pocket than MinLine: the shoulder is a solid pad, so the pivot slides straight back along the barrel
        // RA01: instead of the line getting short (a short line swung the aim sideways - the butt "slid off" to the right).
        Vector3 line = anchor - pocket;
        float reach = Vector3.Dot(line, barrel);
        if (reach < MinLine) line += barrel * (MinLine - reach);
        if (line.LengthSquared() < 0.01f) return;
        Quaternion swing = Quaternion.Slerp(Quaternion.Identity, Arc(barrel, Vector3.Normalize(line)), Math.Clamp(Tuning.StockStrength, 0f, 1f));
        gunTurn = Quaternion.Normalize(swing * gunTurn);
    }

    // RA01: M18-5 - beds the stock in when the butt is within BedIn of the shoulder zone (12 cm to the gun side, 17 cm down, 6.5 cm back from your eyes) and the gun
    // points forward - holding the butt's own spot on your body (your heading's axes), so nothing is dragged sideways. True = bedded now.
    // Cost - CPU: extremely low (a few vector sums; a log line when it beds in); GPU: none.
    // M18-5 - not optimised by the native compiler either (see Stock; ILC 8.0.31 failed here while the pocket was a stored Vector3).
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoOptimization)]
    static bool TryBedIn(Vector3 head, Vector3 facing, Vector3 rightSide, Vector3 butt, bool forward, bool lefty)
    {
        Vector3 zone = head + rightSide * (lefty ? -PocketSide : PocketSide) - facing * PocketBack - Vector3.UnitZ * PocketDown;
        float gap = Vector3.Distance(butt, zone);
        if (gap >= BedIn || !forward) return false;
        Vector3 off = butt - head;
        heldRight = Vector3.Dot(off, rightSide); heldForward = Vector3.Dot(off, facing); heldUp = off.Z;
        bedded = true;
        string sideName = heldRight >= 0 ? "right" : "left";
        Log.Write("RA01 virtual stock: bedded in (" + Cm(gap) + " cm from the shoulder zone; held " + Cm(MathF.Abs(heldRight)) + " cm " + sideName + ", "
                  + Cm(-heldUp) + " cm down, " + Cm(-heldForward) + " cm back from your eyes)");
        return true;
    }

    // RA01: M18-5 - lets the stock go, with why in the log (plain steps, see TryBedIn).
    // Cost - CPU: extremely low (a log line); GPU: none.
    static void Unbed(bool forward, float side)
    {
        bedded = false;
        string why = forward ? "butt " + Cm(side) + " cm off the shoulder" : "gun no longer pointing forward";
        Log.Write("RA01 virtual stock: released (" + why + ")");
    }

    // RA01: metres as whole centimetres for the log. Cost - CPU: extremely low; GPU: none.
    static string Cm(float metres) => MathF.Round(metres * 100f).ToString(System.Globalization.CultureInfo.InvariantCulture);

    // RA01: M18-3 - how far the pocket is from the gun's line behind the gun hand (StockBehindMin to StockBehindMax back along the barrel): pushing the gun
    // straight into the shoulder never counts as moving away; only the shoulder ending up beside the line does.
    // Cost - CPU: extremely low (a point-to-line distance); GPU: none.
    static float SideGap(Vector3 pocket, Vector3 gunHand, Vector3 barrel)
    {
        Vector3 back = gunHand - barrel * StockBehindMax, front = gunHand - barrel * StockBehindMin, along = front - back;
        float t = Math.Clamp(Vector3.Dot(pocket - back, along) / MathF.Max(along.LengthSquared(), 1e-6f), 0f, 1f);
        return Vector3.Distance(pocket, back + along * t);
    }

    // RA01: is the off hand within 'radius' of the gun (M69: ReachMetres to grab, OpenMetres to open)? The gun is the line from BehindHand behind the gun hand to PastMuzzle past the muzzle (muzzle from
    // Core's mailbox, as VRIK placed it last frame). False if the muzzle isn't known.
    // Cost - CPU: extremely low (a mailbox read and a point-to-line distance); GPU: none.
    static bool Near(Vector3 offHand, Vector3 gunHand, float radius)
    {
        if (!WeaponProbe.TryReadMuzzleOnBody(out Vector3 muzzle, out Quaternion muzzleTurn)) return false;
        Vector3 barrel = Vector3.Transform(Vector3.UnitY, muzzleTurn);
        Vector3 start = gunHand - barrel * BehindHand, end = muzzle + barrel * PastMuzzle, along = end - start;
        float t = Math.Clamp(Vector3.Dot(offHand - start, along) / MathF.Max(along.LengthSquared(), 1e-6f), 0f, 1f);
        return Vector3.Distance(offHand, start + along * t) <= radius;
    }

    // RA01: M27-10 - how far your palm is from the HMG's handle (its left_grip bone). The handle is read off the gun as V draws it (from its muzzle), then hung
    // on your gun hand's wrist the M27-6s way, so V's arm stretching can't move it away from your real hands. Builder picture: a second hook welded to the
    // gun - the first hook (the barrel line) stays where it was. float.MaxValue if the handle, muzzle or drawn wrist isn't read right now.
    // Cost - CPU: extremely low (three mailbox reads, one turn, a distance); GPU: none.
    static float HmgHandleGap(Vector3 palm, Vector3 gunHand, bool lefty)
    {
        if (!WeaponProbe.TryGetGripBone(out Vector3 fromMuzzle, out _) || !WeaponProbe.TryReadMuzzleOnBody(out Vector3 muzzle, out Quaternion muzzleTurn)
            || !WeaponProbe.TryReadWrist(lefty, out Vector3 drawnGunWrist)) return float.MaxValue;
        Vector3 handle = gunHand + (muzzle + Vector3.Transform(fromMuzzle, muzzleTurn) - drawnGunWrist);
        return Vector3.Distance(palm, handle);
    }

    // RA01: M27-6t - a controller's grip point (OpenXR room). Cost - CPU: extremely low; GPU: none.
    static Vector3 At(in Xr.Posef p) => new(p.Position.X, p.Position.Y, p.Position.Z);

    // RA01: the smallest turn taking direction a onto direction b (both unit length). Opposite directions turn about any square axis.
    // Cost - CPU: extremely low (a cross, a dot and a normalise); GPU: none.
    static Quaternion Arc(Vector3 a, Vector3 b)
    {
        float d = Vector3.Dot(a, b);
        if (d < -0.9999f) { Vector3 side = Vector3.Cross(a, MathF.Abs(a.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY); return Quaternion.CreateFromAxisAngle(Vector3.Normalize(side), MathF.PI); }
        Vector3 c = Vector3.Cross(a, b);
        return Quaternion.Normalize(new Quaternion(c.X, c.Y, c.Z, 1f + d));
    }

    // RA01: lets go, logged. Cost - CPU: extremely low; GPU: none.
    static void Release(string why)
    {
        holding = false;
        Log.Write($"RA01 two-handed grip: released ({why})");
    }

    // RA01: holds LB/RB back from the game (or lets them through); ControllerMapping logs each change. Cost - CPU: extremely low (a store); GPU: none.
    static void HoldBack(bool on) => ControllerMapping.HoldBackShoulders(on, holding ? "off hand holding the weapon" : on ? "off hand reaching for the weapon" : "off hand away from the weapon");
}
