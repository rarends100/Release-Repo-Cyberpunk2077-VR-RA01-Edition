using RA01.Core;

namespace RA01.Actions;

// RA01: M13-2 - the crosshair rule (Weapons tab "Physical aiming"). What V holds comes from Core's read-only WeaponProbe; this decides whether the game's
// crosshair belongs on the HUD panel and tells Core's HudLayer (HideCrosshair). M13-8 - Core's CrosshairProbe acts on it with the game's own crosshair
// switch (the M13-2 centre wipe is gone). The rule:
//  - a gun drawn: hidden while Physical aiming is on (you aim with the gun, so a dot where your head points only misleads) - except a Smart gun
//    (M13-47: it locks on by itself, so it keeps the head-aimed crosshair; Actions\FireArmAiming.cs makes the shot follow that crosshair);
//  - melee weapons (M13-9), fists or Gorilla Arms (M13-6): always hidden;
//  - Monowire (M13-62, the user's call for now): shown - with M13-61 it is drawn on the game's true aim, which the wire follows. Hiding it for all melee
//    comes back with the planned physical melee hits;
//  - Mantis Blades (M13-8, the user's call: their crosshair looks too good to lose), the Projectile Launch System (M13-7, head-aimed by design), and anything
//    we don't recognise (empty hands): left as the game shows it.
// M54-3 - JOHNNY (Rob 09:27): in Johnny Silverhand's body (GameAnchor.InJohnnysBody) with Physical aiming on, the crosshair is ALWAYS hidden - gun, fists,
// empty hands or anything we don't recognise; he never needs it. V's rules above stay exactly as they are (Rob 09:28).
// M73 - LOOKING THROUGH A CAMERA (GameAnchor.LookingThroughCamera: a mounted turret, a taken-over security camera, a braindance): the crosshair is left
// as the game shows it, whatever V still holds - the turret's reticle is how you aim there, and with the crosshair shown FireArmAiming keeps the game's
// own aim (no barrel takeover from V's rifle at her side; Rob 2026-10-13, Panam's car turret). Overrides the Johnny rule too (a braindance's stand-in).
// M74 - "PRACTICE MAKES PERFECT" (GameAnchor.InTrainingSim): the training avatar is the same body class as Johnny's, so the Johnny rule hid the crosshair
// there; in the training V's own rules apply instead - hidden with a gun, shown for scanning and empty hands (Rob 2026-10-13).
// M75 - Johnny with his gun PUT AWAY keeps the crosshair as the game shows it, for elevator buttons, doors and the like (Rob 2026-10-13/14). His put-away
// state reads as Fists (the game's events) or Melee (the skeleton reading, 00:43 log), so the Johnny rule now hides it ONLY while he holds a gun.
// M75-6 - ...or while his FISTS are up (melee mode: Fists/Melee with the game's melee state anything but NotReady - GameAnchor.TryMeleeState, read from
// his body). Fists put away (NotReady) or nothing in hand: shown (Rob 00:58).
// M76 - UNARMED, V or Johnny (nothing in hand, or fists put away - Johnny's also read as Melee): the game draws no crosshair then, so Core\UnarmedDot.cs is
// switched on (UnarmedDot.Wanted) - our own small olive dot on the true aim. Not while looking through a camera (the game's view) or in a vehicle.
// M79 - IN A TANK (the Basilisk - VehicleState says Tank): always seen in third person and aimed with the game's own camera, so it takes the camera rule
// above - crosshair as the game shows it, the game's own aim - whatever V still holds (her pistol read as "Gun" and hid the crosshair; Rob 2026-10-14).
// Runs just before each controller read is answered (registered with ControllerMapping), about 60 times a second; logs only when the decision changes.
// Cost - CPU: extremely low (a short locked read and a few compares per controller read); GPU: none.
internal static unsafe class WeaponRules
{
    static WeaponProbe.Kind lastKind = (WeaponProbe.Kind)(-1);
    static bool lastHide;
    static string lastName = "";
    static bool lastJohnny;                          // RA01: M54-3 - the Johnny rule was last in force                     // RA01: M13-32 - the exact type last logged (WeaponProbe.HeldName)
    static bool lastCamera;                          // RA01: M73 - the camera rule was last in force
    static bool lastTank;                            // RA01: M79 - the tank rule was last in force

    // RA01: runs Update just before every controller read is answered. Call once at start-up, after XInputHook.
    // Cost - CPU: extremely low (stores one pointer); GPU: none.
    public static void Install() => ControllerMapping.AddBeforeBuild(&Update);

    // RA01: one decision; a log line only when what V holds or the decision changes.
    // Cost - CPU: extremely low (see the class comment); GPU: none.
    static void Update()
    {
        WeaponProbe.Kind kind = WeaponProbe.Held;
        bool hide = kind switch
        {
            WeaponProbe.Kind.Gun => Tuning.PhysicalAiming && !WeaponProbe.HeldSmart,
            WeaponProbe.Kind.Melee or WeaponProbe.Kind.Fists or WeaponProbe.Kind.GorillaArms => true,   // RA01: M13-62 - Monowire keeps its crosshair (for now)
            _ => false,
        };
        bool tank = VehicleState.Mounted && VehicleState.Current == VehicleState.Kind.Tank; // RA01: M79 - the Basilisk
        bool camera = GameAnchor.LookingThroughCamera || tank;                       // RA01: M73 - a turret, a security camera, a braindance (M79: or a tank)
        bool inJohnny = !camera && !GameAnchor.InTrainingSim && GameAnchor.InJohnnysBody;    // RA01: M74 - not the training
        bool fistsUp = kind is WeaponProbe.Kind.Fists or WeaponProbe.Kind.Melee && GameAnchor.TryMeleeState(out int melee) && melee != 0; // RA01: M75-6 - 0 = NotReady
        bool johnny = inJohnny && Tuning.PhysicalAiming && (kind == WeaponProbe.Kind.Gun || fistsUp); // RA01: M54-3 - Johnny with his gun out or fists up (M75/M75-6)
        if (johnny) hide = true;
        else if (inJohnny) hide = false;                                                     // RA01: M75 - his gun put away (Fists / Melee / None): shown, for buttons and doors
        if (camera) hide = false;
        HudLayer.HideCrosshair = hide;
        bool down = (kind == WeaponProbe.Kind.Fists || (inJohnny && kind == WeaponProbe.Kind.Melee)) && GameAnchor.TryMeleeState(out int state) && state == 0; // RA01: M76 - put away
        UnarmedDot.Wanted = !camera && !VehicleState.Mounted && (kind == WeaponProbe.Kind.None || down);                                                  // RA01: M76
        string name = WeaponProbe.HeldName;
        if (kind == lastKind && hide == lastHide && name == lastName && johnny == lastJohnny && camera == lastCamera && tank == lastTank) return; // RA01: (a fists-up change flips 'johnny', so it is logged)
        lastKind = kind; lastHide = hide; lastName = name; lastJohnny = johnny; lastCamera = camera; lastTank = tank;
        if (tank) { Log.Write("RA01 weapons: in a tank (Basilisk) -> crosshair as the game shows it, the game's own aim (M79)"); return; }
        if (camera) { Log.Write("RA01 weapons: looking through a camera (turret, security camera or braindance) -> crosshair as the game shows it, the game's own aim (M73)"); return; }
        if (johnny) { Log.Write($"RA01 weapons: Johnny's body ({(name.Length > 0 ? name : kind.ToString())}{(fistsUp ? ", fists up" : "")}) -> crosshair hidden (Physical aiming, M54-3)"); return; }
        if (inJohnny) { Log.Write($"RA01 weapons: Johnny's body, gun put away ({kind}) -> crosshair as the game shows it (M75)"); return; }
        Log.Write($"RA01 weapons: V holds {kind switch { WeaponProbe.Kind.Gun => "a gun", WeaponProbe.Kind.MantisBlades => "Mantis Blades", WeaponProbe.Kind.Monowire => "Monowire", WeaponProbe.Kind.GorillaArms => "Gorilla Arms", WeaponProbe.Kind.ProjectileLauncher => "the Projectile Launcher", WeaponProbe.Kind.Melee => "a melee weapon", WeaponProbe.Kind.Fists => "fists", _ => "nothing we recognise" }}{(name.Length > 0 ? $" ({name}, from the game's data)" : "")} -> crosshair {(hide ? "hidden" : "as the game shows it")}");
    }
}
