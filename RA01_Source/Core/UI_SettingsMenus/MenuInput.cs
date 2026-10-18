using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RA01.Core;

// RA01: M5a-3, M5a-4 - the mouse for the F10 window. While the window is open the game must not see the mouse (it would turn the camera and shoot), and the window needs to know how far the mouse moved.
// this is done by putting our own function in front of the game window's message handler: while the window is open it swallows the mouse messages and keeps the raw movement for SettingsMenu.
// while the window is closed it passes every message straight on, so the game never notices it. (The mouse button is read like a key, see Keys.MouseLeft.)
// Cost - CPU: extremely low (closed: one bool check per window message; open: a small raw-input read per mouse event, at most a few hundred a second); GPU: none (no GPU work).
internal static unsafe class MenuInput
{
    const int WindowProcedure = -4;      // RA01: GWLP_WNDPROC
    const uint RawInputMessage = 0x00FF; // RA01: WM_INPUT
    const uint FirstMouseMessage = 0x0200, LastMouseMessage = 0x020E; // RA01: WM_MOUSEMOVE to WM_MOUSEHWHEEL

    static nint window, previous;        // RA01: the game window, and the message handler that was there before ours
    static int capturing;                // RA01: 1 while the window is open
    static int pendingX, pendingY;       // RA01: raw mouse movement since it was last taken
    static int rawEvents;                // RA01: raw mouse events seen since the window opened (for the log)
    static long openedAt;                // RA01: Stopwatch time the window opened
    static bool warned;                  // RA01: the "no mouse movement arrived" line has been written for this opening

    // RA01: from now on the mouse belongs to the window. False = it could not be taken (no game window, or Windows refused), the reason is in the log.
    // Cost - CPU: extremely low (once each time F10 opens the window; a few Windows calls (the message handler is put in only the first time)); GPU: none (no GPU work).
    public static bool Start(nint gameWindow)
    {
        if (window == 0)
        {
            if (gameWindow == 0 || !Win32.IsWindow(gameWindow)) { Log.Write("RA01 menu: no game window to take the mouse from, the window can be seen but not clicked"); return false; }
            nint before = Win32.GetWindowLongPtrW(gameWindow, WindowProcedure);
            if (before == 0) { Log.Write("RA01 menu: could not read the game window's message handler, the window can be seen but not clicked"); return false; }
            previous = before; // RA01: set before ours is put in, so a message that arrives at once already has somewhere to go
            nint replaced = Win32.SetWindowLongPtrW(gameWindow, WindowProcedure, (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&Handle);
            if (replaced == 0) { Log.Write("RA01 menu: Windows would not let the mouse be taken over, the window can be seen but not clicked"); return false; }
            previous = replaced;
            window = gameWindow;
        }
        Interlocked.Exchange(ref pendingX, 0);
        Interlocked.Exchange(ref pendingY, 0);
        Interlocked.Exchange(ref rawEvents, 0);
        openedAt = Stopwatch.GetTimestamp();
        warned = false;
        Volatile.Write(ref capturing, 1);
        return true;
    }

    // RA01: the mouse goes back to the game (our function stays in place and does nothing).
    // Cost - CPU: extremely low (once each time the window closes; one log line); GPU: none (no GPU work).
    public static void Stop()
    {
        if (Interlocked.Exchange(ref capturing, 0) == 1)
            Log.Write($"RA01 menu: the mouse is back with the game; {Volatile.Read(ref rawEvents)} raw mouse movements were seen while the window was open (0 would mean the game reads its mouse another way: tell me)");
    }

    // RA01: how far the mouse moved (raw counts) since the last call. If the window has been open for 10 seconds and not one raw movement has come, that is said once: the game may read its mouse another way.
    // Cost - CPU: extremely low (once per headset frame while the window is open; two atomic swaps and one time check); GPU: none (no GPU work).
    public static void TakeMoves(out int dx, out int dy)
    {
        dx = Interlocked.Exchange(ref pendingX, 0);
        dy = Interlocked.Exchange(ref pendingY, 0);
        if (!warned && Volatile.Read(ref rawEvents) == 0 && Stopwatch.GetTimestamp() - openedAt > 10 * Stopwatch.Frequency)
        {
            warned = true;
            Log.Write("RA01 menu: 10 seconds after opening, no raw mouse movement has arrived. If you have been moving the mouse, the game reads its mouse another way: tell me (the keys still work)");
        }
    }

    // RA01: at shutdown: if ours is still the first in line the game's own handler is put back; if something was put in front of ours later, ours stays and only passes on.
    // Cost - CPU: extremely low (once at shutdown; two Windows calls); GPU: none (no GPU work).
    public static void Uninstall()
    {
        Volatile.Write(ref capturing, 0);
        if (window == 0) return;
        if (Win32.GetWindowLongPtrW(window, WindowProcedure) == (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&Handle) Win32.SetWindowLongPtrW(window, WindowProcedure, previous);
        window = 0;
    }

    // RA01: the game window's message handler, runs on the game's own thread. Everything not meant for the window is passed on to the game's handler.
    // Cost - CPU: extremely low (runs for every message of the game window, hundreds a second: closed = one bool check then pass on; open = a few compares, and a raw-input read for mouse events); GPU: none (no GPU work).
    [UnmanagedCallersOnly]
    static nint Handle(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (Volatile.Read(ref capturing) == 1)
        {
            try
            {
                if (message == RawInputMessage && Read(lParam)) return Win32.DefWindowProcW(hwnd, message, wParam, lParam); // RA01: Windows wants this call so it can free the raw input
                if (message >= FirstMouseMessage && message <= LastMouseMessage) return 0;
            }
            catch { } // RA01: nothing may go wrong inside the game's message loop; the message just goes on
        }
        return Win32.CallWindowProcW(previous, hwnd, message, wParam, lParam);
    }

    // RA01: a WM_INPUT message. True = it is the mouse's and belongs to the window (a relative movement is added up; an absolute one, from a tablet or remote desktop, is dropped).
    // False = anything else (a keyboard, a gamepad, or a message that cannot be read): that must still reach the game. RAWINPUT: a 24 byte header (its first number is 0 for a mouse), then RAWMOUSE:
    // flags (2 bytes) at 24, and the movement in x and y at 36 and 40.
    // Cost - CPU: extremely low (one small Windows call and three atomic adds per raw mouse event, about a microsecond); GPU: none (no GPU work).
    static bool Read(nint handle)
    {
        byte* data = stackalloc byte[64];
        uint size = 64;
        if (Win32.GetRawInputData(handle, 0x10000003, data, &size, 24) == uint.MaxValue) return false; // RA01: 0x10000003 = RID_INPUT
        if (*(uint*)data != 0) return false;
        if ((*(ushort*)(data + 24) & 1) != 0) return true;
        Interlocked.Add(ref pendingX, *(int*)(data + 36));
        Interlocked.Add(ref pendingY, *(int*)(data + 40));
        Interlocked.Increment(ref rawEvents);
        return true;
    }
}
