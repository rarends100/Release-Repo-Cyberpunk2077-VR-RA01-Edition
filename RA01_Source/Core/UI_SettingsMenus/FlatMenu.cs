using System.Diagnostics;

namespace RA01.Core;

// RA01: M29-1 - F10 WITHOUT A HEADSET (Rob 2026-10-09: the settings window on the flat screen, so F10 means something on the desktop). Whenever the headset's frame
// loop isn't running (no headset, SteamVR off, the headset asleep), this small helper does what that loop does for the window: F10 opens and closes it, the
// mouse moves its own cursor and clicks (the game doesn't see the mouse while it is open - MenuInput), it repaints when something changed, and the values
// are saved as usual (SettingsFile.Poll). FrameCopy.StampMenu shows it on the monitor, centred. The moment the headset's loop starts, the helper steps
// aside (closing the window if open - VR opens its own) and the headset owns F10 again; when the loop stops, the helper takes over again.
// Builder picture: a night watchman who only walks the site while the day crew is away, and hands the keys back when they arrive.
// Cost - CPU: low (a background thread waking about 60 times a second while there is no headset loop: a few key reads; a repaint only when something in
// the window changed, 1-3 ms; asleep in 100 ms naps while the headset runs); GPU: none of its own (the stamp is FrameCopy's).
internal static unsafe class FlatMenu
{
    const int TickMs = 16, HeadsetNapMs = 100;
    static Thread? thread;
    static bool active, failed;

    // RA01: starts the helper once (PresentHook, when the game's main queue holds steady). Cost - CPU: extremely low (one thread start, once); GPU: none.
    public static void Start()
    {
        if (thread != null) return;
        thread = new Thread(Run) { IsBackground = true, Name = "RA01 flat menu" };
        thread.Start();
    }

    // RA01: the helper's loop: one Tick, then a short sleep - a nap while the headset's loop owns the window. Never throws (an error is logged once, the loop goes on).
    // Cost - CPU: low (see the class comment); GPU: none.
    static void Run()
    {
        while (true)
        {
            bool headset = XrSession.Running;
            try { Tick(headset); }
            catch (Exception e) { if (!failed) { failed = true; Log.Write($"RA01 flat menu: error ({e.Message}) - carrying on"); } }
            Thread.Sleep(headset ? HeadsetNapMs : TickMs);
        }
    }

    // RA01: one look: hand over to (or take over from) the headset's loop, then F10, the mouse, a repaint and the save. Cost - CPU: low; GPU: none.
    static void Tick(bool headset)
    {
        if (headset)
        {
            if (!active) return;
            active = false;
            if (SettingsMenu.Open) { SettingsMenu.Toggle(); MenuInput.Stop(); }
            Keys.Forget();
            Log.Write("RA01 flat menu: the headset is running - F10 and the settings window are the headset's again");
            return;
        }
        if (!active)
        {
            active = true;
            Keys.Forget();
            Log.Write("RA01 flat menu: no headset loop running - F10 opens the settings window on the monitor (use the mouse)");
        }
        long now = Stopwatch.GetTimestamp();
        if (Keys.Pressed(Keys.Menu)) Toggle();
        if (SettingsMenu.Open)
        {
            MenuInput.TakeMoves(out int dx, out int dy);
            SettingsMenu.Input(dx, dy, Keys.Held(Keys.MouseLeft));
            SettingsMenu.TakeRecentre();                                    // RA01: "Recentre" means nothing without a headset
            if (!SettingsMenu.Open) MenuInput.Stop();                       // RA01: closed with the X
            else
                lock (MenuDraw.Gate)
                {
                    if (SettingsMenu.NeedsDraw() && MenuDraw.Paint(null, FrameCopy.Bgra)) SettingsMenu.MarkDrawn();
                    MenuDraw.ToMonitor();
                }
        }
        SettingsFile.Poll(now);
    }

    // RA01: F10: opens the window (taking the mouse from the game) when it can be shown on the monitor, or closes it. Cost - CPU: extremely low; GPU: none.
    static void Toggle()
    {
        if (!SettingsMenu.Open && !FrameCopy.CanStamp)
        {
            Log.Write("RA01 flat menu: F10 - the settings window can't be shown on the monitor (the lines above say why: HDR on, or the game's picture not known yet)");
            return;
        }
        SettingsMenu.Toggle();
        if (SettingsMenu.Open) { MenuInput.Start(FrameCopy.GameWindow); Log.Write("RA01 flat menu: settings window opened on the monitor (F10 or its X closes it)"); }
        else MenuInput.Stop();
    }
}
