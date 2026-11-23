using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Keys = System.Windows.Forms.Keys;

public partial class MainWindow : Window
{
    [DllImport("dwmapi")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
    [DllImport("dwmapi")] static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref Margins m);
    [StructLayout(LayoutKind.Sequential)] struct Margins { public int l, r, t, b; }

    // name, glyph (Segoe UI Symbol), colour
    static readonly Dictionary<Act, (string Name, string Glyph, string Col)> M = new()
    {
        [Act.Up] = ("Move Up", "↑", "#2EE6D6"), [Act.Down] = ("Move Down", "↓", "#2EE6D6"),
        [Act.Left] = ("Move Left", "←", "#2EE6D6"), [Act.Right] = ("Move Right", "→", "#2EE6D6"),
        [Act.R2] = ("R2", "R2", "#FFFFFF"), [Act.L2] = ("L2", "L2", "#FFFFFF"),
        [Act.R1] = ("R1", "R1", "#FFFFFF"), [Act.L1] = ("L1", "L1", "#FFFFFF"),
        [Act.Cross] = ("Cross", "✕", "#6FA8FF"), [Act.Circle] = ("Circle", "○", "#FF6B7A"),
        [Act.Square] = ("Square", "□", "#F08AD8"), [Act.Triangle] = ("Triangle", "△", "#3EE0A0"),
        [Act.L3] = ("L3 (stick click)", "L3", "#FFB347"), [Act.R3] = ("R3 (stick click)", "R3", "#FFB347"),
        [Act.Slow] = ("Slow walk modifier", "½", "#2EE6D6"), [Act.Sprint] = ("Sprint modifier", "»", "#2EE6D6"),
        [Act.Options] = ("Options", "≡", "#FFFFFF"), [Act.Create] = ("Create", "", "#FFFFFF"),
        [Act.DUp] = ("D-pad Up", "▲", "#C9B6FF"), [Act.DDown] = ("D-pad Down", "▼", "#C9B6FF"),
        [Act.DLeft] = ("D-pad Left", "◀", "#C9B6FF"), [Act.DRight] = ("D-pad Right", "▶", "#C9B6FF"),
    };
    static readonly (string Title, Act[] Acts)[] Groups =
    {
        ("MOVEMENT", new[] { Act.Up, Act.Down, Act.Left, Act.Right, Act.Slow, Act.Sprint }),
        ("TRIGGERS & BUMPERS", new[] { Act.R2, Act.L2, Act.R1, Act.L1 }),
        ("FACE BUTTONS", new[] { Act.Cross, Act.Circle, Act.Square, Act.Triangle }),
        ("STICKS & MENU", new[] { Act.L3, Act.R3, Act.Options, Act.Create }),
        ("D-PAD", new[] { Act.DUp, Act.DDown, Act.DLeft, Act.DRight }),
    };

    static SolidColorBrush Br(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    Style S(string key) => (Style)FindResource(key);

    public MainWindow()
    {
        InitializeComponent();
        // Handlers read P.cfg each time, so they follow whichever profile is active.
        Bind(SX, SXv, P.cfg.SensX, v => P.cfg.SensX = v);
        Bind(SY, SYv, P.cfg.SensY, v => P.cfg.SensY = v);
        Bind(DZ, DZv, P.cfg.Dead, v => P.cfg.Dead = v);
        Bind(SM, SMv, P.cfg.SmoothMs, v => P.cfg.SmoothMs = v, "0");
        InvertBtn.Click += (_, _) => { P.cfg.InvertY = !P.cfg.InvertY; ShowInvert(); P.Save(); };
        ShowInvert();
        SwapBtn.Click += (_, _) => { P.cfg.SwapSticks = !P.cfg.SwapSticks; ShowSwap(); P.Save(); };
        ShowSwap();

        ToggleBtn.Click += (_, _) => P.SetOn(!P.on);
        P.onToggle = v => Dispatcher.BeginInvoke(() => Status(v));
        TabSimple.Checked += (_, _) => Tab(true);
        TabMacro.Checked += (_, _) => Tab(false);
        NewMacro.Click += (_, _) =>
        {
            P.cfg.Macros.Add(new Macro { Buttons = { "L3", "R3" } });
            Commit(); BuildMacros();
        };
        P.onProfile = () => Dispatcher.BeginInvoke(RefreshAll);
        BuildSimple(); BuildMacros(); BuildProfiles();

        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp += (_, e) => { if (lBtn != null) { e.Handled = true; if (lPressed.Count > 0) Finish(); } };
        PreviewMouseDown += (_, _) => { if (lBtn != null && !lBtn.IsMouseOver) Cancel(); }; // click elsewhere cancels binding
        SourceInitialized += (_, _) => Glass();        Status(false);
    }

    // ---------- Profiles ----------
    // One selection covers both kinds: a regular profile (full settings) or a complex profile
    // (an orchestrator that only maps hotkeys to regular profiles). Never both at once.
    TextBox nameBox, setNameBox;

    // Re-read everything from the active profile (after a switch, create, copy or delete).
    void RefreshAll()
    {
        var c = P.cfg;
        SX.Value = c.SensX; SY.Value = c.SensY; DZ.Value = c.Dead; SM.Value = c.SmoothMs;
        ShowInvert(); ShowSwap();
        BuildSimple(); BuildMacros(); BuildProfiles();
    }

    void AddProfile(Cfg p)
    {
        P.store.Profiles.Add(p); P.store.Active = P.store.Profiles.Count - 1; P.store.ActiveSet = -1;
        Commit(); RefreshAll();
        FocusName(() => nameBox);
    }

    void FocusName(Func<TextBox> box) =>
        Dispatcher.BeginInvoke(() => { var b = box(); b?.Focus(); b?.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);

    string FreeName(string baseName)
    {
        var names = P.store.Profiles.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName)) return baseName;
        for (int i = 2; ; i++) if (!names.Contains($"{baseName} {i}")) return $"{baseName} {i}";
    }

    string FreeSetName(string baseName)
    {
        var names = P.store.Sets.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName)) return baseName;
        for (int i = 2; ; i++) if (!names.Contains($"{baseName} {i}")) return $"{baseName} {i}";
    }

    void BuildProfiles()
    {
        ProfileBar.Children.Clear();
        var store = P.store;
        bool orch = store.ActiveSet >= 0 && store.ActiveSet < store.Sets.Count;
        // An orchestrator has no bindings of its own, so the regular settings are hidden while it is selected.
        MouseCard.Visibility = TabsCard.Visibility = ContentPanel.Visibility = orch ? Visibility.Collapsed : Visibility.Visible;

        var chips = new WrapPanel();
        for (int i = 0; i < store.Profiles.Count; i++)
        {
            int idx = i;
            var r = new RadioButton { Style = S("TabBtn"), GroupName = "profiles", Content = store.Profiles[i].Name, IsChecked = !orch && i == store.Active, Margin = new Thickness(0, 0, 6, 6) };
            r.Checked += (_, _) => P.SelectProfile(idx);   // raises onProfile -> RefreshAll
            chips.Children.Add(r);
        }
        for (int j = 0; j < store.Sets.Count; j++)
        {
            int idx = j;
            var r = new RadioButton { Style = S("TabBtn"), GroupName = "profiles", IsChecked = orch && j == store.ActiveSet, Margin = new Thickness(0, 0, 6, 6), ToolTip = "complex profile (orchestrator)" };
            r.Content = IconText("swap", store.Sets[j].Name);
            r.Checked += (_, _) => P.SelectSet(idx);
            chips.Children.Add(r);
        }
        var add = new Button { Style = S("AccentBtn"), Content = IconText("plus", "New"), Margin = new Thickness(0, 0, 6, 6), ToolTip = "new profile with the standard Blank setup" };
        add.Click += (_, _) => AddProfile(P.BlankProfile(FreeName("New profile")));
        chips.Children.Add(add);
        var addSet = new Button { Style = S("Pill"), Content = IconText("plus", "Complex"), Margin = new Thickness(0, 0, 6, 6), ToolTip = "new complex profile: hotkeys that switch between your profiles" };
        addSet.Click += (_, _) =>
        {
            store.Sets.Add(new Combo { Name = FreeSetName("Orchestrator") });
            P.SelectSet(store.Sets.Count - 1);   // selecting it enables it
            FocusName(() => setNameBox);
        };
        chips.Children.Add(addSet);
        var missing = P.MissingBuiltins();
        if (missing.Count > 0)   // a built-in was deleted: let the user bring it back
        {
            var rs = new Button { Style = S("Pill"), Content = IconText("undo", "Restore built-ins"), Margin = new Thickness(0, 0, 0, 6), ToolTip = "bring back deleted built-in profiles: " + string.Join(", ", missing) };
            rs.Click += (_, _) => { P.RestoreMissing(); RefreshAll(); };
            chips.Children.Add(rs);
        }
        ProfileBar.Children.Add(chips);

        if (orch) BuildOrchestrator(store.Sets[store.ActiveSet]);
        else BuildProfileActions();
    }

    // Name / copy / delete for the selected regular profile.
    void BuildProfileActions()
    {
        var store = P.store; var cur = P.cfg;
        var row = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
        nameBox = new TextBox { Style = S("ValueBox"), Width = 190, TextAlignment = TextAlignment.Left, Text = cur.Name, ToolTip = "profile name: type, then press Enter", Margin = new Thickness(0, 0, 10, 4), Padding = new Thickness(6, 0, 6, 0) };
        var nb = nameBox;
        void Rename()
        {
            var t = nb.Text.Trim();
            if (t.Length == 0 || t == cur.Name) { nb.Text = cur.Name; return; }
            cur.Name = FreeName(t); P.Save(); BuildProfiles();
        }
        nb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Rename(); Keyboard.ClearFocus(); } };
        nb.LostKeyboardFocus += (_, _) => { if (nb.Text.Trim() != cur.Name) Rename(); };
        nb.GotKeyboardFocus += (_, _) => nb.SelectAll();
        row.Children.Add(nb);

        var dup = new Button { Style = S("Pill"), Content = "Copy", Margin = new Thickness(0, 0, 8, 4), ToolTip = "copy this profile, then rename the copy" };
        dup.Click += (_, _) =>
        {
            var copy = System.Text.Json.JsonSerializer.Deserialize<Cfg>(System.Text.Json.JsonSerializer.Serialize(cur));
            copy.Name = FreeName(cur.Name + " copy"); copy.Id = Guid.NewGuid().ToString("N");
            AddProfile(copy);
        };
        row.Children.Add(dup);

        if (P.IsBuiltin(cur))   // only the built-in profiles can go back to their original form
        {
            var rev = new Button { Style = S("Pill"), Content = IconText("undo", "Revert to original"), Margin = new Thickness(0, 0, 8, 4), ToolTip = "discard your changes to this built-in profile" };
            rev.Click += (_, _) =>
            {
                if (System.Windows.MessageBox.Show($"Revert “{cur.Name}” to its original settings? Your changes to it will be lost.", "M&K2PadEMU", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                P.RevertProfile(cur); RefreshAll();
            };
            row.Children.Add(rev);
        }

        var del = new Button { Style = S("Pill"), Content = "Delete", Foreground = Br("#FF8FA8"), Margin = new Thickness(0, 0, 0, 4), IsEnabled = store.Profiles.Count > 1 };
        del.Click += (_, _) =>
        {
            if (System.Windows.MessageBox.Show($"Delete profile “{cur.Name}”? This cannot be undone.", "M&K2PadEMU", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            int at = store.Active; store.Profiles.RemoveAt(at); store.Active = Math.Max(0, at - 1);
            Commit(); RefreshAll();
        };
        row.Children.Add(del);
        ProfileBar.Children.Add(row);
    }

    void PickProfile(FrameworkElement target, Action<Cfg> chosen)
    {
        var pop = new Popup { PlacementTarget = target, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade };
        var sp = new StackPanel { MinWidth = 200 };
        foreach (var p in P.store.Profiles)
        {
            var b = new Button { Style = S("Pill"), Content = p.Name, Margin = new Thickness(3), HorizontalContentAlignment = HorizontalAlignment.Left };
            b.Click += (_, _) => { pop.IsOpen = false; chosen(p); };
            sp.Children.Add(b);
        }
        pop.Child = new Border
        {
            Background = Br("#F2221047"), BorderBrush = Br("#66FFFFFF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16),
            Padding = new Thickness(8), Margin = new Thickness(14),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Opacity = 0.5 },
            Child = sp,
        };
        pop.IsOpen = true;
    }

    // The orchestrator: only "hotkey -> profile" rows. Selecting it is what enables it.
    void BuildOrchestrator(Combo set)
    {
        var store = P.store;
        ProfileBar.Children.Add(new Border { Height = 1, Background = Br("#33FFFFFF"), Margin = new Thickness(0, 4, 0, 10) });

        var head = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        setNameBox = new TextBox { Style = S("ValueBox"), Width = 190, TextAlignment = TextAlignment.Left, Text = set.Name, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(0, 0, 10, 4), ToolTip = "name: type, then press Enter" };
        var nb = setNameBox;
        void Rename()
        {
            var t = nb.Text.Trim();
            if (t.Length == 0 || t == set.Name) { nb.Text = set.Name; return; }
            set.Name = FreeSetName(t); P.Save(); BuildProfiles();
        }
        nb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Rename(); Keyboard.ClearFocus(); } };
        nb.LostKeyboardFocus += (_, _) => { if (nb.Text.Trim() != set.Name) Rename(); };
        nb.GotKeyboardFocus += (_, _) => nb.SelectAll();
        head.Children.Add(nb);

        if (P.IsBuiltin(set))
        {
            var rev = new Button { Style = S("Pill"), Content = IconText("undo", "Revert to original"), Margin = new Thickness(0, 0, 8, 4), ToolTip = "discard your changes to this built-in complex profile" };
            rev.Click += (_, _) =>
            {
                if (System.Windows.MessageBox.Show($"Revert “{set.Name}” to its original settings? Your changes to it will be lost.", "M&K2PadEMU", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                P.RevertSet(set); RefreshAll();
            };
            head.Children.Add(rev);
        }

        var del = new Button { Style = S("Pill"), Content = "Delete", Foreground = Br("#FF8FA8"), Margin = new Thickness(0, 0, 0, 4) };
        del.Click += (_, _) =>
        {
            if (System.Windows.MessageBox.Show($"Delete complex profile “{set.Name}”? Your profiles are not affected.", "M&K2PadEMU", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            store.Sets.Remove(set); store.ActiveSet = -1;   // back to the regular profile that was running
            Commit(); RefreshAll();
        };
        head.Children.Add(del);
        ProfileBar.Children.Add(head);

        ProfileBar.Children.Add(new TextBlock
        {
            Text = "Running profile:  " + P.cfg.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 2),
            Foreground = Br("#2EE6D6"),
        });
        ProfileBar.Children.Add(new TextBlock
        {
            Text = "While capture is on (F8), press a hotkey to switch to its profile. Bindings live in the regular profiles: pick one above to edit it.",
            Foreground = (Brush)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 8),
        });

        foreach (var e in set.Entries.ToList())
        {
            var line = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
            var chord = Chord(e.Hotkey, Commit, 120); chord.Margin = new Thickness(0, 0, 8, 0);
            line.Children.Add(chord);
            var arrow = Ico("arrow", 14, (Brush)FindResource("Muted")); ((FrameworkElement)arrow).Margin = new Thickness(0, 0, 8, 0);
            line.Children.Add(arrow);
            var prof = store.Profiles.FirstOrDefault(p => p.Id == e.Profile);
            var pb = new Button { Style = S("Pill"), Content = prof?.Name ?? "choose profile…", MinWidth = 190, Margin = new Thickness(0, 0, 8, 0) };
            pb.Click += (_, _) => PickProfile(pb, p => { e.Profile = p.Id; Commit(); BuildProfiles(); });
            line.Children.Add(pb);
            var x = IconButton("x", "remove", () => { set.Entries.Remove(e); Commit(); BuildProfiles(); });
            line.Children.Add(x);
            ProfileBar.Children.Add(line);
        }
        var addEntry = new Button { Style = S("Pill"), Content = IconText("plus", "Add profile"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 6) };
        addEntry.Click += (_, _) =>
        {
            var unused = store.Profiles.FirstOrDefault(p => !set.Entries.Any(en => en.Profile == p.Id)) ?? store.Profiles[0];
            set.Entries.Add(new SwitchEntry { Profile = unused.Id }); Commit(); BuildProfiles();
        };
        ProfileBar.Children.Add(addEntry);
    }

    // Windows 11 acrylic backdrop; falls back to the opaque gradient elsewhere.
    void Glass()
    {
        var h = new WindowInteropHelper(this).Handle;
        int dark = 1, acrylic = 3;
        DwmSetWindowAttribute(h, 20, ref dark, 4);
        if (DwmSetWindowAttribute(h, 38, ref acrylic, 4) != 0) return;
        var m = new Margins { l = -1, r = -1, t = -1, b = -1 };
        DwmExtendFrameIntoClientArea(h, ref m);
        HwndSource.FromHwnd(h).CompositionTarget.BackgroundColor = Colors.Transparent;
        Root.Background = (Brush)FindResource("GlassBg");
    }

    static void Commit() { P.Save(); P.Rebuild(); }

    // Slider + typed value. Typing is clamped to the slider's range; Enter or leaving the box applies it.
    void Bind(Slider s, TextBox box, double val, Action<double> set, string fmt = "0.000")
    {
        string Fmt3(double v) => v.ToString(fmt, CultureInfo.InvariantCulture);
        s.Value = val; box.Text = Fmt3(val);
        s.ValueChanged += (_, e) => { set(e.NewValue); if (!box.IsKeyboardFocused) box.Text = Fmt3(e.NewValue); P.Save(); };
        void Apply()
        {
            if (double.TryParse(box.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                s.Value = Math.Clamp(v, s.Minimum, s.Maximum);
            box.Text = Fmt3(s.Value);
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Apply(); Keyboard.ClearFocus(); } };
        box.LostKeyboardFocus += (_, _) => Apply();
        box.GotKeyboardFocus += (_, _) => box.SelectAll();
    }

    void ShowSwap() => SwapBtn.Content = P.cfg.SwapSticks ? "Sticks swapped:  aim = L3 · move = R3" : "Sticks standard:  aim = R3 · move = L3";

    void ShowInvert() => InvertBtn.Content = P.cfg.InvertY ? "Invert Y:  ON" : "Invert Y:  off";

    void Status(bool on)
    {
        StatusText.Text = on ? "CAPTURE ON  ·  F8 to release" : "capture off";
        Dot.Fill = on ? Br("#2EE6D6") : Br("#99FFFFFF");
    }

    void Tab(bool simple)
    {
        if (SimpleView == null) return;
        SimpleView.Visibility = simple ? Visibility.Visible : Visibility.Collapsed;
        MacroView.Visibility = simple ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------- glyph badge + picker ----------
    // Controller-accurate silhouettes (drawn for the L side; R side is mirrored).
    const string BumperGeo = "M3,18 L3,11 Q3,4 10,4 L33,4 Q34,4 34,6 L34,18 Q34,20 32,20 L5,20 Q3,20 3,18 Z";
    const string TriggerGeo = "M8,2 L33,2 Q35,2 35,4 L35,19 Q35,22 32,22 L16,22 Q3,22 3,10 Q3,2 8,2 Z";
    const string PillGeo = "M10,2 L26,2 A8,8 0 0 1 26,18 L10,18 A8,8 0 0 1 10,2 Z";

    static FrameworkElement Shaped(Act a, double sz)
    {
        string geo = a is Act.L1 or Act.R1 ? BumperGeo : a is Act.L2 or Act.R2 ? TriggerGeo : PillGeo;
        var white = Brushes.White;
        var g = new Grid { Width = sz * 1.45, Height = a is Act.Options or Act.Create ? sz * 0.72 : sz };
        var p = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(geo), Stretch = Stretch.Fill, Margin = new Thickness(1), Stroke = white, StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round, Fill = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255)),
        };
        if (a is Act.R1 or Act.R2) { p.RenderTransformOrigin = new Point(0.5, 0.5); p.RenderTransform = new ScaleTransform(-1, 1); }
        g.Children.Add(p);
        if (a is Act.Options)
        {
            var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            for (int i = 0; i < 3; i++) lines.Children.Add(new Border { Width = sz * 0.5, Height = 1.7, Background = white, Margin = new Thickness(0, 1.2, 0, 1.2), CornerRadius = new CornerRadius(1) });
            g.Children.Add(lines);
        }
        else if (a is Act.Create) // capture-frame corners
            g.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M0,4 L0,0 L4,0 M12,0 L16,0 L16,4 M16,10 L16,14 L12,14 M4,14 L0,14 L0,10"),
                Stroke = white, StrokeThickness = 1.5, Stretch = Stretch.Uniform, Width = sz * 0.38, Height = sz * 0.34,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            });
        else
        {   // the L1/R1/L2/R2 label, centred by mass inside the silhouette's box
            UIElement Label() => new TextBlock
            {
                Text = M[a].Glyph, Foreground = white, FontWeight = FontWeights.Bold, FontSize = sz * 0.36,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            var tb = (FrameworkElement)Label();
            var off = MassOffset($"s{a}{sz}", Label, g.Width, g.Height);      // label mass -> middle of the box
            var body = ShapeMass($"m{a}{sz}", geo, a is Act.R1 or Act.R2, g.Width, g.Height);   // ...then -> the silhouette's own mass centre
            tb.RenderTransform = new TranslateTransform(off.X + body.X - g.Width / 2, off.Y + body.Y - g.Height / 2);
            g.Children.Add(tb);
        }
        return g;
    }

    // Centre of mass of a bumper/trigger silhouette's solid area. These shapes have a cut or rounded corner on one
    // side, so their mass is not at the middle of their box, and a label looks off-centre if it is put there.
    static Vector ShapeMass(string key, string geo, bool mirror, double w, double h)
    {
        if (massCache.TryGetValue(key, out var cached)) return cached;
        var p = new System.Windows.Shapes.Path { Data = Geometry.Parse(geo), Stretch = Stretch.Fill, Margin = new Thickness(1), Fill = Brushes.White };
        if (mirror) { p.RenderTransformOrigin = new Point(0.5, 0.5); p.RenderTransform = new ScaleTransform(-1, 1); }
        var host = new Grid { Width = w, Height = h }; host.Children.Add(p);
        var c = Centroid(host, w, h);
        return massCache[key] = c;
    }

    // Report: how far each silhouette's mass centre is from the middle of its box (this is what the label now follows).
    internal static string ShapeReport()
    {
        var parts = new List<string>();
        foreach (var a in new[] { Act.L1, Act.R1, Act.L2, Act.R2 })
        {
            double sz = 28, w = sz * 1.45, h = sz;
            var geo = a is Act.L1 or Act.R1 ? BumperGeo : TriggerGeo;
            var c = ShapeMass($"m{a}{sz}", geo, a is Act.R1 or Act.R2, w, h);
            parts.Add($"{a}: dx={c.X - w / 2:+0.00;-0.00} dy={c.Y - h / 2:+0.00;-0.00}");
        }
        return string.Join("   ", parts);
    }

    // Centre of mass: render the glyph, weight every pixel by its alpha, and return the shift that moves that
    // point to the middle of the w x h box. A triangle, an arrow or a "½" is centred by its mass, not its bounding box.
    static readonly Dictionary<string, Vector> massCache = new();

    // Alpha-weighted centroid (in DIPs) of everything drawn inside a laid-out w x h panel.
    static Vector Centroid(Panel host, double w, double h)
    {
        // Render inside a margin so a glyph shifted past its box edge is not clipped out of the measurement.
        const double Pad = 8, S = 8;   // S: render at 8x for sub-pixel accuracy
        var outer = new Grid { Width = w + 2 * Pad, Height = h + 2 * Pad };
        outer.Children.Add(host);
        outer.Measure(new Size(outer.Width, outer.Height)); outer.Arrange(new Rect(0, 0, outer.Width, outer.Height)); outer.UpdateLayout();
        int pw = (int)Math.Ceiling(outer.Width * S), ph = (int)Math.Ceiling(outer.Height * S);
        var rtb = new RenderTargetBitmap(pw, ph, 96 * S, 96 * S, PixelFormats.Pbgra32);
        rtb.Render(outer);
        var px = new byte[pw * ph * 4]; rtb.CopyPixels(px, pw * 4, 0);
        double sum = 0, sx = 0, sy = 0;
        for (int y = 0; y < ph; y++)
            for (int x = 0; x < pw; x++)
            {
                byte a = px[(y * pw + x) * 4 + 3];
                if (a == 0) continue;
                sum += a; sx += a * (x + 0.5); sy += a * (y + 0.5);
            }
        return sum == 0 ? new Vector(w / 2, h / 2) : new Vector(sx / sum / S - Pad, sy / sum / S - Pad);
    }

    static Vector MassOffset(string key, Func<UIElement> make, double w, double h, bool canvas = false)
    {
        if (massCache.TryGetValue(key, out var cached)) return cached;
        Panel host = canvas ? new Canvas { Width = w, Height = h } : new Grid { Width = w, Height = h };
        host.Children.Add(make());
        var c = Centroid(host, w, h);
        return massCache[key] = new Vector(w / 2 - c.X, h / 2 - c.Y);
    }

    // Self-test: the worst distance (DIPs) between a glyph's centre of mass and the centre of its box.
    internal static string MassSelfTest(bool corrected)
    {
        double worst = 0; string name = "";
        void Check(string n, Panel host, double w, double h)
        {
            var c = Centroid(host, w, h); double e = Math.Max(Math.Abs(c.X - w / 2), Math.Abs(c.Y - h / 2));
            if (e > worst) { worst = e; name = n; }
        }
        foreach (Act a in Enum.GetValues<Act>())
        {
            if (a is Act.L1 or Act.R1 or Act.L2 or Act.R2 or Act.Options or Act.Create) continue;
            var host = new Grid { Width = 30, Height = 30 }; host.Children.Add(CenteredGlyph(a, 30, corrected)); Check(a.ToString(), host, 30, 30);
        }
        foreach (var k in IconPaths.Keys)
        {
            var host = new Grid { Width = 12, Height = 12 }; host.Children.Add(Ico(k, 12, null, corrected)); Check("icon:" + k, host, 12, 12);
        }
        return $"{worst:0.000} ({name})";
    }

    // A badge's glyph with its centre of mass moved onto the badge centre.
    static FrameworkElement CenteredGlyph(Act a, double sz, bool corrected = true)
    {
        var (_, g, col) = M[a]; var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(col));
        UIElement Make() => VecGlyph(a, brush, sz) ?? new TextBlock
        {
            Text = g, Foreground = brush, FontWeight = FontWeights.Bold,
            FontSize = sz * 0.40, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var glyph = (FrameworkElement)Make();
        if (corrected) { var off = MassOffset($"b{a}{sz}", Make, sz, sz); glyph.RenderTransform = new TranslateTransform(off.X, off.Y); }
        return glyph;
    }

    // Vector glyphs on a 20x20 canvas, so they centre exactly (text glyphs sit differently in every font).
    static UIElement VecGlyph(Act a, Brush brush, double sz)
    {
        string d; bool fill = false; double rot = 0;
        switch (a)
        {
            case Act.Cross: d = "M4,4 L16,16 M16,4 L4,16"; break;
            case Act.Circle: d = "M3,10 A7,7 0 1 0 17,10 A7,7 0 1 0 3,10 Z"; break;
            case Act.Square: d = "M4,4 L16,4 L16,16 L4,16 Z"; break;
            case Act.Triangle: d = "M10,2 L17,14 L3,14 Z"; break;   // apex raised so the optical centre sits in the middle
            case Act.Up: case Act.Down: case Act.Left: case Act.Right:
                d = "M10,16.5 L10,3.5 M4.5,9 L10,3.5 L15.5,9"; rot = a == Act.Right ? 90 : a == Act.Down ? 180 : a == Act.Left ? 270 : 0; break;
            case Act.DUp: case Act.DDown: case Act.DLeft: case Act.DRight:
                d = "M10,2 L16.5,14 L3.5,14 Z"; fill = true;
                rot = a == Act.DRight ? 90 : a == Act.DDown ? 180 : a == Act.DLeft ? 270 : 0; break;
            case Act.Sprint: d = "M3.5,4 L9,10 L3.5,16 M11,4 L16.5,10 L11,16"; break;
            default: return null;
        }
        var p = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(d), Stroke = brush, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Fill = fill ? brush : null,
            RenderTransform = new RotateTransform(rot, 10, 10),
        };
        return new Viewbox { Width = sz * 0.56, Height = sz * 0.56, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = new Canvas { Width = 20, Height = 20, Children = { p } } };
    }

    // 12x12 vector icons used on buttons.
    static readonly Dictionary<string, string> IconPaths = new()
    {
        ["plus"] = "M6,1 L6,11 M1,6 L11,6",
        ["x"] = "M2,2 L10,10 M10,2 L2,10",
        ["arrow"] = "M1,6 L11,6 M7,2 L11,6 L7,10",
        ["swap"] = "M1,3.5 L11,3.5 M8,1 L11,3.5 L8,6 M11,8.5 L1,8.5 M4,6 L1,8.5 L4,11",
        ["undo"] = "M2.2,2.6 L2.2,6.2 L5.8,6.2 M2.7,6 A4.3,4.3 0 1 1 3.7,9.5",
    };

    static UIElement Ico(string kind, double size = 12, Brush stroke = null, bool corrected = true)
    {
        System.Windows.Shapes.Path NewPath() => new()
        {
            Data = Geometry.Parse(IconPaths[kind]), Stroke = stroke ?? Brushes.White, StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        };
        var p = NewPath();
        if (corrected)   // by mass, in the 12x12 icon space
        {
            var off = MassOffset("i" + kind, () => NewPath(), 12, 12, canvas: true);
            p.RenderTransform = new TranslateTransform(off.X, off.Y);
        }
        return new Viewbox { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = new Canvas { Width = 12, Height = 12, Children = { p } } };
    }

    static FrameworkElement IconText(string kind, string text, Brush fg = null) => new StackPanel
    {
        Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
        Children = { Ico(kind, 12, fg), new TextBlock { Text = text, Foreground = fg ?? Brushes.White, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center } },
    };

    Button IconButton(string kind, string tip, Action click)
    {
        var b = new Button { Style = S("IconBtn"), Content = Ico(kind, 12), ToolTip = tip };
        b.Click += (_, _) => click();
        return b;
    }

    // A key/combo box with its remove button. Used by Simple Keys and Macros so both look and behave the same.
    UIElement KeyUnit(List<string> keys, Action remove, string tip, Action rebuild)
    {
        var x = IconButton("x", tip, () => { remove(); Commit(); rebuild(); });
        x.Margin = new Thickness(4, 0, 0, 0);
        return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 3, 0, 3), Children = { Chord(keys, Commit, 130), x } };
    }

    static FrameworkElement Badge(Act a, double sz = 28)
    {
        if (a is Act.L1 or Act.R1 or Act.L2 or Act.R2 or Act.Options or Act.Create) return Shaped(a, sz);
        var (_, g, col) = M[a]; var c = (Color)ColorConverter.ConvertFromString(col);
        return new Border
        {
            Width = sz, Height = sz, CornerRadius = new CornerRadius(sz / 2), BorderThickness = new Thickness(1.5),
            Background = new SolidColorBrush(Color.FromArgb(0x33, c.R, c.G, c.B)), BorderBrush = new SolidColorBrush(c),
            Child = CenteredGlyph(a, sz),   // glyph centred by its mass, not its bounding box
        };
    }

    static FrameworkElement BadgeName(Act a, double sz = 24) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Children = { new Grid { Width = sz * 1.5, Children = { Badge(a, sz) } }, new TextBlock { Text = M[a].Name, Foreground = Brushes.White, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center } },
    };

    void Pick(FrameworkElement target, Action<Act> chosen)
    {
        var pop = new Popup { PlacementTarget = target, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade };
        var wrap = new WrapPanel { Width = 480 };
        foreach (Act a in Enum.GetValues<Act>().Where(x => x != Act.Slow && x != Act.Sprint)) // modifiers, not pad buttons
        {
            var b = new Button { Style = S("Pill"), Margin = new Thickness(3), Padding = new Thickness(10, 0, 8, 0), Width = 148, HorizontalContentAlignment = HorizontalAlignment.Left, Content = BadgeName(a) };
            b.Click += (_, _) => { pop.IsOpen = false; chosen(a); };
            wrap.Children.Add(b);
        }
        pop.Child = new Border
        {
            Background = Br("#F2221047"), BorderBrush = Br("#66FFFFFF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16),
            Padding = new Thickness(8), Margin = new Thickness(14),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Opacity = 0.5 },
            Child = wrap,
        };
        pop.IsOpen = true;
    }

    // ---------- keyboard chord capture ----------
    Button lBtn; List<string> lKeys; Action lShow, lChanged; readonly List<int> lPressed = new();

    static int Norm(int v) => v is 0xA0 or 0xA1 ? 0x10 : v is 0xA2 or 0xA3 ? 0x11 : v is 0xA4 or 0xA5 ? 0x12 : v;

    static string Fmt(IEnumerable<string> keys) => string.Join(" + ", keys.Select(k => k switch
    {
        "ShiftKey" => "Shift", "ControlKey" => "Ctrl", "Menu" => "Alt", "LButton" => "Mouse L", "RButton" => "Mouse R",
        "MButton" => "Mouse M", "XButton1" => "Mouse 4", "XButton2" => "Mouse 5", "Return" => "Enter", "Capital" => "Caps",
        "Next" => "PageDown", "Prior" => "PageUp", "Oemtilde" => "`", _ when k.Length == 2 && k[0] == 'D' && char.IsDigit(k[1]) => k[1].ToString(), _ => k,
    }));

    Button Chord(List<string> keys, Action changed, double minWidth = 170)
    {
        var b = new Button { Style = S("Pill"), MinWidth = minWidth };
        void Show() => b.Content = keys.Count == 0 ? "click to bind" : Fmt(keys);
        b.Opacity = 1;
        b.Click += (_, _) =>
        {
            if (lBtn == b) return;
            Cancel(); lBtn = b; lKeys = keys; lShow = Show; lChanged = changed; lPressed.Clear(); b.Content = "press keys…";
        };
        b.PreviewMouseDown += (_, e) =>
        {
            if (lBtn != b) return;
            var k = e.ChangedButton switch
            {
                MouseButton.Left => Keys.LButton, MouseButton.Right => Keys.RButton, MouseButton.Middle => Keys.MButton,
                MouseButton.XButton1 => Keys.XButton1, _ => Keys.XButton2,
            };
            if (!lPressed.Contains((int)k)) lPressed.Add((int)k);
            e.Handled = true; Finish();
        };
        Show();
        return b;
    }

    void Cancel() { if (lBtn == null) return; var s = lShow; lBtn = null; s(); }

    void Finish()
    {
        lKeys.Clear(); lKeys.AddRange(lPressed.Select(v => ((Keys)v).ToString()));
        var (show, changed) = (lShow, lChanged); lBtn = null; show(); changed();
    }

    void OnKeyDown(object s, KeyEventArgs e)
    {
        if (lBtn == null) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int v = Norm(KeyInterop.VirtualKeyFromKey(key));
        if (v is 0x77 or 0x78 or 0x79 || v == 0) return; // F8/F9 are reserved
        if (!lPressed.Contains(v)) lPressed.Add(v);
        lBtn.Content = Fmt(lPressed.Select(x => ((Keys)x).ToString()));
    }

    // ---------- Simple Keys tab ----------
    UIElement Heading(string t) => new TextBlock { Text = t, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(4, 12, 0, 6) };

    // Movement style: Full tilt / Walk key / Ramp-up. The pad loop reads cfg live, so no rebuild is needed.
    UIElement MoveStyleCard()
    {
        var c = P.cfg;
        var card = new Border { Style = S("Glass"), Margin = new Thickness(0, 0, 0, 2) };
        var col = new StackPanel();
        var seg = new Border { Style = S("Glass"), CornerRadius = new CornerRadius(14), Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Left, Effect = null };
        var segRow = new StackPanel { Orientation = Orientation.Horizontal };
        var desc = new TextBlock { Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 10, 0, 4) };

        Slider S2(double min, double max, double small, double large) => new() { Style = S("GlassSlider"), Minimum = min, Maximum = max, SmallChange = small, LargeChange = large };
        Grid SliderRow(string label, Slider s, TextBox box)
        {
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            g.Children.Add(new TextBlock { Text = label, FontSize = 14, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(s, 1); Grid.SetColumn(box, 2); g.Children.Add(s); g.Children.Add(box);
            return g;
        }
        Grid TierRow(string label, double val, Action<double> set)
        {
            var s = S2(10, 100, 1, 5); var b = new TextBox { Style = S("ValueBox") };
            Bind(s, b, val, set, "0");
            return SliderRow(label, s, b);
        }
        var tierRows = new[]
        {
            TierRow("Normal  (no key)  %", c.NormalPct, v => c.NormalPct = v),
            TierRow("Slow walk  %", c.SlowPct, v => c.SlowPct = v),
        };
        var rampS = S2(20, 600, 5, 25); var rampB = new TextBox { Style = S("ValueBox") };
        Bind(rampS, rampB, c.RampMs, v => c.RampMs = v, "0");
        var rampRow = SliderRow("Ramp time  (ms)", rampS, rampB);

        void Refresh()
        {
            foreach (var r in tierRows) r.Visibility = c.MoveMode == "Tiers" ? Visibility.Visible : Visibility.Collapsed;
            rampRow.Visibility = c.MoveMode == "Ramp" ? Visibility.Visible : Visibility.Collapsed;
            ApplyModifierRows();
            desc.Text = c.MoveMode switch
            {
                "Tiers" => "No key = normal speed. Hold the Slow walk key (bound below) to move at the slow walk speed.",
                "Ramp" => "A tap gives a small step, holding eases the stick up to full speed over the ramp time.",
                _ => "Keys push the stick fully, so you always move at full speed.",
            };
        }
        foreach (var (mode, text) in new[] { ("Full", "Full tilt"), ("Tiers", "Speed keys"), ("Ramp", "Ramp-up") })
        {
            var r = new RadioButton { Style = S("TabBtn"), GroupName = "move", Content = text, IsChecked = c.MoveMode == mode };
            r.Checked += (_, _) => { c.MoveMode = mode; P.Save(); Refresh(); };
            segRow.Children.Add(r);
        }
        seg.Child = segRow;
        col.Children.Add(seg); col.Children.Add(desc);
        foreach (var r in tierRows) col.Children.Add(r);
        col.Children.Add(rampRow);
        Refresh();
        card.Child = col;
        return card;
    }

    // Slow/Sprint key rows only matter in Speed keys mode.
    readonly List<UIElement> modifierRows = new();
    void ApplyModifierRows()
    {
        foreach (var r in modifierRows) r.Visibility = P.cfg.MoveMode == "Tiers" ? Visibility.Visible : Visibility.Collapsed;
    }

    void BuildSimple()
    {
        SimplePanel.Children.Clear(); modifierRows.Clear();
        SimplePanel.Children.Add(Heading("MOVEMENT STYLE"));
        SimplePanel.Children.Add(MoveStyleCard());
        foreach (var (title, acts) in Groups)
        {
            SimplePanel.Children.Add(Heading(title));
            var card = new Border { Style = S("Glass"), Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 0, 2) };
            var col = new StackPanel();
            foreach (var a in acts)
            {
                var keys = P.cfg.Binds[a.ToString()];
                if (!P.cfg.Extra.TryGetValue(a.ToString(), out var extra)) P.cfg.Extra[a.ToString()] = extra = new();
                var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var name = BadgeName(a, 30); ((TextBlock)((StackPanel)name).Children[1]).FontSize = 14;
                name.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(name);

                // Any one of these keys/combos triggers the button: the main binding plus any alternatives.
                var chips = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetColumn(chips, 1); row.Children.Add(chips);
                if (a == Act.Sprint) // sprint is a PS button you hold: pick which one, then the key(s) after it
                {
                    Enum.TryParse<Act>(P.cfg.SprintButton, out var sa);
                    var gb = new Button { Style = S("Pill"), Padding = new Thickness(10, 0, 12, 0), Margin = new Thickness(0, 3, 0, 3), ToolTip = "PS button held while the sprint key is down", Content = BadgeName(sa) };
                    gb.Click += (_, _) => Pick(gb, x => { P.cfg.SprintButton = x.ToString(); Commit(); BuildSimple(); });
                    var arrow = Ico("arrow", 14, (Brush)FindResource("Muted")); ((FrameworkElement)arrow).Margin = new Thickness(0, 0, 8, 0);
                    chips.Children.Add(arrow); chips.Children.Add(gb);
                }
                chips.Children.Add(KeyUnit(keys, keys.Clear, "unbind", BuildSimple));
                foreach (var alt in extra.ToList()) chips.Children.Add(KeyUnit(alt, () => extra.Remove(alt), "remove this alternative", BuildSimple));
                var add = IconButton("plus", "add another key for this button", () => { extra.Add(new List<string>()); BuildSimple(); });
                add.Margin = new Thickness(6, 3, 0, 3);
                chips.Children.Add(add);

                col.Children.Add(row);
                if (a == Act.Slow) modifierRows.Add(row); // slow walk only matters in Speed keys mode; sprint works in every mode
            }
            card.Child = col; SimplePanel.Children.Add(card);
            ApplyModifierRows();
        }
    }

    // ---------- Macros tab ----------
    // One card per macro: the PS buttons it presses (top), and the key(s) that trigger it (below).
    // Key boxes work exactly like Simple Keys: a main key/combo plus any number of alternatives.
    void BuildMacros()
    {
        MacroPanel.Children.Clear();
        var macros = P.cfg.Macros;
        var muted = (Brush)FindResource("Muted");
        if (macros.Count == 0)
            MacroPanel.Children.Add(new TextBlock { Text = "No macros yet. Press “New macro”, pick the PS buttons, then bind a key.", Foreground = muted, Margin = new Thickness(4, 20, 0, 0), TextWrapping = TextWrapping.Wrap });
        foreach (var m in macros.ToList())
        {
            m.Extra ??= new();
            var card = new Border { Style = S("Glass"), Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(14, 10, 14, 10) };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // row 0: PS buttons, joined with small plus signs
            var chips = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < m.Buttons.Count; i++)
            {
                int idx = i;
                if (i > 0)
                {
                    var plus = Ico("plus", 10, muted); ((FrameworkElement)plus).Margin = new Thickness(8, 0, 8, 0);
                    chips.Children.Add(plus);
                }
                Enum.TryParse<Act>(m.Buttons[i], out var act);
                var chip = new Button { Style = S("Pill"), Padding = new Thickness(10, 0, 12, 0), Content = BadgeName(act), Margin = new Thickness(0, 3, 0, 3) };
                chip.Click += (_, _) => Pick(chip, a => { m.Buttons[idx] = a.ToString(); Commit(); BuildMacros(); });
                var unit = new StackPanel { Orientation = Orientation.Horizontal };
                unit.Children.Add(chip);
                if (m.Buttons.Count > 1)
                {
                    var x = IconButton("x", "remove button", () => { m.Buttons.RemoveAt(idx); Commit(); BuildMacros(); });
                    x.Margin = new Thickness(4, 0, 0, 0);
                    unit.Children.Add(x);
                }
                chips.Children.Add(unit);
            }
            if (m.Buttons.Count > 0)
            {
                var plus = Ico("plus", 10, muted); ((FrameworkElement)plus).Margin = new Thickness(8, 0, 8, 0);
                chips.Children.Add(plus);
            }
            var add = new Button { Style = S("AccentBtn"), Content = IconText("plus", "button"), Margin = new Thickness(0, 3, 0, 3) };
            add.Click += (_, _) => Pick(add, a => { m.Buttons.Add(a.ToString()); Commit(); BuildMacros(); });
            chips.Children.Add(add);
            g.Children.Add(chips);

            var del = new Button { Style = S("Pill"), Content = "Delete", Foreground = Br("#FF8FA8"), Margin = new Thickness(12, 3, 0, 3), VerticalAlignment = VerticalAlignment.Top };
            del.Click += (_, _) => { P.cfg.Macros.Remove(m); Commit(); BuildMacros(); };
            Grid.SetColumn(del, 1); g.Children.Add(del);

            // row 1: the keys that trigger it (main + alternatives), same controls as Simple Keys
            var keysRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var arrow = Ico("arrow", 14, muted); ((FrameworkElement)arrow).Margin = new Thickness(2, 0, 2, 0);
            keysRow.Children.Add(arrow);
            keysRow.Children.Add(KeyUnit(m.Chord, m.Chord.Clear, "unbind", BuildMacros));
            foreach (var alt in m.Extra.ToList()) keysRow.Children.Add(KeyUnit(alt, () => m.Extra.Remove(alt), "remove this alternative", BuildMacros));
            var addKey = IconButton("plus", "add another key for this macro", () => { m.Extra.Add(new List<string>()); BuildMacros(); });
            addKey.Margin = new Thickness(6, 3, 0, 3);
            keysRow.Children.Add(addKey);
            Grid.SetRow(keysRow, 1); Grid.SetColumnSpan(keysRow, 2); g.Children.Add(keysRow);

            card.Child = g; MacroPanel.Children.Add(card);
        }
    }
}
