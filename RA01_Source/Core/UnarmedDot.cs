namespace RA01.Core;

// RA01: M76 - THE UNARMED DOT (Rob 2026-10-14): with nothing in hand the game draws no crosshair at all - for V and for Johnny - so pressing an elevator button
// or aiming a look at a door had nothing to go by. Actions\WeaponRules.cs says when you're unarmed (Wanted); XrSession hangs this small painted dot at the
// centre of the game's picture - exactly where the game aims and where its "what am I looking at" check looks - only in play (no menu, no cutscene,
// no popup). The game never sees it.
// Its look: the light muted olive of the Militech line (143, 163, 107) as a solid core, ringed in near-black, with a soft outer edge - the olive shows on dark
// backgrounds, the dark ring on bright ones (sky, white walls).
// Builder picture: a plumb mark chalked on the wall where the laser level points, for when the laser itself is switched off.
// Cost - CPU: extremely low (one volatile store per controller read; a 4 KB paint once); GPU: extremely low (one tiny panel).
internal static class UnarmedDot
{
    public const int ImageSize = 32;
    public const float SizeDeg = 0.6f;           // RA01: the whole panel, degrees across; the olive core is about half of it (about 1 cm at the picture's 2 m)
    const float CoreR = 0.45f, RingR = 0.75f;    // RA01: share of the radius: olive out to CoreR, the dark ring out to RingR, then a soft fade to the edge
    const byte OliveR = 143, OliveG = 163, OliveB = 107, RingShade = 16;

    // RA01: Actions: true while you're unarmed (no gun, no blade, fists not up). Read by XrSession every headset frame.
    public static volatile bool Wanted;

    // RA01: paints the 32 x 32 dot, colour unpremultiplied and coverage in alpha; 'bgra' = the headset image's byte order.
    // Cost - CPU: extremely low (1024 pixels, once per session); GPU: none.
    public static unsafe void Paint(byte* pixels, bool bgra)
    {
        for (int y = 0; y < ImageSize; y++)
            for (int x = 0; x < ImageSize; x++)
            {
                float dx = (x + 0.5f) / (ImageSize / 2f) - 1f, dy = (y + 0.5f) / (ImageSize / 2f) - 1f;
                float r = MathF.Sqrt(dx * dx + dy * dy);
                bool olive = r <= CoreR;
                float alpha = r >= 1f ? 0f : r <= RingR ? (olive ? 1f : 0.85f) : 0.85f * (1f - r) / (1f - RingR);
                if (r > CoreR - 0.06f && r <= CoreR) alpha = 1f;               // RA01: a crisp edge between the core and the ring
                byte cr = olive ? OliveR : RingShade, cg = olive ? OliveG : RingShade, cb = olive ? OliveB : RingShade;
                byte* p = pixels + (y * ImageSize + x) * 4;
                p[0] = bgra ? cb : cr; p[1] = cg; p[2] = bgra ? cr : cb; p[3] = (byte)Math.Clamp(alpha * 255f, 0f, 255f);
            }
    }
}
