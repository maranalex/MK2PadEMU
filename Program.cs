// M&K2PadEMU engine: mouse+keyboard -> virtual DualShock 4 (ViGEmBus). chiaki-ng reads it as a real pad.
// F8 = toggle capture, F9 = quit. While captured, mouse+keyboard input is swallowed (never reaches Windows).
// Settings, bindings and macros live in config.json next to the exe, saved on every change.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using Keys = System.Windows.Forms.Keys;

enum Act { Up, Down, Left, Right, R2, L2, L1, R1, Cross, Circle, Square, Triangle, L3, R3, Options, Create, DUp, DDown, DLeft, DRight, Slow, Sprint }

// One custom combination: pressing the keyboard chord presses all Buttons at once.
class Macro
{
    public List<string> Buttons { get; set; } = new();   // Act names
    public List<string> Chord { get; set; } = new();     // Keys names; all must be held
    public List<List<string>> Extra { get; set; } = new(); // more keys/combos for the same macro (any one triggers it)
}

// Everything in the file: all profiles, all complex profiles, and which are active.
class Store
{
    public int Active { get; set; }                     // index of the profile in use
    public int ActiveSet { get; set; } = -1;            // index of the enabled complex profile, -1 = none
    public List<Cfg> Profiles { get; set; } = new();
    public List<Combo> Sets { get; set; } = new();
}

// Complex profile: no bindings of its own, only "this hotkey switches to that profile".
class Combo
{
    public string Id { get; set; } = "";                // stable id; built-in ones match defaults.json
    public string Name { get; set; } = "Switcher";
    public List<SwitchEntry> Entries { get; set; } = new();
}
class SwitchEntry
{
    public string Profile { get; set; } = "";           // Cfg.Id
    public List<string> Hotkey { get; set; } = new();   // Keys names; all must be held
}

// One profile = a complete, independent setup.
class Cfg
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Default";
    public double SensX { get; set; } = 0.045;   // stick per mouse count per tick (125 Hz)
    public double SensY { get; set; } = 0.045;
    public double Dead { get; set; } = 0.15;     // game inner deadzone to jump over
    public double SmoothMs { get; set; } = 10;   // aim smoothing time constant, 0 = off
    public bool InvertY { get; set; }
    public bool SwapSticks { get; set; } = true;    // true: mouse aims with the LEFT stick, WASD moves with the RIGHT stick
    public string MoveMode { get; set; } = "Full";  // Full | Tiers | Ramp  (left-stick style)
    public double NormalPct { get; set; } = 70;     // Tiers mode: stick % with no modifier held
    public double SlowPct { get; set; } = 30;       //             stick % while the Slow key is held
    public string SprintButton { get; set; } = "Cross"; // PS button (Act name) held while the Sprint key is held; works in every movement style
    public double RampMs { get; set; } = 150;       // Ramp mode: time from first tilt to full tilt
    // Act name -> keyboard chord (Keys names; LButton/RButton/MButton/XButton1/XButton2 = mouse)
    public Dictionary<string, List<string>> Binds { get; set; } = new()
    {
        ["Up"] = new() { "W" }, ["Down"] = new() { "S" }, ["Left"] = new() { "A" }, ["Right"] = new() { "D" },
        ["R2"] = new() { "LButton" }, ["L2"] = new() { "RButton" }, ["L1"] = new() { "Q" }, ["R1"] = new() { "E" },
        ["Cross"] = new() { "ShiftKey" }, ["Circle"] = new() { "C" }, ["Square"] = new() { "Space" }, ["Triangle"] = new() { "F" },
        ["L3"] = new() { "ControlKey" }, ["R3"] = new() { "V" }, ["Options"] = new() { "Tab" }, ["Create"] = new(), ["Slow"] = new() { "Menu" }, ["Sprint"] = new() { "ShiftKey" },
        ["DUp"] = new() { "Up" }, ["DDown"] = new() { "Down" }, ["DLeft"] = new() { "Left" }, ["DRight"] = new() { "Right" },
    };
    // Extra keys/combos for the same pad input (any one triggers it). Kept separate from Binds so old configs still load.
    public Dictionary<string, List<List<string>>> Extra { get; set; } = new();
    public List<Macro> Macros { get; set; } = new();
}

static class P
{
    const int TickMs = 8;
    static readonly string CfgPath = Path.Combine(AppContext.BaseDirectory, "config.json");
    static readonly int ActCount = Enum.GetValues<Act>().Length;

    [DllImport("user32")] static extern short GetAsyncKeyState(int k);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern ushort RegisterClassW(ref WNDCLASS c);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32")] static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32")] static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32")] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint size);
    [DllImport("user32")] static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint header);
    [DllImport("user32")] static extern uint SendInput(uint n, INPUT[] i, int size);
    [DllImport("user32")] static extern IntPtr SetWindowsHookEx(int id, Hook cb, IntPtr mod, uint tid);
    [DllImport("user32")] static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
    [DllImport("user32")] static extern int GetMessage(out MSG m, IntPtr w, uint a, uint b);
    delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASS { public uint style; public WndProc proc; public int clsExtra, wndExtra; public IntPtr inst, icon, cursor, bg; public string menu, name; }
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort page, usage; public uint flags; public IntPtr target; }
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTHEADER { public uint type, size; public IntPtr device, wParam; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public MOUSEINPUT mi; }
    [DllImport("kernel32")] static extern IntPtr GetModuleHandle(string n);
    [DllImport("winmm")] static extern uint timeBeginPeriod(uint ms);
    delegate IntPtr Hook(int n, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr h; public uint m; public IntPtr w, l; public uint t; public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct LL { public int x, y; public uint data, flags, time; public IntPtr extra; }

    internal static Store store = new() { Profiles = { new Cfg() } };
    internal static Cfg cfg => store.Profiles[Math.Clamp(store.Active, 0, store.Profiles.Count - 1)]; // active profile
    internal static volatile bool on;
    internal static Action<bool> onToggle = _ => { };
    internal static Action onProfile = () => { };
    static readonly object saveLock = new();

    // Compiled bindings, swapped atomically so the pad loop never sees a half-edited config.
    class Run { public int[][][] simple; public int[][][] mChord; public int[][] mActs; public int[][] pHot; public int[] pTarget; public int sprintAct = -1; }   // simple[act] = alternative chords; pHot[i] switches to profile pTarget[i]
    static volatile Run run = new() { simple = new int[0][][], mChord = new int[0][][], mActs = new int[0][], pHot = new int[0][], pTarget = new int[0] };

    static int dx, dy;   // raw mouse counts since the last pad tick
    static readonly bool[] mouse = new bool[7];   // VK_LBUTTON 1, RBUTTON 2, MBUTTON 4, XBUTTON1 5, XBUTTON2 6
    static readonly bool[] keys = new bool[256];  // swallowed keys never reach GetAsyncKeyState, so track here
    static Hook mouseRef, kbRef;                  // keep delegates alive

    // Mouse counts -> stick bytes (128 = centre). Works on the movement VECTOR, not per axis:
    // the deadzone offset is added along the direction of travel and the result is capped at the
    // stick circle, so the direction of the mouse movement is kept exactly (a straight vertical
    // swipe with a 1-count sideways wobble stays almost vertical instead of curving).
    public static (byte X, byte Y) Aim(double dx, double dy, double sx, double sy, double dead)
    {
        double vx = dx * sx, vy = dy * sy, m = Math.Sqrt(vx * vx + vy * vy);
        if (m == 0) return (128, 128);
        double k = Math.Min(1, m + dead) / m;
        return (Conv(vx * k), Conv(vy * k));
    }
    // Exponential smoothing with time constant tau (ms). Total movement is conserved, only spread in time.
    public static double Ema(double prev, double x, double dtMs, double tauMs) => prev + (1 - Math.Exp(-dtMs / tauMs)) * (x - prev);

    static byte Conv(double v) => (byte)Math.Round(128 + Math.Clamp(v, -1, 1) * 127);

    static int[] Vks(IEnumerable<string> names) =>
        names.Select(n => Enum.TryParse<Keys>(n, out var k) ? (int)k : 0).Where(v => v > 0).ToArray();

    internal static void Rebuild()
    {
        var r = new Run { simple = new int[ActCount][][] };
        foreach (Act a in Enum.GetValues<Act>())
        {
            var chords = new List<List<string>>();
            if (cfg.Binds.TryGetValue(a.ToString(), out var l)) chords.Add(l);
            if (cfg.Extra.TryGetValue(a.ToString(), out var ex)) chords.AddRange(ex);
            r.simple[(int)a] = chords.Select(Vks).Where(v => v.Length > 0).ToArray();
        }
        var hot = new List<int[]>(); var target = new List<int>();
        if (store.ActiveSet >= 0 && store.ActiveSet < store.Sets.Count)
            foreach (var e in store.Sets[store.ActiveSet].Entries)
            {
                int idx = store.Profiles.FindIndex(p => p.Id == e.Profile); var v = Vks(e.Hotkey);
                if (idx >= 0 && v.Length > 0) { hot.Add(v); target.Add(idx); }
            }
        r.pHot = hot.ToArray(); r.pTarget = target.ToArray();
        r.sprintAct = Enum.TryParse<Act>(cfg.SprintButton, out var sb) && sb != Act.Sprint && sb != Act.Slow ? (int)sb : -1;
        r.mChord = cfg.Macros.Select(m => new[] { m.Chord }.Concat(m.Extra ?? new()).Select(Vks).Where(v => v.Length > 0).ToArray()).ToArray();
        r.mActs = cfg.Macros.Select(m => m.Buttons
            .Select(b => Enum.TryParse<Act>(b, out var x) ? (int)x : -1).Where(i => i >= 0).ToArray()).ToArray();
        run = r;
    }

    internal static void Save()
    {
        lock (saveLock)
        {
            try { File.WriteAllText(CfgPath, JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception e) { Debug.WriteLine(e); }
        }
    }

    // Selecting a regular profile turns any orchestrator off.
    internal static void SelectProfile(int i)
    {
        if (i < 0 || i >= store.Profiles.Count) return;
        store.ActiveSet = -1; store.Active = i; Rebuild(); onProfile();
        ThreadPool.QueueUserWorkItem(_ => Save());
    }

    // Selecting a complex profile enables it, starting on the first profile in its list.
    internal static void SelectSet(int i)
    {
        if (i < 0 || i >= store.Sets.Count) return;
        store.ActiveSet = i;
        int first = store.Sets[i].Entries.Select(e => store.Profiles.FindIndex(p => p.Id == e.Profile)).FirstOrDefault(x => x >= 0, -1);
        if (first >= 0) store.Active = first;
        Rebuild(); onProfile();
        ThreadPool.QueueUserWorkItem(_ => Save());
    }

    internal static void Switch(int i)
    {
        if (i < 0 || i >= store.Profiles.Count || i == store.Active) return;
        store.Active = i; Rebuild(); onProfile();
        ThreadPool.QueueUserWorkItem(_ => Save()); // keep the pad loop from waiting on disk
    }

    internal static void SetOn(bool v)
    {
        on = v;
        Interlocked.Exchange(ref dx, 0); Interlocked.Exchange(ref dy, 0); // drop counts from before capture
        Array.Clear(mouse); onToggle(v);
    }

    static IntPtr OnMouse(int n, IntPtr w, IntPtr l)
    {
        if (n < 0 || !on) return CallNextHookEx(IntPtr.Zero, n, w, l);
        var s = Marshal.PtrToStructure<LL>(l);
        if ((s.flags & 1) != 0) return CallNextHookEx(IntPtr.Zero, n, w, l); // injected by other software: leave alone
        int x = (int)(s.data >> 16) == 2 ? 6 : 5;
        switch ((int)w)
        {
            // Moves are swallowed (cursor stays put); the movement itself comes from Raw Input below.
            case 0x201: mouse[1] = true; break;  case 0x202: mouse[1] = false; break;
            case 0x204: mouse[2] = true; break;  case 0x205: mouse[2] = false; break;
            case 0x207: mouse[4] = true; break;  case 0x208: mouse[4] = false; break;
            case 0x20B: mouse[x] = true; break;  case 0x20C: mouse[x] = false; break;
        }
        return (IntPtr)1;
    }

    static IntPtr OnKey(int n, IntPtr w, IntPtr l)
    {
        if (n < 0) return CallNextHookEx(IntPtr.Zero, n, w, l);
        int code = Marshal.ReadInt32(l), flags = Marshal.ReadInt32(l, 8);
        if ((flags & 0x10) != 0) return CallNextHookEx(IntPtr.Zero, n, w, l); // injected
        if (code > 0 && code < 256) keys[code] = (int)w == 0x100 || (int)w == 0x104;
        // F8/F9 (toggle/quit) and Win stay usable as escape hatches. Alt is swallowed so it can be bound (F8 releases capture).
        bool pass = code == 0x77 || code == 0x78 || code == 0x79 || code == 0x5B || code == 0x5C;
        return on && !pass ? (IntPtr)1 : CallNextHookEx(IntPtr.Zero, n, w, l);
    }

    // Raw Input: the mouse's own counts, before Windows pointer acceleration and integer cursor rounding.
    static readonly IntPtr rawBuf = Marshal.AllocHGlobal(256);
    static WndProc rawProc;   // keep alive
    static IntPtr RawProc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (m == 0x00FF && on) // WM_INPUT
        {
            uint size = 256, hs = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
            if (GetRawInputData(l, 0x10000003, rawBuf, ref size, hs) != uint.MaxValue && Marshal.ReadInt32(rawBuf) == 0 /* RIM_TYPEMOUSE */)
            {
                int o = (int)hs;                                   // RAWMOUSE: flags@0, buttons@4, rawButtons@8, lastX@12, lastY@16
                if ((Marshal.ReadInt16(rawBuf, o) & 1) == 0)       // relative motion only
                {
                    Interlocked.Add(ref dx, Marshal.ReadInt32(rawBuf, o + 12));
                    Interlocked.Add(ref dy, Marshal.ReadInt32(rawBuf, o + 16));
                }
            }
        }
        return DefWindowProcW(h, m, w, l);
    }

    // Own thread + message loop so UI work never delays input hooks or raw input.
    static void HookThread()
    {
        mouseRef = OnMouse; kbRef = OnKey; rawProc = RawProc;
        SetWindowsHookEx(14, mouseRef, GetModuleHandle(null), 0);
        SetWindowsHookEx(13, kbRef, GetModuleHandle(null), 0);

        var wc = new WNDCLASS { proc = rawProc, inst = GetModuleHandle(null), name = "KbmPadRaw" };
        RegisterClassW(ref wc);
        var hwnd = CreateWindowExW(0, "KbmPadRaw", "", 0, 0, 0, 0, 0, (IntPtr)(-3) /* HWND_MESSAGE */, IntPtr.Zero, wc.inst, IntPtr.Zero);
        var dev = new[] { new RAWINPUTDEVICE { page = 1, usage = 2, flags = 0x100 /* INPUTSINK: also when not focused */, target = hwnd } };
        RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) DispatchMessageW(ref msg);
    }

    static bool Held(int v) => v switch
    {
        <= 6 => mouse[v],
        0x10 => keys[0xA0] || keys[0xA1],
        0x11 => keys[0xA2] || keys[0xA3],
        0x12 => keys[0xA4] || keys[0xA5],
        _ => keys[v],
    };
    const double StartTilt = 0.35; // Ramp mode: first tilt on a fresh press (a walking pace)

    // Move cur toward target t (-1/0/1): 0 resets, a direction flip restarts the ramp.
    public static double Ramp(double cur, double t, double step)
    {
        if (t == 0) return 0;
        if (Math.Sign(cur) != Math.Sign(t)) cur = Math.Sign(t) * StartTilt;
        return Math.Sign(t) * Math.Min(1, Math.Abs(cur) + step);
    }
    static byte Stick(double v) => (byte)Math.Round(128 + Math.Clamp(v, -1, 1) * (v < 0 ? 128 : 127));

    static bool ChordDown(int[] c) { if (c.Length == 0) return false; foreach (var v in c) if (!Held(v)) return false; return true; }
    static bool Raw(int k) => (GetAsyncKeyState(k) & 0x8000) != 0;

    static void Loop(IDualShock4Controller pad)
    {
        bool f8 = false;
        var st = new bool[ActCount];
        bool D(Act x) => st[(int)x];
        double rx = 0, ry = 0; // ramp state
        var hot = new bool[0]; // profile-hotkey edge state
        double lastMs = 0;     // time of the previous tick
        double smx = 0, smy = 0; // smoothed mouse counts
        var sw = Stopwatch.StartNew(); long next = 0;
        while (true)
        {
            if (Raw(0x79)) Environment.Exit(0);
            bool t = Raw(0x77);
            if (t && !f8) SetOn(!on);
            f8 = t;

            bool a = on; var c = cfg; var r = run;
            if (hot.Length != r.pHot.Length) hot = new bool[r.pHot.Length];
            for (int i = 0; i < hot.Length; i++)
            {
                bool d = a && ChordDown(r.pHot[i]);
                if (d && !hot[i]) { hot[i] = true; Switch(r.pTarget[i]); } else if (!d) hot[i] = false;
            }
            Array.Clear(st);
            if (a)
            {
                for (int i = 0; i < r.simple.Length; i++) foreach (var ch in r.simple[i]) if (ChordDown(ch)) { st[i] = true; break; }
                for (int m = 0; m < r.mChord.Length; m++)
                    foreach (var ch in r.mChord[m])
                        if (ChordDown(ch)) { foreach (var b in r.mActs[m]) st[b] = true; break; }
            }
            if (a && st[(int)Act.Sprint] && r.sprintAct >= 0) st[r.sprintAct] = true; // sprint key holds the chosen PS button
            int mx = Interlocked.Exchange(ref dx, 0), my = Interlocked.Exchange(ref dy, 0);
            if (c.InvertY) my = -my;
            // Counts are per real elapsed time, so normalise to a nominal 8 ms tick (ticks can run a little long or short).
            double nowMs = sw.Elapsed.TotalMilliseconds, dtMs = Math.Max(1.0, nowMs - lastMs), tscale = Math.Clamp(TickMs / dtMs, 0.5, 2.0); lastMs = nowMs;
            double ax = mx * tscale, ay = my * tscale;
            if (!a) smx = smy = 0;
            else if (c.SmoothMs > 0)
            {   // spread each tick's counts over a few ticks: same total turn, no 0/1/2-count pulsing
                smx = Ema(smx, ax, dtMs, c.SmoothMs); smy = Ema(smy, ay, dtMs, c.SmoothMs);
                if (Math.Abs(smx) + Math.Abs(smy) < 0.1) smx = smy = 0;   // end the tail so the deadzone offset can't keep the camera drifting
                ax = smx; ay = smy;
            }
            var (aimX, aimY) = a ? Aim(ax, ay, c.SensX, c.SensY, c.Dead) : ((byte)128, (byte)128);
            double tx = (D(Act.Right) ? 1 : 0) - (D(Act.Left) ? 1 : 0), ty = (D(Act.Down) ? 1 : 0) - (D(Act.Up) ? 1 : 0);
            double lx = tx, ly = ty;
            if (c.MoveMode == "Tiers")
            {
                double k = (D(Act.Slow) ? c.SlowPct : c.NormalPct) / 100;
                lx *= k; ly *= k;
            }
            else if (c.MoveMode == "Ramp")
            {
                double step = (1 - StartTilt) * TickMs / Math.Max(1, c.RampMs);
                rx = Ramp(rx, tx, step); ry = Ramp(ry, ty, step); lx = rx; ly = ry;
            }
            else { rx = ry = 0; }
            byte movX = Stick(lx), movY = Stick(ly);
            bool swapped = c.SwapSticks;
            pad.SetAxisValue(DualShock4Axis.LeftThumbX, swapped ? aimX : movX);
            pad.SetAxisValue(DualShock4Axis.LeftThumbY, swapped ? aimY : movY);
            pad.SetAxisValue(DualShock4Axis.RightThumbX, swapped ? movX : aimX);
            pad.SetAxisValue(DualShock4Axis.RightThumbY, swapped ? movY : aimY);
            pad.SetSliderValue(DualShock4Slider.RightTrigger, (byte)(D(Act.R2) ? 255 : 0));
            pad.SetSliderValue(DualShock4Slider.LeftTrigger, (byte)(D(Act.L2) ? 255 : 0));
            pad.SetButtonState(DualShock4Button.Cross, D(Act.Cross));
            pad.SetButtonState(DualShock4Button.Circle, D(Act.Circle));
            pad.SetButtonState(DualShock4Button.Square, D(Act.Square));
            pad.SetButtonState(DualShock4Button.Triangle, D(Act.Triangle));
            pad.SetButtonState(DualShock4Button.ShoulderLeft, D(Act.L1));
            pad.SetButtonState(DualShock4Button.ShoulderRight, D(Act.R1));
            pad.SetButtonState(DualShock4Button.ThumbLeft, D(Act.L3));
            pad.SetButtonState(DualShock4Button.ThumbRight, D(Act.R3));
            pad.SetButtonState(DualShock4Button.Options, D(Act.Options));
            pad.SetButtonState(DualShock4Button.Share, D(Act.Create)); // PS5 Create = DS4 Share
            pad.SetDPadDirection(D(Act.DUp) ? DualShock4DPadDirection.North : D(Act.DDown) ? DualShock4DPadDirection.South
                : D(Act.DLeft) ? DualShock4DPadDirection.West : D(Act.DRight) ? DualShock4DPadDirection.East
                : DualShock4DPadDirection.None);
            pad.SubmitReport();

            next += TickMs; long wait = next - sw.ElapsedMilliseconds;
            if (wait > 0) Thread.Sleep((int)wait); else next = sw.ElapsedMilliseconds;
        }
    }

    // Sensible standard setup: mouse aims on the right stick, WASD moves on the left, speed keys on.
    internal static Cfg BlankProfile(string name = "Blank") => new() { Name = name, SwapSticks = false, MoveMode = "Tiers" };

    // ---- Built-in profiles (defaults.json, embedded at build time) ----
    // They are the out-of-the-box setup and the "original" that Revert goes back to.
    // Matching is by Id, so a copy (new Id) or a user's own profile is never treated as built-in.
    static Store originals;
    internal static Store Originals => originals ??= LoadOriginals();

    static Store LoadOriginals()
    {
        try
        {
            using var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("defaults.json");
            var o = s == null ? null : JsonSerializer.Deserialize<Store>(new StreamReader(s).ReadToEnd());
            if (o != null && o.Profiles != null && o.Profiles.Count > 0)
            {
                o.Sets ??= new();
                foreach (var set in o.Sets) set.Entries ??= new();
                return o;
            }
        }
        catch { }
        return new Store { Profiles = { BlankProfile("Default") } };
    }

    static T Clone<T>(T x) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(x));

    internal static bool IsBuiltin(Cfg p) => Originals.Profiles.Any(o => o.Id == p.Id);
    internal static bool IsBuiltin(Combo s) => s.Id != "" && Originals.Sets.Any(o => o.Id == s.Id);

    internal static void RevertProfile(Cfg p)
    {
        var o = Originals.Profiles.FirstOrDefault(x => x.Id == p.Id); int i = store.Profiles.IndexOf(p);
        if (o == null || i < 0) return;
        store.Profiles[i] = Clone(o); Rebuild(); Save();
    }

    internal static void RevertSet(Combo s)
    {
        var o = Originals.Sets.FirstOrDefault(x => x.Id == s.Id); int i = store.Sets.IndexOf(s);
        if (o == null || i < 0) return;
        store.Sets[i] = Clone(o); Rebuild(); Save();
    }

    // Built-ins the user has deleted.
    internal static List<string> MissingBuiltins() =>
        Originals.Profiles.Where(o => store.Profiles.All(p => p.Id != o.Id)).Select(o => o.Name)
            .Concat(Originals.Sets.Where(o => store.Sets.All(s => s.Id != o.Id)).Select(o => o.Name)).ToList();

    internal static void RestoreMissing()
    {
        foreach (var o in Originals.Profiles.Where(o => store.Profiles.All(p => p.Id != o.Id)).ToList()) store.Profiles.Add(Clone(o));
        foreach (var o in Originals.Sets.Where(o => store.Sets.All(s => s.Id != o.Id)).ToList()) store.Sets.Add(Clone(o));
        Rebuild(); Save();
    }

    // Old single-profile files (settings at the root) load as an "Imported" profile.
    static Store LoadStore()
    {
        Store s = null;
        try
        {
            if (File.Exists(CfgPath))
            {
                var txt = File.ReadAllText(CfgPath);
                s = JsonSerializer.Deserialize<Store>(txt);
                if (s == null || s.Profiles == null || s.Profiles.Count == 0)
                {
                    var old = JsonSerializer.Deserialize<Cfg>(txt) ?? new Cfg();
                    old.Name = "Imported";
                    s = new Store { Profiles = { old } };
                }
            }
        }
        catch
        {
            s = null;
            try { File.Copy(CfgPath, CfgPath + ".bak", true); } catch { } // never silently lose an unreadable file
        }
        s ??= Clone(Originals);   // fresh install: the built-in profiles
        for (int i = 0; i < s.Profiles.Count; i++)
        {
            var p = s.Profiles[i];
            p.Extra ??= new(); p.Macros ??= new();
            foreach (var mac in p.Macros) { mac.Chord ??= new(); mac.Extra ??= new(); mac.Buttons ??= new(); }
            if (string.IsNullOrEmpty(p.Id) || s.Profiles.Take(i).Any(o => o.Id == p.Id)) p.Id = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(p.Name)) p.Name = i == 0 ? "Default" : "Profile " + (i + 1);
            foreach (var kv in new Cfg().Binds) p.Binds.TryAdd(kv.Key, kv.Value); // fill missing actions
        }
        s.Active = Math.Clamp(s.Active, 0, s.Profiles.Count - 1);
        s.Sets ??= new();
        foreach (var set in s.Sets)
        {
            set.Entries ??= new();
            if (string.IsNullOrEmpty(set.Id))   // older files: a set named like a built-in one takes its id
            {
                var m = Originals.Sets.FirstOrDefault(o => o.Name == set.Name && s.Sets.All(x => x.Id != o.Id));
                set.Id = m?.Id ?? Guid.NewGuid().ToString("N");
            }
        }
        if (s.ActiveSet < 0 || s.ActiveSet >= s.Sets.Count) s.ActiveSet = -1;
        return s;
    }

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 1 && args[0] == "--rawtest")
        {   // Injects 100 x (0,+3) moves and reports the raw counts the Raw Input path saw; expect about "0 300".
            new Thread(HookThread) { IsBackground = true }.Start(); Thread.Sleep(800);
            on = true;
            var inp = new[] { new INPUT { type = 0, mi = new MOUSEINPUT { dy = 3, flags = 1 /* MOVE */ } } };
            for (int i = 0; i < 100; i++) { SendInput(1, inp, Marshal.SizeOf<INPUT>()); Thread.Sleep(3); }
            Thread.Sleep(300); File.WriteAllText(args[1], $"{dx} {dy}"); return;
        }
        if (args.Length > 1 && args[0] == "--masstest")
        {   // how far glyphs/icons sit from the middle of their box, before and after centring by mass
            File.WriteAllText(args[1], $"before: {MainWindow.MassSelfTest(false)}\nafter:  {MainWindow.MassSelfTest(true)}\nsilhouette mass vs box centre (DIPs): {MainWindow.ShapeReport()}");
            return;
        }
        if (args.Length > 0 && args[0] == "--test")
        {
            const double S = 0.045, D = 0.15;
            var mass = MainWindow.MassSelfTest(true);   // every glyph and icon must sit on its box centre by mass
            if (double.Parse(mass.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture) > 0.15) throw new Exception("Glyph not centred by mass: " + mass);
            if (Aim(0, 0, S, S, D) != ((byte)128, (byte)128)) throw new Exception("Aim centre broken");
            var big = Aim(1000, 0, S, S, D); var neg = Aim(-1000, 0, S, S, D);
            if (big.X != 255 || big.Y != 128 || neg.X != 1) throw new Exception("Aim saturation broken");
            var diag = Aim(5000, 5000, S, S, D);   // capped at the circle: no stronger than a straight push
            if (Math.Sqrt(Math.Pow(diag.X - 128, 2) + Math.Pow(diag.Y - 128, 2)) > 127.6) throw new Exception("Aim not circle-capped");
            double ysum = 0, y = 0;   // smoothing must conserve the total turn: one 10-count burst, then silence
            for (int i = 0; i < 200; i++) { y = Ema(y, i == 0 ? 10 : 0, 8, 10); if (Math.Abs(y) < 0.1 / 2 && i > 0) y = 0; ysum += y; }
            if (Math.Abs(ysum - 10) > 0.5) throw new Exception($"Smoothing lost movement: {ysum}");
            // The regression: a near-vertical swipe (1 sideways count per 10 vertical) must stay near-vertical.
            var vert = Aim(1, 10, S, S, D); double ratio = (vert.X - 128.0) / (vert.Y - 128.0);
            if (Math.Abs(ratio - 0.1) > 0.02) throw new Exception($"Aim direction not preserved: {ratio}");
            if (Math.Abs(Ramp(0, 1, 0.1) - 0.45) > 1e-9 || Ramp(0.9, 1, 0.2) != 1 || Ramp(0.8, 0, 0.1) != 0 || Math.Abs(Ramp(0.8, -1, 0.1) + 0.45) > 1e-9 || Stick(-1) != 0 || Stick(1) != 255 || Stick(0) != 128)
                throw new Exception("Ramp/Stick broken");
            var m = new Cfg { Macros = { new Macro { Buttons = { "L3", "R3" }, Chord = { "B" } } } };
            m.Extra["Circle"] = new() { new() { "ControlKey" } };
            m.Macros[0].Extra.Add(new() { "G" });
            var back = JsonSerializer.Deserialize<Cfg>(JsonSerializer.Serialize(m));
            if (back.Extra["Circle"][0][0] != "ControlKey") throw new Exception("Extra round-trip broken");
            if (back.Macros[0].Extra.Count != 1 || back.Macros[0].Extra[0][0] != "G" || back.Macros[0].Chord[0] != "B") throw new Exception("Macro keys round-trip broken");
            var orig = Originals;   // built-ins must exist, have unique ids, and every switch entry must point at one of them
            if (orig.Profiles.Count != 2 || orig.Sets.Count != 1) throw new Exception("defaults.json wrong shape");
            if (orig.Profiles.Select(p => p.Id).Distinct().Count() != 2 || orig.Sets[0].Id == "") throw new Exception("built-in ids broken");
            if (!orig.Sets[0].Entries.All(e => orig.Profiles.Any(p => p.Id == e.Profile))) throw new Exception("built-in switcher points at a missing profile");
            var cl = Clone(orig.Profiles[0]); cl.SensX = 9;
            if (orig.Profiles[0].SensX == 9 || cl.Id != orig.Profiles[0].Id) throw new Exception("Clone is not independent");
            // revert / restore (these write config.json beside the exe; the test run is done in the build folder only)
            store = Clone(orig); store.Profiles[0].SensX = 0.5; store.Profiles[0].Name = "messed"; store.Profiles.RemoveAt(1); store.Sets[0].Entries.Clear();
            if (MissingBuiltins().Count != 1) throw new Exception("MissingBuiltins wrong");
            RevertProfile(store.Profiles[0]); RevertSet(store.Sets[0]);
            if (store.Profiles[0].SensX == 0.5 || store.Profiles[0].Name != orig.Profiles[0].Name || store.Sets[0].Entries.Count != orig.Sets[0].Entries.Count) throw new Exception("Revert did not restore");
            RestoreMissing();
            if (store.Profiles.Count != 2 || MissingBuiltins().Count != 0) throw new Exception("RestoreMissing wrong");
            var own = Clone(store.Profiles[0]); own.Id = "user-made";
            if (IsBuiltin(own) || !IsBuiltin(store.Profiles[0])) throw new Exception("IsBuiltin wrong");
            var pb = new Cfg { Name = "B" };
            var st2 = new Store { Active = 1, ActiveSet = 0, Profiles = { new Cfg { Name = "A" }, pb },
                Sets = { new Combo { Name = "S", Entries = { new SwitchEntry { Profile = pb.Id, Hotkey = { "F1" } } } } } };
            var sb = JsonSerializer.Deserialize<Store>(JsonSerializer.Serialize(st2));
            if (sb.Active != 1 || sb.ActiveSet != 0 || sb.Profiles.Count != 2 || sb.Sets[0].Entries[0].Profile != pb.Id || sb.Sets[0].Entries[0].Hotkey[0] != "F1")
                throw new Exception("Store round-trip broken");
            if (JsonSerializer.Deserialize<Store>("{\"SensX\":0.1,\"Binds\":{}}").Profiles.Count != 0) throw new Exception("Old-format detection broken");
            if (back.Macros.Count != 1 || back.Macros[0].Buttons.Count != 2 || back.Binds["Up"][0] != "W")
                throw new Exception("Config round-trip broken");
            return;
        }
        store = LoadStore();
        Rebuild();

        timeBeginPeriod(1);
        IDualShock4Controller pad;
        try
        {
            pad = new ViGEmClient().CreateDualShock4Controller();
            pad.AutoSubmitReport = false;
            pad.Connect();
        }
        catch (Exception e)
        {
            System.Windows.MessageBox.Show("ViGEmBus driver not available. Install/repair it and reboot.\n\n" + e.Message, "M&K2PadEMU");
            return;
        }
        new Thread(HookThread) { IsBackground = true }.Start();
        new Thread(() => Loop(pad)) { IsBackground = true }.Start();
        new System.Windows.Application().Run(new MainWindow());
    }
}
