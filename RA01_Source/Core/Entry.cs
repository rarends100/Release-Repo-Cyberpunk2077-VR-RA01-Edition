using System.Runtime.InteropServices;

namespace RA01.Core;

// RA01: the way into the mod: the single function the launcher starts inside the game.
// Cost - CPU: extremely low (it only holds one start-up function, which runs once); GPU: none (no GPU work).
public static class Entry
{
    static int started;   // RA01: M49-2 - 1 once Start has run in this game (2026-10-11 09:03: two launches injected and Start ran twice, every Install with it)

    // RA01: exported by name so the launcher can find it with GetProcAddress and start it on a new thread inside the game.
    // Cost - CPU: extremely low for its own work (runs once at start-up on its own thread: one log line and two calls, whose costs are rated in PresentHook.cs and CameraHook.cs);
    // GPU: none (it starts the hooks, it records no GPU work).
    [UnmanagedCallersOnly(EntryPoint = "RA01_Start")]
    public static uint Start(nint unused)
    {
        if (Interlocked.Exchange(ref started, 1) == 1) { Log.Write("RA01: already started in this game - second start ignored (M49-2)"); return 0; } // RA01: M49-2 - one start per game, whatever called twice
        Log.Write($"RA01 Core started inside {Path.GetFileName(Environment.ProcessPath)} (pid {Environment.ProcessId})");
        CrashWatch.Install();  // RA01: M13-10, M13-11 - first: the black box (breadcrumbs and the last serious fault, kept on disk even through a silent crash)
        CrashWatch.Mark("start-up: PresentHook");
        PresentHook.Install(); // RA01: M1 - start watching the game's frames
        CrashWatch.Mark("start-up: CameraHook");
        CameraHook.Install();  // RA01: M4a - start watching the game's camera; also loads ExeScan, which XInputHook below needs
        CrashWatch.Mark("start-up: HudLayer");
        HudLayer.Install();    // RA01: M10-1 - lifts the HUD off the game's picture onto its own headset panel (see HudLayer.cs)
        CrashWatch.Mark("start-up: PoseHook");
        PoseHook.Install();          // RA01: M7-0 - the doorway to each character's bone list, just before the full body is built (see PoseHook.cs)
        CrashWatch.Mark("start-up: PlayerSkeleton");
        PlayerSkeleton.Install();    // RA01: M7-6 - picks V's own bone list out of every body PoseHook sees and hands only hers on (see PlayerSkeleton.cs)
        CrashWatch.Mark("start-up: CrosshairProbe");
        CrosshairProbe.Install();    // RA01: M13-6 - read-only: notes the game's crosshair manager and logs its visibility fields (see CrosshairProbe.cs)
        CrashWatch.Mark("start-up: WeaponEventProbe");
        WeaponEventProbe.Install();  // RA01: M13-25, M13-32 - the game's own "a weapon went active" event and the weapon's exact type; WeaponProbe.Held uses it first (see WeaponEventProbe.cs)
        CrashWatch.Mark("start-up: AimProbe");
        AimProbe.Install();          // RA01: M13-43, M13-47 - the door to the game's aim line inside PhysX; re-lays it as Actions\FireArmAiming.cs decides (see AimProbe.cs)
        CrashWatch.Mark("start-up: ShotProbe");
        NullPlayerGuard.Install();   // RA01: M56 - load safety: the hold-button indicator handed no player (a save inside Johnny's body) gets "nothing found" instead of a crash (see NullPlayerGuard.cs)
        ShotProbe.Install();         // RA01: M13-52, M13-60 - the player's aim source: steers V's shots, damage and projectiles to the barrel or crosshair (see ShotProbe.cs)
        CrashWatch.Mark("start-up: GameAnchor");
        PerfMonitor.Install();       // RA01: M20-12 - the Performance tab's gauges (frame rate, CPU, GPU, GPU memory), read-only (see PerfMonitor.cs)
        CullingLab.Install();        // RA01: M21-1 - Dev tab test bench for the game's culling settings; finds them by name ~15 s in, changes nothing until a row is moved (see CullingLab.cs)
        NodeProbe.Install();         // RA01: M21-8 - log-only: finds the All Foods lab mesh node when it loads and watches its render objects (Dev row "Room probe (log)", see NodeProbe.cs)
        DistanceScaleFix.Install();  // RA01: M21-9 - TEST: caps the game's field-of-view distance stretch that drops rooms at a wide view (Dev row "Wide-view distance fix (test)", see DistanceScaleFix.cs)
        BlackboardProbe.Install();   // RA01: M21-11 - copies of every blackboard write; keeps the 'radio / car list / phone / popup shown' whiteboard XrSession's crosshair cut-out reads (see BlackboardProbe.cs)
        RenderCameraProbe.Install(); // RA01: M26-1 - exact picture pairing: reads the camera each frame is really drawn with (see RenderCameraProbe.cs)
        ThrowLever.Install();        // RA01: M22-4 - the lever on every projectile's flight path: V's grenades can be given a landing point and angle (see ThrowLever.cs)
        ThrowProbe.Install();        // RA01: M21-4 - log-only test for physical grenade and knife throws (Dev row "Throw probe (test)", see ThrowProbe.cs)
        GameAnchor.Install();        // RA01: M14-0 - log-only test: the game engine -> game systems -> V, and which systems tell menus and loading screens (see GameAnchor.cs)
        AimZoomData.Install();       // RA01: M35 - no aim zoom for every gun and scope, in the game's own data (see AimZoomData.cs)
        AimSwitchProbe.Install();    // RA01: M17-0 - log-only test: what changes on V's gun when you aim (Dev row "Aim switch probe (test)", see AimSwitchProbe.cs)
        VehicleProbe.Install();      // RA01: M19-0 - log-only test: where V keeps her vehicle and seat, and car vs bike (Dev row "Vehicle probe (test)", see VehicleProbe.cs)
        CrashWatch.Mark("start-up: GameHud");
        GameHud.Install();           // RA01: M15-0 - TEST: the game's own HUD shrunk whole (Dev row "Shrink game HUD (test)", see GameHud.cs)
        CrashWatch.Mark("start-up: VRIK");
        Visuals.VRIK.Install();      // RA01: M8-0 - VRIK starts here; for now it only maps V's arm and head bones by name and logs them, nothing is written (see Visuals/VRIK.cs)
        CrashWatch.Mark("start-up: XInputHook");
        XInputHook.Install();  // RA01: M6-0 - installs the synthetic Xbox-controller feed for slot 0, built from the tracked motion controllers (see XInputHook.cs / controls_mapping\ControllerMapping.cs)
        CrashWatch.Mark("start-up: WeaponRules");
        Actions.WeaponRules.Install();    // RA01: M13-2 - which weapon V holds -> whether the HUD crosshair shows (see Actions\WeaponRules.cs)
        CrashWatch.Mark("start-up: MeleeActions");
        Actions.MeleeActions.Install();   // RA01: M13-9 - guard poses block, fast swings strike (see Actions\MeleeActions.cs)
        CrashWatch.Mark("start-up: FireArmAiming");
        Actions.FireArmAiming.Install();  // RA01: M13-47 - after WeaponRules: where the game's aim line runs (the crosshair you see, the game's own; the barrel next) (see Actions\FireArmAiming.cs)
        CrashWatch.Mark("start-up: PhysicalCrouch");
        Actions.PhysicalCrouch.Install(); // RA01: M11-2 - crouch for real to crouch V; registered with ControllerMapping, which stays neutral (see Actions\PhysicalCrouch.cs)
        Actions.PhysicalSwim.Install();
        Actions.PhysicalThrow.Install();  // RA01: M22-4 - swing and let go to throw a grenade where your arm sends it (see Actions\PhysicalThrow.cs)   // RA01: M22-1 - in the water, arm strokes push V forward and side pushes turn her (see Actions\PhysicalSwim.cs)
        Actions.PhysicalLadder.Install(); // RA01: M24-3 - grip and pull to climb a ladder (see Actions\PhysicalLadder.cs)
        Actions.GestureMode.Install();
        Actions.PowerAttack.Install();    // RA01: M27-2 - wind-up / gorilla pose holds RT, the swing lets go (see Actions\PowerAttack.cs)    // RA01: M27-1 - hold B 5 s: grips and triggers pose V's fingers (see Actions\GestureMode.cs)
        GameCall.Install();          // RA01: M27-6c-2 - the Dev tab refill test's step on the XInput thread (idle unless its row is clicked; see GameCall.cs)
        Actions.PhysicalReload.Install(); // RA01: M27-5 - pull the old mag, grab a new one at the off-hand hip, insert it (see Actions\PhysicalReload.cs)
        Visuals.LaserSight.Install();     // RA01: M24-4 - the laser sight's place on your gun hand; Core\HandStrip.cs draws it (see Visuals\LaserSight.cs)
        HookRegistry.WriteReport();       // RA01: M43 - the Rehooker's report (Logs\RA01_Rehooker.txt) and, if a feature is off on this game build, a banner
        CrashWatch.Mark("start-up finished");
        return 0;
    }
}
