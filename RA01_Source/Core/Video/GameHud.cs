using static RA01.Core.Win32;

namespace RA01.Core;

// RA01: M15-0 - THE GAME'S OWN HUD, shrunk whole (TEST). The user's idea: instead of copying the HUD onto RA01's own panel (HudLayer, which stays as the backup),
// shrink the game's whole HUD evenly toward the middle of its own picture - every widget keeps its place relative to the others, like shrinking a poster on
// a photocopier. The crosshair then sits at the picture's true centre by itself.
// How (field names from RED4ext.SDK's class data - reference only, nothing copied): every HUD piece is a widget (inkWidget), and widgets nest inside each
// other like boxes in a crate. Each widget has a parent (a weak link at +0x168, object first), a name code (+0x188), and its own size dial:
// renderTransform at +0xB8 (translation x, y at +0x0, SCALE x, y at +0x8) with the point it shrinks toward, renderTransformPivot at +0x1A8 (0.5, 0.5 = its
// middle). The crosshair probe already knows one controller inside the HUD (the crosshair manager); its widget is one of the pointers near its start.
// From that widget the parent links are climbed to the top - the HUD's outermost box. This test:
//  - logs the chain once per new HUD (each widget's class code and name code - decoded offline against the SDK's names);
//  - while the Dev row "Shrink game HUD (test)" is on, sets the top box's scale to TestScale around its middle, twice a second (in case the game resets it);
//    turned off, puts the scale back to 1.
// M15-0b - the M15-0 climb stopped after two boxes (crosshair canvas -> its inkVirtualWindow): the game keeps each HUD piece in its OWN window, and the
// windows sit side by side on the HUD LAYER (inkHUDLayer), like photos pinned to one board. So now, from the crosshair's window:
//  - FindLayer: its memory is searched for the link back to the board - an object whose class is (built on) inkLayer - and the place is logged;
//  - LayerWindows: in the layer, a list (pointer, then count at +0xC, of 16-byte handles) whose entries are widgets - the pinned windows - is logged
//    and kept. The HUD windows each cover the whole screen, so shrinking EVERY one around its own middle shrinks the whole HUD evenly around the screen's
//    middle, every piece keeping its place. Without a layer list, the test falls back to the crosshair's own window (visible with fists or empty hands).
// Every read is a safe read and every write a safe write; nothing in the game is called.
// Cost - CPU: extremely low (asleep between ticks; a few dozen 8-byte safe reads to climb, and while the test is on two 16-byte safe writes twice a second);
// GPU: none.
internal static unsafe class GameHud
{
    const int ClassAt = 0x30, NameAt = 0x18, ParentClassAt = 0x10;
    const int Parent231 = 0x168, Name231 = 0x188, Scale231 = 0xB8 + 0x8, Pivot231 = 0x1A8;   // RA01: M44 - 2.31's places; the fields are now found by NAME (Fields)
    static int parentAt = -1, nameAt = -1, scaleAt = -1, pivotAt = -1;                         // RA01: M44 - this build's places (-1 = not known yet / not usable)
    const int ControllerScanBytes = 0x100, MaxClimb = 32, TickMs = 500;
    const int WindowScanBytes = 0x5B0, LayerScanBytes = 0x200, MaxWindows = 128;
    const float TestScale = 0.5f;
    static readonly ulong WidgetName = NameCode("inkWidget"), LayerName = NameCode("inkLayer");
    static readonly nint[] windows = new nint[MaxWindows]; // RA01: M15-0b - the HUD windows to shrink (0 = empty slot)
    static int windowCount;
    static readonly nint Self = GetCurrentProcess();
    static int started;

    // RA01: starts the test thread. Call once at start-up.
    // Cost - CPU: extremely low (starts one background thread); GPU: none.
    public static void Install()
    {
        if (Interlocked.Exchange(ref started, 1) == 1) return;
        new Thread(Loop) { IsBackground = true, Name = "RA01 game HUD" }.Start();
    }

    // RA01: each tick: from the crosshair manager to the HUD's top widget (climbed again when the manager changes), then the test row's scale. Any error is logged
    // once and the thread stops - the game's HUD is then simply left as it is.
    // Cost - CPU: extremely low (see the class comment); GPU: none.
    static void Loop()
    {
        nint lastManager = 0;
        bool wasOn = false;
        try
        {
            while (true)
            {
                Thread.Sleep(TickMs);
                nint manager = CrosshairProbe.Manager;
                if (manager != 0 && manager != lastManager) { lastManager = manager; FindWindows(Climb(manager)); }
                bool on = Tuning.HudShrinkTest && windowCount > 0;
                if (on || wasOn) for (int i = 0; i < windowCount; i++) SetScale(windows[i], on ? TestScale : 1f);
                if (wasOn && !on) Log.Write("RA01 game HUD (test): scale put back to 1");
                if (on && !wasOn) Log.Write($"RA01 game HUD (test): {windowCount} HUD window(s) scaled to {TestScale} around their middles");
                wasOn = on;
            }
        }
        catch (Exception e) { Log.Write($"RA01 game HUD (test) stopped: {e.Message}"); }
    }

    // RA01: from a HUD controller: the first widget among the pointers near its start, then up its parents to the top. Logs the chain; returns the top (0 if none).
    // Cost - CPU: extremely low (up to 32 pointer checks, then up to 32 steps of three small safe reads); GPU: none.
    static nint Climb(nint controller)
    {
        nint widget = 0;
        for (int at = 0x40; at < ControllerScanBytes && widget == 0; at += 8)
        {
            nint p = Ptr(controller + at);
            if (IsWidget(p)) widget = p;
        }
        if (widget == 0) { Log.Write($"RA01 game HUD (test): no widget found near the crosshair manager 0x{controller:X}"); return 0; }
        if (!Fields(widget)) { Log.Write("RA01 game HUD (test): the widget's fields can't be found by name on this game build - the test stays off"); return 0; }
        string chain = "";
        nint top = widget;
        for (int step = 0; step < MaxClimb; step++)
        {
            ulong name = 0;
            Read(top + nameAt, &name, 8);
            chain += $" {ClassName(top):X16}/{name:X16}";
            nint parent = Ptr(top + parentAt);
            if (!IsWidget(parent)) break;
            top = parent;
        }
        Log.Write($"RA01 game HUD (test): from the crosshair manager up to the top (class/name codes):{chain}");
        return top;
    }

    // RA01: M15-0b - from the crosshair's window to the HUD layer and its windows (see the class comment). Fills 'windows' (the layer's list, or just the
    // crosshair's window) and logs what was found.
    // Cost - CPU: extremely low, once per new crosshair manager (up to about 180 pointer checks in the window, 64 in the layer, plus the list); GPU: none.
    static void FindWindows(nint window)
    {
        windowCount = 0;
        if (window == 0) return;
        nint layer = 0;
        int layerAt = -1;
        for (int at = 0x40; at < WindowScanBytes && layer == 0; at += 8)
        {
            nint p = Ptr(window + at);
            if (IsA(p, LayerName)) { layer = p; layerAt = at; }
        }
        if (layer == 0) Log.Write("RA01 game HUD (test): no layer found in the crosshair's window - shrinking only that window");
        else
        {
            Log.Write($"RA01 game HUD (test): the crosshair's window +0x{layerAt:X} -> layer 0x{layer:X}, class {ClassName(layer):X16}");
            for (int at = 0x30; at < LayerScanBytes && windowCount == 0; at += 8)
            {
                nint list = Ptr(layer + at);
                uint count = 0;
                if (list == 0 || !Read(layer + at + 0xC, &count, 4) || count == 0 || count > MaxWindows || !IsWidget(Ptr(list))) continue;
                string kinds = "";
                for (int i = 0; i < count; i++)
                {
                    nint w = Ptr(list + i * 16);
                    if (!IsWidget(w)) continue;
                    windows[windowCount++] = w;
                    if (i < 12) kinds += $" {ClassName(w):X16}";
                }
                Log.Write($"RA01 game HUD (test): layer +0x{at:X} is a list of {count} widgets ({windowCount} readable), classes:{kinds}{(count > 12 ? " ..." : "")}");
            }
            if (windowCount == 0) Log.Write("RA01 game HUD (test): no list of widgets found in the layer - shrinking only the crosshair's window");
        }
        if (windowCount == 0) windows[windowCount++] = window;
    }

    // RA01: sets a widget's render scale (x and y) and puts its pivot in its middle. Safe writes.
    // Cost - CPU: extremely low (two safe writes); GPU: none.
    static void SetScale(nint widget, float scale)
    {
        if (scaleAt < 0 || pivotAt < 0) return;   // RA01: M44 - never write to a place not found by name
        float* pair = stackalloc float[2];
        nuint done = 0;
        pair[0] = pair[1] = scale;
        WriteProcessMemory(Self, widget + scaleAt, pair, 8, &done);
        pair[0] = pair[1] = 0.5f;
        WriteProcessMemory(Self, widget + pivotAt, pair, 8, &done);
    }

    // RA01: M44 - the four widget fields this test uses, by their names in the game's catalogue (FieldNames): inkWidget.parentWidget, .name,
    // .renderTransform.scale and .renderTransformPivot. False if any can't be found (then nothing is read or written by place).
    // Cost - CPU: extremely low (four look-ups, each a short list walk the first time, then a dictionary read); GPU: none.
    static bool Fields(nint widget)
    {
        parentAt = FieldNames.At(widget, HookRegistry.HudParent, "inkWidget", "parentWidget", null, Parent231);
        nameAt = FieldNames.At(widget, HookRegistry.HudName, "inkWidget", "name", null, Name231);
        scaleAt = FieldNames.At(widget, HookRegistry.HudScale, "inkWidget", "renderTransform", "scale", Scale231);
        pivotAt = FieldNames.At(widget, HookRegistry.HudPivot, "inkWidget", "renderTransformPivot", null, Pivot231);
        return parentAt >= 0 && nameAt >= 0 && scaleAt >= 0 && pivotAt >= 0;
    }

    // RA01: is this object a widget (its class, or one of up to 12 parents, is inkWidget)?
    // Cost - CPU: extremely low (up to 13 pairs of 8-byte safe reads); GPU: none.
    static bool IsWidget(nint obj) => IsA(obj, WidgetName);

    // RA01: is this object of the named class, or built on it (checked up to 12 parents)?
    // Cost - CPU: extremely low (up to 13 pairs of 8-byte safe reads); GPU: none.
    static bool IsA(nint obj, ulong className)
    {
        nint cls = obj != 0 ? Ptr(obj + ClassAt) : 0;
        for (int depth = 0; cls != 0 && depth < 13; depth++, cls = Ptr(cls + ParentClassAt))
        {
            ulong name = 0;
            if (!Read(cls + NameAt, &name, 8)) return false;
            if (name == className) return true;
        }
        return false;
    }

    // RA01: an object's class name code, 0 if unreadable.
    // Cost - CPU: extremely low (two 8-byte safe reads); GPU: none.
    static ulong ClassName(nint obj)
    {
        nint cls = obj != 0 ? Ptr(obj + ClassAt) : 0;
        ulong name = 0;
        if (cls != 0) Read(cls + NameAt, &name, 8);
        return name;
    }

    // RA01: a pointer stored at 'at', 0 if unreadable or not plausibly a pointer.
    // Cost - CPU: extremely low (one 8-byte safe read); GPU: none.
    static nint Ptr(nint at)
    {
        nint value = 0;
        if (at < 0x10000 || !Read(at, &value, 8)) return 0;
        return value > 0x10000 && value < 0x7FFFFFFFFFFF ? value : 0;
    }

    // RA01: the game's name code: FNV-1a 64-bit over the name's bytes (as GameAnchor.NameCode).
    // Cost - CPU: extremely low (a few letters, once at start-up); GPU: none.
    static ulong NameCode(string name)
    {
        ulong h = 0xCBF29CE484222325;
        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(name)) { h ^= b; h *= 0x100000001B3; }
        return h;
    }

    // RA01: safe read - a bad address fails this one call instead of crashing the game.
    // Cost - CPU: extremely low (one ReadProcessMemory call); GPU: none.
    static bool Read(nint address, void* buffer, int bytes)
    {
        nuint got = 0;
        return ReadProcessMemory(Self, address, buffer, (nuint)bytes, &got) && got == (nuint)bytes;
    }
}
