using System.Numerics;

namespace RA01.Core;

// RA01: M24-4 - A GLOWING STRIP HELD IN A HAND (drawn by the mod in the headset, never by the game). Something in Visuals says "hold a strip in this hand, starting
// here and pointing there, in the hand's own frame" (Hold); every headset frame XrSession hangs it on that controller's live pose, so it moves with your real
// hand with no game lag at all. Core knows nothing about guns: Visuals\LaserSight.cs is what makes it a laser sight.
// TWO FLAT PANELS, like the HUD: the BEAM is a long thin panel from the start point along the direction, 'Laser depth' long, turned on its own long axis to
// face your head (a ribbon of tape that always shows you its flat side); the DOT is a small round panel at the far end, facing your head. Both come from one
// 128 x 64 image painted on the CPU (left half: the beam's soft-edged stripe, right half: the dot's glow) in the Weapons tab's colour and brightness,
// painted again only when those change.
// M24-5 - TWO WAYS TO POINT: Hold - along a direction fixed in the hand (a barrel), 'Laser depth' long; HoldToward - toward a point fixed to your HEAD
// (where a camera-aimed gun's crosshair sits, 'Laser depth' ahead along it), so the beam runs from the hand to that point and the dot sits on it.
// The headset draws these panels over the game's picture, so the beam can't be hidden by a wall - it always reaches its full length.
// Builder picture: a chalk line snapped from a nail in your hand - the nail is the muzzle, the line runs where the barrel points.
// Cost - CPU: extremely low (per headset frame: a few vector turns and one quaternion from three axes; a 32 KB paint only on a colour change);
// GPU: extremely low (two tiny panels the headset runtime blends in; a 32 KB upload on a colour change).
internal static class HandStrip
{
    public const int ImageWidth = 128, ImageHeight = 64, Half = 64;
    const long StaleMs = 200;
    const float StartGapM = 0.35f;            // RA01: M26-1 - the beam starts this far past the muzzle and fades in: the headset draws it OVER the game's picture, so the part
                                              // RA01: nearest the muzzle showed through the gun itself when aiming down the barrel (Rob 2026-10-08)                 // RA01: no Hold for this long (Visuals stopped, the gun put away) = nothing drawn

    // RA01: the colours on the Weapons tab, in order (Tuning.LaserColour is an index into this).
    public static readonly (string Name, byte R, byte G, byte B)[] Colours =
    {
        ("Red", 255, 32, 24), ("Green", 48, 255, 64), ("Blue", 48, 112, 255), ("Cyan", 0, 232, 255), ("Purple", 200, 64, 255), ("White", 255, 255, 255)
    };

    static readonly object gate = new();
    static readonly float[] held = new float[6]; // RA01: start (x, y, z) in the hand's own OpenXR frame, then the direction in the hand's frame - or (HoldToward) the end
                                                 // RA01: point in the head's frame (arrays, not Vector3 fields - the ILC quirk)
    static bool towardHead;                      // RA01: M24-5 - held[3..5] is a point from the head, not a direction in the hand
    static int heldHand;                         // RA01: 0 left, 1 right
    static long heldAt;                          // RA01: Environment.TickCount64 of the last Hold (0 = dropped)

    // RA01: Visuals, as often as it likes: hold the strip in 'hand' (0 left, 1 right), starting at 'start' and pointing along 'direction', both in that controller's
    // own frame (OpenXR axes). Cost - CPU: extremely low (an uncontended lock and seven stores); GPU: none.
    public static void Hold(int hand, Vector3 start, Vector3 direction) => Set(hand, start, direction, false);

    // RA01: M24-5 - Visuals: hold the strip in 'hand' from 'start' (the hand's own frame) to 'end', a point in the HEAD's own frame (OpenXR axes) - a camera-aimed
    // gun's aim point. Cost - CPU: extremely low (an uncontended lock and eight stores); GPU: none.
    public static void HoldToward(int hand, Vector3 start, Vector3 end) => Set(hand, start, end, true);

    // RA01: the shared store for Hold and HoldToward. Cost - CPU: extremely low (an uncontended lock and eight stores); GPU: none.
    static void Set(int hand, Vector3 start, Vector3 direction, bool toward)
    {
        lock (gate)
        {
            towardHead = toward;
            held[0] = start.X; held[1] = start.Y; held[2] = start.Z;
            held[3] = direction.X; held[4] = direction.Y; held[5] = direction.Z;
            heldHand = hand;
            heldAt = Environment.TickCount64;
        }
    }

    // RA01: Visuals: nothing to draw now. Cost - CPU: extremely low (one store); GPU: none.
    public static void Drop() { lock (gate) heldAt = 0; }

    // RA01: XrSession: the strip as last held, if it is fresh. Cost - CPU: extremely low (an uncontended lock and seven reads); GPU: none.
    public static bool TryRead(out int hand, out Vector3 start, out Vector3 direction, out bool toward)
    {
        lock (gate)
        {
            hand = heldHand; toward = towardHead;
            start = new Vector3(held[0], held[1], held[2]);
            direction = new Vector3(held[3], held[4], held[5]);
            return heldAt != 0 && Environment.TickCount64 - heldAt < StaleMs;
        }
    }

    // RA01: what the painted image depends on (colour and brightness): a new number = paint it again. Cost - CPU: extremely low; GPU: none.
    public static int Look => Tuning.LaserColour * 1000 + (int)MathF.Round(Tuning.LaserBrightness * 100f);

    // RA01: paints the 128 x 64 image: left half the beam (a soft stripe across, whiter in its core, fading a little toward the far end at the top), right half
    // the dot (a round glow, white-hot in the middle). Colour unpremultiplied, glow in the alpha. 'bgra' = the headset image's byte order.
    // Cost - CPU: low (8192 pixels, two exponentials each, only when the colour or brightness changes); GPU: none.
    public static unsafe void Paint(byte* pixels, bool bgra)
    {
        var c = Colours[Math.Clamp(Tuning.LaserColour, 0, Colours.Length - 1)];
        float bright = Math.Clamp(Tuning.LaserBrightness, 0.05f, 1f);
        for (int y = 0; y < ImageHeight; y++)
            for (int x = 0; x < ImageWidth; x++)
            {
                float glow, core;
                if (x < Half)
                {
                    float across = ((x + 0.5f) / Half) * 2f - 1f;                  // RA01: -1 .. 1 across the beam
                    float along = (y + 0.5f) / ImageHeight;                          // RA01: 0 at the far end (the image's top) .. 1 at the muzzle
                    glow = MathF.Exp(-4.5f * across * across) * Math.Min(1f, 0.35f + along * 2.6f) * Math.Min(1f, (1f - along) * 6f); // RA01: M26-1 - and fades in at the muzzle end
                    core = MathF.Exp(-40f * across * across);
                }
                else
                {
                    float dx = (x - Half + 0.5f) / (Half / 2f) - 1f, dy = (y + 0.5f) / (ImageHeight / 2f) - 1f;
                    float r2 = dx * dx + dy * dy;
                    glow = r2 >= 1f ? 0f : MathF.Exp(-4.5f * r2);
                    core = MathF.Exp(-30f * r2);
                }
                float white = 0.6f * core;                                           // RA01: the hot centre leans toward white
                byte r = (byte)(c.R + (255 - c.R) * white), g = (byte)(c.G + (255 - c.G) * white), b = (byte)(c.B + (255 - c.B) * white);
                byte a = (byte)Math.Clamp(glow * bright * 255f, 0f, 255f);
                byte* p = pixels + (y * ImageWidth + x) * 4;
                p[0] = bgra ? b : r; p[1] = g; p[2] = bgra ? r : b; p[3] = a;
            }
    }

    // RA01: where the two panels stand this frame, in the controllers' space: the hand's pose, your head's pose, the held start and direction (or, 'toward',
    // the end point in the head's frame: then the beam is as long as the gap to it).
    // The beam: its middle halfway along, its long side (+Y) along the direction, its face (+Z) turned toward the head as far as it can turn about the
    // direction. The dot: at the end, facing the head. Sizes in metres. False if the direction is unusable.
    // Cost - CPU: extremely low (a few vector turns and two quaternions from axes); GPU: none.
    public static bool Place(in Xr.Posef handPose, in Xr.Posef headPose, Vector3 start, Vector3 direction, bool toward,
                             out Xr.Posef beam, out float beamWidth, out float beamLength, out Xr.Posef dot, out float dotSize)
    {
        beam = dot = default; beamWidth = beamLength = dotSize = 0;
        Quaternion hand = Quaternion.Normalize(new Quaternion(handPose.Orientation.X, handPose.Orientation.Y, handPose.Orientation.Z, handPose.Orientation.W));
        Vector3 from = new Vector3(handPose.Position.X, handPose.Position.Y, handPose.Position.Z) + Vector3.Transform(start, hand);
        Vector3 head = new(headPose.Position.X, headPose.Position.Y, headPose.Position.Z);
        Quaternion headTurn = Quaternion.Normalize(new Quaternion(headPose.Orientation.X, headPose.Orientation.Y, headPose.Orientation.Z, headPose.Orientation.W));
        Vector3 along = toward ? head + Vector3.Transform(direction, headTurn) - from : Vector3.Transform(direction, hand); // RA01: M24-5 - toward the head-held point
        beamLength = toward ? along.Length() : Tuning.LaserLengthM;
        if (!(along.LengthSquared() > 1e-6f) || !(beamLength > 0.05f)) return false;
        along = Vector3.Normalize(along);
        beamWidth = Tuning.LaserThicknessMm * 3f / 1000f;                           // RA01: the core is a third of the panel; the rest is glow
        Vector3 end = from + along * beamLength;
        float gap = Math.Min(StartGapM, beamLength * 0.5f);                          // RA01: M26-1 - the beam starts StartGapM past the muzzle (see the constant)
        from += along * gap; float visible = beamLength - gap;
        Vector3 middle = from + along * (visible / 2f);
        beam = Pose(middle, along, head - middle);
        beamLength = visible;
        dotSize = (0.01f + beamLength * 0.003f) * Tuning.LaserThicknessMm / 3f;     // RA01: about 5.5 cm at 15 m with a 3 mm beam - grows with distance to stay visible
        Vector3 toHead = head - end;
        Vector3 up = MathF.Abs(Vector3.Dot(Vector3.Normalize(toHead), Vector3.UnitY)) > 0.95f ? Vector3.UnitZ : Vector3.UnitY;
        dot = Pose(end, up, toHead);
        return float.IsFinite(middle.X + middle.Y + middle.Z + end.X + end.Y + end.Z);
    }

    // RA01: a panel pose at 'at' whose +Y runs along 'length' and whose face (+Z) turns toward 'face' as far as it can (the part of 'face' across 'length').
    // Builder picture: a door on a hinge pole - the pole is the length, the door swings round it until it looks at you.
    // Cost - CPU: extremely low (two cross products, a normalise and one quaternion from three axes); GPU: none.
    static Xr.Posef Pose(Vector3 at, Vector3 length, Vector3 face)
    {
        Vector3 y = Vector3.Normalize(length);
        Vector3 z = face - y * Vector3.Dot(face, y);
        if (z.LengthSquared() < 1e-8f) z = Vector3.Cross(y, MathF.Abs(y.X) < 0.9f ? Vector3.UnitX : Vector3.UnitZ);
        z = Vector3.Normalize(z);
        Vector3 x = Vector3.Cross(y, z);                                              // RA01: right-handed: x = y cross z
        Matrix4x4 m = new(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1); // RA01: each row is where that axis goes (System.Numerics' convention)
        Quaternion q = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
        return new Xr.Posef { Orientation = { X = q.X, Y = q.Y, Z = q.Z, W = q.W }, Position = { X = at.X, Y = at.Y, Z = at.Z } };
    }
}
