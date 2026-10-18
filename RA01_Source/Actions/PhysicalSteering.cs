using System.Diagnostics;
using System.Numerics;
using RA01.Core;

namespace RA01.Actions;

// RA01: M19-1 - PHYSICAL STEERING: seated in a car, tank or on a motorcycle, grab the real steering wheel (or handlebars) with your grips and turn it.
//  1. WHERE THE WHEEL IS: the game's own driving animation puts V's wrists on the wheel rim (or the bike's grips) - VRIK hands us those two spots on her
//     body (VehicleState.TryReadGameHands). While the wheel is straight they are averaged in: the hub is halfway between them, the rim's radius half the gap
//     across - like finding a wheel's hub by measuring between two bolts on opposite sides of the rim. A bike's two spots are its grips; the pivot is between.
//  2. GRABBING: a grip squeeze only takes hold when that hand is in reach - a car: within RimReach of the rim ring (not the middle, not out in the air);
//     a bike: within GripReach of its own grip. It holds until the squeeze is let go, wherever the hand then goes.
//  3. TURNING: each holding hand is a peg on the rim; how far it moved AROUND the hub since the last frame is how far it turned the wheel (a car: around the
//     steering column, V's forward axis - a clock hand on the wheel's face; a bike: around the upright pivot - a bar swinging flat). The wheel follows the
//     hand that moved MORE, so one hand can rest on the wheel while the other turns it, either hand can steer alone, and hand-over-hand works: let go, grab
//     again further round, keep turning - the wheel stays where it was.
//  4. FEEL: WheelLockDeg (bike: BarLockDeg) of turn = the stick all the way over. A gentle middle (small turns do a little, big turns a lot), a power-
//     steering damper (the stick moves at most SteerSpeed of its travel per second) and self-centring (no hand on the wheel: it drifts back to straight
//     at SteerReturn full locks per second, like a real wheel returning).
//  5. While steering is on: the grips are never LB/RB, and the left stick's left/right comes from the wheel (your thumb on the real stick still wins).
// M19-2 - V's arms FOLLOW YOUR HANDS while seated (M19-1 handed them to the game's driving animation, so her hands sat on the wheel wherever yours were).
// A holding hand is pinned onto the rim at its own angle round the hub (it slides round the rim with your hand) - on a bike, onto its grip, swung with
// the bar. Near the wheel V's hand opens ready to grab, closes with the squeeze and is a fist while holding (TryHandCurl; VRIK bends the fingers).
// Reach is a little kinder (12 cm of the rim, 15 cm of a bike grip): M19-1's test missed by 2 cm.
// M19-3 - A HOLDING HAND STAYS PUT ON THE WHEEL: it is glued to the spot on the rim it grabbed and moves only as the GAME'S wheel turns (the game's own
// wrists show the turn: the line between them tilts exactly as much as the wheel has turned, like a stripe painted across a wheel) - not with your hand. On
// a bike it is glued to the game's own grip. VRIK leaves a glued hand exactly where it is told (VRIK.SolveArms: no reach scaling - PinnedHand).
// THE GAME'S STICK DEAD ZONE: the car ignored the first part of a turn - the game ignores a left stick under about a quarter of its travel. Anything past
// centre is now sent starting at OutDeadZone (a quarter), so the first degrees of wheel already steer - like taking up the slack in a steering box.
// SELF-CENTRING, EASED (both hands off): the wheel turns itself back to straight like a real car's - quicker when turned far, slowing as it nears centre
// (each moment it closes the same share of the gap that is left, Steering return a second, with a tiny steady push, ReturnFloor, so it really arrives) -
// a door on a soft closer, not a snap. M19-1's was a steady 3 full locks a second: from full lock, straight in a third of a second.
// M19-4 - TURNING, REWORKED (M19-3's test: two hands felt stiff, one hand ripped the wheel to full lock):
//  - TWO HANDS = A BROOMSTICK: with both holding, the wheel turns by how much the line between your two hands tilts (a car: on the wheel's face; a bike:
//    flat) - a real wheel's turn, whatever the hub's exact spot. (Each hand round the game wheel's hub read a smaller, different turn: your hands are not
//    exactly on its rim.)
//  - ONE HAND = A HAND ON THE RIM: its turn round the hub is scaled by how far out it is (a hand halfway in counts half) - a crank handle held right next
//    to its pivot swings a big angle for a small move; that was the rip. And no single frame may turn the wheel more than MaxStep.
//  - THE FEEL is the player's: "Steering feel" picks Performance (a sports car: quick ratio, fast and direct - the default) or Comfort (a relaxed cruiser);
//    moving any feel slider makes it Custom. Centre softness (Tuning.SteerSoftness) replaces M19-1's fixed gentle middle.
// M20-3 - DRIVING WITH A GUN OUT (the game's "driver combat" seat state): steering stays on, but only your OFF hand (left; right when left-handed)
// can hold the wheel - the gun hand aims and its grip stays a normal button (the game moves the pedals to the shoulder buttons then). The off hand's grip
// is never LB. It may instead two-hand the gun (TwoHandGrip) - then nobody holds the wheel and it unwinds to straight, the car driving itself while you
// shoot; a hand on the wheel can't grab the gun and the other way round (OffHandOnWheel / TwoHandGrip.Holding). While a gun is out the game's own
// wrists aren't both on the wheel, so the wheel's spot isn't re-learned and a glued hand turns with OUR wheel instead of the game's.
// BY DESIGN (Rob, 2026-10-05): with a gun out the game stops turning its own steering wheel, so V's held hand slides round the still rim as you
// steer. Gluing it to the grab spot was offered and declined - seeing your hand move with your steering is more fun than a frozen hand on a frozen
// wheel. Turning the wheel itself would need the car's steering-wheel part found first (a later job, if ever).
// M20-3b - the first test found no wheel with a gun out: the gun reached V's hand a moment BEFORE the seat state turned "driver combat", steering
// stopped for that moment and forgot the wheel, and with a gun out it is never re-learned. Now a gun in hand while driving counts as driver combat
// at once, and a stop only forgets the wheel when V has left the vehicle.
// M28-6 - THE TOP AND BOTTOM OF THE WHEEL (Rob 2026-10-08: the sides were perfect, the top and bottom grabbed short of / past the rim). Two fixes:
//  - THE WHEEL LEANS: a car's wheel tips its top away from you. Its sides can't show that (upright or leaning, 9 and 3 o'clock are the same spot), but
//    whenever the GAME turns the wheel, V's real wrists ride round the true rim - rising and moving forward together on a leaning wheel. Like finding the
//    slope of a door frame by watching which way the handle travels as it swings: their forward-per-up is the lean (LearnLean, a running best fit). Until
//    LeanSamples are in it is upright, as before. The wheel's face is then measured along the lean: "across" (right), "up the face", and "out of the face".
//  - (M28-6's "palm on the rim, 7 cm inside the wrists" was WRONG and is gone - Rob's test: V grabbed toward the horn. The game's wrists sit ON the rim:
//    one car's wrist circle was 19 cm and Rob's palms grabbed at about 25 cm. V's wrist is pinned on the learned circle again, as before.) Bikes are unchanged.
// Not with a weapon drawn (vehicle combat keeps normal aiming), not in a flying car (nothing to hold), not in menus. VR Controls tab "Physical steering".
// Called from HandTargets.TryGet (VRIK, every frame) with your wrists on V's body; reads Core, tells ControllerMapping, never touches Visuals.
// M83 - THE CAR'S OWN GUNS (Rob 2026-10-15): in a car with built-in guns the game's drive-by mode (seat state 6) fires the car's guns and V draws nothing,
// so "drive-by" alone no longer means her right hand holds a gun: only a weapon really in her right hand makes it the gun hand. With empty hands in
// drive-by mode both hands keep the wheel (full control while firing), and - as with a gun out - the wheel isn't learned from the game's wrists then.
// Cost - CPU: extremely low (a few field reads, two angles, a handful of multiplies per frame while seated; one bool read otherwise); GPU: none.
internal static class PhysicalSteering
{
    const float GrabSqueeze = 0.5f, LetGoSqueeze = 0.35f;     // RA01: grip squeeze to take hold, and below which it lets go (a gap, so it doesn't flutter)
    const float RimReach = 0.12f, FaceReach = 0.15f;          // RA01: a car: within 12 cm of the rim ring, and 15 cm in front of / behind the wheel's face
    const float GripReach = 0.15f;                            // RA01: a bike: within 15 cm of its own grip
    const float MinRadius = 0.05f;                            // RA01: a hand closer than this to the hub has no clear angle (it is ignored until it moves out)
    const float LearnRate = 0.1f, StraightStick = 0.05f;      // RA01: how fast the wheel's spot is averaged in, and only while the stick is within this of centre
    const int LearnFrames = 10;                                // RA01: the wheel counts as found after this many straight frames
    const float MaxStep = 0.3f;                                // RA01: M19-4 - the most one frame may turn the wheel, radians (about 17 degrees)
    static bool lineValid;                                     // RA01: M19-4 - both hands held last frame (lastLine is their line's angle then)
    static float lastLine;
    const float ReturnFloor = 0.05f;                           // RA01: M19-3 - the self-centring's smallest push, full locks a second
    const float OutDeadZone = 0.25f, OutTiny = 0.01f;          // RA01: M19-3 - the game's stick dead zone (XInput's left-stick one is 7849 of 32767), skipped past
    static bool bikeNow, combatNow;             // RA01: combatNow - V's drawn hands follow OUR wheel (a gun out; M85: or the car's guns with the game's wrists off its wheel)
    static bool carGunsWas;                     // RA01: M83 - the car's own guns were in use at the last frame
    const float LeanTurn = 0.26f, LeanMinUp = 0.03f;           // RA01: M28-6 - lean samples only while the game's wheel is turned past ~15 deg, wrists 3+ cm up or down the face
    const int LeanSamples = 40;                                // RA01: M28-6 - the lean is trusted after this many samples
    const float MaxLean = 1.05f;                               // RA01: M28-6 - the most lean believed, radians (60 deg either way - a bus wheel lies flatter, a car's ~20-30)
    static float leanSin, leanCos = 1f, leanUpFwd, leanUpUp;   // RA01: M28-6 - the lean's sine and cosine (0 / 1 = upright), and its running best-fit sums
    static int leanCount;

    // RA01: M20-3 - for TwoHandGrip: is the off hand on the wheel (then it can't grab the gun)? Cost - CPU: extremely low (a read); GPU: none.
    public static bool OffHandOnWheel => holding[Tuning.LeftHanded ? 1 : 0];
    static float straightLine;                                 // RA01: M19-3 - the game wrists' line angle while the wheel is straight
    static readonly float[] grabRim = new float[2], grabGame = new float[2]; // RA01: M19-3 - each holding hand's rim angle and the game wheel's turn, at the grab
    static bool active, found;
    static int learned;
    static float glx, gly, glz, grx, gry, grz;                 // RA01: the game's wrist spots on V's body (averaged) - plain numbers, not stored Vector3s
                                                               // RA01: (ILC 8.0.31 fails "Code generation failed" on a static Vector3 field - see RA01_Known_Quirks)
    static readonly bool[] holding = new bool[2], gripWasDown = new bool[2];
    static readonly float[] lastAngle = new float[2];
    static float wheel, stick;                                 // RA01: the wheel's turn (radians, + = anticlockwise as you look at it) and the stick sent
    static long lastTicks, nextNearLog, nextStatusLog;
    static readonly float[] handCurl = { -1f, -1f };           // RA01: M19-2 - how closed each of V's hands should be (0 open .. 1 fist), -1 = not near the wheel

    // RA01: M19-2 - for VRIK: how closed V's left (or right) hand should be - 0 open (within reach of the wheel), up to 1 with the squeeze, 1 holding. False = leave
    // that hand's fingers alone. Cost - CPU: extremely low (a read); GPU: none.
    public static bool TryHandCurl(bool left, out float curl)
    {
        curl = handCurl[left ? 0 : 1];
        return curl >= 0f;
    }

    // RA01: one frame (see the class comment): turns the wheel, and moves a holding hand's target onto the rim or grip. Never throws.
    // Cost - CPU: extremely low (see the class comment); GPU: none.
    public static void Update(ref Vector3 left, bool leftOk, ref Vector3 right, bool rightOk)
    {
        long ticks = Stopwatch.GetTimestamp();
        float dt = lastTicks == 0 ? 0f : Math.Clamp((ticks - lastTicks) / (float)Stopwatch.Frequency, 0f, 0.1f);
        lastTicks = ticks;
        VehicleState.Kind kind = VehicleState.Current;
        bool weapon = GameAnchor.TryRightHandWeapon(out nint w) && w != 0;
        bool carGuns = VehicleState.IsDriverCombat && !weapon;                 // RA01: M83 - drive-by mode with V's hands empty: the car's own guns shoot
        bool combat = (VehicleState.IsDriverCombat || (VehicleState.IsDriving && weapon)) && !carGuns; // RA01: M20-3 - M20-3b: the weapon shows up a moment before the seat state turns 6
        bool on = Tuning.PhysicalSteering && ((VehicleState.IsDriving && !weapon) || combat || carGuns) && (kind is VehicleState.Kind.Car or VehicleState.Kind.Bike or VehicleState.Kind.Tank)
                  && !SettingsMenu.Open && !MenuSense.ShowsMenu;
        if (!on)
        {
            if (active) Stop(weapon && VehicleState.IsDriving ? "weapon drawn" : !VehicleState.IsDriving ? "not driving (getting out, a passenger, or a weapon out)" : !Tuning.PhysicalSteering ? "switched off" : SettingsMenu.Open ? "settings window open"
                             : MenuSense.ShowsMenu ? "a game menu is showing" : $"a {kind} - nothing to hold");
            return;
        }
        if (!active) { active = true; Log.Write($"RA01 steering: on ({kind}) - grab the {(kind == VehicleState.Kind.Bike ? "handlebars" : "wheel")} with your grips"); }
        bool bike = kind == VehicleState.Kind.Bike;
        bikeNow = bike;
        bool wristsOn = carGuns && !bike && found && GameWristsOnRim(); // RA01: M85 - the car's guns, and the game still has both of V's wrists on its wheel
        if (!carGuns) wristsOnWas = false;                              // RA01: M85 - each time the guns come out, the strict test first
        bool own = combat || (carGuns && !wristsOn);                    // RA01: M83/M85 - our own wheel when the game's wrists are off it (a gun out)
        if (own != combatNow)                                           // RA01: M85 - switching wheels: each holding hand keeps the spot it is drawn at (no jump)
        {
            float before = GameTurn();
            combatNow = own;
            float after = GameTurn();
            for (int h = 0; h < 2; h++) if (holding[h]) grabGame[h] += Wrap(after - before);
            if (carGuns) Log.Write($"RA01 steering: V's hands follow {(own ? "your wheel (the game's wrists are off its wheel)" : "the game's wheel (its wrists are on the rim)")} (M85)");
        }
        if (combat || carGuns) LookDriveBy(own);                         // RA01: M85 - log only, once a second
        if (carGuns != carGunsWas) { carGunsWas = carGuns; Log.Write(carGuns ? "RA01 steering: the car's own guns are in use (drive-by, V's hands empty) - both hands on the wheel (M83)" : "RA01 steering: the car's own guns are put away (M83)"); }
        int offHand = Tuning.LeftHanded ? 1 : 0;
        if (!combat && !carGuns) Learn(bike);                           // RA01: M20-3 - a gun out (M83: or the car's guns): the game's wrists aren't both on the wheel
        if (!combat && !carGuns && !bike && found) LearnLean();                     // RA01: M28-6
        float lockRad = (bike ? Tuning.BarLockDeg : Tuning.WheelLockDeg) * MathF.PI / 180f;

        float best = 0f;
        bool anyHeld = false;
        for (int i = 0; i < 2; i++)
        {
            ref Vector3 hand = ref i == 0 ? ref left : ref right;
            bool ok = (i == 0 ? leftOk : rightOk) && (!combat || i == offHand) && !(i == offHand && TwoHandGrip.Holding); // RA01: M20-3 - gun out: only the free off hand
            float squeeze = i == 0 ? Controllers.LeftGrip : Controllers.RightGrip;
            bool down = ok && squeeze > (gripWasDown[i] ? LetGoSqueeze : GrabSqueeze);
            bool pressed = down && !gripWasDown[i];
            gripWasDown[i] = down;
            if (holding[i] && !down) { holding[i] = false; Log.Write($"RA01 steering: {Side(i)} hand let go"); }
            if (holding[i] && !ok) { holding[i] = false; Log.Write($"RA01 steering: {Side(i)} hand off the wheel (now the gun hand / on the gun)"); }
            float reachGap = found && ok ? Gap(i, hand, bike) : 1f;
            handCurl[i] = !Tuning.OffHandFingers ? -1f : holding[i] ? 1f : reachGap <= 0f ? Math.Clamp(squeeze, 0f, 1f) : -1f; // RA01: M19-2 - open in reach, closing with the squeeze
            if (pressed && !found && Environment.TickCount64 >= nextNearLog) { nextNearLog = Environment.TickCount64 + 1000; Log.Write("RA01 steering: grip squeezed, but the wheel isn't found yet (no game hand spots from VRIK - is \"Arms follow controllers\" on?)"); }
            if (pressed && found)
            {
                float gap = reachGap;
                if (gap <= 0f && Angle(hand, bike, out lastAngle[i], out _))
                {
                    holding[i] = true;
                    grabRim[i] = lastAngle[i];
                    grabGame[i] = GameTurn();
                    Log.Write($"RA01 steering: {Side(i)} hand on the {(bike ? "handlebar" : "wheel")}");
                }
                else if (Environment.TickCount64 >= nextNearLog)
                {
                    nextNearLog = Environment.TickCount64 + 1000;
                    Vector3 d = hand - (bike ? (i == 0 ? GripL : GripR) : Hub);
                    Log.Write($"RA01 steering: {Side(i)} grip squeezed {Cm(gap)} cm out of reach of the {(bike ? "grip" : "rim")} - your hand is {Cm(d.X)} cm right, {Cm(d.Y)} cm forward, {Cm(d.Z)} cm up of {(bike ? "it" : "the hub")}");
                }
            }
            if (!holding[i]) continue;
            anyHeld = true;
            if (!Angle(hand, bike, out float a, out float r)) continue;
            float delta = Wrap(a - lastAngle[i]) * MathF.Min(1f, r / RimRadius(bike)); // RA01: M19-4 - a hand inside the rim counts as on it
            lastAngle[i] = a;
            if (MathF.Abs(delta) > MathF.Abs(best)) best = delta;     // RA01: one hand: the hand that moved more turns the wheel
        }
        bool both = holding[0] && holding[1];
        if (both)                                                      // RA01: M19-4 - two hands: the broomstick between them
        {
            float line = LineAngle(left, right, bike);
            best = lineValid ? Wrap(line - lastLine) : 0f;
            lastLine = line;
        }
        lineValid = both;
        best = Math.Clamp(best, -MaxStep, MaxStep);
        if (anyHeld) wheel = Math.Clamp(wheel + best, -lockRad, lockRad);
        else wheel = Toward(wheel - wheel * (1f - MathF.Exp(-Tuning.SteerReturn * dt)), 0f, ReturnFloor * lockRad * dt); // RA01: M19-3 - self-centring, eased
        if (holding[0]) left = OnWheel(0, left, bike);                     // RA01: M19-2 - V's holding hands sit on the rim / grips
        if (holding[1]) right = OnWheel(1, right, bike);
        if (kind == VehicleState.Kind.Car && found) Horn(ref left, ref right, combat, offHand); // RA01: M30-1 - the physical horn (a car's wheel only)
        else HornOff();

        float target = Math.Clamp(-wheel / lockRad, -1f, 1f);          // RA01: clockwise (right hand down / back) = steer right = stick positive
        float soft = Math.Clamp(Tuning.SteerSoftness, 0f, 0.9f);
        target *= 1f - soft + soft * MathF.Abs(target);                 // RA01: centre softness (M19-4: the player's)
        stick = Toward(stick, target, Tuning.SteerSpeed * dt);          // RA01: power-steering damper
        float sent = MathF.Abs(stick) < OutTiny ? 0f : MathF.Sign(stick) * (OutDeadZone + (1f - OutDeadZone) * MathF.Abs(stick)); // RA01: M19-3 - past the game's dead zone
        ControllerMapping.SetSteer(true, sent, combat ? 1 - offHand : -1, carGuns); // RA01: M20-3 - gun out: the gun hand's grip stays a button; M84 - the car's own guns: A fires
        if (anyHeld && Environment.TickCount64 >= nextStatusLog)
        {
            nextStatusLog = Environment.TickCount64 + 2000;
            Log.Write($"RA01 steering: wheel {-wheel * 180f / MathF.PI:F0} deg, stick sent {sent:F2}, game's wheel {-GameTurn() * 180f / MathF.PI:F0} deg (hands: {(holding[0] ? "left " : "")}{(holding[1] ? "right" : "")})");
        }
    }

    // RA01: M19-2/3 - where a holding hand sits: a car - on the rim at the angle it grabbed, turned since by as much as the game's wheel has turned; a bike - on the
    // game's own grip for that hand (its wrist this frame). Cost - CPU: extremely low (a mailbox read, a sine and a cosine); GPU: none.
    static Vector3 OnWheel(int i, Vector3 hand, bool bike)
    {
        if (!VehicleState.TryReadGameHands(out Vector3 gl, out Vector3 gr)) return hand;
        if (bike && !combatNow) return i == 0 ? gl : gr;
        if (bike) { Vector3 h = Hub, g = (i == 0 ? GripL : GripR) - h; float c = MathF.Cos(wheel), sn = MathF.Sin(wheel); return new Vector3(h.X + g.X * c - g.Y * sn, h.Y + g.X * sn + g.Y * c, h.Z + g.Z); } // RA01: M20-3 - gun out: the learned grip, swung by our bar
        float a = grabRim[i] + Wrap(GameTurn(gl, gr) - grabGame[i]), r = Radius();
        return FromFace(r * MathF.Cos(a), r * MathF.Sin(a), 0f);                 // RA01: M28-6 - on the (leaning) rim
    }

    // RA01: M19-3 - how far the GAME'S wheel has turned from straight (radians, + = anticlockwise as you look at it): how much the line between its two wrists
    // (on the rim) has tilted since it was learned straight - a car on the wheel's face, a bike flat. 0 if not known.
    // Cost - CPU: extremely low (a mailbox read and one atan2); GPU: none.
    static float GameTurn() => VehicleState.TryReadGameHands(out Vector3 gl, out Vector3 gr) ? GameTurn(gl, gr) : 0f;
    static float GameTurn(Vector3 gl, Vector3 gr) => !found ? 0f : combatNow ? wheel : Wrap(LineAngle(gl, gr, bikeNow) - straightLine); // RA01: M20-3 - gun out: our wheel

    // RA01: M19-3 - the angle of the line from the left wrist to the right one: a car on the wheel's face (right, up), a bike flat (right, forward).
    // Cost - CPU: extremely low (one atan2); GPU: none.
    static float LineAngle(Vector3 l, Vector3 r, bool bike) => MathF.Atan2(bike ? r.Y - l.Y : UpFace(r - l), r.X - l.X); // RA01: M28-6 - a car: up its leaning face

    // RA01: M19-3 - for VRIK: is that hand glued to the wheel this frame (then its target is exact - no reach scaling)? Cost - CPU: extremely low (a read); GPU: none.
    public static bool PinnedHand(bool left) => holding[left ? 0 : 1];

    // RA01: averages in the game's wrist spots while the wheel is straight; logs once when the wheel is found. Until then nothing can be grabbed.
    // Cost - CPU: extremely low (a mailbox read and two blends per frame); GPU: none.
    static void Learn(bool bike)
    {
        if (MathF.Abs(stick) > StraightStick || MathF.Abs(wheel) > 0.05f || !VehicleState.TryReadGameHands(out Vector3 l, out Vector3 r)) return;
        if (learned > 0) { l = Vector3.Lerp(GripL, l, LearnRate); r = Vector3.Lerp(GripR, r, LearnRate); }
        glx = l.X; gly = l.Y; glz = l.Z; grx = r.X; gry = r.Y; grz = r.Z;
        straightLine = LineAngle(GripL, GripR, bike);                      // RA01: M19-3
        if (++learned < LearnFrames || found) return;
        found = true;
        Vector3 hub = Hub;
        Log.Write(bike ? $"RA01 steering: found the handlebars - grips {Cm(Vector3.Distance(GripL, GripR))} cm apart, pivot {Cm(hub.Y)} cm forward and {Cm(hub.Z)} cm up from V's feet"
                       : $"RA01 steering: found the wheel - radius {Cm(Radius())} cm, hub {Cm(hub.X)} cm right, {Cm(hub.Y)} cm forward, {Cm(hub.Z)} cm up from V's feet");
        if (VehicleState.WheelIsNew()) Banner.Show(bike ? "HANDLEBARS LEARNED" : "STEERING WHEEL LEARNED"); // RA01: M33-3 - first time per model (or every time if unreadable)
    }

    // RA01: M85 - THE CAR'S GUNS KEEP THE GAME'S WHEEL (Rob 2026-10-15): the wheel you see is turned by the game, slower and less far than your hands (03:03 log:
    // yours 49 deg, the game's 24). With the car's guns out V's hands were drawn on OUR wheel, so they slid round the visible rim whenever you turned. Her
    // hands are empty then, so the game still animates both wrists on its wheel: while both sit within WristOnRim of the learned rim ring (and of its face)
    // and are about a rim's width apart (within WristApart), her hands follow the game's wheel exactly as in normal driving. A little slack once on
    // (WristOffRim) so it doesn't flicker at the edge. Like checking both hands are really on the wheel before trusting where it points.
    // Cost - CPU: extremely low (a mailbox read, two square roots and a few compares per frame, only in drive-by mode); GPU: none.
    static bool GameWristsOnRim()
    {
        if (!VehicleState.TryReadGameHands(out Vector3 gl, out Vector3 gr)) return wristsOnWas = false;
        float limit = wristsOnWas ? WristOffRim : WristOnRim, rim = 2f * Radius();
        wristOffL = OffRim(gl); wristOffR = OffRim(gr); wristApart = Vector3.Distance(gl, gr);
        wristsOnWas = wristOffL <= limit && wristOffR <= limit && MathF.Abs(wristApart - rim) <= WristApart * rim;
        return wristsOnWas;
    }
    const float WristOnRim = 0.06f, WristOffRim = 0.10f, WristApart = 0.25f;
    static bool wristsOnWas;
    static float wristOffL, wristOffR, wristApart;

    // RA01: M85 - how far a spot is off the rim ring or out of the wheel's face, whichever is more (metres). Cost - CPU: extremely low (one square root); GPU: none.
    static float OffRim(Vector3 p)
    {
        Vector3 d = p - Hub;
        float up = UpFace(d);
        return MathF.Max(MathF.Abs(MathF.Sqrt(d.X * d.X + up * up) - Radius()), MathF.Abs(OutOfFace(d)));
    }

    // RA01: M85 LOOK (log only) - once a second in drive-by mode: the game's wrists against its rim, which wheel V's hands follow, both wheels' angles. At most
    // LookLines lines a session. Cost - CPU: extremely low (a clock read; a mailbox read and one log line a second); GPU: none.
    static void LookDriveBy(bool own)
    {
        long now = Environment.TickCount64;
        if (now < lookAt || lookLines >= LookLines || !found || bikeNow) return;
        lookAt = now + 1000; lookLines++;
        float game = VehicleState.TryReadGameHands(out Vector3 gl, out Vector3 gr) ? Wrap(LineAngle(gl, gr, false) - straightLine) : 0f;
        Log.Write($"RA01 steering (M85 look): game's wrists off its rim L {Cm(wristOffL)} cm / R {Cm(wristOffR)} cm, {Cm(wristApart)} cm apart (rim {Cm(2f * Radius())} cm) - hands follow {(own ? "your wheel" : "the game's wheel")}; game's wheel {-game * 180f / MathF.PI:F0} deg, yours {-wheel * 180f / MathF.PI:F0} deg");
    }
    static long lookAt;
    static int lookLines;
    const int LookLines = 600;

    // RA01: the rim's radius: half the gap between the two game wrists across the wheel's face (right and up). Cost - CPU: extremely low; GPU: none.
    static float Radius() => MathF.Max(0.08f, 0.5f * MathF.Sqrt((glx - grx) * (glx - grx) + (glz - grz) * (glz - grz)));

    // RA01: the averaged game wrist spots, and the hub halfway between them. Cost - CPU: extremely low (three float reads each); GPU: none.
    static Vector3 GripL => new(glx, gly, glz);
    static Vector3 GripR => new(grx, gry, grz);
    static Vector3 Hub => new((glx + grx) * 0.5f, (gly + gry) * 0.5f, (glz + grz) * 0.5f);

    // RA01: how far a hand is OUTSIDE its reach zone (0 or less = in reach): a car - off the rim ring (and the wheel's face); a bike - from its own grip.
    // Cost - CPU: extremely low (a few subtractions and a square root); GPU: none.
    static float Gap(int i, Vector3 hand, bool bike)
    {
        if (bike) return Vector3.Distance(hand, i == 0 ? GripL : GripR) - GripReach;
        Vector3 d = hand - Hub;                                                   // RA01: M28-6 - measured on the wheel's (leaning) face
        float up = UpFace(d), ring = MathF.Abs(MathF.Sqrt(d.X * d.X + up * up) - Radius()) - RimReach;
        return MathF.Max(ring, MathF.Abs(OutOfFace(d)) - FaceReach);
    }

    // RA01: a hand's angle around the hub (radians): a car - on the wheel's face (right, up), like a clock hand; a bike - flat (right, forward) about the
    // upright pivot; and how far from the hub it is. False when the hand is within MinRadius of the hub (no clear angle). Cost - CPU: extremely low (one atan2); GPU: none.
    static bool Angle(Vector3 hand, bool bike, out float angle, out float radius)
    {
        Vector3 d = hand - Hub;
        float a = d.X, b = bike ? d.Y : UpFace(d);                                // RA01: M28-6 - a car: up its leaning face
        angle = MathF.Atan2(b, a);
        radius = MathF.Sqrt(a * a + b * b);
        return radius > MinRadius;
    }

    // RA01: M19-4 - how far out a hand "on the rim" is: a car - the wheel's radius; a bike - half the gap between its grips. Cost - CPU: extremely low; GPU: none.
    static float RimRadius(bool bike) => bike ? MathF.Max(0.1f, 0.5f * Vector3.Distance(GripL, GripR)) : Radius();

    // RA01: M28-6 - the wheel's face, leaning: a gap from the hub (V's body: x right, y forward, z up) measured "up the face" and "out of the face" (forward of it).
    // Upright (no lean learned) they are plain up and forward, as before. Cost - CPU: extremely low (two multiplies and an add each); GPU: none.
    static float UpFace(Vector3 d) => d.Z * leanCos + d.Y * leanSin;
    static float OutOfFace(Vector3 d) => d.Y * leanCos - d.Z * leanSin;

    // RA01: M28-6 - back again: 'across' right, 'up' the face and 'out' of it from the hub, to a spot on V's body. Cost - CPU: extremely low; GPU: none.
    static Vector3 FromFace(float across, float up, float outOf)
    {
        Vector3 hub = Hub;
        return new Vector3(hub.X + across, hub.Y + up * leanSin + outOf * leanCos, hub.Z + up * leanCos - outOf * leanSin);
    }

    // RA01: M28-6 - LEARNING THE LEAN (see the class comment): while the GAME has its wheel turned well off straight, each of its two wrists is some way up or down
    // the wheel and some way forward or back of the hub; on a leaning wheel the two go together. A running best fit of "forward per up" over every such
    // sample (both wrists, every frame) gives the lean's slope; trusted after LeanSamples, logged once, refined ever after. Forgotten with the wheel.
    // Cost - CPU: extremely low (a mailbox read, a few multiplies and adds, an atan per frame while seated and turned); GPU: none.
    static void LearnLean()
    {
        if (!VehicleState.TryReadGameHands(out Vector3 gl, out Vector3 gr)) return;
        float turn = Wrap(MathF.Atan2(gr.Z - gl.Z, gr.X - gl.X) - straightLine);   // RA01: plain up (not the face's): the slope must not lean on itself
        if (MathF.Abs(turn) < LeanTurn) return;
        Vector3 hub = Hub;
        LeanSample(gl - hub);
        LeanSample(gr - hub);
        if (leanCount < LeanSamples || leanUpUp < 1e-6f) return;
        float lean = Math.Clamp(MathF.Atan(leanUpFwd / leanUpUp), -MaxLean, MaxLean);
        leanSin = MathF.Sin(lean); leanCos = MathF.Cos(lean);
        if (leanLogged) return;
        leanLogged = true;
        Log.Write($"RA01 steering: learned the wheel's lean - its top tips {MathF.Abs(lean) * 180f / MathF.PI:F0} deg {(lean >= 0f ? "away from you" : "toward you")} (from {leanCount} samples of the game turning it)");
    }

    // RA01: M28-6 - one wrist's sample for LearnLean (skipped near the hub's height, where it says nothing about the lean). Cost - CPU: extremely low; GPU: none.
    static void LeanSample(Vector3 d)
    {
        if (MathF.Abs(d.Z) < LeanMinUp) return;
        leanUpFwd += d.Z * d.Y;
        leanUpUp += d.Z * d.Z;
        leanCount++;
    }
    static bool leanLogged;

    // RA01: everything back to rest and the controls back to normal, with why in the log. Cost - CPU: extremely low (a few stores and a log line); GPU: none.
    static void Stop(string why)
    {
        active = false;
        if (!VehicleState.Mounted) { found = false; learned = 0; leanSin = 0f; leanCos = 1f; leanUpFwd = leanUpUp = 0f; leanCount = 0; leanLogged = false; } // RA01: M28-6 - the lean too // RA01: M20-3b - the wheel's spot is kept while V stays in the vehicle (a gun drawn stopped it, then nothing re-learned it)
        holding[0] = holding[1] = lineValid = false;
        handCurl[0] = handCurl[1] = -1f;
        wheel = stick = 0f;
        ControllerMapping.SetSteer(false, 0f);
        HornOff();                                                     // RA01: M30-1 - X lets go by itself (HoldHorn times out); this logs it
        Log.Write($"RA01 steering: off ({why})");
    }

    // RA01: M30-1 - THE PHYSICAL HORN (Rob 2026-10-10), a car only: the middle of the wheel is a doorbell. A free hand (not holding the rim; with a gun out only
    // the off hand, and not while it two-hands the gun) whose REAL palm (the controller's grip point, on V's body) is over the hub - within HubShare of the
    // rim's radius, measured on the wheel's leaning face - and pushed to the face (within HornPress of it; let go once back past HornRelease, a gap so it
    // doesn't stutter) holds the game's horn (ControllerMapping.HoldHorn: X - M30-1b). THE PAD PUSHES BACK: past PadGive into the face, V's hand is held there -
    // moved back out along the face's square-on direction - like a padded button that sinks a little, then stops: your real hand can go on, V's can't go
    // through the wheel. Logged on and off; at most every 2 s, how far short of the face a palm over the hub is (for tuning HornPress).
    // Cost - CPU: extremely low (a frame read, two quaternion turns and a few subtractions per hand); GPU: none.
    static void Horn(ref Vector3 left, ref Vector3 right, bool combat, int offHand)
    {
        if (!Controllers.TryReadFrame(out Controllers.Frame f)) { HornOff(); return; }
        float hubRadius = HubShare * Radius(), shortest = float.MaxValue;
        int pressing = -1;
        for (int i = 0; i < 2; i++)
        {
            if (holding[i] || (combat && i != offHand) || (i == offHand && TwoHandGrip.Holding) || !(i == 0 ? f.LeftValid : f.RightValid)) continue;
            Xr.Posef p = i == 0 ? f.Left : f.Right;
            if (!HandTargets.TryRoomToBody(f, new Vector3(p.Position.X, p.Position.Y, p.Position.Z), out Vector3 palm)) continue;
            Vector3 d = palm - Hub;
            float up = UpFace(d), outOf = OutOfFace(d) + PadOut;          // RA01: outOf: + = past the pad's surface (into the wheel), - = in front of it (your side)
            if (d.X * d.X + up * up > hubRadius * hubRadius || outOf > FaceReach || outOf < -FaceReach) continue;
            if (outOf < -(hornOn ? HornRelease : HornPress)) { shortest = MathF.Min(shortest, -outOf); continue; }
            pressing = i;
            if (outOf > PadGive)                                           // RA01: the pad pushes back: V's hand stops PadGive into the face
            {
                Vector3 back = new Vector3(0f, leanCos, -leanSin) * (outOf - PadGive);  // RA01: the face's square-on direction (out of the face, see OutOfFace)
                if (i == 0) left -= back; else right -= back;
            }
        }
        if (pressing >= 0)
        {
            ControllerMapping.HoldHorn();
            if (!hornOn) { hornOn = true; Log.Write($"RA01 steering: horn on ({Side(pressing)} palm on the hub)"); }
            return;
        }
        HornOff();
        if (shortest < float.MaxValue && Environment.TickCount64 >= nextHornLog)
        {
            nextHornLog = Environment.TickCount64 + 2000;
            Log.Write($"RA01 steering: a palm is over the hub, {Cm(shortest)} cm short of the horn pad (the horn needs under {Cm(HornPress)})");
        }
    }

    // RA01: M30-1 - the horn's "off" (X itself lets go within HornHoldMs - see ControllerMapping.HoldHorn), logged once. Cost - CPU: extremely low; GPU: none.
    static void HornOff()
    {
        if (!hornOn) return;
        hornOn = false;
        Log.Write("RA01 steering: horn off");
    }
    const float HubShare = 0.45f;                              // RA01: M30-1 - the horn pad: the middle 45% of the wheel's radius (about 8-11 cm on most cars)
    const float HornPress = 0.01f, HornRelease = 0.03f;        // RA01: M30-1 - honks within 1 cm of the face; lets go once 3 cm back from it
    const float PadGive = 0.015f;                              // RA01: M30-1 - how far the pad sinks before V's hand stops
    const float PadOut = 0.02f;                                // RA01: M30-1c - Rob 09:16: the pad's surface sits 2 cm out from the rim's face, toward you (it was inside the wheel)
    static bool hornOn;
    static long nextHornLog;

    // RA01: an angle step folded into -pi..pi (a hand crossing the 9 o'clock line isn't a full turn). Cost - CPU: extremely low; GPU: none.
    static float Wrap(float a) => a > MathF.PI ? a - 2f * MathF.PI : a < -MathF.PI ? a + 2f * MathF.PI : a;

    // RA01: moves 'from' toward 'to' by at most 'step'. Cost - CPU: extremely low; GPU: none.
    static float Toward(float from, float to, float step) => MathF.Abs(to - from) <= step ? to : from + MathF.Sign(to - from) * step;

    // RA01: for the log. Cost - CPU: extremely low; GPU: none.
    static string Side(int i) => i == 0 ? "left" : "right";
    static string Cm(float metres) => MathF.Round(metres * 100f).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
