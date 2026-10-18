using System.Numerics;
using RA01.Core;

namespace RA01.Actions;

// RA01: M13-37 to M13-61 - AIMING: where V's shots come from and which way they go. Full hunt: project doc claude/RA01_M13-46_47_Aim_Line_2026-10-04.md.
//     Two doors in Core, both re-laid as this class decides (it only DECIDES; Core carries it out):
//   - Core\Probes\ShotProbe.cs - the player's AIM SOURCE: every shot, its damage, every pellet, special rounds, tracers and projectiles take their start and
//     direction from it (M13-59: steering it put hits AND damage on the barrel);
//   - Core\Probes\AimProbe.cs - the AIM LINE (the camera trace the bullet holes and the "what am I looking at" check use), kept in step with it while firing.
// THE RULE (Decide, before each controller read, about 60 times a second):
//  - a gun with Physical aiming on (not Smart, so its crosshair is hidden): BARREL while firing - fire trigger past FirePress (or the left mouse button),
//    and FireTailMs after;
//  - everything else, and that gun between shots: GAME - the game's own aim, the middle of its picture. M13-61: the crosshair is now always drawn there
//    (Core\Video\VRRuntimes\XrSession.cs cuts it out of the HUD panel), so what you see is what you hit - Smart lock-on, the launcher, Mantis Blades, Monowire, fists,
//    Gorilla Arms, pick-ups and screens need no steering. (M13-60's "through the crosshair" steering aimed through the lowered panel - removed.)
// Like a gunsmith's bench: the barrel only matters when the hammer falls - the rest of the time you are just looking around.
// (Later idea, the user's: a per-fist / per-blade hit target - steer the aim source toward the striking hand at the moment of a strike.)
// BARREL AXIS LOG (kept, the user's call - for re-checking after updates; a later "quiet log" setting will silence it): while a barrel-aimed gun is
// drawn, once a second, the angle between each muzzle axis and your view. Aiming down the sights, the barrel's axis reads near 0 (M13-47: +Y).
// DEV BEND TEST (Dev tab "Bend the shot (test)", off by default, not saved): for finding the aim line again after a game update. Turns the multi-hit
// runner's rays 20 degrees right (all three callers, or only one; keys 7, 8, 9 pick caller 1, 2, 3 while it is on) and logs the tally once a second.
// Cost - CPU: extremely low (Decide: a few compares per controller read; the test thread: a few compares 20 times a second and at most two log lines
// a second); GPU: none.
internal static unsafe class FireArmAiming
{
    const int TickMs = 50, MaxLines = 3600;              // RA01: log lines this session (about half an hour at two a second); aiming carries on after
    const float BendDegrees = 20f;                       // RA01: how far the bend test turns a ray, to the right
    const float FirePress = 0.15f;                       // RA01: M13-49 - a light touch already counts, so the barrel takes over before the game's own shot
    const long FireTailMs = 250;                         // RA01: M13-49 - the barrel stays a quarter second after release (the last shot of a burst, a charged release)
    static int started;
    static int lastPlan = -1;                            // RA01: 0 game's own aim, 2 barrel while firing: logged only when this changes
    static long lastFireMs;                              // RA01: when the fire trigger was last seen pressed (Environment.TickCount64)
    static volatile bool barrelGun;                      // RA01: a gun with Physical aiming on (not Smart) is drawn - Barrel while firing, and the barrel axis log runs

    // RA01: registers Decide (after WeaponRules, so the crosshair decision is fresh) and starts the test thread. Call once at start-up, after WeaponRules.Install().
    //  * Cost - CPU: extremely low (stores one pointer, starts one background thread); GPU: none.
    public static void Install()
    {
        if (Interlocked.Exchange(ref started, 1) == 1) return;
        ControllerMapping.AddBeforeBuild(&Decide);
        new Thread(Loop) { IsBackground = true, Name = "RA01 firearm aiming" }.Start();
    }

    // RA01: THE RULE (see the class comment): picks the mode, tells Core, logs a change of plan (not every trigger pull).
    // Cost - CPU: extremely low (a short locked read, a clock read and a few compares, about 60 times a second); GPU: none.
    static void Decide()
    {
        WeaponProbe.Kind kind = WeaponProbe.Held;
        bool crosshair = !HudLayer.HideCrosshair;
        long now = Environment.TickCount64;
        if (ControllerMapping.FireTrigger > FirePress || Keys.Held(Keys.MouseLeft)) lastFireMs = now;
        bool firing = now - lastFireMs <= FireTailMs;
        barrelGun = kind == WeaponProbe.Kind.Gun && !crosshair;
        int plan = barrelGun ? 2 : 0;
        AimProbe.SetAimMode(plan == 2 && firing ? AimProbe.AimMode.Barrel : AimProbe.AimMode.Game);
        if (plan == lastPlan) return;
        lastPlan = plan;
        Log.Write(plan == 2 ? "RA01 aiming: shots follow the barrel while firing" : "RA01 aiming: the game's own aim (the crosshair is drawn on it)");
    }

    // RA01: the test thread. Each tick: the bend test's setting (tell Core, log a change), keys 7/8/9 while it is on, trigger pulls. Each second: the world
    // offset refreshed (AimProbe's camera check needs it - so this keeps running after the log limit), then the log lines that apply. Any error is caught
    // and logged once; the thread then stops (aiming itself carries on, with the last world offset).
    // Cost - CPU: extremely low (asleep between ticks); GPU: none.
    static void Loop()
    {
        var rvas = new long[AimProbe.Slots];
        var totals = new long[AimProbe.Slots];
        var fromCamera = new long[AimProbe.Slots];
        var cameraBefore = new long[AimProbe.Slots];
        var multiHit = new long[4];
        var multiHitBefore = new long[4];
        int test = 0, pulls = 0, ticks = 0, lines = 0;
        bool wasPulled = false;
        try
        {
            while (true)
            {
                Thread.Sleep(TickMs);
                if (Tuning.BendTest > 0)
                    for (int k = 0; k < 3; k++)                                        // RA01: 7, 8, 9 (top row 0x37..0x39, numpad 0x67..0x69) - 1 to 6 are the game's weapon slots
                        if (Keys.Pressed(0x37 + k) || Keys.Pressed(0x67 + k)) Tuning.BendTest = Tuning.BendTest == k + 2 ? 1 : k + 2;
                if (Tuning.BendTest != test)
                {
                    test = Tuning.BendTest;
                    AimProbe.BendCameraRays(test > 0, BendDegrees);
                    AimProbe.BendOnlyMultiHitCaller(test >= 2 ? test - 2 : -1);
                    Log.Write(test == 0 ? "RA01 bend test OFF" : $"RA01 bend test ON - the multi-hit runner's camera rays from {(test == 1 ? "all three callers" : $"ONLY caller {test - 1} (0x{AimProbe.MultiHitCallers[test - 2]:X})")} are turned {BendDegrees:F0} degrees right");
                }
                bool pulled = ControllerMapping.FireTrigger > 0.5f || Keys.Held(Keys.MouseLeft);
                if (pulled && !wasPulled) pulls++;
                wasPulled = pulled;
                if (++ticks < 1000 / TickMs) continue;

                ticks = 0;
                var shift = AimProbe.RefreshShift();
                AimProbe.ReadMultiHit(multiHit);
                if (lines < MaxLines && barrelGun && BarrelAxisLine(out string axes)) { Log.Write(axes); lines++; }
                if (lines < MaxLines && test > 0) { Log.Write(TallyLine(rvas, totals, fromCamera, cameraBefore, multiHit, multiHitBefore, pulls, shift)); lines++; }
                if (lines == MaxLines) { Log.Write("RA01 aiming: log line limit reached - aiming log lines stop (aiming itself carries on)"); lines++; }
                Array.Copy(multiHit, multiHitBefore, 4);
                pulls = 0;
            }
        }
        catch (Exception e) { Log.Write($"RA01 aiming test thread stopped: {e.Message} (aiming itself carries on)"); }
    }

    // RA01: BARREL AXIS LOG line: the angle between each of the muzzle's three axes and your view, both measured on V's body (Core mailboxes filled by VRIK
    // and the camera), and how far the muzzle is from your head. Angle = how far apart two arrows point: 0 = same way, 90 = across, 180 = opposite.
    // False if either reading is stale.
    // Cost - CPU: extremely low (four vector turns, three dot products, once a second); GPU: none.
    static bool BarrelAxisLine(out string line)
    {
        line = "";
        if (!CameraBodyProbe.TryRead(out CameraBodyProbe.OnBody view) || !WeaponProbe.TryReadMuzzleOnBody(out Vector3 muzzle, out Quaternion turn)) return false;
        Vector3 look = Vector3.Transform(Vector3.UnitY, view.Turn);                // RA01: the view looks along its own +Y
        float Angle(Vector3 axis) => MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Transform(axis, turn), look), -1f, 1f)) * (180f / MathF.PI);
        line = $"RA01 barrel axis: muzzle axis vs your view - x {Angle(Vector3.UnitX):F0}, y {Angle(Vector3.UnitY):F0}, z {Angle(Vector3.UnitZ):F0} degrees " +
               $"(aiming down the sights, +Y reads near 0); muzzle {Vector3.Distance(muzzle, view.Position) * 100:F0} cm from your head";
        return true;
    }

    // RA01: DEV BEND TEST line: trigger pulls last second, rays per multi-hit caller, and each caller that sent rays from the camera.
    // Cost - CPU: extremely low (a pass over 64 slots and one string, once a second); GPU: none.
    static string TallyLine(long[] rvas, long[] totals, long[] fromCamera, long[] cameraBefore, long[] multiHit, long[] multiHitBefore, int pulls, (float x, float y, float z) shift)
    {
        AimProbe.ReadTally(rvas, totals, fromCamera, out _, out _);
        string cameraList = "";
        for (int i = 0; i < AimProbe.Slots; i++)
        {
            if (rvas[i] == 0) continue;
            long thisSecond = fromCamera[i] - cameraBefore[i];
            cameraBefore[i] = fromCamera[i];
            if (thisSecond > 0) cameraList += $", {Where(rvas[i])} x{thisSecond}";
        }
        return $"RA01 bend test: last second - trigger pulls {pulls}; multi-hit callers 1 (0x{AimProbe.MultiHitCallers[0]:X}) x{multiHit[0] - multiHitBefore[0]}, " +
               $"2 (0x{AimProbe.MultiHitCallers[1]:X}) x{multiHit[1] - multiHitBefore[1]}, 3 (0x{AimProbe.MultiHitCallers[2]:X}) x{multiHit[2] - multiHitBefore[2]}, other x{multiHit[3] - multiHitBefore[3]}; " +
               $"PhysX queries from the camera: {(cameraList.Length > 0 ? cameraList[2..] : "none")}; world offset ({shift.x:F1}, {shift.y:F1}, {shift.z:F1})";
    }

    // RA01: a caller's name for the log: "ray" or "sweep", then its exe RVA, or "PhysX+offset" for one inside PhysX3_x64.dll (its own batch queries).
    // Cost - CPU: extremely low (one compare and one string); GPU: none.
    static string Where(long rva)
    {
        string kind = rva >= AimProbe.SweepMark ? "sweep " : "ray ";
        if (rva >= AimProbe.SweepMark) rva -= AimProbe.SweepMark;
        return kind + (rva >= AimProbe.PhysXMark ? $"PhysX+0x{rva - AimProbe.PhysXMark:X}" : $"0x{rva:X}");
    }
}
