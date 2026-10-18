namespace RA01.Core;

// RA01: M6-1 - the "open the settings window with your hands" gesture, and the VR pointer that drives it once it is open, so nobody has to take the headset off and walk back to
// a mouse and keyboard to change a setting.
// the gesture is the same menu chord (M20-1: X then LT; was X+Y) ControllerMapping's menu-chord quirk already uses, but with the right stick pushed LEFT instead of up or down - TogglePressed only
// watches for that one moment (chord + left) and reports it exactly once per squeeze, the same "only once per press" edge a keyboard key already gets, so holding the pose does
// not flicker the window open and shut every frame it is checked. XrSession is the one that actually opens or closes it (the same ToggleMenu the F10 key already calls).
// once the window is open, the right hand (M28-4: the left in left-hand mode) becomes a laser pointer: Controllers.cs already tracks where that hand is and which way it is pointed every headset frame (RightAimPose),
// and XrSession already knows exactly where the settings panel is standing in the room (see PlaceMenu) - Update traces a straight line from the hand in the direction it is
// pointed until it crosses the panel's own flat plane, then turns the sideways and up/down distance from the panel's centre to that crossing point into a panel pixel, the
// same coordinate SettingsMenu.Input already takes from a real mouse (see SettingsMenu.Point). The right trigger is the click, squeezed the way you would squeeze a remote's button.
// a real mouse still works at the same time - Update reports back whether the hand was tracked this frame, and XrSession only calls SettingsMenu.Input (the mouse's own way in)
// when it was not, so the two never fight over the same click. M6-2: they used to both run every frame regardless, each with its own idea of whether the button was "down" -
// since the mouse button is almost always up, that told SettingsMenu "the mouse just let go" the instant after the pointer's own trigger-down had already grabbed a slider,
// which is a real release-and-re-grab, not just a flicker; it just happens once every headset frame, over and over, so a slider dragged with the pointer never actually got
// dragged - it was let go and re-grabbed at the current aim point something like ninety times a second, wherever the hand's own natural tremor put it that instant. That is
// the same reason two people cannot both hold one steering wheel and expect it to go anywhere useful - whichever one let go last instant, however briefly, is the one whose
// grip decides where the wheel sits right now, and the other one's hold before that never happened as far as the wheel is concerned.
// Cost - CPU: extremely low (a handful of dot products and one divide, once per headset frame: the gesture check always, the pointer math only while the window is open); GPU: none.
internal static class MenuPointer
{
    const float StickPressDeadzone = 0.4f; // RA01: matches ControllerMapping's own - how far left the stick has to move before it counts as the "left" of the chord
    const float ClickThreshold = 0.5f;     // RA01: trigger squeeze past halfway counts as a click, the same halfway ControllerMapping uses for the grip buttons
    // RA01: M28-4 - which hand is the pointer (and clicks with its trigger) while the window is open: the right hand, or the left in left-hand mode.
    // RA01: Cost - CPU: extremely low (a setting read); GPU: none.
    static bool RightHandPoints => !Tuning.LeftHanded;

    static bool chordWasActive; // RA01: last frame's chord + left, so TogglePressed only reports the moment it becomes true (see the class comment)
    const float ScrollDeadzone = 0.5f;   // RA01: M8-3 - how far up or down either stick must be pushed before it scrolls the F10 list
    const long ScrollRepeatMs = 450;     // RA01: M8-3 - holding the stick keeps scrolling, a page this often (M19-3: a page, so slower than the old 180 ms per row)
    static long nextScrollMs;            // RA01: M8-3 - when a held stick may scroll again (0 = the stick is centred, the next push scrolls at once)

    // RA01: call once per headset frame, whether or not the window is open. True exactly once, the frame the gesture is first made.
    // Cost - CPU: extremely low (three comparisons); GPU: none.
    public static bool TogglePressed()
    {
        bool active = ControllerMapping.MenuChord && ControllerMapping.TurnStick.X < -StickPressDeadzone; // RA01: M28-2/3 - the turning stick (the left one in left-hand mode) // RA01: M20-1 - X then LT (was X + Y)
        bool pressed = (active && !chordWasActive) | ControllerMapping.TakeViewToggle(); // RA01: M50-2 - or a Steam Frame View hold (non-short-circuit, so a waiting hold is always taken)
        chordWasActive = active;
        return pressed;
    }

    // RA01: M8-3 - while the window is open: either thumbstick pushed up or down scrolls the list a page (SettingsMenu.ScrollStep rows, M19-3) at once, then a page every ScrollRepeatMs
    // for as long as it is held - like holding an arrow key. Up is up the list. Centring the stick makes the next push scroll straight away again.
    // Cost - CPU: extremely low (two stick reads, a compare and a clock read, once per headset frame while the window is open); GPU: none.
    public static void StickScroll()
    {
        float y = MathF.Abs(Controllers.LeftStick.Y) > MathF.Abs(Controllers.RightStick.Y) ? Controllers.LeftStick.Y : Controllers.RightStick.Y;
        if (MathF.Abs(y) < ScrollDeadzone) { nextScrollMs = 0; return; }
        long now = Environment.TickCount64;
        if (now < nextScrollMs) return;
        SettingsMenu.ScrollBy(y > 0 ? -SettingsMenu.ScrollStep : SettingsMenu.ScrollStep); // RA01: M19-3 - a page (5 rows) per push, like Page Up / Page Down
        nextScrollMs = now + ScrollRepeatMs;
    }

    // RA01: while the window is open: aims from the pointing hand to the panel's own flat plane and feeds SettingsMenu the panel pixel it lands on, plus whether the click button
    // is down. Returns whether the pointing hand was tracked this frame at all - XrSession uses that (not whether a panel pixel was actually reached) to decide whether the
    // mouse also gets a turn this same frame; see the class comment's M6-2 note for why it must be one or the other, never both, every single frame. True but otherwise idle
    // (the mouse still stays locked out even though nothing below fired) when the hand is aimed so nearly along the panel's own face that there is no sensible crossing
    // point, or the crossing point is behind the hand's own pointing direction - both are just "not pointed at the panel right now", not "not using the pointer at all".
    // 'panelPose' and the panel's width and height in metres are XrSession's own - see PlaceMenu and ShowMenu - and must be in the same room space Controllers tracks in
    // (the floor space; XrSession only calls this then).
    // Cost - CPU: extremely low (about a dozen multiplies for the hand's own pointing direction, a few dot products, one divide, one SettingsMenu.Point call); GPU: none.
    public static bool Update(in Xr.Posef panelPose, float panelWidthMetres, float panelHeightMetres)
    {
        bool tracked = RightHandPoints ? Controllers.RightAimPoseValid : Controllers.LeftAimPoseValid;
        if (!tracked) return false; // RA01: nothing to aim with - let the mouse have this frame
        Xr.Posef aim = RightHandPoints ? Controllers.RightAimPose : Controllers.LeftAimPose;

        if (!HeadPose.YawOf(panelPose.Orientation, out float yaw)) return true; // RA01: the panel is placed with a pure yaw turn (see PlaceMenu) - this recovers that same angle
        float sin = MathF.Sin(yaw), cos = MathF.Cos(yaw);
        Vec right = new(cos, 0, -sin);   // RA01: the panel's own "sideways" direction in the room
        Vec up = new(0, 1, 0);           // RA01: the panel is never tilted, so "up" is just the room's own up
        Vec facing = new(sin, 0, cos);   // RA01: the panel's visible front, pointed back toward wherever the head was when it opened

        Vec beam = Forward(aim.Orientation); // RA01: the straight line the hand is pointing along
        float closingSpeed = Dot(beam, facing);
        if (MathF.Abs(closingSpeed) < 0.05f) return true; // RA01: aimed almost exactly edge-on to the panel - no sensible crossing point, but the hand is still the one in charge
        Vec toPanel = new(panelPose.Position.X - aim.Position.X, panelPose.Position.Y - aim.Position.Y, panelPose.Position.Z - aim.Position.Z);
        float distance = Dot(toPanel, facing) / closingSpeed;
        if (distance < 0) return true; // RA01: the panel is behind the hand's own pointing direction - the beam would have to run backwards to reach it

        Vec hit = new(aim.Position.X + beam.X * distance, aim.Position.Y + beam.Y * distance, aim.Position.Z + beam.Z * distance);
        Vec fromCentre = new(hit.X - panelPose.Position.X, hit.Y - panelPose.Position.Y, hit.Z - panelPose.Position.Z);
        float sideways = Dot(fromCentre, right), upward = Dot(fromCentre, up);

        float panelX = (sideways / panelWidthMetres + 0.5f) * SettingsMenu.PanelWidth;
        float panelY = (0.5f - upward / panelHeightMetres) * SettingsMenu.PanelHeight; // RA01: panel pixel rows count down from the top, room "up" counts up, so this one flips
        SettingsMenu.Point(panelX, panelY, (RightHandPoints ? Controllers.RightTrigger : Controllers.LeftTrigger) > ClickThreshold);
        return true;
    }

    // RA01: a plain room-space direction or offset - just three numbers, kept separate from Xr.Vector3f so the dot-product and turn math below reads as ordinary arithmetic.
    readonly record struct Vec(float X, float Y, float Z);

    // RA01: how much one direction points the same way as another (1 = the same way, 0 = square across, -1 = opposite) times both their lengths - here both inputs are unit
    // length, so it is just "the same way-ness" on its own.
    // Cost - CPU: extremely low (three multiplies and two adds); GPU: none.
    static float Dot(Vec a, Vec b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    // RA01: OpenXR is right-handed with y up and -z forward (HeadPose.cs already notes this), so a pose's own "straight ahead" is its local (0,0,-1) turned by its orientation -
    // the usual v + 2w(o x v) + 2o x (o x v) turn, worked out here for that one fixed v so it is a handful of multiplies rather than a general-purpose rotate.
    // Cost - CPU: extremely low (about a dozen multiplies and adds); GPU: none.
    static Vec Forward(in Xr.Quaternionf q) => new(
        -2f * (q.W * q.Y + q.X * q.Z),
        2f * (q.W * q.X - q.Y * q.Z),
        -1f + 2f * (q.X * q.X + q.Y * q.Y));
}
