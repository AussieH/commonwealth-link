using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CommonwealthLink;

public static unsafe class Tray
{
    const uint WM_DESTROY = 0x0002, WM_TIMER = 0x0113, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_TRAY = 0x8001;
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    const uint MF_STRING = 0, MF_GRAYED = 1, MF_CHECKED = 8, MF_SEPARATOR = 0x800, TPM_RIGHTBUTTON = 2, TPM_RETURNCMD = 0x100;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "Commonwealth Link";

    [StructLayout(LayoutKind.Sequential)]
    struct NOTIFYICONDATAW
    {
        public uint cbSize; public IntPtr hWnd; public uint uID; public uint uFlags; public uint uCallbackMessage; public IntPtr hIcon;
        public fixed char szTip[128]; public uint dwState; public uint dwStateMask; public fixed char szInfo[256]; public uint uVersion;
        public fixed char szInfoTitle[64]; public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct WNDCLASSEXW
    {
        public uint cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance;
        public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground; public IntPtr lpszMenuName; public IntPtr lpszClassName; public IntPtr hIconSm;
    }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x; public int y; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x; public int y; }

    [DllImport("user32.dll")] static extern ushort RegisterClassExW(WNDCLASSEXW* wc);
    [DllImport("user32.dll")] static extern IntPtr CreateWindowExW(uint ex, IntPtr cls, IntPtr name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetMessageW(MSG* m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] static extern int TranslateMessage(MSG* m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(MSG* m);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] static extern int AppendMenuW(IntPtr menu, uint flags, nuint id, char* text);
    [DllImport("user32.dll")] static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr h, IntPtr rect);
    [DllImport("user32.dll")] static extern int DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern int GetCursorPos(POINT* p);
    [DllImport("user32.dll")] static extern int SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern nuint SetTimer(IntPtr h, nuint id, uint ms, IntPtr fn);
    [DllImport("user32.dll")] static extern int DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr LoadIconW(IntPtr inst, IntPtr name);
    [DllImport("shell32.dll")] static extern int Shell_NotifyIconW(uint msg, NOTIFYICONDATAW* d);
    [DllImport("shell32.dll")] static extern uint ExtractIconExW(char* file, int index, IntPtr* large, IntPtr* small, uint n);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandleW(IntPtr name);

    static IntPtr hwnd, icon;

    public static void Run()
    {
        var cls = Marshal.StringToHGlobalUni("CommonwealthLinkTray");
        var wc = new WNDCLASSEXW { cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc, hInstance = GetModuleHandleW(IntPtr.Zero), lpszClassName = cls };
        RegisterClassExW(&wc);
        hwnd = CreateWindowExW(0, cls, IntPtr.Zero, 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        var exe = Environment.ProcessPath ?? "";
        IntPtr small = IntPtr.Zero;
        fixed (char* p = exe) ExtractIconExW(p, 0, null, &small, 1);
        icon = small != IntPtr.Zero ? small : LoadIconW(IntPtr.Zero, (IntPtr)32512);
        var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        Shell_NotifyIconW(NIM_ADD, &d);
        SetTimer(hwnd, 1, 1000, IntPtr.Zero);
        MSG m;
        while (GetMessageW(&m, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(&m); DispatchMessageW(&m); }
        var gone = Data(0);
        Shell_NotifyIconW(NIM_DELETE, &gone);
    }

    static string Status() { lock (Engine.Gate) return Engine.StatusLine(); }

    static NOTIFYICONDATAW Data(uint flags)
    {
        var d = new NOTIFYICONDATAW { cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = hwnd, uID = 1, uFlags = flags, uCallbackMessage = WM_TRAY, hIcon = icon };
        var tip = $"Commonwealth Link: {Status()}";
        if (tip.Length > 127) tip = tip[..127];
        for (int i = 0; i < tip.Length; i++) d.szTip[i] = tip[i];
        return d;
    }

    static bool StartsWithWindows()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(RunName) is string; } catch (Exception) { return false; }
    }
    static void SetStartWithWindows(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue(RunName, $"\"{Environment.ProcessPath}\""); else k.DeleteValue(RunName, false);
        }
        catch (Exception) { }
    }

    static void Add(IntPtr menu, uint flags, nuint id, string text) { fixed (char* t = text) AppendMenuW(menu, flags, id, t); }

    static void Menu()
    {
        var menu = CreatePopupMenu();
        Add(menu, MF_STRING | MF_GRAYED, 1, $"Commonwealth Link: {Status()}");
        Add(menu, MF_SEPARATOR, 0, "");
        Add(menu, MF_STRING | (Settings.TouchControls ? MF_CHECKED : 0), 5, "Touch controls on the widget");
        Add(menu, MF_STRING | (Settings.MapPicture ? MF_CHECKED : 0), 6, "Map picture from your game");
        Add(menu, MF_STRING | (Settings.ShowName ? MF_CHECKED : 0), 7, "Show the character's name");
        Add(menu, MF_SEPARATOR, 0, "");
        Add(menu, MF_STRING, 2, "Open the folder");
        Add(menu, MF_STRING | (StartsWithWindows() ? MF_CHECKED : 0), 3, "Start with Windows");
        Add(menu, MF_SEPARATOR, 0, "");
        Add(menu, MF_STRING, 4, "Quit");
        POINT pt; GetCursorPos(&pt);
        SetForegroundWindow(hwnd);
        int cmd = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.x, pt.y, 0, hwnd, IntPtr.Zero);
        DestroyMenu(menu);
        if (cmd == 2) { try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Program.Dir}\"") { UseShellExecute = false }); } catch (Exception) { } }
        if (cmd == 3) SetStartWithWindows(!StartsWithWindows());
        if (cmd is 5 or 6 or 7)
        {
            if (cmd == 5) Settings.TouchControls = !Settings.TouchControls;
            if (cmd == 6) Settings.MapPicture = !Settings.MapPicture;
            if (cmd == 7) Settings.ShowName = !Settings.ShowName;
            Settings.Save();
            Engine.SettingsChanged();
        }
        if (cmd == 4) { Program.Quit = true; DestroyWindow(hwnd); }
    }

    [UnmanagedCallersOnly]
    static IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (m == WM_TRAY && ((uint)l == WM_RBUTTONUP || (uint)l == WM_LBUTTONUP)) { Menu(); return IntPtr.Zero; }
        if (m == WM_TIMER) { var d = Data(NIF_TIP); Shell_NotifyIconW(NIM_MODIFY, &d); return IntPtr.Zero; }
        if (m == WM_DESTROY) { PostQuitMessage(0); return IntPtr.Zero; }
        return DefWindowProcW(h, m, w, l);
    }
}
