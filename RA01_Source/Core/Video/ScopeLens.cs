using System.Numerics;

namespace RA01.Core;

// RA01: M16-A - THE SCOPE MAGNIFIER: a jeweller's loupe held over the picture the game has already painted. While you aim through a long scope (the game asks for
// at least Tuning.ScopeMinZoom), a small square of each eye's picture - around the spot the scope's centre lands on - is cut out, and XrSession shows it
// on a small round panel ON THE SCOPE'S EYEPIECE. The square is the piece of picture the panel covers, shrunk by the scope's zoom, so it is stretched by
// exactly that zoom. The eyes stay unzoomed; the game draws nothing extra.
// M16-A2 - where the eyepiece is: GunCalibration knows, per gun and scope, where the sight line runs and where the game puts your eye behind it when it
// aims for you; the eyepiece is LensAhead in front of that eye point, riding with the muzzle. Both eyes' panels stand at that one spot in the room, so the
// lens looks fixed on the scope (M16-A floated it on each eye's line to the target - it fused 50 m away and slid off the scope as the gun moved).
// Builder's view: the eyepiece is a hole drilled at a measured spot on the gun's rail; behind the glass sits a blown-up photo of what's down the sight line.
// Spots seen from the camera become "how far right and up per metre ahead" (two slopes); on the picture a slope becomes metres by the picture's own size.
// Cost - CPU: extremely low (a few vector turns and divisions per eye picture while scoped, a log line a second); GPU: none here (the copy is FrameCopy.ShowLens).
internal static class ScopeLens
{
    public const int MaxSide = 256;  // RA01: the biggest square cut out of the picture (pixels); also the lens images' size
    const int MinSide = 16;
    const float MinDepth = 0.03f;    // RA01: an eyepiece closer to the eye than this (or behind it) isn't shown
    static bool wasOn, wasMissing;
    static long nextLog;
    static volatile string lastKey = "";

    // RA01: one cut-out: its top left corner and side in the picture's pixels; the spot it centres on (the target) and where the eyepiece appears, both in
    // metres on the picture (right, up from its middle); how far the eyepiece is from the eye, and the lens width (metres).
    public struct Cutout { public int X, Y, Side; public float SpotRight, SpotUp, LensRight, LensUp, LensDepth, LensMetres; }

    // RA01: M16-A2 - the gun and scope the lens was last shown for (the Weapons tab sliders change that one's lens). Empty until a scope has been aimed through.
    public static string LastKey => lastKey;

    // RA01: is the magnifier on now, and how strong (the game's asked zoom)? Logs every switch on and off.
    // Cost - CPU: extremely low (a few reads and compares, per eye per headset frame); GPU: none.
    public static bool Wanted(out float zoom)
    {
        zoom = CameraHook.ScopeZoomAsked;
        bool on = Tuning.ScopeMagnifier && zoom >= Tuning.ScopeMinZoom && !SettingsMenu.Open && !MenuSense.ShowsMenu;
        if (on != wasOn)
        {
            wasOn = on;
            Log.Write(on ? $"RA01 scope lens: on - the game asks x{zoom:F2} (long scope, x{Tuning.ScopeMinZoom:F1} or more); {(Tuning.ScopeLensRound ? "round" : "square")} lens" : "RA01 scope lens: off");
        }
        return on;
    }

    // RA01: the square to cut out of one eye's picture and where the eyepiece appears on it. The picture is widthM x heightM metres, 'distance' in front of the
    // eye that built it (that eye is 'eyeInHead' from the head's centre, OpenXR axes), drawn with a field of view of 'fovDeg' (top to bottom) over
    // pixelsWide x pixelsHigh. False if the gun isn't calibrated yet (its eye is measured in about 2 s of aiming) or a spot is off the picture.
    // Cost - CPU: extremely low (a few vector turns, one tangent and a few sums, per new eye picture while scoped); GPU: none.
    public static bool Cut(float zoom, float widthM, float heightM, float fovDeg, int pixelsWide, int pixelsHigh, float distance, Xr.Vector3f eyeInHead, out Cutout cut)
    {
        cut = default;
        if (!(widthM > 0 && heightM > 0 && fovDeg > 1) || !AimProbe.TryMuzzleInWorld(out Vector3 muzzle, out Quaternion turn, out CameraBodyProbe.OnBody view)) return false;
        string key = GunCalibration.CurrentKey();
        bool known = GunCalibration.TryGetEntry(key, out GunCalibration.Entry gun) && !float.IsNaN(gun.EyeAhead);
        if (!known)
        {
            if (!wasMissing) { wasMissing = true; Log.Write($"RA01 scope lens: waiting for {key} to be measured (keep aiming about 2 s)"); }
            return false;
        }
        wasMissing = false;
        lastKey = key;

        Vector3 barrel = Vector3.Transform(Vector3.UnitY, turn), onSight = muzzle + Vector3.Transform(gun.Sight, turn);
        Vector3 eyeCam = new(eyeInHead.X, -eyeInHead.Z, eyeInHead.Y);                    // RA01: OpenXR (x right, y up, -z ahead) to the camera's axes (x right, y ahead, z up)
        Quaternion back = Quaternion.Conjugate(view.WorldTurn);
        Vector3 target = Vector3.Transform(onSight + barrel * GunCalibration.ZeroMetres - view.WorldHead, back) - eyeCam;
        Vector3 piece = Vector3.Transform(onSight + barrel * (gun.LensAhead - gun.EyeAhead) - view.WorldHead, back) - eyeCam;
        if (!(target.Y > 1f) || !(piece.Y > MinDepth)) return false;

        float perSlope = heightM / (2f * MathF.Tan(fovDeg * MathF.PI / 360f));               // RA01: metres on the picture for a slope of 1
        float spotRight = target.X / target.Y * perSlope, spotUp = target.Z / target.Y * perSlope;
        float depth = piece.Length();
        float patchM = gun.LensMetres * distance / depth / zoom;                              // RA01: what the lens covers on the picture, shrunk by the zoom
        int side = Math.Clamp((int)(patchM / widthM * pixelsWide) & ~1, MinSide, MaxSide);
        int x = (int)(pixelsWide * (0.5f + spotRight / widthM)) - side / 2, y = (int)(pixelsHigh * (0.5f - spotUp / heightM)) - side / 2;
        if (x < 0 || y < 0 || x + side > pixelsWide || y + side > pixelsHigh) return false;
        cut = new Cutout
        {
            X = x, Y = y, Side = side, SpotRight = spotRight, SpotUp = spotUp,
            LensRight = piece.X / piece.Y * perSlope, LensUp = piece.Z / piece.Y * perSlope, LensDepth = depth, LensMetres = gun.LensMetres
        };
        long now = Environment.TickCount64;
        if (now >= nextLog)
        {
            nextLog = now + 1000;
            Log.Write($"RA01 scope lens: {key} x{zoom:F2}, cut {side}x{side} px at ({x}, {y}); eyepiece {depth * 100:F1} cm from the eye, {cut.LensRight * 100:F1} cm right {cut.LensUp * 100:F1} cm up on the picture; " +
                      $"lens {gun.LensMetres * 100:F1} cm wide, {gun.LensAhead * 100:F1} cm in front of the game's eye point ({gun.EyeAhead * 100:F0} cm behind the muzzle)");
        }
        return true;
    }

    // RA01: where the lens panel stands: on the line from the eye ('distance' in front of the picture's middle) through where the eyepiece appears on the
    // picture, at the eyepiece's own distance - so it stands on the scope itself. It faces the same way as the picture; returned in the picture's space.
    // Cost - CPU: extremely low (a normalise and one vector turn, per lens per headset frame); GPU: none.
    public static Xr.Posef Placed(in Xr.Posef picture, float distance, in Cutout cut)
    {
        Vector3 eye = new(0, 0, distance), toPiece = Vector3.Normalize(new Vector3(cut.LensRight, cut.LensUp, 0) - eye); // RA01: the picture's axes: x right, y up, the eye on +z
        Quaternion turn = new(picture.Orientation.X, picture.Orientation.Y, picture.Orientation.Z, picture.Orientation.W);
        Vector3 at = new Vector3(picture.Position.X, picture.Position.Y, picture.Position.Z) + Vector3.Transform(eye + toPiece * cut.LensDepth, turn);
        return new Xr.Posef { Orientation = picture.Orientation, Position = { X = at.X, Y = at.Y, Z = at.Z } };
    }

    // RA01: M16-A2 - the Weapons tab sliders: the last scoped gun's lens width and distance (the defaults until a scope has been aimed through), and changing them.
    // RA01: Cost - CPU: extremely low (a dictionary lookup per menu draw; a small file write per change); GPU: none.
    public static float LensWidthCm => GunCalibration.TryGetEntry(lastKey, out GunCalibration.Entry e) ? e.LensMetres * 100f : GunCalibration.DefaultLensMetres * 100f;
    public static float LensAheadCm => GunCalibration.TryGetEntry(lastKey, out GunCalibration.Entry e) ? e.LensAhead * 100f : GunCalibration.DefaultLensAhead * 100f;
    public static void SetLensWidthCm(float cm) => GunCalibration.SetLens(lastKey, MathF.Round(cm * 2f) / 200f, LensAheadCm / 100f);
    public static void SetLensAheadCm(float cm) => GunCalibration.SetLens(lastKey, LensWidthCm / 100f, MathF.Round(cm * 2f) / 200f);
    public static string LensFor() => lastKey.Length > 0 ? lastKey : "aim through a scope first";
}
