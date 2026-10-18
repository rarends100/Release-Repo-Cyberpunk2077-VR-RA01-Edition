using System.Numerics;

namespace RA01.Core;

// RA01: M27-5c - THE MAG POUCH GLOW (Weapons tab "Mag pouch glow" and "Mag pouch colour"; Rob 2026-10-09): a glowing, half see-through ball at your off-hand hip,
// shown only while you need a new magazine (the old one is out), so you can find the pouch without looking for it. Drawn by the mod in the headset, never by
// the game: something in Actions says "show it, this far below your eyes, this far out to that side, this far ahead" (Show); every headset frame XrSession
// hangs it there from your LIVE head - turning with your heading only, not your nods - so it never lags your head. Core knows nothing about magazines:
// Actions\PhysicalReload.cs is what makes it a pouch (and uses Centre for its grab zone, so the ball and the zone are one spot).
// ONE FLAT ROUND PANEL that always faces your head (like the laser's dot), painted as a ball: an even body with a soft edge, a brighter, whiter middle,
// and never more than 50% opaque (Rob). It slowly breathes (BreatheShare bigger and smaller, once every BreatheMs) so it catches the eye without flashing.
// The headset draws it over the game's picture, so it shows through V's arm.
// Builder picture: a reflector on a fence post - you don't need to see the post, the glow tells you where to reach.
// Cost - CPU: extremely low (per headset frame: a few vector sums and one quaternion; a 16 KB paint only on a colour change); GPU: extremely low (one small panel).
internal static class PouchGlow
{
    public const int ImageSize = 64;
    const float DiameterM = 0.09f;           // RA01: the panel, about 9 cm at 100% (Weapons tab "Mag pouch size"); the ball's soft edge leaves about 7 cm that reads as solid
    const float WellDiameterM = 0.022f;      // RA01: M27-6g - the magazine well's ball: about 2 cm - very small but visible (Rob 23:11); not sized by the slider
    const float BreatheShare = 0.10f;        // RA01: grows and shrinks this share of its size...
    const long BreatheMs = 1500;             // RA01: ...once every this long
    const float MostOpaque = 0.5f;           // RA01: Rob: 50% see-through at most
    const long StaleMs = 200;                // RA01: no Show for this long (the pouch isn't needed, a menu) = nothing drawn

    // RA01: the colours on the Weapons tab, in order (Tuning.PouchColour is an index into this): the laser's six, then Yellow (the default) and Orange.
    public static readonly (string Name, byte R, byte G, byte B)[] Colours =
    {
        ("Red", 255, 32, 24), ("Green", 48, 255, 64), ("Blue", 48, 112, 255), ("Cyan", 0, 232, 255), ("Purple", 200, 64, 255), ("White", 255, 255, 255),
        ("Yellow", 255, 220, 40), ("Orange", 255, 136, 24)
    };

    static readonly object gate = new();
    static readonly float[] spot = new float[3]; // RA01: below your eyes, out to the off-hand side, ahead - metres (an array, not a Vector3 field: the ILC quirk)
    static int side;                             // RA01: the off hand: 0 left, 1 right
    static long shownAt;                         // RA01: Environment.TickCount64 of the last Show (0 = dropped)

    // RA01: Actions, as often as it likes: show the ball 'below' metres under your eyes, 'outward' out to the 'offHand' side (0 left, 1 right), 'ahead' in front.
    // Cost - CPU: extremely low (an uncontended lock and five stores); GPU: none.
    public static void Show(int offHand, float below, float outward, float ahead)
    {
        lock (gate) { side = offHand; spot[0] = below; spot[1] = outward; spot[2] = ahead; shownAt = Environment.TickCount64; }
    }

    // RA01: Actions: nothing to show now. Cost - CPU: extremely low (one store); GPU: none.
    public static void Drop() { lock (gate) shownAt = 0; }

    // RA01: M27-6g - THE WELL BALL (Rob 23:11: show where the magazine or shells go): Actions, while a new magazine is carried, gives the spot in the room
    // (OpenXR floor space, the same space the controllers are read in) - it is worked out from the gun controller, so it moves with the gun. A tiny
    // copy of the pouch ball is drawn there. Cost - CPU: extremely low (a lock and four stores); GPU: none.
    public static void ShowWell(Vector3 room) { lock (gate) { well[0] = room.X; well[1] = room.Y; well[2] = room.Z; wellAt = Environment.TickCount64; } }
    public static void DropWell() { lock (gate) wellAt = 0; }
    static readonly float[] well = new float[3];  // RA01: M27-6g - the well spot in the room (an array, not a Vector3 field: the ILC quirk)
    static long wellAt;

    // RA01: M27-6g - XrSession: where the well ball stands this frame (facing your head) and its size, if shown and fresh. Cost - CPU: extremely low (as TryPlace); GPU: none.
    public static bool TryPlaceWell(in Xr.Posef head, out Xr.Posef pose, out float size)
    {
        pose = default; size = 0;
        Vector3 centre; long at;
        lock (gate) { centre = new Vector3(well[0], well[1], well[2]); at = wellAt; }
        long now = Environment.TickCount64;
        if (at == 0 || now - at > StaleMs || !Facing(head, centre, out pose)) return false;
        size = WellDiameterM * (1f + BreatheShare * MathF.Sin(now % BreatheMs / (float)BreatheMs * MathF.Tau));
        return true;
    }

    // RA01: the spot from a head pose (OpenXR: x right, y up, -z ahead): down from the eyes, then along your heading (level) and out to that side. Shared with
    // Actions\PhysicalReload.cs so the ball sits in the middle of its grab zone. Cost - CPU: extremely low (one turn, a cross product); GPU: none.
    public static Vector3 Centre(in Xr.Posef head, int offHand, float below, float outward, float ahead)
    {
        Quaternion turn = Quaternion.Normalize(new Quaternion(head.Orientation.X, head.Orientation.Y, head.Orientation.Z, head.Orientation.W));
        Vector3 forward = Vector3.Transform(-Vector3.UnitZ, turn);
        forward.Y = 0;
        forward = forward.LengthSquared() < 1e-4f ? -Vector3.UnitZ : Vector3.Normalize(forward);   // RA01: looking straight up or down: keep a sane heading
        Vector3 rightward = Vector3.Cross(forward, Vector3.UnitY);
        Vector3 eyes = new(head.Position.X, head.Position.Y, head.Position.Z);
        return eyes - Vector3.UnitY * below + rightward * (offHand == 0 ? -outward : outward) + forward * ahead;
    }

    // RA01: XrSession: where the ball stands this frame (facing your head) and its breathing size, if it is shown and fresh.
    // Cost - CPU: extremely low (a lock, a few vector sums, one quaternion from three axes); GPU: none.
    public static bool TryPlace(in Xr.Posef head, out Xr.Posef pose, out float size)
    {
        pose = default; size = 0;
        int hand; float below, outward, ahead; long at;
        lock (gate) { hand = side; below = spot[0]; outward = spot[1]; ahead = spot[2]; at = shownAt; }
        long now = Environment.TickCount64;
        if (at == 0 || now - at > StaleMs) return false;
        Vector3 centre = Centre(head, hand, below, outward, ahead);
        if (!Facing(head, centre, out pose)) return false;
        size = DiameterM * Math.Clamp(Tuning.PouchSize, Tuning.MinPouchSize, Tuning.MaxPouchSize) * (1f + BreatheShare * MathF.Sin(now % BreatheMs / (float)BreatheMs * MathF.Tau));
        return true;
    }

    // RA01: a panel pose at 'centre' whose face looks at your eyes (its +Z toward them). False if it can't be made (at your eyes, or a bad number).
    // Cost - CPU: extremely low (a few vector sums and one quaternion from three axes); GPU: none.
    static bool Facing(in Xr.Posef head, Vector3 centre, out Xr.Posef pose)
    {
        pose = default;
        Vector3 eyes = new(head.Position.X, head.Position.Y, head.Position.Z);
        Vector3 face = eyes - centre;
        if (!(face.LengthSquared() > 1e-4f)) return false;
        face = Vector3.Normalize(face);
        Vector3 up = MathF.Abs(face.Y) > 0.95f ? Vector3.UnitZ : Vector3.UnitY;
        Vector3 x = Vector3.Normalize(Vector3.Cross(up, face)), y = Vector3.Cross(face, x);          // RA01: the panel's +Z looks at your eyes
        Matrix4x4 m = new(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, face.X, face.Y, face.Z, 0, 0, 0, 0, 1);  // RA01: each row is where that axis goes
        Quaternion q = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
        pose = new Xr.Posef { Orientation = { X = q.X, Y = q.Y, Z = q.Z, W = q.W }, Position = { X = centre.X, Y = centre.Y, Z = centre.Z } };
        return float.IsFinite(centre.X + centre.Y + centre.Z + q.X + q.Y + q.Z + q.W);
    }

    // RA01: what the painted image depends on (the colour): a new number = paint it again. Cost - CPU: extremely low; GPU: none.
    public static int Look => Math.Clamp(Tuning.PouchColour, 0, Colours.Length - 1);

    // RA01: paints the 64 x 64 ball: an even body out to 80% of the radius fading to nothing at the edge, the middle brighter and whiter (a lit ball), at most
    // MostOpaque. Colour unpremultiplied, coverage in the alpha. 'bgra' = the headset image's byte order.
    // Cost - CPU: low (4096 pixels, one exponential each, only when the colour changes); GPU: none.
    public static unsafe void Paint(byte* pixels, bool bgra)
    {
        var c = Colours[Look];
        for (int y = 0; y < ImageSize; y++)
            for (int x = 0; x < ImageSize; x++)
            {
                float dx = (x + 0.5f) / (ImageSize / 2f) - 1f, dy = (y + 0.5f) / (ImageSize / 2f) - 1f;
                float r = MathF.Sqrt(dx * dx + dy * dy);
                float body = r >= 1f ? 0f : r <= 0.8f ? 1f : (1f - r) / 0.2f;   // RA01: solid, then a soft rim
                float white = 0.45f * MathF.Exp(-6f * r * r);                     // RA01: the lit middle leans toward white
                byte cr = (byte)(c.R + (255 - c.R) * white), cg = (byte)(c.G + (255 - c.G) * white), cb = (byte)(c.B + (255 - c.B) * white);
                byte a = (byte)Math.Clamp(body * MostOpaque * 255f, 0f, 255f);
                byte* p = pixels + (y * ImageSize + x) * 4;
                p[0] = bgra ? cb : cr; p[1] = cg; p[2] = bgra ? cr : cb; p[3] = a;
            }
    }
}
