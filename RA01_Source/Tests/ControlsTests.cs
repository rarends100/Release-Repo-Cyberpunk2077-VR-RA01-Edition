using RA01.Core;
using Xunit;

namespace RA01.Tests;

// RA01: M47 - THE CONTROLLER MAPPING (Core\controls_mapping\ControllerMapping.cs Build): what the game's "Xbox pad" sees for what your VR controllers do.
// Each test sets the controllers' state by hand (no headset needed), asks Build for the pad, and checks the buttons, triggers and sticks.
// Builder picture: testing the wiring of a switch panel with a meter - flip one switch, check which bulb lights.
// Cost - CPU: extremely low (a handful of Build calls); GPU: none.
public sealed class ControlsTests : IDisposable
{
    const ushort DpadUp = 0x0001, Start = 0x0010, LeftShoulder = 0x0100, RightShoulder = 0x0200, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;
    readonly Action restoreTuning = Kit.Remember(typeof(Tuning)), restoreControllers = Kit.Remember(typeof(Controllers)), restoreMapping = Kit.Remember(typeof(ControllerMapping));
    readonly bool menuWasOpen = SettingsMenu.Open;

    // RA01: hands at rest, right-handed, playing (no menu), and the extras that read the game (body follow, keep level, aim stick) off.
    // RA01: Cost - CPU: extremely low; GPU: none.
    public ControlsTests()
    {
        Tuning.LeftHanded = false; Tuning.MenuView = 2; Tuning.StickPitch = true; Tuning.KeepViewLevel = false; Tuning.BodyFollowEnabled = false; Tuning.AimStickDpad = false;
        SettingsMenu.Open = false;
        Controllers.LeftTrigger = Controllers.RightTrigger = Controllers.LeftGrip = Controllers.RightGrip = 0;
        Controllers.LeftStick = default; Controllers.RightStick = default;
        Controllers.LeftStickClick = Controllers.RightStickClick = Controllers.LeftFaceButton = Controllers.RightFaceButton = false;
        Controllers.LeftUpperFaceButton = Controllers.RightUpperFaceButton = Controllers.MenuButton = false;
        Build(); Build();   // RA01: settle the "was down" memories (chord, quick click) on the rest state
    }

    // RA01: everything back as it was. Cost - CPU: extremely low; GPU: none.
    public void Dispose()
    {
        SettingsMenu.Open = menuWasOpen;
        restoreMapping(); restoreControllers(); restoreTuning();
    }

    static ControllerMapping.Gamepad Build() => ControllerMapping.Build();
    static bool Has(ControllerMapping.Gamepad pad, ushort button) => (pad.Buttons & button) != 0;

    // RA01: nothing touched: a quiet pad. Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Hands_at_rest_send_nothing()
    {
        ControllerMapping.Gamepad pad = Build();
        Assert.Equal((ushort)0, pad.Buttons);
        Assert.Equal((0, 0, 0, 0), (pad.LeftTrigger, pad.RightTrigger, (int)pad.ThumbLX, (int)pad.ThumbLY));
    }

    // RA01: the face buttons: right hand lower/upper = A/B, left hand lower/upper = X/Y; left-hand mode swaps the hands. Cost - CPU: extremely low; GPU: none.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Face_buttons(bool leftHanded)
    {
        Tuning.LeftHanded = leftHanded;
        Controllers.RightFaceButton = true; Assert.True(Has(Build(), leftHanded ? X : A), "right lower button"); Controllers.RightFaceButton = false; Build();
        Controllers.RightUpperFaceButton = true; Assert.True(Has(Build(), leftHanded ? Y : B), "right upper button"); Controllers.RightUpperFaceButton = false; Build();
        Controllers.LeftFaceButton = true; Assert.True(Has(Build(), leftHanded ? A : X), "left lower button"); Controllers.LeftFaceButton = false; Build();
        Controllers.LeftUpperFaceButton = true; Assert.True(Has(Build(), leftHanded ? B : Y), "left upper button"); Controllers.LeftUpperFaceButton = false; Build();
        Controllers.MenuButton = true; Assert.True(Has(Build(), Start), "menu button = Start");
    }

    // RA01: triggers are analog 0..255 (left-hand mode swaps them while playing); the grips press the shoulder buttons past halfway.
    // Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Triggers_and_grips()
    {
        Controllers.LeftTrigger = 0.5f; Controllers.RightTrigger = 1f;
        ControllerMapping.Gamepad pad = Build();
        Assert.Equal((byte)127, pad.LeftTrigger);
        Assert.Equal((byte)255, pad.RightTrigger);
        Tuning.LeftHanded = true;
        pad = Build();
        Assert.Equal((byte)255, pad.LeftTrigger);
        Assert.Equal((byte)127, pad.RightTrigger);
        Tuning.LeftHanded = false;
        Controllers.LeftTrigger = Controllers.RightTrigger = 0;
        Controllers.LeftGrip = 0.4f; Controllers.RightGrip = 0.9f;
        pad = Build();
        Assert.False(Has(pad, LeftShoulder), "a grip under halfway is not a press");
        Assert.True(Has(pad, RightShoulder), "a grip past halfway presses RB");
    }

    // RA01: the sticks go through as -32768..32767; with the F10 window open the game gets no sticks and no right trigger (they work the window).
    // Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Sticks_and_the_F10_window()
    {
        Controllers.LeftStick = new Xr.Vector2f { X = 0.5f, Y = -1f };
        Controllers.RightTrigger = 1f;
        ControllerMapping.Gamepad pad = Build();
        Assert.Equal((short)16383, pad.ThumbLX);
        Assert.Equal((short)-32767, pad.ThumbLY);
        SettingsMenu.Open = true;
        pad = Build();
        Assert.Equal((0, 0, 0, 0, 0), ((int)pad.ThumbLX, (int)pad.ThumbLY, (int)pad.ThumbRX, (int)pad.ThumbRY, (int)pad.RightTrigger));
    }

    // RA01: right stick held in: the left stick becomes the D-pad (and stops walking). Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Right_stick_click_turns_left_stick_into_dpad()
    {
        Controllers.RightStickClick = true;
        Controllers.LeftStick = new Xr.Vector2f { X = 0f, Y = 0.9f };
        ControllerMapping.Gamepad pad = Build();
        Assert.True(Has(pad, DpadUp), "D-pad up");
        Assert.Equal((short)0, pad.ThumbLY);
    }

    // RA01: the menu chord: hold X, then pull LT, then push the right stick up = Start (the pause menu). Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Menu_chord_opens_the_pause_menu()
    {
        Controllers.LeftFaceButton = true; Build();
        Controllers.LeftTrigger = 1f; Build();
        Controllers.RightStick = new Xr.Vector2f { X = 0f, Y = 0.9f };
        ControllerMapping.Gamepad pad = Build();
        Assert.True(Has(pad, Start), "Start from the chord");
        Assert.Equal((byte)0, pad.LeftTrigger);   // RA01: the chord's trigger stays quiet while it is held
    }

    // RA01: a held trigger from a feature (e.g. a physical block) presses it fully without your finger; letting go returns it to your finger.
    // Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Held_triggers_from_features()
    {
        ControllerMapping.HoldRightTrigger(true, "test");
        Assert.Equal((byte)255, Build().RightTrigger);
        ControllerMapping.HoldRightTrigger(false, "test");
        Assert.Equal((byte)0, Build().RightTrigger);
        ControllerMapping.HoldLeftTrigger(true, "test");
        Assert.Equal((byte)255, Build().LeftTrigger);
        ControllerMapping.HoldLeftTrigger(false, "test");
        Assert.Equal((byte)0, Build().LeftTrigger);
    }
}
