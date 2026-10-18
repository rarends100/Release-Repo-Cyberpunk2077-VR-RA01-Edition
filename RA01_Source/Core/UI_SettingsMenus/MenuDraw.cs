using static RA01.Core.Win32;

namespace RA01.Core;

// RA01: M5a-3 - paints the F10 window with Windows' own drawing functions (GDI) into a bitmap in memory, then copies it into the memory the headset copy reads from.
// the panel outside the window is a "key" colour that becomes fully transparent, so only the window itself is seen. Where things are comes from SettingsMenu.Layout.
// Cost - CPU: low (only while the F10 window is open and something changed; GDI fills and text into a 1280x960 bitmap); GPU: none (all drawing is on the CPU; the caller uploads the bitmap, a small GPU cost there).
internal static unsafe class MenuDraw
{
    const uint Key = 0xFF00FF;                         // RA01: the transparent colour (magenta: no drawing colour below is this)
    // RA01: M20-13 - the night-city look (our own picks): near-black navy, neon cyan lines, electric yellow for whatever is chosen or pressed, deep blue for the rest.
    // RA01: Text on yellow is dark (AccentText) so it stays readable.
    static readonly uint Background = Rgb(10, 12, 24), Line = Rgb(0, 240, 255), LineDim = Rgb(0, 110, 130), Accent = Rgb(252, 238, 10), AccentDim = Rgb(20, 40, 90);
    static readonly uint Track = Rgb(25, 45, 70), Text = Rgb(225, 245, 250), TextDim = Rgb(120, 170, 185), Hover = Rgb(0, 70, 90), Edge = Rgb(252, 238, 10);
    static readonly uint TipBackground = Rgb(252, 238, 10), TipText = Rgb(10, 10, 10), ButtonColor = Rgb(20, 40, 90), AccentText = Rgb(10, 12, 24), CloseHover = Rgb(255, 40, 90);

    static nint dc, bitmap;                            // RA01: the drawing surface (a memory device context and its bitmap)
    static uint* pixels;                               // RA01: the bitmap's memory, PanelWidth x PanelHeight, one 0x00RRGGBB number per pixel
    static nint labelFont, tipFont, badgeFont;
    static int labelSize, tipSize, badgeSize;
    static readonly Dictionary<uint, nint> brushes = new(), pens = new(); // RA01: one brush and one pen per colour, made when first needed and kept

    // RA01: Cost - CPU: extremely low (three shifts and two ORs); GPU: none (no GPU work).
    static uint Rgb(int r, int g, int b) => (uint)(r | g << 8 | b << 16); // RA01: GDI's colour number

    // RA01: M29-1 - one painter at a time: the headset's thread and FlatMenu's never use the drawing surface together (lock it around Paint, PaintBanner and ToMonitor).
    public static readonly object Gate = new();
    static int paintCount, monitorCount = -1;             // RA01: M29-1 - window pictures painted, and the one last handed to the monitor
    static bool bannerSince;                              // RA01: M29-1 - a banner borrowed the surface's corner since the last window paint (wait for the next one)
    static readonly int[] pieces = new int[12];           // RA01: M29-1 - the last paint's window, tooltip and cursor boxes (left, top, right, bottom)
    static int pieceCount;

    // RA01: paints the window and writes it into 'destination' (PanelWidth x PanelHeight pixels, 4 bytes each, rows packed). 'bgra' = the headset image's byte order is blue, green, red, alpha;
    // otherwise red, green, blue, alpha. False = the drawing surface could not be made. M29-1 - 'destination' null = paint only (FlatMenu: ToMonitor takes it from there).
    // Cost - CPU: medium (only while F10 is open and NeedsDraw says something changed; GDI repaint plus a 1.2M pixel copy loop, about 1 to 3 ms per repaint); GPU: none (the caller uploads the 4.9 MB result, a small GPU cost).
    public static bool Paint(uint* destination, bool bgra)
    {
        if (dc == 0 && !Create()) return false;
        SettingsMenu.Layout();
        Rect panel = new() { Left = 0, Top = 0, Right = SettingsMenu.PanelWidth, Bottom = SettingsMenu.PanelHeight };
        FillRect(dc, &panel, Brush(Key));

        Rect window = SettingsMenu.Window;
        Fill(window, Background);
        pieceCount = 0;
        AddPiece(window.Left, window.Top, window.Right, window.Bottom);   // RA01: M29-1 - the window first (DrawTip and DrawCursor add theirs)
        int size = (int)MathF.Round(19 * SettingsMenu.Scale);
        SetFont(ref labelFont, ref labelSize, size, 600);
        DrawTitle(size);
        DrawTabs();
        DrawItems();
        DrawFooter();                                   // RA01: M65
        Frame(window, 3, Accent);                       // RA01: M20-13 - yellow outer edge, thin cyan line inside it
        Frame(new Rect { Left = window.Left + 3, Top = window.Top + 3, Right = window.Right - 3, Bottom = window.Bottom - 3 }, 1, Line);
        DrawEdges();
        DrawTip();
        DrawCursor();
        GdiFlush();
        paintCount++;
        bannerSince = false;
        if (destination == null) return true;

        int count = SettingsMenu.PanelWidth * SettingsMenu.PanelHeight;
        for (int i = 0; i < count; i++)
        {
            uint c = pixels[i] & 0xFFFFFF;
            if (c == Key) destination[i] = 0; // RA01: transparent
            else destination[i] = 0xFF000000 | (bgra ? c : (c & 0xFF) << 16 | (c & 0xFF00) | (c >> 16));
        }
        return true;
    }

    // RA01: M27-1 - THE BANNER (Core\Banner.cs): a big Cyberpunk-style message - yellow capitals on the window's near-black navy, a yellow edge and a thin cyan line
    // inside it, like the F10 window - painted into the top-left width x height of the same drawing surface and written into 'destination' (rows packed, opaque).
    // The F10 window repaints its whole surface every time, so borrowing the corner never shows in it. OpenXR thread only (the same thread as Paint).
    // Cost - CPU: low (one fill, two frames, a few text measures and one text draw, then a copy of width x height pixels - only when a new banner is shown); GPU: none.
    public static bool PaintBanner(uint* destination, int width, int height, string text, bool bgra)
    {
        if (dc == 0 && !Create()) return false;
        bannerSince = true;                                // RA01: M29-1 - the corner is the banner's now, not the window's (ToMonitor waits for the next window paint)
        Rect box = new() { Left = 0, Top = 0, Right = width, Bottom = height };
        Fill(box, Background);
        Frame(box, 6, Accent);
        Frame(new Rect { Left = 10, Top = 10, Right = width - 10, Bottom = height - 10 }, 2, Line);
        // RA01: M33-1 - Rob 12:09: long messages were cut off. The text is measured first: too wide, the font steps down (like a smaller stencil) to a fifth
        // RA01: of the banner's height; still too wide there, it wraps onto two lines. Short messages keep the full size, as before.
        int size = height * 2 / 5, smallest = height / 5, inner = width - 48;
        Rect measure;
        while (true)
        {
            SetFont(ref bannerFont, ref bannerSize, size, 800);
            measure = new Rect { Left = 0, Top = 0, Right = 0, Bottom = 0 };
            DrawTextW(dc, text, -1, &measure, 0x20 | 0x400);           // RA01: one line, measure only
            if (measure.Right <= inner || size <= smallest) break;
            size = Math.Max(smallest, size - 4);
        }
        if (measure.Right <= inner) Write(text, box, 1 | 4 | 0x20, Accent);
        else
        {
            Rect wrap = new() { Left = 24, Top = 0, Right = width - 24, Bottom = 0 };
            DrawTextW(dc, text, -1, &wrap, 0x10 | 0x400 | 1);           // RA01: wrapped, measure only: the height it needs
            int top = Math.Max(14, (height - wrap.Bottom) / 2);
            Write(text, new Rect { Left = 24, Top = top, Right = width - 24, Bottom = height - 14 }, 1 | 0x10, Accent);
        }
        GdiFlush();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                uint c = pixels[y * SettingsMenu.PanelWidth + x] & 0xFFFFFF;
                destination[y * width + x] = 0xFF000000 | (bgra ? c : (c & 0xFF) << 16 | (c & 0xFF00) | (c >> 16));
            }
        return true;
    }
    static nint bannerFont;
    static int bannerSize;

    // RA01: makes the drawing surface (a memory device context and a 1280x960 bitmap) the first time the window is painted. False = Windows would not make it.
    // Cost - CPU: low (once per session, one 4.9 MB bitmap and a few Windows calls); GPU: none (no GPU work).
    static bool Create()
    {
        dc = CreateCompatibleDC(0);
        BitmapInfoHeader info = new() { Size = 40, Width = SettingsMenu.PanelWidth, Height = -SettingsMenu.PanelHeight, Planes = 1, BitCount = 32 }; // RA01: a negative height = the top row first
        nint bits = 0;
        bitmap = dc == 0 ? 0 : CreateDIBSection(dc, &info, 0, &bits, 0, 0);
        if (dc == 0 || bitmap == 0) { Log.Write("RA01 menu: could not make the drawing surface (Windows error)"); dc = 0; return false; }
        SelectObject(dc, bitmap);
        SetBkMode(dc, 1); // RA01: TRANSPARENT: text is drawn without a background box
        pixels = (uint*)bits;
        return true;
    }

    // RA01: gets the brush for a colour; it is made the first time that colour is used and kept.
    // Cost - CPU: extremely low (one dictionary lookup; a brush is created only the first time a colour is seen); GPU: none (no GPU work).
    static nint Brush(uint colour)
    {
        if (!brushes.TryGetValue(colour, out nint brush)) brushes[colour] = brush = CreateSolidBrush(colour);
        return brush;
    }

    // RA01: gets the pen for a colour; it is made the first time that colour is used and kept.
    // Cost - CPU: extremely low (one dictionary lookup; a pen is created only the first time a colour is seen); GPU: none (no GPU work).
    static nint Pen(uint colour)
    {
        if (!pens.TryGetValue(colour, out nint pen)) pens[colour] = pen = CreatePen(0, 1, colour);
        return pen;
    }

    // RA01: fills a rectangle with one colour.
    // Cost - CPU: low (one GDI fill; the cost grows with the area, the whole window is up to 1.2M pixels but it is a fast memory fill, once per repaint); GPU: none (no GPU work).
    static void Fill(Rect r, uint colour) => FillRect(dc, &r, Brush(colour));

    // RA01: a hollow frame of the given thickness inside the rectangle.
    // Cost - CPU: extremely low (four thin GDI fills); GPU: none (no GPU work).
    static void Frame(Rect r, int thickness, uint colour)
    {
        Fill(new Rect { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Top + thickness }, colour);
        Fill(new Rect { Left = r.Left, Top = r.Bottom - thickness, Right = r.Right, Bottom = r.Bottom }, colour);
        Fill(new Rect { Left = r.Left, Top = r.Top, Right = r.Left + thickness, Bottom = r.Bottom }, colour);
        Fill(new Rect { Left = r.Right - thickness, Top = r.Top, Right = r.Right, Bottom = r.Bottom }, colour);
    }

    // RA01: makes sure the given font slot holds a Segoe UI font of the wanted size and weight and selects it; a new font is made only when the size has changed.
    // Cost - CPU: extremely low (normally just selects the kept font; creating a font (low) happens only when the window is resized); GPU: none (no GPU work).
    static void SetFont(ref nint font, ref int current, int wanted, int weight)
    {
        wanted = Math.Max(wanted, 8);
        if (font != 0 && current == wanted) { SelectObject(dc, font); return; }
        nint old = font;
        font = CreateFontW(-wanted, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI"); // RA01: 4 = antialiased. The text is always drawn on an opaque fill, never on the transparent colour.
        current = wanted;
        SelectObject(dc, font);
        if (old != 0) DeleteObject(old); // RA01: only after the new one is selected: a font that is still selected cannot be deleted
    }

    // RA01: text in a box. flags: 0 left, 1 centre, 2 right, +4 centred up and down, +0x20 one line, +0x10 wrapped.
    // Cost - CPU: low (one GDI text draw, antialiased, tens of microseconds; about 40 per repaint); GPU: none (no GPU work).
    static void Write(string text, Rect box, uint flags, uint colour)
    {
        SetTextColor(dc, colour);
        DrawTextW(dc, text, -1, &box, flags);
    }

    // RA01: M65 - the maker's stamp: Rob's Militech line (as on the Breach Protocol screen), small capitals in a light muted olive, centred in the fixed strip under
    // the buttons (SettingsMenu.Footer) - it stays put while the settings list scrolls, like a plate riveted to a machine's frame.
    // Cost - CPU: extremely low (one small text draw per repaint, only while the window is open); GPU: none (no GPU work).
    static void DrawFooter()
    {
        Rect box = SettingsMenu.Footer;
        SetFont(ref footFont, ref footSize, Math.Min(box.Bottom - box.Top - 2, (box.Right - box.Left) / 38), 600); // RA01: fits the strip's height and its width (55 capitals)
        Write("// SYSTEM ENHANCEMENT SECURED BY MILITECH DEFENSE CORP //", box, 1 | 4 | 0x20, Olive);
    }
    static nint footFont;
    static int footSize;
    static readonly uint Olive = Rgb(143, 163, 107);

    // RA01: highlights in orange the window edge or edges under the cursor (4 pixels wide), so it is clear what a drag would resize.
    // Cost - CPU: extremely low (at most four small fills, per repaint); GPU: none (no GPU work).
    static void DrawEdges()
    {
        Rect w = SettingsMenu.Window;
        int e = SettingsMenu.HoverEdges;
        if ((e & 1) != 0) Fill(new Rect { Left = w.Left, Top = w.Top, Right = w.Left + 4, Bottom = w.Bottom }, Edge);
        if ((e & 2) != 0) Fill(new Rect { Left = w.Right - 4, Top = w.Top, Right = w.Right, Bottom = w.Bottom }, Edge);
        if ((e & 4) != 0) Fill(new Rect { Left = w.Left, Top = w.Top, Right = w.Right, Bottom = w.Top + 4 }, Edge);
        if ((e & 8) != 0) Fill(new Rect { Left = w.Left, Top = w.Bottom - 4, Right = w.Right, Bottom = w.Bottom }, Edge);
    }

    // RA01: paints the title bar, its text and the close button (red when the cursor is over it).
    // Cost - CPU: low (two fills and two text draws, per repaint); GPU: none (no GPU work).
    static void DrawTitle(int size)
    {
        Rect bar = SettingsMenu.TitleBar;
        Fill(bar, AccentDim);                           // RA01: M20-13 - deep blue bar, yellow title
        int badgeRight = DrawBadge(bar);                // RA01: M20-13 - the RA01 badge at the bar's left end
        Rect inset = new() { Left = badgeRight + size / 2, Top = bar.Top, Right = bar.Right, Bottom = bar.Bottom };
        Write(Tuning.LeftHanded ? "RA01 settings  (F10 or A+RT+left stick left closes - left-hand mode)" : "RA01 settings  (F10 or X+LT+right stick left closes)", inset, 4 | 0x20, Accent); // RA01: M28-3 - the chord moves with left-hand mode
        Rect close = SettingsMenu.Hits[0].Box; // RA01: the first spot is always the close button
        bool over = SettingsMenu.Hover == 0;
        Fill(close, over ? CloseHover : AccentDim);
        Write("X", close, 1 | 4 | 0x20, Text);
    }

    // RA01: M20-13 - THE RA01 BADGE (our own design, no game artwork): a slanted plate with clipped corners - navy inside, yellow outline and a yellow stripe along
    // the top - with "RA01" in bold italic yellow over a cyan copy shifted a little left and down (a "glitch" glow), and a thin cyan underline ending in a
    // small yellow tab. Drawn from shapes each repaint (no image file), sized from the title bar, so it scales with the window. Returns its right edge.
    // Font: Windows' built-in Bahnschrift (Windows 10 and 11); Windows picks a close stand-in if it is missing.
    // Cost - CPU: low (three small polygons, two fills and two text draws per repaint, only while the F10 window is open and something changed); GPU: none.
    static int DrawBadge(Rect bar)
    {
        int h = bar.Bottom - bar.Top - 4, y = bar.Top + 2, x = bar.Left + 4;
        int w = h * 21 / 10, slant = h * 21 / 100, clip = h * 16 / 100, stripe = Math.Max(h * 8 / 100, 2);
        Point* p = stackalloc Point[5];
        p[0] = new Point { X = x + slant, Y = y }; p[1] = new Point { X = x + w, Y = y }; p[2] = new Point { X = x + w - slant, Y = y + h };
        p[3] = new Point { X = x + clip, Y = y + h }; p[4] = new Point { X = x, Y = y + h - clip };
        SelectObject(dc, Brush(Background));
        SelectObject(dc, Pen(Accent));
        Polygon(dc, p, 5);                                                       // RA01: the plate
        SelectObject(dc, Brush(Accent));
        p[0] = new Point { X = x + slant + 1, Y = y + 1 }; p[1] = new Point { X = x + w - 1, Y = y + 1 };
        p[2] = new Point { X = x + w - 1 - stripe / 3, Y = y + 1 + stripe }; p[3] = new Point { X = x + slant - stripe / 3, Y = y + 1 + stripe };
        Polygon(dc, p, 4);                                                       // RA01: the top stripe
        int font = Math.Max(h * 6 / 10, 8);
        if (badgeFont == 0 || badgeSize != font)
        {
            nint old = badgeFont;
            badgeFont = CreateFontW(-font, 0, 0, 0, 700, 1, 0, 0, 1, 0, 0, 4, 0, "Bahnschrift"); // RA01: bold italic, antialiased
            badgeSize = font;
            if (old != 0) DeleteObject(old);
        }
        nint previous = SelectObject(dc, badgeFont);
        int shift = Math.Max(h / 30, 1);
        Rect text = new() { Left = x + slant / 2, Top = y + stripe, Right = x + w - slant / 2, Bottom = y + h - stripe };
        Rect glow = new() { Left = text.Left - 2 * shift, Top = text.Top + shift, Right = text.Right - 2 * shift, Bottom = text.Bottom + shift };
        Write("RA01", glow, 1 | 4 | 0x20, Line);                                 // RA01: the cyan glitch copy, behind
        Write("RA01", text, 1 | 4 | 0x20, Accent);                               // RA01: the yellow letters
        SelectObject(dc, previous);
        int lineY = y + h - Math.Max(h * 12 / 100, 3);
        Fill(new Rect { Left = x + clip + 3, Top = lineY, Right = x + w - slant - h * 30 / 100, Bottom = lineY + Math.Max(h / 36, 1) }, Line); // RA01: the cyan underline
        SelectObject(dc, Brush(Accent));
        SelectObject(dc, Pen(Accent));
        int tabRight = x + w - slant - h * 6 / 100, tabLeft = tabRight - h * 20 / 100, tabTop = lineY - stripe / 2, tabBottom = lineY + stripe;
        p[0] = new Point { X = tabLeft + stripe, Y = tabTop }; p[1] = new Point { X = tabRight + stripe, Y = tabTop };
        p[2] = new Point { X = tabRight, Y = tabBottom }; p[3] = new Point { X = tabLeft, Y = tabBottom };
        Polygon(dc, p, 4);                                                       // RA01: the yellow tab at the underline's end
        return x + w;
    }

    // RA01: M6-1 - the two tab buttons under the title bar: filled solid (brighter under the cursor) for whichever tab is showing, just outlined for the other, so it always reads
    // as "one of these is pressed in" rather than two equal buttons.
    // Cost - CPU: low (up to two fills, a frame and a text draw each, per repaint); GPU: none (no GPU work).
    static void DrawTabs()
    {
        for (int h = 1; h < SettingsMenu.HitCount; h++)
        {
            SettingsMenu.Hit hit = SettingsMenu.Hits[h];
            if (hit.Kind != SettingsMenu.KindTab) continue;
            bool selected = SettingsMenu.Tab == hit.Item, over = SettingsMenu.Hover == h;
            Fill(hit.Box, selected ? Accent : (over ? Hover : ButtonColor));
            Frame(hit.Box, 1, selected ? Accent : LineDim);
            Write(SettingsMenu.TabNames[hit.Item], hit.Box, 1 | 4 | 0x20, selected ? AccentText : TextDim);
        }
    }

    // RA01: writes each visible setting's name (and its value for a slider), then draws every clickable spot on the current tab: radio choices, sliders, buttons and the scroll arrows.
    // M5a-14, M6-1 - only the VisibleRows items of the current tab currently scrolled into view are drawn, not every item on it, since Rows[] for the rest is stale.
    // Cost - CPU: low (VisibleRows rows and at most 40 spots, each a small fill or text draw, per repaint; the biggest part of a repaint after the pixel copy); GPU: none (no GPU work).
    static void DrawItems()
    {
        int tabStart = SettingsMenu.TabStart[SettingsMenu.Tab], tabCount = SettingsMenu.TabCount[SettingsMenu.Tab];
        for (int slot = 0; slot < SettingsMenu.VisibleRows && SettingsMenu.ScrollTop + slot < tabCount; slot++)
        {
            int i = tabStart + SettingsMenu.ScrollTop + slot;
            SettingsMenu.Item item = SettingsMenu.Items[i];
            if (item.Trigger != null) continue; // RA01: M6-6 - a full-row button draws its own centred label below, not this name/value line
            Rect row = SettingsMenu.Rows[i];
            Rect top = new() { Left = row.Left, Top = row.Top, Right = row.Right, Bottom = row.Top + (int)MathF.Round(24 * SettingsMenu.Scale) };
            Write(item.Name, top, 4 | 0x20, Text);
            if (item.Status != null) Write(item.Status(), top, 2 | 4 | 0x20, TextDim); // RA01: M6-6 - a label row's own live text, in place of a value
            else if (item.Options == null) Write(SettingsMenu.ValueText(i), top, 2 | 4 | 0x20, TextDim);
        }
        for (int h = 1; h < SettingsMenu.HitCount; h++)
        {
            SettingsMenu.Hit hit = SettingsMenu.Hits[h];
            bool over = SettingsMenu.Hover == h;
            if (hit.Kind == SettingsMenu.KindOption) DrawOption(hit, over);
            else if (hit.Kind == SettingsMenu.KindSlider) DrawSlider(hit, over);
            else if (hit.Kind == SettingsMenu.KindButton) DrawButton(hit, over);
            else if (hit.Kind == SettingsMenu.KindItemButton) DrawItemButton(hit, over); // RA01: M6-6
            else if (hit.Kind == SettingsMenu.KindScrollUp || hit.Kind == SettingsMenu.KindScrollDown) DrawScrollArrow(hit, over);
        }
    }

    // RA01: M5a-14 - one scroll arrow: a "^" or "v" glyph, dim and inert-looking when there is nowhere left to scroll that way (matches ScrollTop/MaxScroll, not drawn from a separate flag).
    // Cost - CPU: extremely low (one fill and one text draw, at most twice per repaint); GPU: none (no GPU work).
    static void DrawScrollArrow(SettingsMenu.Hit hit, bool over)
    {
        bool canGo = hit.Kind == SettingsMenu.KindScrollUp ? SettingsMenu.ScrollTop > 0 : SettingsMenu.ScrollTop < SettingsMenu.MaxScroll;
        if (over && canGo) Fill(hit.Box, Hover);
        Write(hit.Kind == SettingsMenu.KindScrollUp ? "^" : "v", hit.Box, 1 | 4 | 0x20, canGo ? Text : TextDim);
    }

    // RA01: a radio button: a ring that is filled when this choice is the current one, and the choice's name beside it.
    // Cost - CPU: low (one ellipse and one text draw, called about 30 times per repaint); GPU: none (no GPU work).
    static void DrawOption(SettingsMenu.Hit hit, bool over)
    {
        SettingsMenu.Item item = SettingsMenu.Items[hit.Item];
        bool chosen = (int)MathF.Round(item.Get()) == hit.Value;
        if (over) Fill(hit.Box, Hover);
        int r = (int)MathF.Round(8 * SettingsMenu.Scale), cx = hit.Box.Left + r + 4, cy = (hit.Box.Top + hit.Box.Bottom) / 2;
        SelectObject(dc, Brush(chosen ? Accent : Background));
        SelectObject(dc, Pen(chosen ? Text : Line));
        Ellipse(dc, cx - r, cy - r, cx + r, cy + r);
        Rect label = new() { Left = cx + r + 6, Top = hit.Box.Top, Right = hit.Box.Right, Bottom = hit.Box.Bottom };
        Write(item.Options![hit.Value], label, 4 | 0x20, chosen ? Accent : TextDim); // RA01: M20-13 - the chosen option reads yellow
    }

    // RA01: a slider: a track, the part of it up to the value in the accent colour, and a knob.
    // Cost - CPU: extremely low (three small fills and some float math, per slider per repaint); GPU: none (no GPU work).
    static void DrawSlider(SettingsMenu.Hit hit, bool over)
    {
        SettingsMenu.Item item = SettingsMenu.Items[hit.Item];
        float s = SettingsMenu.Scale;
        int half = (int)MathF.Round(8 * s), cy = (hit.Box.Top + hit.Box.Bottom) / 2, thick = Math.Max(2, (int)MathF.Round(3 * s));
        float t = Math.Clamp((item.Get() - item.Min) / (item.Max - item.Min), 0f, 1f);
        int left = hit.Box.Left + half, right = hit.Box.Right - half, knob = left + (int)MathF.Round(t * (right - left));
        Fill(new Rect { Left = left, Top = cy - thick, Right = right, Bottom = cy + thick }, Track);
        Fill(new Rect { Left = left, Top = cy - thick, Right = knob, Bottom = cy + thick }, Accent);
        int tall = (int)MathF.Round(11 * s);
        Fill(new Rect { Left = knob - half, Top = cy - tall, Right = knob + half, Bottom = cy + tall }, over ? Text : Rgb(210, 216, 228));
    }

    // RA01: a push button: a filled box (brighter under the cursor), a thin frame and the centred label.
    // Cost - CPU: low (two fills, a frame and one text draw, three buttons per repaint); GPU: none (no GPU work).
    static void DrawButton(SettingsMenu.Hit hit, bool over)
    {
        Fill(hit.Box, over ? Accent : ButtonColor);
        Frame(hit.Box, 1, Line);
        Write(SettingsMenu.ButtonNames[hit.Item], hit.Box, 1 | 4 | 0x20, over ? AccentText : Text);
    }

    // RA01: M6-6 - a full-row push button (Item.Trigger, not the shared bottom row's KindButton): same look as DrawButton, but its label is the ITEM's own name, not ButtonNames.
    // Cost - CPU: low (two fills, a frame and one text draw, at most a couple per repaint); GPU: none (no GPU work).
    static void DrawItemButton(SettingsMenu.Hit hit, bool over)
    {
        Fill(hit.Box, over ? Accent : ButtonColor);
        Frame(hit.Box, 1, Line);
        Write(SettingsMenu.Items[hit.Item].Name, hit.Box, 1 | 4 | 0x20, over ? AccentText : Text);
    }

    // RA01: the tooltip: a light box next to the cursor with the words for whatever is under it, kept inside the panel.
    // Cost - CPU: low (only while the cursor is on something; measures and draws one wrapped text block, tens of microseconds); GPU: none (no GPU work).
    static void DrawTip()
    {
        string tip = SettingsMenu.Tip();
        if (tip.Length == 0) return;
        SetFont(ref tipFont, ref tipSize, 19, 400);
        int width = Math.Min(360, SettingsMenu.PanelWidth - 16);
        Rect measure = new() { Left = 0, Top = 0, Right = width - 20, Bottom = 0 };
        DrawTextW(dc, tip, -1, &measure, 0x10 | 0x400); // RA01: 0x400 = only measure (the height it needs), 0x10 = wrap the words
        int height = measure.Bottom + 16;
        int x = (int)SettingsMenu.CursorX + 22, y = (int)SettingsMenu.CursorY + 26;
        if (x + width > SettingsMenu.PanelWidth - 4) x = SettingsMenu.PanelWidth - 4 - width;
        if (y + height > SettingsMenu.PanelHeight - 4) y = (int)SettingsMenu.CursorY - 8 - height;
        x = Math.Max(x, 4);
        y = Math.Max(y, 4);
        Rect box = new() { Left = x, Top = y, Right = x + width, Bottom = y + height };
        AddPiece(x, y, x + width, y + height);             // RA01: M29-1
        Fill(box, TipBackground);
        Frame(box, 2, Line);
        Rect inner = new() { Left = x + 10, Top = y + 8, Right = x + width - 10, Bottom = y + height - 8 };
        Write(tip, inner, 0x10, TipText);
    }

    // RA01: the mouse cursor: a white arrow with a black edge, always the same size on the panel.
    // Cost - CPU: extremely low (one seven-point polygon, per repaint); GPU: none (no GPU work).
    static void DrawCursor()
    {
        int x = (int)SettingsMenu.CursorX, y = (int)SettingsMenu.CursorY;
        Point* arrow = stackalloc Point[7];
        int[] shape = { 0, 0, 0, 17, 4, 13, 8, 20, 11, 19, 7, 12, 12, 12 };
        for (int i = 0; i < 7; i++) arrow[i] = new Point { X = x + shape[2 * i], Y = y + shape[2 * i + 1] };
        SelectObject(dc, Brush(Rgb(255, 255, 255)));
        SelectObject(dc, Pen(Rgb(0, 0, 0)));
        Polygon(dc, arrow, 7);
        AddPiece(x, y, x + 13, y + 22);                    // RA01: M29-1
    }

    // RA01: M29-1 - notes a box for the monitor (clipped to the panel). A tooltip or cursor box wholly inside the window adds nothing (the window carries it).
    // Cost - CPU: extremely low (a few compares); GPU: none.
    static void AddPiece(int l, int t, int r, int b)
    {
        l = Math.Max(l, 0); t = Math.Max(t, 0); r = Math.Min(r, SettingsMenu.PanelWidth); b = Math.Min(b, SettingsMenu.PanelHeight);
        if (r <= l || b <= t || pieceCount >= 3) return;
        if (pieceCount > 0 && l >= pieces[0] && t >= pieces[1] && r <= pieces[2] && b <= pieces[3]) return;
        pieces[pieceCount * 4] = l; pieces[pieceCount * 4 + 1] = t; pieces[pieceCount * 4 + 2] = r; pieces[pieceCount * 4 + 3] = b;
        pieceCount++;
    }

    // RA01: M29-1 - THE MONITOR'S COPY: when a newer window picture was painted than the monitor shows, its pieces (window, and tooltip / cursor sticking out) are
    // converted from the drawing surface into a free monitor buffer (FrameCopy.BeginMonitorMenu) and handed over - only the pieces, so it is quick. The
    // transparent key colour becomes black there (only inside a tooltip's or cursor's corner). Waits (does nothing) while a banner holds the surface's corner,
    // or both buffers are busy - tried again next call. Call with Gate held, on the painting thread, after Paint.
    // Cost - CPU: low (only after a repaint: converts the window's pixels, about 0.5 MB-2 MB, well under a millisecond to a millisecond); GPU: none.
    public static void ToMonitor()
    {
        if (paintCount == monitorCount || bannerSince || pixels == null) return;
        uint* destination = (uint*)FrameCopy.BeginMonitorMenu();
        if (destination == null) return;
        bool bgra = FrameCopy.Bgra;
        int stride = SettingsMenu.PanelWidth;
        for (int k = 0; k < pieceCount; k++)
        {
            int l = pieces[k * 4], t = pieces[k * 4 + 1], r = pieces[k * 4 + 2], b = pieces[k * 4 + 3];
            for (int y = t; y < b; y++)
                for (int x = l; x < r; x++)
                {
                    int i = y * stride + x;
                    uint c = pixels[i] & 0xFFFFFF;
                    destination[i] = c == Key ? 0xFF000000 : 0xFF000000 | (bgra ? c : (c & 0xFF) << 16 | (c & 0xFF00) | (c >> 16));
                }
        }
        FrameCopy.EndMonitorMenu(pieces, pieceCount);
        monitorCount = paintCount;
    }
}
