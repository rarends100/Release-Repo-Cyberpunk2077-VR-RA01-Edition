using System.Runtime.InteropServices;

// RA01: M40 - THE LAUNCHER WINDOW (Rob approved 2026-10-11): a small borderless desktop window in the RA01 look (the in-game F10 window's colours: dark navy,
// cyan lines, yellow accent; cut corners; the RA01 emblem from the launcher's own icon), shown before anything starts. Two tabs:
//   SETUP    - game resolution (the same GameResolutionPercent as the in-game row, 25-140% in 5% steps), VR runtime (OpenXR; a list, ready for more),
//              the game's build (M40b: from the exe's version stamp; red if it isn't the build RA01 was made for),
//              the game folder (auto-detected, or picked with BROWSE - pick Cyberpunk2077.exe), how to start the game (directly or through Steam), and LAUNCH;
//   CONTROLS & FEATURES - short cards read from RA01_Guide.txt beside the launcher (editable without a rebuild; the mouse wheel scrolls).
// Plain Windows calls only (GDI, like Core\UI_SettingsMenus\MenuDraw.cs), so the launcher stays one self-contained exe with nothing for players to install.
// Program.cs owns everything about the game (finding it, the doorman, starting, injecting, tidying up); this file only shows, asks and reports. After
// LAUNCH the window stays as a status board; closing it while the game runs only hides it - the launcher still tidies up when the game closes.
// Cost - CPU: extremely low (it redraws only when something changes: a click, a hover, a status line); GPU: none (no GPU work - plain desktop drawing).
static unsafe class LauncherWindow
{
    // RA01: what the window shows and edits - Program.cs fills these before Run and reads them on LAUNCH.
    public static int ResolutionPercent = 50;
    public static string Runtime = "OpenXR";
    public static string GameFolder = "";          // RA01: the game's bin\x64 folder ("" = not found)
    public static string FolderSource = "";        // RA01: where it was found (Steam, GOG, Epic, your choice, next to the launcher)
    public static bool StartDirect = true;
    public static bool MetaLinkActive;             // RA01: M70 - Meta's OpenXR runtime (Quest Link) is the active one: the game always starts directly (see Program.MetaLinkActive)
    public static string GameBuild = "";           // RA01: M40b - the game's build from Cyberpunk2077.exe's version stamp ("" = unknown)
    public const string MadeForBuild = "2.31";     // RA01: M40b - the game build RA01 was made and tested on (its addresses and patterns); another build is flagged in red
    public static Action? OnLaunch;                // RA01: LAUNCH pressed (Program starts its launch thread)
    public static Action? OnSettingsChanged;       // RA01: a setting changed (Program saves it at once)
    public static Func<string, (string? folder, string source)>? CheckPicked;   // RA01: BROWSE picked a file - Program says whether it is the game

    static readonly string[] Runtimes = { "OpenXR" };   // RA01: more later (Rob)
    const int Width = 760, Height = 584;   // RA01: M65 - 24 taller (was 560) for the Militech line along the bottom
    const int MinResolution = 25, MaxResolution = 140, ResolutionStep = 5;   // RA01: the in-game row's range and steps (Tuning.Min/MaxGameResolutionPercent)

    static readonly uint Background = Rgb(10, 12, 24), Line = Rgb(0, 240, 255), LineDim = Rgb(0, 110, 130), Accent = Rgb(252, 238, 10), Panel = Rgb(20, 40, 90);
    static readonly uint Olive = Rgb(143, 163, 107);   // RA01: M65 - a light, muted military olive for the Militech line (reads on the navy, doesn't fight the yellow)
    const string Militech = "// SYSTEM ENHANCEMENT SECURED BY MILITECH DEFENSE CORP //";   // RA01: M65 - Rob's line, as on the Breach Protocol screen
    static readonly uint Text = Rgb(225, 245, 250), TextDim = Rgb(120, 170, 185), Hover = Rgb(0, 70, 90), Dark = Rgb(10, 12, 24), Red = Rgb(255, 40, 90);

    enum Hit { None, Close, TabSetup, TabGuide, ResMinus, ResPlus, Runtime, Browse, Direct, Steam, Launch }

    static nint window, emblem;
    static float scale = 1f;
    static int tab, guideScroll, guideHeight;
    static Hit hover;
    static bool launched, hidden, finished;
    static string status = "Pick your settings, then press LAUNCH.";
    static readonly object gate = new();
    static readonly List<(Hit hit, Rect box)> hits = new();
    static List<(string title, string body)>? cards;
    static nint fontTitle, fontTab, fontLabel, fontSmall, fontButton, fontLaunch;

    const uint WM_DESTROY = 0x02, WM_PAINT = 0x0F, WM_CLOSE = 0x10, WM_ERASEBKGND = 0x14, WM_NCHITTEST = 0x84, WM_MOUSEMOVE = 0x200, WM_LBUTTONUP = 0x202;
    const uint WM_MOUSEWHEEL = 0x20A, WM_MOUSELEAVE = 0x2A3, WM_APP_STATUS = 0x8001, WM_APP_DONE = 0x8002;

    // RA01: shows the window and runs its message loop until it closes (after the game has closed and been tidied up, if it was launched).
    // Cost - CPU: extremely low (asleep in GetMessage between events); GPU: none.
    public static int Run()
    {
        SetProcessDPIAware();
        nint screen = GetDC(0);
        scale = GetDeviceCaps(screen, 88) / 96f;                                     // RA01: LOGPIXELSX - so text is the same size on a 4K monitor
        ReleaseDC(0, screen);
        nint instance = GetModuleHandleW(null);
        fixed (char* name = "RA01LauncherWindow")
        {
            var wc = new WndClassEx { Size = (uint)sizeof(WndClassEx), WndProc = &WndProc, Instance = instance, Cursor = LoadCursorW(0, 32512), ClassName = name };
            wc.Icon = wc.IconSmall = LoadIconFromExe(32);
            RegisterClassExW(&wc);
            int w = S(Width), h = S(Height);
            int x = (GetSystemMetrics(0) - w) / 2, y = (GetSystemMetrics(1) - h) / 2;
            fixed (char* title = "Cyberpunk 2077 VR - RA01 Edition")
                window = CreateWindowExW(0, name, title, 0x80000000 | 0x10000000 | 0x00020000, x, y, w, h, 0, 0, instance, 0);   // RA01: WS_POPUP | WS_VISIBLE | WS_MINIMIZEBOX
        }
        if (window == 0) return 1;
        emblem = LoadIconFromExe(S(52));
        fontTitle = Font(26, 700); fontTab = Font(17, 600); fontLabel = Font(17, 400); fontSmall = Font(14, 400); fontButton = Font(16, 700); fontLaunch = Font(28, 700);
        Msg msg;
        while (GetMessageW(&msg, 0, 0, 0) > 0) { TranslateMessage(&msg); DispatchMessageW(&msg); }
        return 0;
    }

    // RA01: a new status line from the launch (any thread): kept, and the window is asked to redraw. Cost - CPU: extremely low; GPU: none.
    public static void SetStatus(string text)
    {
        lock (gate) status = text.StartsWith("RA01: ") ? text[6..] : text;
        if (window != 0) PostMessageW(window, WM_APP_STATUS, 0, 0);
    }

    // RA01: the launch has finished (the game closed and everything was put back, or it failed): a hidden window now closes the launcher.
    // M57 - 'close' true (the game ran and has closed): the window closes even when shown, and Program starts a fresh launcher.
    // Cost - CPU: extremely low; GPU: none.
    public static void LaunchFinished(bool close = false)
    {
        if (window != 0) PostMessageW(window, WM_APP_DONE, close ? 1 : 0, 0);
    }

    // RA01: the window's events. Cost - CPU: extremely low (a few compares per event); GPU: none.
    [UnmanagedCallersOnly]
    static nint WndProc(nint h, uint m, nint w, nint l)
    {
        try
        {
            switch (m)
            {
                case WM_PAINT: Paint(h); return 0;
                case WM_ERASEBKGND: return 1;
                case WM_NCHITTEST:
                {
                    nint result = DefWindowProcW(h, m, w, l);
                    Point p = new() { X = (short)(l & 0xFFFF), Y = (short)((l >> 16) & 0xFFFF) };
                    ScreenToClient(h, &p);
                    return result == 1 && p.Y < S(64) && At(p.X, p.Y) == Hit.None ? 2 : result;   // RA01: HTCLIENT -> HTCAPTION on the title bar: drag the window
                }
                case WM_MOUSEMOVE:
                {
                    Hit now = At((short)(l & 0xFFFF), (short)((l >> 16) & 0xFFFF));
                    if (now != hover) { hover = now; InvalidateRect(h, 0, false); }
                    var track = new TrackMouse { Size = (uint)sizeof(TrackMouse), Flags = 2, Window = h };   // RA01: TME_LEAVE - so hover clears when the mouse leaves
                    TrackMouseEvent(&track);
                    return 0;
                }
                case WM_MOUSELEAVE: if (hover != Hit.None) { hover = Hit.None; InvalidateRect(h, 0, false); } return 0;
                case WM_LBUTTONUP: Click(At((short)(l & 0xFFFF), (short)((l >> 16) & 0xFFFF))); InvalidateRect(h, 0, false); return 0;
                case WM_MOUSEWHEEL:
                    if (tab == 1) { guideScroll = Math.Clamp(guideScroll - (short)((w >> 16) & 0xFFFF) / 2, 0, Math.Max(0, guideHeight - ContentHeight())); InvalidateRect(h, 0, false); }
                    return 0;
                case WM_APP_STATUS: InvalidateRect(h, 0, false); return 0;
                case WM_APP_DONE:
                    finished = true;
                    if (hidden || w == 1) DestroyWindow(h); else InvalidateRect(h, 0, false);   // RA01: M57 - w = 1: closing for the restart
                    return 0;
                case WM_CLOSE:
                    if (launched && !finished) { hidden = true; ShowWindow(h, 0); return 0; }   // RA01: the game is running - keep going in the background to tidy up
                    DestroyWindow(h);
                    return 0;
                case WM_DESTROY: PostQuitMessage(0); return 0;
            }
        }
        catch { }
        return DefWindowProcW(h, m, w, l);
    }

    // RA01: a click on one of the window's parts. Cost - CPU: extremely low; GPU: none.
    static void Click(Hit hit)
    {
        switch (hit)
        {
            case Hit.Close: PostMessageW(window, WM_CLOSE, 0, 0); return;
            case Hit.TabSetup: tab = 0; return;
            case Hit.TabGuide: tab = 1; return;
        }
        if (launched) return;                                                         // RA01: settings are fixed once the game is starting
        switch (hit)
        {
            case Hit.ResMinus: ResolutionPercent = Math.Max(MinResolution, ResolutionPercent - ResolutionStep); break;
            case Hit.ResPlus: ResolutionPercent = Math.Min(MaxResolution, ResolutionPercent + ResolutionStep); break;
            case Hit.Runtime: Runtime = Runtimes[(Array.IndexOf(Runtimes, Runtime) + 1) % Runtimes.Length]; break;
            case Hit.Direct: StartDirect = true; break;
            case Hit.Steam: if (!MetaLinkActive) StartDirect = false; break;   // RA01: M70-2 - locked to DIRECTLY while Meta Quest Link is active
            case Hit.Browse: Browse(); break;
            case Hit.Launch:
                if (GameFolder.Length == 0 && StartDirect) { SetStatus("The game folder isn't known - press BROWSE and pick Cyberpunk2077.exe, or choose THROUGH STEAM."); return; }
                launched = true; OnLaunch?.Invoke(); return;
            default: return;
        }
        OnSettingsChanged?.Invoke();
    }

    // RA01: BROWSE: the Windows file picker, asking for Cyberpunk2077.exe; Program checks it really is the game. Cost - CPU: extremely low (the picker is Windows'); GPU: none.
    static void Browse()
    {
        char* file = stackalloc char[1024];
        file[0] = '\0';
        fixed (char* filter = "Cyberpunk 2077 (Cyberpunk2077.exe)\0Cyberpunk2077.exe\0\0")
        fixed (char* title = "Find Cyberpunk2077.exe (in the game's bin\\x64 folder)")
        {
            var ofn = new OpenFileName { Size = (uint)sizeof(OpenFileName), Owner = window, Filter = filter, File = file, MaxFile = 1024, Title = title, Flags = 0x1000 | 0x800 | 0x8 };
            if (!GetOpenFileNameW(&ofn)) return;
        }
        var (folder, source) = CheckPicked?.Invoke(new string(file)) ?? (null, "");
        if (folder == null) { SetStatus("That isn't the game's Cyberpunk2077.exe - it lives in <game folder>\\bin\\x64."); return; }
        GameFolder = folder; FolderSource = source;
        SetStatus("Game folder saved.");
    }

    // RA01: draws the whole window into a back buffer, then onto the screen in one go (no flicker). Cost - CPU: extremely low (a few dozen GDI calls, only on a change); GPU: none.
    static void Paint(nint h)
    {
        PaintStruct ps;
        nint screen = BeginPaint(h, &ps);
        int w = S(Width), ht = S(Height);
        nint dc = CreateCompatibleDC(screen), bitmap = CreateCompatibleBitmap(screen, w, ht), old = SelectObject(dc, bitmap);
        SetBkMode(dc, 1);
        hits.Clear();
        Fill(dc, 0, 0, Width, Height, Background);
        Frame(dc);
        if (emblem != 0) DrawIconEx(dc, S(18), S(8), emblem, S(52), S(52), 0, 0, 3);
        Label(dc, "CYBERPUNK 2077  //  VR", 82, 10, 500, 34, fontTitle, Accent, 0);
        Label(dc, "RA01 EDITION  -  LAUNCHER", 84, 40, 500, 20, fontSmall, Line, 0);
        Button(dc, Hit.Close, Width - 50, 14, 34, 30, "X", fontButton, false, Red);
        Button(dc, Hit.TabSetup, 20, 76, 170, 32, "SETUP", fontTab, tab == 0, 0);
        Button(dc, Hit.TabGuide, 200, 76, 260, 32, "CONTROLS & FEATURES", fontTab, tab == 1, 0);
        HLine(dc, 20, 112, Width - 40, LineDim);
        if (tab == 0) Setup(dc); else Guide(dc);
        string line; lock (gate) line = status;
        HLine(dc, 20, Height - 76, Width - 40, LineDim);
        Label(dc, line, 26, Height - 70, Width - 52, 36, fontSmall, finished ? Accent : TextDim, 0x10 | 0x8000);   // RA01: DT_WORDBREAK | DT_END_ELLIPSIS
        Label(dc, Militech, 20, Height - 30, Width - 40, 20, fontSmall, Olive, 1);   // RA01: M65 - the maker's stamp, centred along the very bottom
        BitBlt(screen, 0, 0, w, ht, dc, 0, 0, 0x00CC0020);
        SelectObject(dc, old); DeleteObject(bitmap); DeleteDC(dc);
        EndPaint(h, &ps);
    }

    // RA01: the SETUP tab. Cost - CPU: extremely low; GPU: none.
    static void Setup(nint dc)
    {
        int y = 134, x = 300;
        Label(dc, "Game resolution", 36, y + 6, 250, 26, fontLabel, Text, 0);
        Button(dc, Hit.ResMinus, x, y, 40, 34, "-", fontButton, false, 0);
        Label(dc, $"{ResolutionPercent}% of headset", x + 48, y + 6, 180, 26, fontLabel, Accent, 1);
        Button(dc, Hit.ResPlus, x + 236, y, 40, 34, "+", fontButton, false, 0);
        Label(dc, "Higher looks sharper but costs much more GPU.", x, y + 38, 420, 20, fontSmall, TextDim, 0);
        y += 72;
        Label(dc, "VR runtime", 36, y + 6, 250, 26, fontLabel, Text, 0);
        Button(dc, Hit.Runtime, x, y, 276, 34, Runtime, fontButton, false, 0);
        y += 58;
        Label(dc, "Game folder", 36, y + 6, 250, 26, fontLabel, Text, 0);
        Fill(dc, x, y, 312, 34, Panel);
        Label(dc, GameFolder.Length > 0 ? GameFolder : "NOT FOUND - press BROWSE", x + 8, y + 7, 298, 22, fontSmall, GameFolder.Length > 0 ? Text : Red, 0x4000);   // RA01: DT_PATH_ELLIPSIS
        Button(dc, Hit.Browse, x + 320, y, 104, 34, "BROWSE", fontButton, false, 0);
        if (FolderSource.Length > 0 || GameBuild.Length > 0)   // RA01: M40b - where it was found and which game build it is
        {
            bool match = GameBuild.Length == 0 || GameBuild == MadeForBuild;
            string build = GameBuild.Length == 0 ? "" : match ? $"Game build {GameBuild}" : $"Game build {GameBuild} - RA01 was made for {MadeForBuild}, some features may not work";
            string found = FolderSource.Length > 0 ? "Found: " + FolderSource : "";
            Label(dc, found + (found.Length > 0 && build.Length > 0 ? "   |   " : "") + build, x, y + 38, 440, 20, fontSmall, match ? TextDim : Red, 0x8000);
        }
        y += 72;
        Label(dc, "Start the game", 36, y + 6, 250, 26, fontLabel, Text, 0);
        bool direct = StartDirect || MetaLinkActive;                                 // RA01: M70-2 - Meta Quest Link active: always DIRECTLY, THROUGH STEAM can't be picked
        Button(dc, Hit.Direct, x, y, 140, 34, "DIRECTLY", fontButton, direct, 0);
        if (MetaLinkActive)
        {
            Fill(dc, x + 150, y, 190, 34, Panel);                                     // RA01: M70-2 - drawn locked: dim edge and text, and not clickable (no hit spot)
            Box(dc, x + 150, y, 190, 34, LineDim);
            Label(dc, "THROUGH STEAM", x + 150, y + 5, 190, 34, fontButton, TextDim, 1);
            Label(dc, "Meta Quest Link is the active VR runtime: the game always starts DIRECTLY (through Steam crashes with Link).", x - 264, y + 38, 704, 20, fontSmall, Red, 0); // RA01: M70
        }
        else
        {
            Button(dc, Hit.Steam, x + 150, y, 190, 34, "THROUGH STEAM", fontButton, !StartDirect, 0);
            Label(dc, StartDirect ? "Runs Cyberpunk2077.exe itself (Steam, GOG or Epic copies)." : "Asks Steam to start the game (Steam copies only).", x, y + 38, 440, 20, fontSmall, TextDim, 0);
        }
        Button(dc, Hit.Launch, 250, 432, 260, 58, launched ? (finished ? "DONE" : "RUNNING") : "LAUNCH", fontLaunch, !launched, 0);
    }

    // RA01: the CONTROLS & FEATURES tab: the guide's cards, clipped to the content area, scrolled by the mouse wheel. Cost - CPU: extremely low; GPU: none.
    static void Guide(nint dc)
    {
        cards ??= LoadGuide();
        int top = 120, bottom = Height - 82;
        nint clip = CreateRectRgn(S(20), S(top), S(Width - 20), S(bottom));
        SelectClipRgn(dc, clip);
        int y = top + 8 - guideScroll, start = y;
        foreach (var (title, body) in cards)
        {
            Label(dc, title.ToUpperInvariant(), 36, y, Width - 72, 24, fontTab, Accent, 0);
            y += 26;
            int tall = Measure(dc, body, Width - 92, fontLabel);
            Fill(dc, 36, y, 3, tall, LineDim);
            Label(dc, body, 50, y, Width - 92, tall, fontLabel, Text, 0x10);
            y += tall + 16;
        }
        guideHeight = y - start + 8;
        SelectClipRgn(dc, 0);
        DeleteObject(clip);
        if (guideHeight > ContentHeight()) Label(dc, "scroll for more", Width - 170, bottom - 20, 140, 18, fontSmall, TextDim, 2);
    }

    // RA01: reads RA01_Guide.txt beside the launcher into cards ("## " starts a card; "# " lines are notes). Cost - CPU: extremely low (one small file, once); GPU: none.
    static List<(string, string)> LoadGuide()
    {
        var list = new List<(string, string)>();
        try
        {
            string title = "", body = "";
            foreach (string raw in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "RA01_Guide.txt")))
            {
                string text = raw.TrimEnd();
                if (text.StartsWith("## ")) { if (title.Length > 0) list.Add((title, body.Trim())); title = text[3..]; body = ""; }
                else if (text.StartsWith("# ") || text == "#") continue;
                else if (title.Length > 0) body += text + "\n";
            }
            if (title.Length > 0) list.Add((title, body.Trim()));
        }
        catch { }
        if (list.Count == 0) list.Add(("Guide missing", "RA01_Guide.txt wasn't found beside the launcher."));
        return list;
    }

    static int ContentHeight() => (Height - 82 - 120);

    // RA01: which part of the window is under a point (window pixels). Cost - CPU: extremely low (a short list); GPU: none.
    static Hit At(int x, int y)
    {
        foreach (var (hit, box) in hits) if (x >= box.Left && x < box.Right && y >= box.Top && y < box.Bottom) return hit;
        return Hit.None;
    }

    // RA01: the cyan frame with two cut corners (top-left, bottom-right), the RA01 menu's shape. Cost - CPU: extremely low; GPU: none.
    static void Frame(nint dc)
    {
        int c = 22, r = Width - 2, b = Height - 2;
        Point* p = stackalloc Point[7];
        p[0] = P(1 + c, 1); p[1] = P(r, 1); p[2] = P(r, b - c); p[3] = P(r - c, b); p[4] = P(1, b); p[5] = P(1, 1 + c); p[6] = P(1 + c, 1);
        nint pen = CreatePen(0, Math.Max(1, S(2)), Line), old = SelectObject(dc, pen);
        Polyline(dc, p, 7);
        SelectObject(dc, old); DeleteObject(pen);
        Fill(dc, Width - 120, Height - 6, 90, 3, Accent);                            // RA01: the yellow tick on the bottom edge
    }

    // RA01: a button: filled box (yellow when selected, teal when hovered), a cyan edge and centred text. 'colour' overrides the edge (0 = cyan).
    // Cost - CPU: extremely low; GPU: none.
    static void Button(nint dc, Hit hit, int x, int y, int w, int h, string text, nint font, bool selected, uint colour)
    {
        bool over = hover == hit;
        Fill(dc, x, y, w, h, selected ? Accent : over ? Hover : Panel);
        Box(dc, x, y, w, h, colour != 0 ? colour : selected ? Accent : Line);
        Label(dc, text, x, y + (h - 24) / 2 - (font == fontLaunch ? 4 : 0), w, h, font, selected ? Dark : Text, 1);
        hits.Add((hit, new Rect { Left = S(x), Top = S(y), Right = S(x + w), Bottom = S(y + h) }));
    }

    // RA01: text in a box (window units). 'align': 0 left, 1 centre, 2 right, or DT_ flags. Cost - CPU: extremely low; GPU: none.
    static void Label(nint dc, string text, int x, int y, int w, int h, nint font, uint colour, uint align)
    {
        nint old = SelectObject(dc, font);
        SetTextColor(dc, colour);
        Rect r = new() { Left = S(x), Top = S(y), Right = S(x + w), Bottom = S(y + h) };
        uint flags = align switch { 1 => 0x1u, 2 => 0x2u, _ => align } | 0x800;     // RA01: DT_NOPREFIX
        fixed (char* t = text) DrawTextW(dc, t, text.Length, &r, flags);
        SelectObject(dc, old);
    }

    // RA01: how tall wrapped text is, in window units. Cost - CPU: extremely low; GPU: none.
    static int Measure(nint dc, string text, int w, nint font)
    {
        nint old = SelectObject(dc, font);
        Rect r = new() { Left = 0, Top = 0, Right = S(w), Bottom = 0 };
        fixed (char* t = text) DrawTextW(dc, t, text.Length, &r, 0x10 | 0x400 | 0x800);   // RA01: DT_WORDBREAK | DT_CALCRECT | DT_NOPREFIX
        SelectObject(dc, old);
        return (int)MathF.Ceiling(r.Bottom / scale);
    }

    static void Fill(nint dc, int x, int y, int w, int h, uint colour)
    {
        nint brush = CreateSolidBrush(colour);
        Rect r = new() { Left = S(x), Top = S(y), Right = S(x + w), Bottom = S(y + h) };
        FillRect(dc, &r, brush);
        DeleteObject(brush);
    }
    static void Box(nint dc, int x, int y, int w, int h, uint colour) { Fill(dc, x, y, w, 1, colour); Fill(dc, x, y + h - 1, w, 1, colour); Fill(dc, x, y, 1, h, colour); Fill(dc, x + w - 1, y, 1, h, colour); }
    static void HLine(nint dc, int x, int y, int w, uint colour) => Fill(dc, x, y, w, 1, colour);
    static int S(int v) => (int)MathF.Round(v * scale);
    static Point P(int x, int y) => new() { X = S(x), Y = S(y) };
    static uint Rgb(int r, int g, int b) => (uint)(r | g << 8 | b << 16);
    static nint Font(int size, int weight) { fixed (char* face = "Bahnschrift") return CreateFontW(-S(size), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, face); }   // RA01: CLEARTYPE_QUALITY

    // RA01: the RA01 emblem: the launcher's own icon (stamped in by build.ps1 from assets\RA01.ico) at 'size' pixels. 0 if there is none.
    // Cost - CPU: extremely low (once); GPU: none.
    static nint LoadIconFromExe(int size)
    {
        nint icon = 0;
        string? exe = Environment.ProcessPath;
        if (exe == null) return 0;
        fixed (char* path = exe) PrivateExtractIconsW(path, 0, size, size, &icon, null, 1, 0);
        return icon;
    }

    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct Msg { public nint Window; public uint Message; public nint W, L; public uint Time; public Point Pt; public uint Private; }
    [StructLayout(LayoutKind.Sequential)] struct PaintStruct { public nint Dc; public int Erase; public Rect Area; public int Restore, IncUpdate; public fixed byte Reserved[32]; }
    [StructLayout(LayoutKind.Sequential)] struct TrackMouse { public uint Size, Flags; public nint Window; public uint HoverTime; }
    [StructLayout(LayoutKind.Sequential)]
    struct WndClassEx { public uint Size, Style; public delegate* unmanaged<nint, uint, nint, nint, nint> WndProc; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Brush; public char* MenuName, ClassName; public nint IconSmall; }
    [StructLayout(LayoutKind.Sequential)]
    struct OpenFileName
    {
        public uint Size; public nint Owner, Instance; public char* Filter, CustomFilter; public uint MaxCustomFilter, FilterIndex; public char* File; public uint MaxFile;
        public char* FileTitle; public uint MaxFileTitle; public char* InitialDir, Title; public uint Flags; public ushort FileOffset, FileExtension; public char* DefaultExt;
        public nint CustomData, Hook; public char* Template; public nint Reserved; public uint ReservedNumber, FlagsEx;
    }

    const string U = "user32.dll", G = "gdi32.dll";
    [DllImport(U)] static extern bool SetProcessDPIAware();
    [DllImport(U)] static extern nint GetDC(nint window);
    [DllImport(U)] static extern int ReleaseDC(nint window, nint dc);
    [DllImport(U)] static extern int GetSystemMetrics(int index);
    [DllImport(U)] static extern ushort RegisterClassExW(WndClassEx* wc);
    [DllImport(U)] static extern nint CreateWindowExW(uint exStyle, char* className, char* title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport(U)] static extern int GetMessageW(Msg* msg, nint window, uint min, uint max);
    [DllImport(U)] static extern bool TranslateMessage(Msg* msg);
    [DllImport(U)] static extern nint DispatchMessageW(Msg* msg);
    [DllImport(U)] static extern nint DefWindowProcW(nint window, uint msg, nint w, nint l);
    [DllImport(U)] static extern bool PostMessageW(nint window, uint msg, nint w, nint l);
    [DllImport(U)] static extern void PostQuitMessage(int code);
    [DllImport(U)] static extern bool DestroyWindow(nint window);
    [DllImport(U)] static extern bool ShowWindow(nint window, int command);
    [DllImport(U)] static extern bool InvalidateRect(nint window, nint rect, bool erase);
    [DllImport(U)] static extern bool ScreenToClient(nint window, Point* point);
    [DllImport(U)] static extern bool TrackMouseEvent(TrackMouse* track);
    [DllImport(U)] static extern nint BeginPaint(nint window, PaintStruct* ps);
    [DllImport(U)] static extern bool EndPaint(nint window, PaintStruct* ps);
    [DllImport(U)] static extern int FillRect(nint dc, Rect* rect, nint brush);
    [DllImport(U)] static extern int DrawTextW(nint dc, char* text, int count, Rect* rect, uint format);
    [DllImport(U)] static extern nint LoadCursorW(nint instance, nint name);
    [DllImport(U)] static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int w, int h, uint step, nint brush, uint flags);
    [DllImport(U)] static extern uint PrivateExtractIconsW(char* file, int index, int cx, int cy, nint* icons, uint* ids, uint count, uint flags);
    [DllImport("kernel32.dll")] static extern nint GetModuleHandleW(char* name);
    [DllImport("comdlg32.dll")] static extern bool GetOpenFileNameW(OpenFileName* ofn);
    [DllImport(G)] static extern int GetDeviceCaps(nint dc, int index);
    [DllImport(G)] static extern nint CreateCompatibleDC(nint dc);
    [DllImport(G)] static extern nint CreateCompatibleBitmap(nint dc, int w, int h);
    [DllImport(G)] static extern nint SelectObject(nint dc, nint obj);
    [DllImport(G)] static extern bool DeleteObject(nint obj);
    [DllImport(G)] static extern bool DeleteDC(nint dc);
    [DllImport(G)] static extern int SetBkMode(nint dc, int mode);
    [DllImport(G)] static extern uint SetTextColor(nint dc, uint colour);
    [DllImport(G)] static extern nint CreateSolidBrush(uint colour);
    [DllImport(G)] static extern nint CreatePen(int style, int width, uint colour);
    [DllImport(G)] static extern bool Polyline(nint dc, Point* points, int count);
    [DllImport(G)] static extern bool BitBlt(nint dest, int x, int y, int w, int h, nint source, int sx, int sy, uint rop);
    [DllImport(G)] static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport(G)] static extern int SelectClipRgn(nint dc, nint region);
    [DllImport(G)] static extern nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrecision, uint clipPrecision, uint quality, uint pitch, char* face);
}
