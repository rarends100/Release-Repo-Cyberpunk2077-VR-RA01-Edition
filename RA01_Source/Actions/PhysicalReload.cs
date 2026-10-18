using System.Numerics;
using RA01.Core;

namespace RA01.Actions;

// RA01: M27-5 - PHYSICAL RELOADING (Weapons tab "Physical reloading", off by default while experimental; Rob's design 2026-10-08). Four steps, all with your OFF hand:
//  1. PULL THE OLD MAGAZINE: reach to the gun's magazine well (the SLOT under the gun - see M27-5b below; it rides with the gun), squeeze the
//     grip and pull DOWN the gun, more than PullOut from where you grabbed. The old magazine is out and in your hand; let go of the grip anywhere to drop it.
//     (Pulled BACK toward your shoulder instead, it stays the two-handed grip - the well sits inside that grip's reach, so the direction tells them apart.)
//  2. GRAB A NEW ONE: reach your off hand down to your off-hand hip - the mag pouch: more than PouchBelowEyes below your eyes, out on that side - and
//     squeeze its grip. Let go of the grip before step 4 and it is dropped (back to step 2).
//  3. CARRY it to the magazine well.
//  4. INSERT: in the slot the mod taps X for you (the game's reload), so the game plays its normal reload. Back to step 1.
// The game's own X still reloads too (Rob), and resets the steps. A new weapon, or holstering, also resets them (a fresh weapon comes loaded).
// While the off hand holds a magazine (old or new) or is at the pouch, its grip is kept from the game (ControllerMapping.HoldBackGrip: a long LB press
// is the scanner), and the two-handed grip lets go and ignores it (TwoHandGrip reads OffHandBusy).
// HANDEDNESS: the gun hand and off hand come from Tuning.LeftHanded, so in left-hand mode the pouch is on your right hip and the well follows your left hand.
// Not in menus, the F10 window, gesture mode or vehicles; guns only. Hands are measured in the room against your head's facing (like PhysicalSwim).
// M27-5b - THE MAGAZINE SLOT FOLLOWS THE REAL GUN (Rob's first test: step 1 never caught - the well was a guessed point 8 cm under and 6 cm ahead of the
// controller, but the guns sit far out: muzzles 47-76 cm from the controller). Now the gun is measured the way the laser measures it: the game's muzzle and
// barrel, written down in the gun controller's own frame (AimProbe.TryMuzzleInWorld, eased). From that, three tape-measure readings of your off hand:
//  - AHEAD: along the barrel, from the gun hand (0) toward the muzzle;
//  - BELOW: how far under the barrel's line (M27-5d: the gun's "down" is the muzzle bone's own axis nearest the floor when the gun is measured);
//  - SIDE: how far left or right of it.
// The SLOT is a box under the gun: SlotBehind behind the hand to SlotAheadShare of the way to the muzzle (at least SlotAheadMin), SlotBelowMin to SlotBelowMax
// under the barrel, within SlotSide either side - under the hand on a pistol, the magazine well in front of the trigger on a rifle. Step 1 grabs in it,
// step 4 inserts in it. The pull is measured from where you grabbed, in the gun's frame (moving the whole gun is not a pull).
// No muzzle reading yet (a draw animation, a fresh gun): a standard gun shape is used (StandardUp, StandardAhead) until one arrives.
// NEAR MISSES are logged (at most one a second): a squeeze near the gun outside the slot, or a carried magazine brought near the gun outside it, with the
// three readings and the slot's limits - so a test that doesn't catch says exactly why.
// M27-5c - THE POUCH IS EASY TO FIND AND GRAB (Rob 2026-10-09): while a magazine is needed (step 2), Core\PouchGlow.cs shows a glowing ball at its middle
// (PouchBelow under your eyes, PouchOut to the off-hand side), and a squeeze anywhere within PouchGrab of the ball counts, as well as the hip zone.
// M27-5e - Rob's test: catches worked but half the pulls were let go of before 12 cm, grabs sat 10-13 cm to the side, and LB reached the game while
// reaching (a quick LB is the game's gun bash, a long one the scanner). Now: SlotSide 16 cm, PullOut 8 cm within 60 degrees of down, a let-go after a
// downward pull of PullOnLetGo still counts, and LB is held back whenever the off hand is in the slot or pulling (plus SlotHoldMs after).
// M27-5f - THE MAGAZINE POINT (Rob 2026-10-09): some magazines have no bones of their own, so the mod marks one: from the gun's real mag_slot bone (the top
// of its magazine well, M27-6a) MagDrop straight down the gun - the middle of the magazine, where a hand closes on it - like a chalk X measured down from
// the well. A squeeze within MagGrab of the X takes hold (step 1) and a carried magazine within MagGrab of it goes in (step 4). The slot box still counts
// as well (and alone on guns without a mag_slot). The log says which one caught.
// M27-5g - INSERT REFILLS DIRECTLY (Rob 18:39): step 4 asks Core\GameData\GameCall.RequestRefill - the game's own StartReload / StopReload(Standard), no X tap,
// no game animation. If that can't run (another game build), it taps X as before.
// M27-6c-3b - THE GAME'S OWN RELOAD IS OFF while this is on (Core\GameData\GameCall.cs switches its reload rule to "no"): no auto-reload - an empty gun clicks - and
// X doesn't reload. X is the MAGAZINE RELEASE instead: pressed with a gun out, the magazine drops (straight to "grab a new one at your hip", the ball lights) -
// on every gun, which is how revolvers and shotguns are emptied; rifles can still be pulled by hand. If the rule couldn't be switched off (another game build),
// X reloads as before. Note: X is also the game's interact button, so pressing it to loot with a gun drawn drops the magazine too.
// M27-6d - THE MAGAZINE IS SEEN (Core\Probes\WeaponProbe.SetMagazineHold): pulled, it slides out with your hand; dropped (let go, or X) it is gone; a new one from the
// hip shows in your hand until it goes in. Paused (menus, vehicles...) it shows in the gun.
// M51 - THE TRIGGER PULL (Rob 2026-10-12): the old magazine can be pulled by hand again - with the OFF-HAND TRIGGER, so it never fights the two-handed
// grip (M27-6t's reason for removing the grip pull). Off hand on the magazine with its grip OPEN: push and hold the trigger and pull down the gun more than
// PullOut (within 60 degrees of down) - the magazine is out and in your hand (1 stays chambered); let go of the trigger anywhere and it drops. Let go during
// the pull after at least PullOnLetGo down: out and dropped in one go; less: nothing happens. Off hand GRIPPING the gun: its trigger aims as always.
// The new magazine at the hip is taken with the grip OR the trigger, and carried while either is held. WHILE YOU RELOAD (the hand on the magazine with the
// grip open, pulling, holding the old one, needing and carrying a new one) the off hand's grip and trigger send nothing to the game (HoldBackGrip,
// HoldBackAimTrigger); the rest of the time they work as always. X HELD for a second still drops the magazine (M53: a tap no longer does).
// Builder picture: swapping a drill's battery - pop the flat one off, grab a charged one from your tool belt, slide it on until it clicks.
// Cost - CPU: extremely low (a few vector sums and distances per controller read, about 60 a second, only with a gun out); GPU: none.
internal static class PhysicalReload
{
    const float PouchBelowEyes = 0.55f;      // RA01: metres below your eyes - hip height and lower
    const float PouchOutSide = 0.05f;        // RA01: at least this far out on the off-hand side of your head's centre line
    const float PouchFrontBack = 0.35f;      // RA01: no further than this in front of or behind you
    const float PouchBelow = 0.65f, PouchOut = 0.18f, PouchAhead = 0.0f; // RA01: M27-5c - the pouch's middle (where the glowing ball sits): below your eyes, out to the off-hand side, ahead
    const float PouchGrab = 0.25f;           // RA01: M27-5c - Rob: a generous grab zone - anywhere within this of the ball also counts (as well as the hip zone above)
    const float GrabSqueeze = 0.6f, DropSqueeze = 0.3f; // RA01: grip past this grabs; under this lets go
    const float SlotBehind = 0.05f;          // RA01: M27-5b - the slot starts this far behind the gun hand (along the barrel)
    const float SlotAheadShare = 0.6f, SlotAheadMin = 0.15f; // RA01: M27-5d - was 0.4 / 0.10 (Rob's SMG squeezes landed 15-22 cm ahead)
    // RA01: M27-5b - ...and reaches this share of the way to the muzzle, but at least SlotAheadMin ahead
    const float SlotBelowMin = 0.0f, SlotBelowMax = 0.25f;   // RA01: M27-5d - from the barrel's line down (was 0.03)
     // RA01: M27-5b - how far under the barrel's line
    const float SlotSide = 0.16f;            // RA01: M27-5b - how far either side of it (M27-5d: 0.12; M27-5e: 0.16 - Rob grabs 10-13 cm out to the side)
    const float NearGun = 0.40f;             // RA01: M27-5b - a squeeze (or a carried magazine) this close to the gun hand but outside the slot is a logged near miss
    const float PullOut = 0.08f;             // RA01: step 1: moved this far from where you grabbed (in the gun's frame), mostly downward (PullDown), it is out (M27-5e: was 0.12)
    const float PullDown = 0.5f;             // RA01: step 1: how much of the pull must point down the gun (0.5 = within 60 degrees of straight down; M27-5e: was 0.6)
    const float PullOnLetGo = 0.05f;         // RA01: M27-5e - letting go after a downward pull at least this long still counts (pulled out and dropped in one go)
    const float MagDrop = 0.08f, MagGrab = 0.15f; // RA01: M27-5f - the magazine grab point: this far down the gun from its mag_slot bone; a carried magazine within MagGrab of it goes in
    const float MagGrabOld = 0.07f;               // RA01: M27-6m - now the radius of a COLUMN hanging under the magazine (MagColumnUp above its middle to MagColumnDown below, along
    const float MagColumnUp = 0.03f, MagColumnDown = 0.20f; // RA01: the gun's down) - Rob 01:30: no room to reach DOWN to the magazine; nothing reaches forward or back toward the grips
                                                  // RA01: M27-6i - Rob 00:00: taking hold of the OLD magazine needs the hand this close (was MagGrab and the whole slot box, which
                                                  // RA01: caught two-handed grips along the gun); guns without a mag_slot bone keep the slot box
    const float PullShow = 0.03f;                 // RA01: M27-6i - the visible magazine only starts following once pulled this far (a grip that stays put doesn't wiggle it)
    const long SlotHoldMs = 150;
    const float StillMove = 0.04f; const long StillMs = 1500;   // RA01: M27-6p - Rob 03:14: held still on the magazine this long (was 350 ms) before it becomes the two-handed grip on the magwell   // RA01: M27-6e - held this still for this long after the squeeze: it is the two-handed grip, not a pull             // RA01: M27-5e - LB stays held back this long after the off hand leaves the slot
    const float Blend = 0.25f, MaxReach = 1.0f, FirePress = 0.15f; // RA01: M27-5b - the gun note: easing, the furthest muzzle that counts as in the hand, trigger = firing (note held)
    const int TapMs = 120;                   // RA01: the reload press (X), a short tap
    const long XHoldMs = 450;                // RA01: M53 - X held this long drops the magazine (Rob 05:54: a tap is a door or loot, never the release). M53-1 (Rob 11:08):
                                             // RA01: 1 s was too long in a gunfight - 0.45 s; a tap (doors, loot, talk) is well under 0.25 s, so it still never drops it
    const long CarryMaxMs = 8000;            // RA01: a new magazine carried longer than this is dropped (logged)
    const long MissLogMs = 1000;             // RA01: M27-5b - near-miss lines at most this often

    enum Step { Loaded, Pulling, OldInHand, Empty, Carrying }
    static volatile int step;                // RA01: a Step (an int so other threads can read it safely)
    static bool gripWasDown, xWasDown, triggerWasDown;   // RA01: M51 - triggerWasDown: the off-hand trigger at the last look
    static long xDownAt;                                   // RA01: M53 - when X went down (for the hold-to-release)
    static bool xHoldUsed;                                 // RA01: M53 - this X hold already released the magazine (once per hold)
    static readonly float[] pullFrom = new float[3];       // RA01: M51 - where the trigger pull started, in the gun's frame (an array: the ILC quirk)
    static long carriedSince, missLoggedAt, slotHoldUntil;
    static float lastMagGap;                       // RA01: M27-5f - this look's distance to the magazine point (for the near-miss line)
    static bool carryMissLogged, noted, standardLogged, haveMagSlot, magSlotLogged; // RA01: M27-6a - the gun's mag_slot bone read, and logged once per gun
    static string weaponNow = "";
    static readonly float[] note = new float[12];   // RA01: M27-5b - the muzzle in the gun controller's frame (x, y, z), then the barrel's direction, then (M27-5d) the gun's down, then (M27-6a) the gun's own mag_slot - arrays, not Vector3 fields (the ILC quirk)
    static int downAxis = -1, downChosen;                      // RA01: M27-5d - which of the muzzle bone's own axes is the gun's down: 0 +X, 1 -X, 2 +Z, 3 -Z (-1 = not chosen yet for this gun)
    const float WristBack = 0.07f;                 // RA01: M27-6s - HandTargets.PalmToWrist: the wrist sits this far behind the controller's grip point (its +Z)
    const float StandardUp = 0.06f, StandardAhead = 0.35f; // RA01: M27-5b - no reading yet: a muzzle 35 cm ahead (-Z) and 6 cm up, barrel straight ahead (plain floats: the ILC quirk)

    // RA01: true while the off hand holds a magazine (the old one or a new one): TwoHandGrip lets go and won't grab then. Any thread.
    // RA01: Cost - CPU: extremely low (a read); GPU: none.
    public static bool OffHandBusy => step is (int)Step.OldInHand or (int)Step.Carrying;

    // RA01: M27-6e - true while a squeeze in the magazine slot is not yet decided (magazine pull or two-handed grip): TwoHandGrip waits. Any thread.
    // RA01: Cost - CPU: extremely low (a read); GPU: none.
    public static bool Deciding => step == (int)Step.Pulling;

    // RA01: runs Update just before every controller read is answered. Call once at start-up, after XInputHook.
    // Cost - CPU: extremely low (stores one pointer); GPU: none.
    public static unsafe void Install() => ControllerMapping.AddBeforeBuild(&Update);

    // RA01: one look: the four steps. Never throws (ControllerMapping wraps it too).
    // Cost - CPU: extremely low (see the class comment); GPU: none.
    static void Update()
    {
        int off = Tuning.LeftHanded ? 1 : 0;                                  // RA01: the off hand: 0 left, 1 right
        bool gun = WeaponProbe.Held == WeaponProbe.Kind.Gun;
        bool usable = Tuning.PhysicalReload && gun && !SettingsMenu.Open && !MenuSense.ShowsMenu && !GestureMode.Posing && !VehicleState.Mounted
                      && !WeaponProbe.HeldName.StartsWith("Heavy machine gun", StringComparison.Ordinal); // RA01: M27-9a - Rob 07:08: the HMG (a turret gun, no reload in the game) is left out: no X release, no balls - its handle sits by the hip pouch and the pouch kept taking the two-handed grip
        float squeeze = off == 0 ? Controllers.LeftGrip : Controllers.RightGrip;
        float trigger = off == 0 ? Controllers.LeftTrigger : Controllers.RightTrigger;   // RA01: M51 - the off-hand trigger (your physical one: in left-hand mode, the right)
        bool xDown = ControllerMapping.ReloadButton;                       // RA01: M28-2 - whichever button is the game's X (left-hand mode: your right A)
        bool xPressed = xDown && !xWasDown;
        long xNow = Environment.TickCount64;
        if (xPressed) { xDownAt = xNow; xHoldUsed = false; }
        // RA01: M53 - HOLD X TO RELEASE (Rob 2026-10-12 05:54): X is also the game's interact button, so a tap (a door, looting) dropped the magazine. Now the
        // RA01: release needs X held XHoldMs (once per hold); taps reach the game as always and leave the magazine alone. Builder picture: a safety catch on
        // RA01: the release lever - a knock doesn't open it, a deliberate press-and-hold does.
        bool xHeld = xDown && !xHoldUsed && xNow - xDownAt >= XHoldMs;
        if (xHeld && GameCall.GameReloadBlocked)                          // RA01: M27-6c-3b - the game can't reload now: X (M53: held) is the magazine release
        {
            xHoldUsed = true;
            if (usable && step is (int)Step.Loaded or (int)Step.Pulling or (int)Step.OldInHand)
            {
                if (step != (int)Step.OldInHand) { Sound(Out); GameCall.KeepChamberedRound("X held - magazine released", ChamberKeeps); } // RA01: M27-6g-2 - the release (an old one already in your hand only falls); M27-7 - 1 stays chambered
                Sound(Floor);
                To(Step.Empty, $"X held {XHoldMs / 1000.0:0.##} s - magazine dropped out; grab a new one at your hip (M53)");
            }
        }
        else if (xPressed && !GameCall.GameReloadBlocked && step != (int)Step.Loaded) To(Step.Loaded, "you pressed X - the game reloads"); // RA01: M53 - the game's own reload stays a press
        xWasDown = xDown;
        if (WeaponProbe.HeldName != weaponNow) { weaponNow = WeaponProbe.HeldName; noted = false; downAxis = -1; haveMagSlot = magSlotLogged = false; if (step != (int)Step.Loaded) To(Step.Loaded, "a new weapon or holstered - it comes loaded"); }

        if (!usable || !Controllers.TryReadFrame(out Controllers.Frame f) || !f.HeadValid || !f.LeftValid || !f.RightValid)
        {
            if (step is (int)Step.Pulling or (int)Step.OldInHand) To(Step.Empty, "paused with the old magazine out");
            else if (step == (int)Step.Carrying) To(Step.Empty, "paused - new magazine dropped");
            ControllerMapping.HoldBackGrip(off, false, "physical reload not in use");
            ControllerMapping.HoldBackAimTrigger(false, "physical reload not in use"); // RA01: M51
            WeaponProbe.SetMagazineHold(0, off);                                 // RA01: M27-6d - paused: the magazine shows in the gun
            PouchGlow.Drop(); PouchGlow.DropWell();
            gripWasDown = squeeze > GrabSqueeze;
            triggerWasDown = trigger > GrabSqueeze;
            return;
        }
        Xr.Posef offPose = off == 0 ? f.Left : f.Right, gunPose = off == 0 ? f.Right : f.Left;
        Vector3 head = At(f.Head), offHand = At(offPose);
        Quaternion gunTurn = Turn(gunPose.Orientation);
        if (ControllerMapping.FireTrigger <= FirePress && AimProbe.TryMuzzleInWorld(out Vector3 muzzle, out Quaternion barrelTurn, out CameraBodyProbe.OnBody view))
            Note(f, gunPose, muzzle, barrelTurn, view);
        Vector3 rel = Vector3.Transform(offHand - At(gunPose), Quaternion.Conjugate(gunTurn));   // RA01: M27-5b - the off hand in the gun controller's frame
        Measure(rel, out float ahead, out float below, out float side, out float slotAhead, out Vector3 gunDown);
        bool inBox = ahead > -SlotBehind && ahead < slotAhead && below > SlotBelowMin && below < SlotBelowMax && MathF.Abs(side) < SlotSide;
        Vector3 magPoint = new Vector3(note[9], note[10], note[11]) + gunDown * MagDrop;      // RA01: M27-5f - the middle of the magazine, below the gun's own well
        // RA01: M27-6n - THE GUN AS V HOLDS IT (Rob 02:03: magazines still couldn't be pulled; the 02:02 log put a rifle's muzzle 19 cm from the hand and its well
        // RA01: 14 cm BEHIND it and 13 cm to the side - the controller-frame note above is unreliable on long guns). When VRIK's muzzle and the gun's mag_slot are
        // RA01: read, the magazine is measured in the GUN'S OWN frame instead (the muzzle bone's axes: +Y the barrel): your off hand's grip point put on V's body
        // RA01: (HandTargets.TryRoomToBody), then into that frame; the magazine point is the well plus MagDrop "down" - down being the way from the barrel line to
        // RA01: the well (it can't flip). The pull, the column and the insert are all measured there. Builder picture: measure the hand against the real gun on
        // RA01: the bench, not against a drawing of it. Cost - CPU: extremely low (three mailbox reads and a few turns); GPU: none.
        Vector3 gp = rel, gDown = gunDown, gMag = magPoint;
        bool onGun = false;
        Vector3 bodyMuzzle = default; Quaternion bodyMuzzleTurn = Quaternion.Identity;
        // RA01: M27-6s - the muzzle is where VRIK DREW the gun (your gun hand's target scaled about your head to fit V's arm length), your palm is not scaled: so
        // RA01: both are taken from their own gun hand - the muzzle against V's drawn gun wrist, your palm against your gun hand's wrist (the controller's grip point
        // RA01: moved back WristBack, as HandTargets does) - and bodyMuzzle is re-hung on your gun hand's wrist. Same gun shape, same space as your hands.
        Vector3 gunWristRoom = At(gunPose) + Vector3.Transform(new Vector3(0f, 0f, WristBack), gunTurn);
        if (WeaponProbe.TryReadMuzzleOnBody(out bodyMuzzle, out bodyMuzzleTurn) && WeaponProbe.TryGetMagSlot(out Vector3 well) && HandTargets.TryRoomToBody(f, offHand, out Vector3 palmBody)
            && WeaponProbe.TryReadWrist(off == 1, out Vector3 drawnGunWrist) && HandTargets.TryRoomToBody(f, gunWristRoom, out Vector3 gunWristBody))
        {
            bodyMuzzle = gunWristBody + (bodyMuzzle - drawnGunWrist);                       // RA01: M27-6s - the drawn muzzle, hung on your gun hand's wrist
            Vector3 under = well - Vector3.UnitY * well.Y;                                   // RA01: barrel line -> well (muzzle axes)
            if (under.Length() > 0.015f && well.Length() < 1.5f)
            {
                gDown = Vector3.Normalize(under);
                gMag = well + gDown * MagDrop;
                gp = Vector3.Transform(palmBody - bodyMuzzle, Quaternion.Conjugate(bodyMuzzleTurn));
                onGun = gp.Length() < 2f;
                if (!onGun) { gp = rel; gDown = gunDown; gMag = magPoint; }
            }
        }
        // RA01: M27-9b - PISTOLS (Rob 07:09: the well ball sat on TOP of the pistol): a pistol's mag_slot bone sits up on the slide, so it is not used - the
        // RA01: magazine goes into the bottom of the grip, under your own fist (PistolButt, measured from the controller, so V's arm scaling can't move it).
        bool pistol = WeaponProbe.HeldName.StartsWith("Handgun (pistol)", StringComparison.Ordinal);
        if (pistol) { onGun = false; gp = rel; magPoint = gMag = PistolButt(); gDown = Vector3.Normalize(gMag); }
        float magGap = !haveMagSlot && !onGun && !pistol ? float.MaxValue
            : step == (int)Step.Carrying ? Vector3.Distance(gp, gMag)                          // RA01: putting a new one in: around the magazine point
            : ToColumn(gp, gMag - gDown * MagColumnUp, gMag + gDown * MagColumnDown);         // RA01: M27-6m - taking the old one: the column under it
        bool atMag = magGap < (step == (int)Step.Carrying ? MagGrab : MagGrabOld);            // RA01: M27-6i - tight to take the old one out, generous to put a new one in
        lastMagGap = magGap;
        bool inSlot = haveMagSlot || onGun || pistol ? atMag : inBox;                                            // RA01: M27-6i - the slot box only for guns without mag_slot (was: either)
        string zone = atMag ? $"{magGap * 100:F0} cm from the magazine{(step == (int)Step.Carrying ? " point" : " (the column under it)")}" : "in the slot box";
        bool nearGun = rel.Length() < NearGun;

        bool gripDown = squeeze > (gripWasDown ? DropSqueeze : GrabSqueeze);
        bool pressed = gripDown && !gripWasDown;
        gripWasDown = gripDown;
        bool triggerDown = trigger > (triggerWasDown ? DropSqueeze : GrabSqueeze);   // RA01: M51 - the same press / let-go levels as the grip
        bool triggerPressed = triggerDown && !triggerWasDown;
        triggerWasDown = triggerDown;
        long now = Environment.TickCount64;
        bool inPouch = InPouch(f, head, offHand, off) || Vector3.Distance(offHand, PouchGlow.Centre(f.Head, off, PouchBelow, PouchOut, PouchAhead)) < PouchGrab; // RA01: M27-5c

        switch ((Step)step)
        {
            case Step.Loaded:                                                     // RA01: step 1 - X (above), or (M51) the off-hand trigger on the magazine
                // RA01: M27-6t - Rob 06:50: pulling with the GRIP was removed (it fought the two-handed grip); a squeeze on the gun is always the two-handed grip.
                // RA01: M51 - the TRIGGER pulls instead, only with the off-hand grip open (gripping = the two-handed grip, whose trigger aims).
                if (triggerPressed && inSlot && !gripDown)
                {
                    pullFrom[0] = gp.X; pullFrom[1] = gp.Y; pullFrom[2] = gp.Z;
                    To(Step.Pulling, $"off-hand trigger on the magazine ({zone}) - pull it down");
                }
                break;
            case Step.Pulling:                                                    // RA01: M51 - pulling with the trigger held
            {
                Vector3 pulled = gp - new Vector3(pullFrom[0], pullFrom[1], pullFrom[2]);
                float down = Vector3.Dot(pulled, gDown), length = pulled.Length();
                if (!triggerDown)
                {
                    if (down >= PullOnLetGo) { Sound(Out); GameCall.KeepChamberedRound("trigger pull - magazine out", ChamberKeeps); Sound(Floor); To(Step.Empty, $"magazine pulled out and dropped ({down * 100:F0} cm) - grab a new one at your hip"); }
                    else To(Step.Loaded, $"trigger let go before the pull ({down * 100:F0} cm down) - magazine stays in");
                }
                else if (length >= PullOut && down >= PullDown * length) { Sound(Out); GameCall.KeepChamberedRound("trigger pull - magazine out", ChamberKeeps); To(Step.OldInHand, $"old magazine pulled out ({down * 100:F0} cm down) - let go of the trigger to drop it"); }
                break;
            }
            case Step.OldInHand:                                                  // RA01: M51 - the old magazine in your hand until the trigger lets go
                if (!triggerDown) { Sound(Floor); To(Step.Empty, "old magazine dropped - grab a new one at your hip"); }
                break;
            case Step.Empty:                                                      // RA01: step 2 - a new one from the pouch (M51: with the grip or the trigger)
                if ((pressed || triggerPressed) && inPouch) { carriedSince = now; carryMissLogged = false; To(Step.Carrying, $"new magazine grabbed at the pouch ({(triggerPressed ? "trigger" : "grip")}) - carry it to the magazine well"); }
                break;
            case Step.Carrying:                                                   // RA01: steps 3 and 4
                if (!gripDown && !triggerDown) { Sound(Floor); To(Step.Empty, "new magazine dropped (let go before the gun)"); } // RA01: M51 - held by the grip or the trigger
                else if (now - carriedSince > CarryMaxMs) { Sound(Floor); To(Step.Empty, $"new magazine dropped (carried longer than {CarryMaxMs / 1000} s)"); }
                else if (inSlot)
                {
                    bool direct = GameCall.RequestRefill();                       // RA01: M27-5g - the game's own reload functions, no X and no animation
                    if (direct) Sound(In);                                        // RA01: M27-6g-2 - (a tapped X plays the game's own reload sound instead)
                    To(Step.Loaded, $"new magazine inserted ({Where(ahead, below, side)}; {zone}){(direct ? " - refilling" : "")}");
                    if (!direct) ControllerMapping.TapX(TapMs, $"physical reload - magazine inserted ({Where(ahead, below, side)}; {zone})");
                }
                else if (nearGun && rel.Length() < NearGun * 0.6f && !carryMissLogged)  // RA01: M27-5b - once per carry, when it comes close to the gun but misses the slot
                {
                    carryMissLogged = true;
                    Miss(now, "new magazine near the gun, outside the magazine slot", ahead, below, side, slotAhead);
                }
                break;
        }
        if (step == (int)Step.Empty) PouchGlow.Show(off, PouchBelow, PouchOut, PouchAhead);   // RA01: M27-5c - the ball marks the pouch only while a magazine is needed
        else PouchGlow.Drop();
        if (step == (int)Step.Carrying)                                                   // RA01: M27-6g - a tiny ball where the new magazine goes in (the magazine point)
            PouchGlow.ShowWell(onGun && HandTargets.TryBodyToRoom(f, bodyMuzzle + Vector3.Transform(gMag, bodyMuzzleTurn), out Vector3 ballRoom) ? ballRoom // RA01: M27-6n - on V's gun
                               : At(gunPose) + Vector3.Transform(magPoint, gunTurn));
        else PouchGlow.DropWell();
        if (inSlot && step == (int)Step.Carrying) slotHoldUntil = now + SlotHoldMs; // RA01: M27-5e - inserting a new magazine (M27-6t: no pulling any more, so nothing on a loaded gun)
        bool atSlot = now < slotHoldUntil;
        WeaponProbe.SetMagazineHold(step switch { (int)Step.Pulling or (int)Step.OldInHand => 1, (int)Step.Empty => 4, (int)Step.Carrying => 3, _ => 0 }, off); // RA01: M27-6e - Empty: falls, then gone // RA01: M27-6d - the visible magazine
        bool reloading = step != (int)Step.Loaded;                                       // RA01: M51 - from the magazine coming out until the new one is in
        bool onMagazine = step == (int)Step.Loaded && inSlot && !gripDown;               // RA01: M51 - the off hand on the magazine, grip open: its trigger is the pull
        bool camera = GameAnchor.LookingThroughCamera;                                   // RA01: M58 - a taken-over camera: the reload waits, LB (the scanner) and LT reach the game
        bool hold = (reloading || atSlot) && !camera;                                    // RA01: M51 - was: holding a magazine, at the pouch, or at the slot
        ControllerMapping.HoldBackGrip(off, hold, camera ? "looking through a camera (M58)" : OffHandBusy ? "holding a magazine" : reloading ? "reloading" : "reaching for the magazine");
        ControllerMapping.HoldBackAimTrigger((reloading || onMagazine) && !camera, camera ? "looking through a camera (M58)" : reloading ? "reloading - the off-hand trigger handles magazines" : "off hand on the magazine (grip open)");
    }

    // RA01: M27-5b - the muzzle and barrel from the game's camera -> from your head -> into the room -> into the gun controller's frame, eased into the note
    // (the same sum as Visuals\LaserSight.cs, kept here because Actions may not read Visuals). A muzzle further than MaxReach isn't in the hand yet.
    // Cost - CPU: extremely low (six vector turns, a few adds); GPU: none.
    static void Note(in Controllers.Frame f, in Xr.Posef gunPose, Vector3 muzzle, Quaternion barrelTurn, in CameraBodyProbe.OnBody view)
    {
        Quaternion back = Quaternion.Conjugate(view.WorldTurn);
        Vector3 m = Vector3.Transform(muzzle - view.WorldHead, back), b = Vector3.Transform(Vector3.Transform(Vector3.UnitY, barrelTurn), back);
        Quaternion headTurn = Turn(f.Head.Orientation), fromRoom = Quaternion.Conjugate(Turn(gunPose.Orientation));
        Vector3 room = At(f.Head) + Vector3.Transform(new Vector3(m.X, m.Z, -m.Y), headTurn), roomAlong = Vector3.Transform(new Vector3(b.X, b.Z, -b.Y), headTurn);
        Vector3 start = Vector3.Transform(room - At(gunPose), fromRoom), along = Vector3.Transform(roomAlong, fromRoom);
        if (!(start.Length() < MaxReach) || !(along.LengthSquared() > 0.5f)) return;
        // RA01: M27-5d - the gun's down from the muzzle bone's own axes (a spirit level built into the gun). Rob's first M27-5b test: the old plumb line from the
        // RA01: barrel to the controller pointed UP on a rifle (its barrel runs below the controller's grip point). The bone's X or Z axis that points nearest the
        // RA01: floor when the gun is first measured is kept for this gun.
        Vector3 roomDown = Vector3.Zero;
        float bestDot = -2f;
        for (int a = 0; a < 4; a++)
        {
            Vector3 axis = Vector3.Transform(a < 2 ? Vector3.UnitX : Vector3.UnitZ, barrelTurn) * (a % 2 == 0 ? 1f : -1f);
            Vector3 c = Vector3.Transform(axis, back);
            Vector3 r = Vector3.Transform(new Vector3(c.X, c.Z, -c.Y), headTurn);              // RA01: camera axes -> OpenXR -> the room
            if (downAxis < 0) { float d = -r.Y; if (d > bestDot) { bestDot = d; roomDown = r; downChosen = a; } }
            else if (a == downAxis) roomDown = r;
        }
        if (downAxis < 0)
        {
            downAxis = downChosen;
            Log.Write($"RA01 physical reload: the gun's down is the muzzle bone's {(downAxis % 2 == 0 ? "+" : "-")}{(downAxis < 2 ? "X" : "Z")} ({MathF.Acos(Math.Clamp(bestDot, -1f, 1f)) * 180f / MathF.PI:F0} deg from straight down)");
        }
        Vector3 downHand = Vector3.Transform(roomDown, fromRoom);
        float k = noted ? Blend : 1f;
        for (int i = 0; i < 3; i++)
        {
            note[i] += (start[i] - note[i]) * k;
            note[i + 3] += (along[i] - note[i + 3]) * k;
            note[i + 6] += (downHand[i] - note[i + 6]) * k;
        }
        Vector3 dir = Vector3.Normalize(new Vector3(note[3], note[4], note[5]));
        note[3] = dir.X; note[4] = dir.Y; note[5] = dir.Z;
        if (!noted) { noted = true; standardLogged = false; Log.Write($"RA01 physical reload: measured the gun - muzzle {start.Length() * 100:F0} cm from your hand"); }
        if (WeaponProbe.TryGetMagSlot(out Vector3 slotFromMuzzle))             // RA01: M27-6a - the gun's own magazine well, hung off the muzzle like the barrel
        {
            Vector3 w = Vector3.Transform(muzzle + Vector3.Transform(slotFromMuzzle, barrelTurn) - view.WorldHead, back);
            Vector3 well = Vector3.Transform(At(f.Head) + Vector3.Transform(new Vector3(w.X, w.Z, -w.Y), headTurn) - At(gunPose), fromRoom);
            if (well.Length() < MaxReach)
            {
                for (int i = 0; i < 3; i++) note[9 + i] += (well[i] - note[9 + i]) * (haveMagSlot ? Blend : 1f);
                haveMagSlot = true;
                if (!magSlotLogged)
                {
                    magSlotLogged = true;
                    Measure(new Vector3(note[9], note[10], note[11]), out float a, out float bl, out float sd, out float reach, out _);
                    Log.Write($"RA01 physical reload: the gun's own mag_slot bone (its magazine well) is {Where(a, bl, sd)} (the slot box reaches {-SlotBehind * 100:F0} to {reach * 100:F0} cm ahead)");
                }
            }
        }
    }

    // RA01: M27-5b - the off hand ('rel', in the gun controller's frame) read against the gun: cm AHEAD along the barrel, BELOW its line, to the SIDE; how far
    // ahead the slot reaches; and the gun's down (M27-5d: the muzzle bone's own down, square to the barrel).
    // Cost - CPU: extremely low (a handful of dot and cross products); GPU: none.
    static void Measure(Vector3 rel, out float ahead, out float below, out float side, out float slotAhead, out Vector3 down)
    {
        Vector3 muzzle = noted ? new Vector3(note[0], note[1], note[2]) : new Vector3(0f, StandardUp, -StandardAhead);
        Vector3 along = noted ? new Vector3(note[3], note[4], note[5]) : -Vector3.UnitZ;
        if (!noted && !standardLogged) { standardLogged = true; Log.Write("RA01 physical reload: no muzzle reading yet - using a standard gun shape"); }
        // RA01: M27-6i - THE GUN'S DOWN, ALWAYS UNDER THE GUN (Rob 00:00: the slot and the ball sometimes jumped to the far side of the gun). The muzzle bone's axis
        // RA01: picked when the gun was first measured could be a SIDE axis if the gun was tilted then. Now: on a gun with a mag_slot bone, down is the way from
        // RA01: the barrel line to the gun's own magazine well (the well is under the gun by its build, so this can't flip); otherwise the controller's own down
        // RA01: (your palm holds the grip, so the gun's bottom points that way). Worked out fresh every look, nothing is locked in.
        down = -Vector3.UnitY;
        if (haveMagSlot)
        {
            Vector3 toWell = new Vector3(note[9], note[10], note[11]) - muzzle;
            toWell -= along * Vector3.Dot(toWell, along);
            if (toWell.Length() > 0.02f) down = toWell;
        }
        down -= along * Vector3.Dot(down, along);                              // RA01: square to the barrel
        if (down.LengthSquared() < 1e-4f) down = -Vector3.UnitY - along * Vector3.Dot(-Vector3.UnitY, along);
        down = Vector3.Normalize(down);
        Vector3 across = Vector3.Cross(along, down);
        ahead = Vector3.Dot(rel, along);
        below = Vector3.Dot(rel - muzzle, down);
        side = Vector3.Dot(rel, across);
        slotAhead = MathF.Max(SlotAheadMin, Vector3.Dot(muzzle, along) * SlotAheadShare);
    }

    // RA01: M27-9b - a pistol's magazine point, in the gun controller's frame: the bottom of the grip, PistolButtDown under your controller's grip point.
    // M27-9d - Rob 08:08: the 9b ball sat high ABOVE the pistol and behind it - the controller's own "down" (-Y) is not the gun's down in this frame.
    // Now down is read off the gun itself: the barrel sits above your fist, so "down" is the way from the barrel's line to your hand (it can't flip);
    // before the muzzle is read, the little-finger end of your fist (+Z, the OpenXR grip frame's "little finger to thumb" is -Z). No back/forward shift.
    // Builder picture: drop a plumb line from the barrel through your fist - the magazine goes in where it comes out under your hand.
    // Cost - CPU: extremely low (a few multiplies); GPU: none.
    static Vector3 PistolButt()
    {
        Vector3 down = Vector3.UnitZ;
        if (noted)
        {
            Vector3 muzzle = new Vector3(note[0], note[1], note[2]), along = new Vector3(note[3], note[4], note[5]);
            if (along.LengthSquared() > 1e-4f)
            {
                along = Vector3.Normalize(along);
                Vector3 toHand = -muzzle - along * Vector3.Dot(-muzzle, along);   // RA01: from the barrel's line square across to the grip point
                if (toHand.Length() > 0.02f) down = Vector3.Normalize(toHand);
            }
        }
        return down * PistolButtDown;
    }
    const float PistolButtDown = 0.08f;   // RA01: M27-9b - the bottom of a pistol's grip below the controller's grip point

    // RA01: M27-6m - the distance from 'p' to the line piece a-b (a column's middle line). Cost - CPU: extremely low (a dot product and a distance); GPU: none.
    static float ToColumn(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float t = Math.Clamp(Vector3.Dot(p - a, ab) / MathF.Max(ab.LengthSquared(), 1e-6f), 0f, 1f);
        return Vector3.Distance(p, a + ab * t);
    }

    // RA01: M27-5b - a near-miss line (at most one a MissLogMs): the three readings and the slot's limits. Cost - CPU: extremely low; GPU: none.
    static void Miss(long now, string what, float ahead, float below, float side, float slotAhead)
    {
        if (now - missLoggedAt < MissLogMs) return;
        missLoggedAt = now;
        string well = haveMagSlot ? $" ({lastMagGap * 100:F0} cm from the magazine point, needs under {(step == (int)Step.Carrying ? MagGrab : MagGrabOld) * 100:F0})" : "";   // RA01: M27-6a, M27-5f
        Log.Write($"RA01 physical reload: {what} - {Where(ahead, below, side)}{well}; the slot is {-SlotBehind * 100:F0} to {slotAhead * 100:F0} cm ahead, " +
                  $"{SlotBelowMin * 100:F0} to {SlotBelowMax * 100:F0} cm below, within {SlotSide * 100:F0} cm to the side");
    }

    // RA01: M27-5b - "12 cm ahead, 9 cm below the barrel, 2 cm to the side". Cost - CPU: extremely low; GPU: none.
    static string Where(float ahead, float below, float side) =>
        $"{ahead * 100:F0} cm ahead, {below * 100:F0} cm below the barrel, {MathF.Abs(side) * 100:F0} cm to the side";

    // RA01: the off hand at its own hip: low, out on its side, not far in front or behind. Cost - CPU: extremely low (two dot products); GPU: none.
    static bool InPouch(in Controllers.Frame f, Vector3 head, Vector3 offHand, int off)
    {
        Vector3 forward = Vector3.Transform(-Vector3.UnitZ, Turn(f.Head.Orientation));
        forward.Y = 0;
        if (forward.LengthSquared() < 1e-4f) return false;
        forward = Vector3.Normalize(forward);
        Vector3 rightward = Vector3.Cross(forward, Vector3.UnitY);
        Vector3 fromHead = offHand - head;
        float outward = Vector3.Dot(fromHead, rightward) * (off == 0 ? -1f : 1f);         // RA01: + = out on the off hand's own side
        return fromHead.Y < -PouchBelowEyes && outward > PouchOutSide && MathF.Abs(Vector3.Dot(fromHead, forward)) < PouchFrontBack;
    }

    // RA01: M27-6g-2 - THE RELOAD SOUNDS: the game's own sound events. Out = the magazine comes out (pulled, or X), In = a new one goes in, Floor = a dropped
    // RA01: magazine lands, played FallDelayMs later (when the visible one has fallen). M27-6j/6k - which sound each gun plays is Actions\GunSounds.cs's choice
    // RA01: (its own model's from the game's database, else its type's; all editable in Settings\RA01_GunSounds.txt). Core\GameData\GameCall plays them on V's gun;
    // RA01: nothing is played with physical reloading off (this code doesn't run then) or with Reload sounds off.
    // RA01: Cost - CPU: extremely low (a few string compares and one queue add, only at those moments); GPU: none.
    const int Out = 0, In = 1, Floor = 2;

    // RA01: M27-7g - rounds the gun keeps when its magazine leaves (Rob 01:16): 1 in the chamber for magazine guns and pump shotguns (a round stays chambered
    // RA01: when the tube is emptied); 0 for a revolver (opening the cylinder dumps every round) and a double-barrel (breaking it open ejects both shells).
    // RA01: Cost - CPU: extremely low (two string compares); GPU: none.
    static int ChamberKeeps => WeaponProbe.HeldName.StartsWith("Revolver", StringComparison.Ordinal) || WeaponProbe.HeldName.StartsWith("Double-barrel", StringComparison.Ordinal) ? 0 : 1;
    const int FallDelayMs = 350;                 // RA01: Core\Probes\WeaponProbe.cs FallMs - the visible magazine lands then
    static void Sound(int what)
    {
        if (!Tuning.ReloadSounds) return;                                                 // RA01: M27-6k - Weapons tab "Reload sounds"
        string name = GunSounds.Pick(what, WeaponProbe.HeldAudioSet, WeaponProbe.HeldName, WeaponProbe.HeldSmart);   // RA01: M27-6k - Settings\RA01_GunSounds.txt
        if (name.Length > 0) GameCall.PlaySound(name, what == Floor ? FallDelayMs : 0);
    }

    // RA01: moves to a step (logged). Cost - CPU: extremely low; GPU: none.
    static void To(Step s, string why)
    {
        step = (int)s;
        Log.Write($"RA01 physical reload: {why}");
    }

    static Vector3 At(in Xr.Posef p) => new(p.Position.X, p.Position.Y, p.Position.Z);
    static Quaternion Turn(in Xr.Quaternionf q) => Quaternion.Normalize(new Quaternion(q.X, q.Y, q.Z, q.W));
}
