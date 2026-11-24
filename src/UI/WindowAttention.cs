using System;
using System.Runtime.InteropServices;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace Sts2Matchmaker.UI;

/// <summary>
/// "매칭 성사" window alert - flashes the taskbar icon (Windows' own orange flash) and pulls the game window to
/// the front, so a player who alt-tabbed away while waiting notices the match.
///
/// Flashing goes through Godot's DisplayServer.WindowRequestAttention (FlashWindowEx on Windows, keeps flashing
/// until the window gets focus). Bringing to front is done with Win32 directly: Godot's WindowMoveToForeground is
/// a bare SetForegroundWindow, which Windows' foreground lock silently refuses for a background process, and it
/// doesn't un-minimize. Attaching to the current foreground thread's input is the standard way past that lock. If
/// Windows still refuses, the taskbar flash is the fallback - nothing breaks.
/// </summary>
public static class WindowAttention
{
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    public static void Request()
    {
        try
        {
            DisplayServer.WindowRequestAttention();
            if (OperatingSystem.IsWindows())
                BringToFrontWindows();
        }
        catch (Exception e)
        {
            Log.Error($"[sts2_matchmaker] Window attention request failed: {e}");
        }
    }

    private static void BringToFrontWindows()
    {
        var hWnd = (IntPtr)DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle);
        if (hWnd == IntPtr.Zero)
            return;

        if (IsIconic(hWnd))
            ShowWindow(hWnd, SW_RESTORE);

        IntPtr foreground = GetForegroundWindow();
        if (foreground == hWnd)
            return;

        uint fgThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, IntPtr.Zero);
        uint ourThread = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != ourThread && AttachThreadInput(ourThread, fgThread, true);
        try
        {
            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached)
                AttachThreadInput(ourThread, fgThread, false);
        }
    }
}
