namespace RA01.Core;

// RA01: M27-1 - THE BANNER: a big message floating in front of you for a moment ("GESTURE MODE ACTIVATED"), in the F10 window's Cyberpunk look. Anything may call
// Show; XrSession paints it once (MenuDraw.PaintBanner, uploaded through FrameCopy's small-image path) and shows it as one panel fixed to your view,
// slightly above the middle, until its time is up. A newer message replaces an older one at once.
// Builder picture: a sign held up in front of your face for two seconds, then taken away.
// Cost - CPU: extremely low (a few reads per headset frame; one paint of 1024 x 192 pixels per message); GPU: extremely low (one panel; a 0.8 MB upload per message).
internal static class Banner
{
    public const int Width = 1024, Height = 192;          // RA01: the image, in pixels (4:0.75 - one line of big capitals)
    public const float PanelWidthM = 0.8f, PanelUpM = 0.12f, PanelAheadM = 1.3f; // RA01: the panel: 80 cm wide, 12 cm above the middle of your view, 1.3 m ahead

    static readonly object gate = new();
    static string text = "";
    static long until;                                     // RA01: Environment.TickCount64 the message stops showing at
    static int version;                                    // RA01: goes up with every Show, so XrSession knows to paint again

    // RA01: shows 'message' for 'ms' milliseconds (any thread). Logged. Cost - CPU: extremely low (a lock and three stores); GPU: none.
    public static void Show(string message, int ms = 2500)
    {
        lock (gate) { text = message; until = Environment.TickCount64 + ms; version++; }
        Log.Write($"RA01 banner: \"{message}\"");
    }

    // RA01: XrSession, per headset frame: the message to show now (false = none), and its version. Cost - CPU: extremely low (a lock and a compare); GPU: none.
    public static bool TryCurrent(out string message, out int messageVersion)
    {
        lock (gate)
        {
            message = text; messageVersion = version;
            return text.Length > 0 && Environment.TickCount64 < until;
        }
    }
}
