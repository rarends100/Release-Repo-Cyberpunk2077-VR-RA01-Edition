using System.Diagnostics;

namespace RA01.Core;

// RA01: M6-0, layer 1 - the OpenXR action set: reads both motion controllers (triggers, grip squeeze, thumbsticks and their click, both face buttons per hand, the left menu button,
// and the grip/aim poses) once per headset frame into plain public fields, the same way HeadPose reads the head. This file only tracks the controllers, it never touches the game;
// Core\controls_mapping\ControllerMapping.cs (layer 2's mapping table) and XInputHook.cs are what actually act on these values.
// bindings are suggested for Quest Touch (Rob's own headset) and the generic khr/simple_controller fallback every OpenXR runtime supports.
// M50 (Rob 2026-10-12) - also Valve Index, Windows Mixed Reality, HP Reverb G2 and the Steam Frame (the last two only when the runtime offers their extension),
// each its own list, so a runtime that refuses one only skips that one. Not tested on real hardware yet (no such headset here); see each Suggest below.
// HTC Vive wands are NOT supported (Rob 01:38: too few buttons for this game - no stick, no Y); the Vive XR Elite's controllers are Touch-style.
// Cost - CPU: low (about 90 times a second: one xrSyncActions call plus thirteen xrGetActionState... calls and four xrLocateSpace calls, each a few microseconds inside the runtime, plus once at start-up
// a couple of dozen small setup calls); GPU: none (no GPU work of any kind).
internal static unsafe class Controllers
{
    static ulong xrInstance, xrSession, worldSpace;
    static ulong actionSet;
    static ulong leftPath, rightPath;
    static ulong gripPoseAction, aimPoseAction, triggerAction, gripAction, stickAction, stickClickAction, faceButtonAction, upperFaceButtonAction, menuButtonAction;
    static ulong leftGripSpace, rightGripSpace, leftAimSpace, rightAimSpace;
    static bool syncErrorLogged;
    static long nextReport; // RA01: M6-0 temporary verification report, see Report() below
    public static bool HpExtension; // RA01: M50 - set by XrSession.Connect: the runtime offered XR_EXT_hp_mixed_reality_controller and it was switched on
    public static bool FrameExtension; // RA01: M50-2 - set by XrSession.Connect: XR_VALVE_frame_controller_interaction switched on (the Frame-only inputs below exist only then)
    public static volatile Layout CurrentLayout; // RA01: M50-3 - checked once a second (CheckLayout); Other unless the runtime says otherwise
    public static bool FrameX, FrameY, DpadUp, DpadDown, DpadLeft, DpadRight, ViewButton, StartButton, LeftBumper, RightBumper; // RA01: M50-2 - Frame-only inputs; always false elsewhere
    static ulong frameXAction, frameYAction, dpadUpAction, dpadDownAction, dpadLeftAction, dpadRightAction, viewAction, bumperAction; // RA01: M50-2 - 0 unless FrameExtension
    static ulong startAction;                        // RA01: M50-3 - a right-hand Start (Index right trackpad press, Frame Menu); made for every runtime, unbound on Touch
    static ulong indexProfile, wmrProfile, frameProfile; // RA01: M50-3 - each profile's path once the runtime accepted its list (0 = refused / not sent)

    // RA01: M50-3 - which controller layout is in use (Other = Touch, G2, the simple fallback, or not known - all take the old path).
    // Cost - CPU: extremely low (a plain value); GPU: none.
    public enum Layout { Other, Index, Wmr, Frame }

    // RA01: M50-2 - a Steam Frame controller is the one in use right now. Cost - CPU: extremely low (a read); GPU: none.
    public static bool FrameActive => CurrentLayout == Layout.Frame;

    // RA01: the read side: what the rest of the mod looks at. Ready says whether Setup succeeded this run; everything else is only meaningful once it has, and is updated once per headset frame by Sync.
    public static bool Ready;
    public static float LeftTrigger, RightTrigger, LeftGrip, RightGrip;
    public static Xr.Vector2f LeftStick, RightStick;
    public static bool LeftStickClick, RightStickClick, LeftFaceButton, RightFaceButton, LeftUpperFaceButton, RightUpperFaceButton, MenuButton; // RA01: Lower = X/A, Upper = Y/B
    public static Xr.Posef LeftGripPose, RightGripPose, LeftAimPose, RightAimPose;
    public static bool LeftGripPoseValid, RightGripPoseValid, LeftAimPoseValid, RightAimPoseValid;

    // RA01: step 1 - once, called from XrSession.CreateSession right after the session and its floor space exist and before the first headset frame: makes the action set and its actions,
    // suggests bindings for the two profiles above, attaches the set to the session, and creates the four spaces the two poses (grip, aim) are located through each frame.
    // any failure here is caught and only turns controller tracking off (Ready stays false) - it must never stop the headset picture the mod already shows, so nothing here is allowed to
    // reach XrSession and abort the whole OpenXR thread the way a failure in XrSession's own start-up steps would.
    // Cost - CPU: low (runs once at start-up: about two dozen small OpenXR calls, well under a millisecond in total); GPU: none.
    public static void Setup(ulong instance, ulong session, ulong floorSpace)
    {
        try
        {
            xrInstance = instance;
            xrSession = session;
            worldSpace = floorSpace;
            leftPath = StringToPath("/user/hand/left");
            rightPath = StringToPath("/user/hand/right");
            ulong* bothHands = stackalloc ulong[2] { leftPath, rightPath };
            ulong* onlyLeft = stackalloc ulong[1] { leftPath }; // RA01: a static field's address needs a local copy first (unfixed), unlike a local variable's

            Xr.ActionSetCreateInfo setInfo = new() { Type = Xr.TypeActionSetCreateInfo, Priority = 0 };
            Xr.SetText(setInfo.ActionSetName, 64, "ra01_controls");
            Xr.SetText(setInfo.LocalizedActionSetName, 128, "RA01 controls");
            ulong setHandle;
            Check(Xr.CreateActionSet(xrInstance, &setInfo, &setHandle), "xrCreateActionSet");
            actionSet = setHandle;

            gripPoseAction = CreateAction("grip_pose", "Hand position", Xr.ActionTypePose, bothHands, 2);
            aimPoseAction = CreateAction("aim_pose", "Hand aim direction", Xr.ActionTypePose, bothHands, 2);
            triggerAction = CreateAction("trigger", "Trigger", Xr.ActionTypeFloat, bothHands, 2);
            gripAction = CreateAction("grip", "Grip squeeze", Xr.ActionTypeFloat, bothHands, 2);
            stickAction = CreateAction("stick", "Thumbstick", Xr.ActionTypeVector2f, bothHands, 2);
            stickClickAction = CreateAction("stick_click", "Thumbstick click", Xr.ActionTypeBoolean, bothHands, 2);
            faceButtonAction = CreateAction("face_button", "Lower face button (A/X)", Xr.ActionTypeBoolean, bothHands, 2);
            upperFaceButtonAction = CreateAction("upper_face_button", "Upper face button (B/Y)", Xr.ActionTypeBoolean, bothHands, 2);
            menuButtonAction = CreateAction("menu_button", "Menu button", Xr.ActionTypeBoolean, onlyLeft, 1); // RA01: Touch has only one, on the left controller
            ulong* onlyRight = stackalloc ulong[1] { rightPath };
            startAction = CreateAction("start_button", "Start (right hand)", Xr.ActionTypeBoolean, onlyRight, 1); // RA01: M50-3 - Index right trackpad press, Frame Menu
            if (FrameExtension)
            {
                // RA01: M50-2 - the Steam Frame's extra inputs: X / Y on the right hand, the D-pad and View on the left, a bumper on each.
                frameXAction = CreateAction("frame_x", "X button (right hand)", Xr.ActionTypeBoolean, onlyRight, 1);
                frameYAction = CreateAction("frame_y", "Y button (right hand)", Xr.ActionTypeBoolean, onlyRight, 1);
                dpadUpAction = CreateAction("dpad_up", "D-pad up", Xr.ActionTypeBoolean, onlyLeft, 1);
                dpadDownAction = CreateAction("dpad_down", "D-pad down", Xr.ActionTypeBoolean, onlyLeft, 1);
                dpadLeftAction = CreateAction("dpad_left", "D-pad left", Xr.ActionTypeBoolean, onlyLeft, 1);
                dpadRightAction = CreateAction("dpad_right", "D-pad right", Xr.ActionTypeBoolean, onlyLeft, 1);
                viewAction = CreateAction("view_button", "View button", Xr.ActionTypeBoolean, onlyLeft, 1);
                bumperAction = CreateAction("bumper", "Bumper", Xr.ActionTypeBoolean, bothHands, 2);
            }

            SuggestTouch();
            SuggestIndex();   // RA01: M50 - each one alone; a refusal only logs and skips that profile
            SuggestWmr();
            if (HpExtension) SuggestHp();
            if (FrameExtension) SuggestFrame();
            SuggestSimple();

            ulong attachSet = actionSet;
            Xr.SessionActionSetsAttachInfo attach = new() { Type = Xr.TypeSessionActionSetsAttachInfo, CountActionSets = 1, ActionSets = &attachSet };
            Check(Xr.AttachSessionActionSets(xrSession, &attach), "xrAttachSessionActionSets");

            leftGripSpace = CreateActionSpace(gripPoseAction, leftPath);
            rightGripSpace = CreateActionSpace(gripPoseAction, rightPath);
            leftAimSpace = CreateActionSpace(aimPoseAction, leftPath);
            rightAimSpace = CreateActionSpace(aimPoseAction, rightPath);

            nextReport = Stopwatch.GetTimestamp() + Stopwatch.Frequency; // RA01: first verification line one second from now, not immediately
            Ready = true;
            Log.Write($"RA01 controllers: action set attached (Touch, Index, WMR{(HpExtension ? ", HP Reverb G2" : "")}{(FrameExtension ? ", Steam Frame" : "")} and the generic fallback suggested - the log lines above say which the runtime accepted)");
        }
        catch (Exception e)
        {
            Ready = false;
            Log.Write($"RA01 controllers: setup failed, no controller tracking this run ({e.Message})");
        }
    }

    // RA01: one action, made in the set above. countSubactionPaths/subactionPaths say which hand(s) it applies to (1 and &leftPath for the menu button, 2 and bothHands for everything else).
    // Cost - CPU: extremely low (one small OpenXR call, run eight times total at start-up); GPU: none.
    static ulong CreateAction(string name, string niceName, int type, ulong* subactionPaths, uint count)
    {
        Xr.ActionCreateInfo info = new() { Type = Xr.TypeActionCreateInfo, ActionType = type, CountSubactionPaths = count, SubactionPaths = subactionPaths };
        Xr.SetText(info.ActionName, 64, name);
        Xr.SetText(info.LocalizedActionName, 128, niceName);
        ulong handle;
        Check(Xr.CreateAction(actionSet, &info, &handle), $"xrCreateAction({name})");
        return handle;
    }

    // RA01: the space a pose action (grip or aim) is located through each frame, same "no extra offset" pose CreateSpace in XrSession.cs uses for the fixed reference spaces.
    // Cost - CPU: extremely low (one small OpenXR call, run four times total at start-up); GPU: none.
    static ulong CreateActionSpace(ulong action, ulong subactionPath)
    {
        Xr.ActionSpaceCreateInfo info = new() { Type = Xr.TypeActionSpaceCreateInfo, Action = action, SubactionPath = subactionPath };
        info.PoseInActionSpace.Orientation.W = 1;
        ulong handle;
        Check(Xr.CreateActionSpace(xrSession, &info, &handle), "xrCreateActionSpace");
        return handle;
    }

    // RA01: the input paths Quest Touch offers for each action. A binding call failing (the runtime rejects a path) only logs and skips this one profile - the fallback below still gets tried.
    // Cost - CPU: extremely low (one OpenXR call carrying thirteen bindings, run once at start-up); GPU: none.
    static void SuggestTouch()
    {
        try
        {
            Suggest("/interaction_profiles/oculus/touch_controller",
                (gripPoseAction, "/user/hand/left/input/grip/pose"), (gripPoseAction, "/user/hand/right/input/grip/pose"),
                (aimPoseAction, "/user/hand/left/input/aim/pose"), (aimPoseAction, "/user/hand/right/input/aim/pose"),
                (triggerAction, "/user/hand/left/input/trigger/value"), (triggerAction, "/user/hand/right/input/trigger/value"),
                (gripAction, "/user/hand/left/input/squeeze/value"), (gripAction, "/user/hand/right/input/squeeze/value"),
                (stickAction, "/user/hand/left/input/thumbstick"), (stickAction, "/user/hand/right/input/thumbstick"),
                (stickClickAction, "/user/hand/left/input/thumbstick/click"), (stickClickAction, "/user/hand/right/input/thumbstick/click"),
                (faceButtonAction, "/user/hand/left/input/x/click"), (faceButtonAction, "/user/hand/right/input/a/click"),
                (upperFaceButtonAction, "/user/hand/left/input/y/click"), (upperFaceButtonAction, "/user/hand/right/input/b/click"),
                (menuButtonAction, "/user/hand/left/input/menu/click"));
        }
        catch (Exception e) { Log.Write($"RA01 controllers: Touch bindings not accepted by the runtime ({e.Message})"); }
    }

    // RA01: M50 - Valve Index: laid out like Touch, but A and B on BOTH hands (left A = X, left B = Y) and no menu button (its system button is the runtime's).
    // Start (the pause menu) is a firm press on the LEFT trackpad (trackpad/force - the Index pad has no click; OpenXR turns the press into a button).
    // Cost - CPU: extremely low (one OpenXR call carrying thirteen bindings, run once at start-up); GPU: none.
    static void SuggestIndex()
    {
        try
        {
            indexProfile = Suggest("/interaction_profiles/valve/index_controller",
                (gripPoseAction, "/user/hand/left/input/grip/pose"), (gripPoseAction, "/user/hand/right/input/grip/pose"),
                (aimPoseAction, "/user/hand/left/input/aim/pose"), (aimPoseAction, "/user/hand/right/input/aim/pose"),
                (triggerAction, "/user/hand/left/input/trigger/value"), (triggerAction, "/user/hand/right/input/trigger/value"),
                (gripAction, "/user/hand/left/input/squeeze/value"), (gripAction, "/user/hand/right/input/squeeze/value"),
                (stickAction, "/user/hand/left/input/thumbstick"), (stickAction, "/user/hand/right/input/thumbstick"),
                (stickClickAction, "/user/hand/left/input/thumbstick/click"), (stickClickAction, "/user/hand/right/input/thumbstick/click"),
                (faceButtonAction, "/user/hand/left/input/a/click"), (faceButtonAction, "/user/hand/right/input/a/click"),
                (upperFaceButtonAction, "/user/hand/left/input/b/click"), (upperFaceButtonAction, "/user/hand/right/input/b/click"),
                (menuButtonAction, "/user/hand/left/input/trackpad/force"),
                (startAction, "/user/hand/right/input/trackpad/force"));   // RA01: M50-3 - the right pad press: Start in left-hand mode (ControllerMapping)
        }
        catch (Exception e) { Log.Write($"RA01 controllers: Index bindings not accepted by the runtime ({e.Message})"); }
    }

    // RA01: M50 - Windows Mixed Reality controllers (Rob's layout): thumbsticks (with clicks) as on Touch, plus a trackpad and a menu button per hand, no A/B/X/Y.
    // A LEFT pad click = X, a RIGHT pad click = A; left menu = Start, right menu = B; grip on/off only. No Y (weapon swap).
    // Cost - CPU: extremely low (one OpenXR call carrying sixteen bindings, run once at start-up); GPU: none.
    static void SuggestWmr()
    {
        try
        {
            wmrProfile = Suggest("/interaction_profiles/microsoft/motion_controller",
                (gripPoseAction, "/user/hand/left/input/grip/pose"), (gripPoseAction, "/user/hand/right/input/grip/pose"),
                (aimPoseAction, "/user/hand/left/input/aim/pose"), (aimPoseAction, "/user/hand/right/input/aim/pose"),
                (triggerAction, "/user/hand/left/input/trigger/value"), (triggerAction, "/user/hand/right/input/trigger/value"),
                (gripAction, "/user/hand/left/input/squeeze/click"), (gripAction, "/user/hand/right/input/squeeze/click"),
                (stickAction, "/user/hand/left/input/thumbstick"), (stickAction, "/user/hand/right/input/thumbstick"),
                (stickClickAction, "/user/hand/left/input/thumbstick/click"), (stickClickAction, "/user/hand/right/input/thumbstick/click"),
                (faceButtonAction, "/user/hand/left/input/trackpad/click"), (faceButtonAction, "/user/hand/right/input/trackpad/click"),
                (upperFaceButtonAction, "/user/hand/right/input/menu/click"),
                (menuButtonAction, "/user/hand/left/input/menu/click"));
        }
        catch (Exception e) { Log.Write($"RA01 controllers: WMR bindings not accepted by the runtime ({e.Message})"); }
    }

    // RA01: M50 - HP Reverb G2 controllers: X / Y on the left, A / B on the right, a menu button, thumbsticks with clicks, analog grips - the same layout as Touch.
    // Only suggested when XrSession switched on XR_EXT_hp_mixed_reality_controller (HpExtension); otherwise a G2 uses the WMR list above.
    // Cost - CPU: extremely low (one OpenXR call carrying seventeen bindings, run once at start-up); GPU: none.
    static void SuggestHp()
    {
        try
        {
            Suggest("/interaction_profiles/hp/mixed_reality_controller",
                (gripPoseAction, "/user/hand/left/input/grip/pose"), (gripPoseAction, "/user/hand/right/input/grip/pose"),
                (aimPoseAction, "/user/hand/left/input/aim/pose"), (aimPoseAction, "/user/hand/right/input/aim/pose"),
                (triggerAction, "/user/hand/left/input/trigger/value"), (triggerAction, "/user/hand/right/input/trigger/value"),
                (gripAction, "/user/hand/left/input/squeeze/value"), (gripAction, "/user/hand/right/input/squeeze/value"),
                (stickAction, "/user/hand/left/input/thumbstick"), (stickAction, "/user/hand/right/input/thumbstick"),
                (stickClickAction, "/user/hand/left/input/thumbstick/click"), (stickClickAction, "/user/hand/right/input/thumbstick/click"),
                (faceButtonAction, "/user/hand/left/input/x/click"), (faceButtonAction, "/user/hand/right/input/a/click"),
                (upperFaceButtonAction, "/user/hand/left/input/y/click"), (upperFaceButtonAction, "/user/hand/right/input/b/click"),
                (menuButtonAction, "/user/hand/left/input/menu/click"));
        }
        catch (Exception e) { Log.Write($"RA01 controllers: HP Reverb G2 bindings not accepted by the runtime ({e.Message})"); }
    }

    // RA01: M50-2 - Steam Frame controllers (Rob's layout, 2026-10-12; paths from Valve's Steam Frame input docs). Right hand: A, B, X, Y (the game's A, B, X, Y)
    // and Menu (= Start). Left hand: the D-pad (= the game's D-pad) and View (a tap = Back, held 1 s = the F10 window - see ControllerMapping). Both: sticks
    // with clicks, triggers, analog grips, and a bumper that only ever presses LB / RB. Only suggested when XrSession switched on the Frame extension.
    // Cost - CPU: extremely low (one OpenXR call carrying twenty-six bindings, run once at start-up); GPU: none.
    static void SuggestFrame()
    {
        try
        {
            frameProfile = Suggest("/interaction_profiles/valve/frame_controller_valve",
                (gripPoseAction, "/user/hand/left/input/grip/pose"), (gripPoseAction, "/user/hand/right/input/grip/pose"),
                (aimPoseAction, "/user/hand/left/input/aim/pose"), (aimPoseAction, "/user/hand/right/input/aim/pose"),
                (triggerAction, "/user/hand/left/input/trigger/value"), (triggerAction, "/user/hand/right/input/trigger/value"),
                (gripAction, "/user/hand/left/input/squeeze/value"), (gripAction, "/user/hand/right/input/squeeze/value"),
                (stickAction, "/user/hand/left/input/thumbstick"), (stickAction, "/user/hand/right/input/thumbstick"),
                (stickClickAction, "/user/hand/left/input/thumbstick/click"), (stickClickAction, "/user/hand/right/input/thumbstick/click"),
                (faceButtonAction, "/user/hand/right/input/a/click"), (upperFaceButtonAction, "/user/hand/right/input/b/click"),
                (frameXAction, "/user/hand/right/input/x/click"), (frameYAction, "/user/hand/right/input/y/click"),
                (startAction, "/user/hand/right/input/menu/click"), (viewAction, "/user/hand/left/input/view/click"),
                (dpadUpAction, "/user/hand/left/input/dpad_up/click"), (dpadDownAction, "/user/hand/left/input/dpad_down/click"),
                (dpadLeftAction, "/user/hand/left/input/dpad_left/click"), (dpadRightAction, "/user/hand/left/input/dpad_right/click"),
                (bumperAction, "/user/hand/left/input/bumper/click"), (bumperAction, "/user/hand/right/input/bumper/click"));
        }
        catch (Exception e) { Log.Write($"RA01 controllers: Steam Frame bindings not accepted by the runtime ({e.Message})"); }
    }

    // RA01: M50-3 - which controller layout is in use (the runtime's current profile for the right hand), for the per-layout rules in ControllerMapping
    // (left-hand mode, the Steam Frame's D-pad and View). Logged on a change. Any failure, or a profile not listed here (Touch, G2, simple) = Other:
    // the controls then take exactly their old path.
    // Cost - CPU: extremely low (one small OpenXR call, once a second); GPU: none.
    static void CheckLayout()
    {
        Layout now = Layout.Other;
        if (Xr.GetCurrentInteractionProfile != null)
        {
            Xr.InteractionProfileState state = new() { Type = Xr.TypeInteractionProfileState };
            if (Xr.GetCurrentInteractionProfile(xrSession, rightPath, &state) >= 0 && state.InteractionProfile != 0)
            {
                ulong p = state.InteractionProfile;
                now = p == frameProfile ? Layout.Frame : p == indexProfile ? Layout.Index : p == wmrProfile ? Layout.Wmr : Layout.Other;
            }
        }
        if (now != CurrentLayout) Log.Write($"RA01 controllers: controller layout in use - {now} (M50-3){(now == Layout.Frame ? ": real D-pad, View tap = Back, hold View 1 s = settings window" : "")}");
        CurrentLayout = now;
    }

    // RA01: khr/simple_controller is the one profile every OpenXR runtime must support, but it only offers a handful of inputs (no thumbstick, no grip squeeze, no A/X):
    // select/click stands in for the trigger and is read as an on/off value, and the poses and menu button are named the same as on Touch.
    // Cost - CPU: extremely low (one OpenXR call carrying five bindings, run once at start-up); GPU: none.
    static void SuggestSimple()
    {
        try
        {
            Suggest("/interaction_profiles/khr/simple_controller",
                (gripPoseAction, "/user/hand/left/input/grip/pose"), (gripPoseAction, "/user/hand/right/input/grip/pose"),
                (aimPoseAction, "/user/hand/left/input/aim/pose"), (aimPoseAction, "/user/hand/right/input/aim/pose"),
                (triggerAction, "/user/hand/left/input/select/click"), (triggerAction, "/user/hand/right/input/select/click"),
                (menuButtonAction, "/user/hand/left/input/menu/click"));
        }
        catch (Exception e) { Log.Write($"RA01 controllers: the generic fallback bindings not accepted by the runtime ({e.Message})"); }
    }

    // RA01: builds and sends one xrSuggestInteractionProfileBindings call for one interaction profile, from a plain list of (action, input path) pairs.
    // Cost - CPU: extremely low (a handful of xrStringToPath calls plus one OpenXR call, run twice total at start-up); GPU: none.
    static ulong Suggest(string profile, params (ulong Action, string Path)[] bindings)
    {
        ulong profilePath = StringToPath(profile);
        Xr.ActionSuggestedBinding* list = stackalloc Xr.ActionSuggestedBinding[bindings.Length];
        for (int i = 0; i < bindings.Length; i++)
            list[i] = new Xr.ActionSuggestedBinding { Action = bindings[i].Action, Binding = StringToPath(bindings[i].Path) };
        Xr.InteractionProfileSuggestedBinding info = new() { Type = Xr.TypeInteractionProfileSuggestedBinding, InteractionProfile = profilePath, CountSuggestedBindings = (uint)bindings.Length, SuggestedBindings = list };
        Check(Xr.SuggestInteractionProfileBindings(xrInstance, &info), $"xrSuggestInteractionProfileBindings({profile})");
        Log.Write($"RA01 controllers: bindings suggested for {profile}");
        return profilePath; // RA01: M50-3 - for CheckLayout
    }

    // RA01: turns a path string ("/user/hand/left/input/trigger/value") into the XrPath atom every other action-set call uses.
    // Cost - CPU: extremely low (one small OpenXR call, run about twenty times total at start-up); GPU: none.
    static ulong StringToPath(string path)
    {
        byte[] text = System.Text.Encoding.ASCII.GetBytes(path + "\0");
        fixed (byte* bytes = text)
        {
            ulong handle;
            Check(Xr.StringToPath(xrInstance, bytes, &handle), $"xrStringToPath({path})");
            return handle;
        }
    }

    // RA01: step 2 - every headset frame, called from XrSession.RunFrame right alongside PollKeys: syncs the action set with the runtime, then reads every action's current value into the
    // public fields above. A failure here only leaves this frame's values where they were and is logged once, not thrown - one bad frame of controller data must never take down the
    // headset picture, which is why this differs from the rest of the OpenXR frame loop (see the note on Setup).
    // Cost - CPU: low (about 90 times a second: xrSyncActions, thirteen xrGetActionState... calls and four xrLocateSpace calls, each a few microseconds inside the runtime); GPU: none.
    public static void Sync(long displayTime)
    {
        if (!Ready) return;
        try
        {
            Xr.ActiveActionSet active = new() { ActionSet = actionSet, SubactionPath = 0 };
            Xr.ActionsSyncInfo syncInfo = new() { Type = Xr.TypeActionsSyncInfo, CountActiveActionSets = 1, ActiveActionSets = &active };
            Check(Xr.SyncActions(xrSession, &syncInfo), "xrSyncActions");

            LeftTrigger = GetFloat(triggerAction, leftPath);
            RightTrigger = GetFloat(triggerAction, rightPath);
            LeftGrip = GetFloat(gripAction, leftPath);
            RightGrip = GetFloat(gripAction, rightPath);
            LeftStick = GetStick(stickAction, leftPath);
            RightStick = GetStick(stickAction, rightPath);
            LeftStickClick = GetBool(stickClickAction, leftPath);
            RightStickClick = GetBool(stickClickAction, rightPath);
            LeftFaceButton = GetBool(faceButtonAction, leftPath);
            RightFaceButton = GetBool(faceButtonAction, rightPath);
            LeftUpperFaceButton = GetBool(upperFaceButtonAction, leftPath);
            RightUpperFaceButton = GetBool(upperFaceButtonAction, rightPath);
            MenuButton = GetBool(menuButtonAction, leftPath);
            StartButton = GetBool(startAction, rightPath);           // RA01: M50-3 - unbound on Touch: always false there
            if (FrameExtension)
            {
                FrameX = GetBool(frameXAction, rightPath);           // RA01: M50-2 - Frame-only inputs (unbound on any other controller, so they read false)
                FrameY = GetBool(frameYAction, rightPath);
                DpadUp = GetBool(dpadUpAction, leftPath);
                DpadDown = GetBool(dpadDownAction, leftPath);
                DpadLeft = GetBool(dpadLeftAction, leftPath);
                DpadRight = GetBool(dpadRightAction, leftPath);
                ViewButton = GetBool(viewAction, leftPath);
                LeftBumper = GetBool(bumperAction, leftPath);
                RightBumper = GetBool(bumperAction, rightPath);
            }

            LeftGripPoseValid = LocatePose(leftGripSpace, displayTime, out LeftGripPose);
            RightGripPoseValid = LocatePose(rightGripSpace, displayTime, out RightGripPose);
            LeftAimPoseValid = LocatePose(leftAimSpace, displayTime, out LeftAimPose);
            RightAimPoseValid = LocatePose(rightAimSpace, displayTime, out RightAimPose);
            syncErrorLogged = false; // RA01: it works again, so a later failure is worth saying again

            long now = Stopwatch.GetTimestamp();
            long due = Volatile.Read(ref nextReport);
            if (now >= due && Interlocked.CompareExchange(ref nextReport, now + Stopwatch.Frequency, due) == due)
            {
                CheckLayout(); // RA01: M50-3 - once a second
                Report();
            }
        }
        catch (Exception e)
        {
            if (!syncErrorLogged) Log.Write($"RA01 controllers: sync failed this frame, values held at their last reading ({e.Message})");
            syncErrorLogged = true;
        }
    }

    // RA01: temporary M6-0 verification report - once a second, prints what Layer 1 is actually tracking for each hand, so a real play session with the controllers moving can be checked
    // by eye against this log before Layer 2 (the real synthetic controller feed) is built on top of it. Purely a log line: it reads the public fields Sync already filled this frame
    // and changes nothing about tracking itself. Safe to remove, or leave running, once that's confirmed - either is fine, it costs almost nothing either way.
    // Cost - CPU: extremely low (two log lines built from fields already read this frame, at most once a second); GPU: none.
    static void Report()
    {
        Log.Write($"RA01 controllers: L trigger {LeftTrigger:0.00} grip {LeftGrip:0.00} stick ({LeftStick.X:0.00},{LeftStick.Y:0.00}){(LeftStickClick ? " CLICK" : "")}{(LeftFaceButton ? " X" : "")}{(LeftUpperFaceButton ? " Y" : "")}{(MenuButton ? " MENU" : "")}, pose {(LeftGripPoseValid ? $"({LeftGripPose.Position.X:0.00},{LeftGripPose.Position.Y:0.00},{LeftGripPose.Position.Z:0.00})" : "not tracked")}");
        Log.Write($"RA01 controllers: R trigger {RightTrigger:0.00} grip {RightGrip:0.00} stick ({RightStick.X:0.00},{RightStick.Y:0.00}){(RightStickClick ? " CLICK" : "")}{(RightFaceButton ? " A" : "")}{(RightUpperFaceButton ? " B" : "")}, pose {(RightGripPoseValid ? $"({RightGripPose.Position.X:0.00},{RightGripPose.Position.Y:0.00},{RightGripPose.Position.Z:0.00})" : "not tracked")}");
    }

    // RA01: one action's current on/off value. IsActive false (nothing bound, or the profile does not offer this input) reads as off, never as a stuck press.
    // Cost - CPU: extremely low (one small OpenXR call, run five times per hand-independent action per headset frame); GPU: none.
    static bool GetBool(ulong action, ulong subactionPath)
    {
        Xr.ActionStateGetInfo info = new() { Type = Xr.TypeActionStateGetInfo, Action = action, SubactionPath = subactionPath };
        Xr.ActionStateBoolean state = new() { Type = Xr.TypeActionStateBoolean };
        Xr.GetActionStateBoolean(xrSession, &info, &state);
        return state.IsActive != 0 && state.CurrentState != 0;
    }

    // RA01: one action's current 0-1 value (trigger pull, grip squeeze). IsActive false reads as 0, never as a stuck pull.
    // Cost - CPU: extremely low (one small OpenXR call, run per axis per headset frame); GPU: none.
    static float GetFloat(ulong action, ulong subactionPath)
    {
        Xr.ActionStateGetInfo info = new() { Type = Xr.TypeActionStateGetInfo, Action = action, SubactionPath = subactionPath };
        Xr.ActionStateFloat state = new() { Type = Xr.TypeActionStateFloat };
        Xr.GetActionStateFloat(xrSession, &info, &state);
        return state.IsActive != 0 ? state.CurrentState : 0f;
    }

    // RA01: a thumbstick's current position, -1 to 1 on each axis. IsActive false reads as centred (0,0).
    // Cost - CPU: extremely low (one small OpenXR call, run twice per headset frame); GPU: none.
    static Xr.Vector2f GetStick(ulong action, ulong subactionPath)
    {
        Xr.ActionStateGetInfo info = new() { Type = Xr.TypeActionStateGetInfo, Action = action, SubactionPath = subactionPath };
        Xr.ActionStateVector2f state = new() { Type = Xr.TypeActionStateVector2f };
        Xr.GetActionStateVector2f(xrSession, &info, &state);
        return state.IsActive != 0 ? state.CurrentState : default;
    }

    // RA01: where a hand's grip or aim pose is right now, in the same floor space the headset picture and head tracking already use. False if the runtime cannot say (hand out of view, just
    // turned on, and so on) - the last good pose is left in 'pose' rather than being zeroed, the same "keep the last good value" choice GetBool/GetFloat/GetStick make above.
    // Cost - CPU: extremely low (one small OpenXR call, run four times per headset frame); GPU: none.
    static bool LocatePose(ulong actionSpace, long displayTime, out Xr.Posef pose)
    {
        Xr.SpaceLocation location = new() { Type = Xr.TypeSpaceLocation };
        int result = Xr.LocateSpace(actionSpace, worldSpace, displayTime, &location);
        pose = location.Pose;
        return result >= 0 && (location.LocationFlags & Xr.SpaceLocationPositionValid) != 0 && (location.LocationFlags & Xr.SpaceLocationOrientationValid) != 0;
    }

    // RA01: step 3 - once, called from XrSession.Cleanup alongside its own destroy calls: frees the four action spaces, then every action, then the set itself.
    // Cost - CPU: extremely low (runs once at shutdown: fourteen small destroy calls); GPU: none.
    public static void Cleanup()
    {
        if (!Ready) return;
        Ready = false;
        if (leftGripSpace != 0) Xr.DestroySpace(leftGripSpace);
        if (rightGripSpace != 0) Xr.DestroySpace(rightGripSpace);
        if (leftAimSpace != 0) Xr.DestroySpace(leftAimSpace);
        if (rightAimSpace != 0) Xr.DestroySpace(rightAimSpace);
        Xr.DestroyAction(gripPoseAction);
        Xr.DestroyAction(aimPoseAction);
        Xr.DestroyAction(triggerAction);
        Xr.DestroyAction(gripAction);
        Xr.DestroyAction(stickAction);
        Xr.DestroyAction(stickClickAction);
        Xr.DestroyAction(faceButtonAction);
        Xr.DestroyAction(upperFaceButtonAction);
        Xr.DestroyAction(menuButtonAction);
        foreach (ulong action in new[] { frameXAction, frameYAction, dpadUpAction, dpadDownAction, dpadLeftAction, dpadRightAction, viewAction, startAction, bumperAction })
            if (action != 0) Xr.DestroyAction(action); // RA01: M50-2 - the Frame-only actions, if they were made
        if (actionSet != 0) Xr.DestroyActionSet(actionSet);
    }

    // RA01: a negative result is a failure. Kept local to this file the same way XrSession keeps its own copy - both wrap the same "a negative OpenXR result is an error" rule,
    // but XrSession's version is private to it and there is no shared place for it to live without a bigger reshuffle this milestone does not need.
    // Cost - CPU: extremely low (called after every OpenXR call in this file: one comparison, the name lookup and the throw happen only on a failure); GPU: none.
    static void Check(int result, string what)
    {
        if (result >= 0) return;
        string name = result.ToString();
        if (Xr.ResultToString != null && xrInstance != 0)
        {
            byte* text = stackalloc byte[64]; // RA01: XR_MAX_RESULT_STRING_SIZE
            if (Xr.ResultToString(xrInstance, result, text) >= 0) name = Xr.GetText(text);
        }
        throw new InvalidOperationException($"{what} failed: {name} ({result})");
    }

    // RA01: ---- M8-2: the tracking snapshot handed to Actions (HandTargets) ----

    // RA01: the headset and both grip poses from ONE headset frame, all in the floor space, copied together so a reader never mixes the head from one frame with a hand
    // from another (that mix would show up as hands jittering against the head).
    // Cost - CPU: extremely low (a plain 100-byte struct copied by value); GPU: none.
    public struct Frame
    {
        public Xr.Posef Head, Left, Right;
        public bool HeadValid, LeftValid, RightValid;
        public long Time; // RA01: Stopwatch ticks when it was published
    }

    // RA01: the same lock-free 4-slot ring HeadPose uses: the OpenXR thread fills the next slot and only then counts it as published, so readers never see a half-written one.
    static readonly Frame[] frames = new Frame[4];
    static int framesPublished;
    static readonly long MaxFrameAge = Stopwatch.Frequency / 10; // RA01: older than 100 ms = the headset has stopped rendering

    // RA01: OpenXR thread, once per rendered headset frame, right after the head is located (XrSession.RunFrame). The grip poses were read by Sync earlier in the same frame.
    // Cost - CPU: extremely low (one struct copy and a volatile write, run once per headset frame); GPU: none.
    public static void PublishFrame(bool haveHead, in Xr.Posef head)
    {
        Frame f = new()
        {
            Head = head, Left = LeftGripPose, Right = RightGripPose,
            HeadValid = haveHead, LeftValid = Ready && LeftGripPoseValid, RightValid = Ready && RightGripPoseValid,
            Time = Stopwatch.GetTimestamp(),
        };
        int next = framesPublished + 1;
        frames[next & 3] = f;
        Volatile.Write(ref framesPublished, next);
    }

    // RA01: any thread (the game's animation threads, through HandTargets). True = 'frame' is a whole, fresh snapshot.
    // Cost - CPU: extremely low (one struct copy, a memory barrier and two volatile reads; a retry is rare); GPU: none.
    public static bool TryReadFrame(out Frame frame)
    {
        for (int tries = 0; tries < 4; tries++)
        {
            int newest = Volatile.Read(ref framesPublished);
            frame = frames[newest & 3];
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref framesPublished) - newest < 3)
                return newest > 0 && Stopwatch.GetTimestamp() - frame.Time < MaxFrameAge;
        }
        frame = default;
        return false;
    }
}
