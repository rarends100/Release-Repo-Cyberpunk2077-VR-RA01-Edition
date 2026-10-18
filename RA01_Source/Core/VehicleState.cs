using System.Diagnostics;
using System.Numerics;

namespace RA01.Core;

// RA01: M19-1 - IS V IN A VEHICLE, IS SHE SEATED, AND WHAT KIND IS IT - for physical steering (Actions\PhysicalSteering.cs) and for pausing physical crouch.
// Read-only, from V's own script fields (GameAnchor.TryVehicle: m_mountedVehicle and m_vehicleState, found by name - M19-0's test) at most every RefreshMs,
// whichever thread asks. The kind is the vehicle object's class (RED4ext reference classes): vehicleCarBaseObject = a car (a wheel), vehicleBikeBaseObject =
// a motorcycle (handlebars), vehicleAVBaseObject = a flying car, vehicleTankBaseObject = a tank - looked up once per vehicle, not every read.
// Also THE GAME'S HANDS MAILBOX: VRIK (Visuals) drops in where the game's own driving animation put V's two wrists this frame, on her body, BEFORE anything
// of ours moves them - that is where the real steering wheel's rim (or the bike's grips) is. Actions reads it (Visuals never talks to Actions directly).
// M19-2 - it also carries her HEAD bone (every frame, on foot too): Actions anchors your head on it while seated, because in a vehicle the camera is
// measured from something that isn't her feet (M19-1's log: 0.72 m in a car, 0.32 m on a bike, 1.60 m on foot).
// Body axes: X right, Y forward, Z up, from her feet - as CameraBodyProbe.OnBody.
// Cost - CPU: extremely low (four or five 8-byte safe reads at most 10 times a second; a short lock and a few copies per mailbox use); GPU: none.
internal static class VehicleState
{
    public enum Kind { None, Car, Bike, Flying, Tank, Other }
    const long RefreshMs = 100;
    const int Driving = 1, Transition = 4, DriverCombat = 6;                                      // RA01: m_vehicleState (gamePSMVehicle): 0 on foot, 1 driving, 2 combat, 3 passenger,
                                                                                // RA01: 4 getting in / out, 5 turret, 6 driver combat (a gun drawn while driving), 7 scene
    static readonly string[] StateNames = { "on foot", "driving", "in combat", "a passenger", "getting in / out", "on a turret", "driving with a weapon drawn", "in a scene" };
    static readonly ulong[] KindNames = { GameAnchor.NameCode("vehicleCarBaseObject"), GameAnchor.NameCode("vehicleBikeBaseObject"),
                                          GameAnchor.NameCode("vehicleAVBaseObject"), GameAnchor.NameCode("vehicleTankBaseObject"), GameAnchor.NameCode("vehicleBaseObject") };
    static readonly Kind[] KindOfName = { Kind.Car, Kind.Bike, Kind.Flying, Kind.Tank, Kind.Other };
    static readonly object gate = new();
    static long nextRead;
    static nint vehicle;
    static volatile bool mounted, seated, driving, driverCombat;
    static int lastState = -1;
    static ulong model;                                                        // RA01: M33-3 - the current vehicle's model key (its sound name), 0 = none / unreadable
    const int AudioName231 = 0x1C8;                                            // RA01: M33-3 - gameObject.audioResourceName, a CName (RED4ext reference: game/Object.hpp). M44 - now found by
                                                                               // RA01: its name (FieldNames); this is only 2.31's place, used if the name isn't in the catalogue
    static HashSet<ulong>? knownWheels;
    const string KnownWheelsFile = "RA01_KnownWheels.txt";

    // RA01: M33-3 - THE CAR'S MODEL, for "STEERING WHEEL LEARNED" once per model (Rob 12:13, option B): every game object carries its model's sound name
    // (audioResourceName, e.g. v_car_archer_hella) - one 8-byte safe read, no game call. Variants of a car (police, gang, paint) share it, and they share a
    // cabin and wheel too, so "a model seen before" = "a wheel seen before". 0 if it can't be read. Cost - CPU: extremely low (one safe read per vehicle); GPU: none.
    static unsafe ulong ReadModel(nint v)
    {
        ulong name = 0; nuint got = 0;
        int at = FieldNames.At(v, HookRegistry.VehicleModel, "gameObject", "audioResourceName", null, AudioName231);   // RA01: M44 - -1 on a new build without it: 0 = "announce every time"
        return at >= 0 && Win32.ReadProcessMemory(-1, v + at, &name, 8, &got) && got == 8 ? name : 0;
    }

    // RA01: M33-3 - for PhysicalSteering when it finds a wheel: true = say "learned" - this model's wheel isn't in Settings\RA01_KnownWheels.txt yet (it is added,
    // written in the background), or - Rob's fallback - the model couldn't be read, so it is announced every time (the old way). False = known: stay quiet.
    // Cost - CPU: extremely low (a set look-up; the small file is read once per session and appended once per new model); GPU: none.
    public static bool WheelIsNew()
    {
        ulong m = model;
        if (m == 0) return true;
        lock (gate)
        {
            string file = Path.Combine(ModFolders.Settings, KnownWheelsFile);
            if (knownWheels == null)
            {
                knownWheels = new HashSet<ulong>();
                try { if (File.Exists(file)) foreach (string line in File.ReadAllLines(file)) if (ulong.TryParse(line.Trim(), System.Globalization.NumberStyles.HexNumber, null, out ulong k)) knownWheels.Add(k); }
                catch (Exception e) { Log.Write($"RA01 vehicle: {KnownWheelsFile} couldn't be read ({e.Message}) - wheels are announced as new"); }
            }
            if (!knownWheels.Add(m)) return false;
            Task.Run(() => { try { File.AppendAllText(file, $"{m:X16}\n"); } catch (Exception e) { Log.Write($"RA01 vehicle: {KnownWheelsFile} couldn't be written ({e.Message})"); } });
            return true;
        }
    }
    static volatile int kind;

    // RA01: true from the moment V starts getting into a vehicle until she is out again. Cost - CPU: extremely low (see Refresh); GPU: none.
    public static bool Mounted { get { Refresh(); return mounted; } }

    // RA01: true while V sits in a vehicle (not getting in or out). Cost - CPU: extremely low (see Refresh); GPU: none.
    public static bool IsSeated { get { Refresh(); return seated; } }

    // RA01: M20-2 - true only while V drives with nothing in her hands (m_vehicleState 1) - physical steering's time. IsSeated also covers driving with a gun
    // RA01: drawn, passenger seats, turrets and scenes (M20-1's test: drawing a gun is state 6 - read as "getting out", the car view height let go and the
    // RA01: view rose through the roof). Cost - CPU: extremely low (see Refresh); GPU: none.
    public static bool IsDriving { get { Refresh(); return driving; } }

    // RA01: M20-3 - true while V drives with a weapon drawn (m_vehicleState 6, "driver combat"). Cost - CPU: extremely low (see Refresh); GPU: none.
    public static bool IsDriverCombat { get { Refresh(); return driverCombat; } }

    // RA01: the kind of vehicle V is in (None on foot). Cost - CPU: extremely low (see Refresh); GPU: none.
    public static Kind Current { get { Refresh(); return (Kind)kind; } }

    // RA01: reads V's two fields if RefreshMs has passed; logs getting in (with the kind), sitting down and getting out.
    // Cost - CPU: extremely low (a clock read; every RefreshMs four or five safe reads, and on a new vehicle up to 13 pairs more); GPU: none.
    static void Refresh()
    {
        long now = Environment.TickCount64;
        if (now < Volatile.Read(ref nextRead) || !Monitor.TryEnter(gate)) return;
        try
        {
            nextRead = now + RefreshMs;
            if (!GameAnchor.TryVehicle(out nint v, out int state)) v = 0;
            if (v != vehicle)
            {
                vehicle = v;
                int i = v == 0 ? -1 : GameAnchor.ClassIs(v, KindNames);
                kind = (int)(v == 0 ? Kind.None : i < 0 ? Kind.Other : KindOfName[i]);
                model = v == 0 ? 0 : ReadModel(v);                                  // RA01: M33-3
                Log.Write(v == 0 ? "RA01 vehicle: V is on foot" : $"RA01 vehicle: V is getting into a {(Kind)kind} (model sound name 0x{model:X16}{(model == 0 ? " - unreadable, the wheel is announced every time" : "")})");
            }
            bool nowSeated = v != 0 && state != 0 && state != Transition;          // RA01: M20-2 - any seat state, not only driving
            if (v != 0 && state != lastState) Log.Write($"RA01 vehicle: V is {(state >= 0 && state < StateNames.Length ? StateNames[state] : $"in vehicle state {state}")} ({(Kind)kind})");
            lastState = v != 0 ? state : -1;
            mounted = v != 0;
            seated = nowSeated;
            driving = v != 0 && state == Driving;
            driverCombat = v != 0 && state == DriverCombat;
        }
        finally { Monitor.Exit(gate); }
    }

    // RA01: VRIK, once per frame: the game's own head and wrist spots on V's body, before ours move them. Cost - CPU: extremely low (a short lock and copies); GPU: none.
    public static void PublishGamePose(Vector3 head, Vector3 left, Vector3 right)
    {
        lock (handsGate) { hx = head.X; hy = head.Y; hz = head.Z; lx = left.X; ly = left.Y; lz = left.Z; rx = right.X; ry = right.Y; rz = right.Z; gameHandsAt = Stopwatch.GetTimestamp(); }
    }

    // RA01: M19-2 - the latest game head bone spot; false if none came in the last 100 ms. Cost - CPU: extremely low (a short lock and copies); GPU: none.
    public static bool TryReadGameHead(out Vector3 head)
    {
        lock (handsGate)
        {
            head = new Vector3(hx, hy, hz);
            return gameHandsAt != 0 && Stopwatch.GetTimestamp() - gameHandsAt < Stopwatch.Frequency / 10;
        }
    }

    // RA01: the latest game wrist spots; false if none came in the last 100 ms. Cost - CPU: extremely low (a short lock and copies); GPU: none.
    public static bool TryReadGameHands(out Vector3 left, out Vector3 right)
    {
        lock (handsGate)
        {
            left = new Vector3(lx, ly, lz); right = new Vector3(rx, ry, rz);
            return gameHandsAt != 0 && Stopwatch.GetTimestamp() - gameHandsAt < Stopwatch.Frequency / 10;
        }
    }
    static readonly object handsGate = new();
    static float hx, hy, hz, lx, ly, lz, rx, ry, rz;   // RA01: plain numbers, not stored Vector3s (the ILC 8.0.31 quirk - see RA01_Known_Quirks)
    static long gameHandsAt;
}
