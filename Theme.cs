using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FileInventory;

enum AppTheme
{
    System,
    Light,
    Dark,
    DarkBlue
}

static class Theme
{
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const int DWMWA_BORDER_COLOR = 34;
    const int DWMWA_CAPTION_COLOR = 35;
    const int DWMWA_TEXT_COLOR = 36;

    const int WM_PAINT = 0x000F;
    const int WM_ERASEBKGND = 0x0014;
    const int WM_NCPAINT = 0x0085;
    const int WM_THEMECHANGED = 0x031A;
    const int LVM_GETHEADER = 0x101F;
    const int DTM_GETMONTHCAL = 0x1008;
    const int CB_GETCOMBOBOXINFO = 0x0164;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? pszSubAppName, string? pszSubIdList);

    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    static extern int SetPreferredAppMode(int appMode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    static extern void FlushMenuThemes();

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool GetComboBoxInfo(IntPtr hwndCombo, ref COMBOBOXINFO info);

    [DllImport("user32.dll")]
    static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public bool fErase;
        public RECT rcPaint;
        public bool fRestore;
        public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct COMBOBOXINFO
    {
        public int cbSize;
        public RECT rcItem;
        public RECT rcButton;
        public int stateButton;
        public IntPtr hwndCombo;
        public IntPtr hwndEdit;
        public IntPtr hwndList;
    }

    sealed class Palette
    {
        public required Color Bg { get; init; }
        public required Color Surface { get; init; }
        public required Color SurfaceReadOnly { get; init; }
        public required Color SurfaceAlt { get; init; }
        public required Color SurfaceHover { get; init; }
        public required Color SurfacePressed { get; init; }
        public required Color SurfaceDisabled { get; init; }
        public required Color HeaderBg { get; init; }
        public required Color Fg { get; init; }
        public required Color Muted { get; init; }
        public required Color DisabledFg { get; init; }
        public required Color Border { get; init; }
        public required Color BorderStrong { get; init; }
        public required Color Accent { get; init; }
        public required Color AccentHover { get; init; }
        public required Color AccentPressed { get; init; }
        public required Color AccentDisabledBg { get; init; }
        public required Color AccentDisabledFg { get; init; }
        public required Color TrackBg { get; init; }
    }

    static readonly Palette LightPalette = new()
    {
        Bg = Color.FromArgb(243, 243, 245),
        Surface = Color.FromArgb(255, 255, 255),
        SurfaceReadOnly = Color.FromArgb(249, 249, 251),
        SurfaceAlt = Color.FromArgb(252, 252, 253),
        SurfaceHover = Color.FromArgb(242, 244, 248),
        SurfacePressed = Color.FromArgb(232, 235, 240),
        SurfaceDisabled = Color.FromArgb(240, 241, 244),
        HeaderBg = Color.FromArgb(246, 247, 250),
        Fg = Color.FromArgb(24, 24, 27),
        Muted = Color.FromArgb(96, 98, 108),
        DisabledFg = Color.FromArgb(148, 150, 160),
        Border = Color.FromArgb(214, 216, 224),
        BorderStrong = Color.FromArgb(188, 191, 202),
        Accent = Color.FromArgb(0, 103, 192),
        AccentHover = Color.FromArgb(22, 118, 201),
        AccentPressed = Color.FromArgb(0, 84, 158),
        AccentDisabledBg = Color.FromArgb(206, 220, 234),
        AccentDisabledFg = Color.FromArgb(120, 138, 156),
        TrackBg = Color.FromArgb(224, 226, 232)
    };

    static readonly Palette DarkPalette = new()
    {
        Bg = Color.FromArgb(32, 32, 34),
        Surface = Color.FromArgb(43, 43, 46),
        SurfaceReadOnly = Color.FromArgb(38, 38, 41),
        SurfaceAlt = Color.FromArgb(48, 48, 52),
        SurfaceHover = Color.FromArgb(58, 58, 63),
        SurfacePressed = Color.FromArgb(40, 40, 44),
        SurfaceDisabled = Color.FromArgb(39, 39, 42),
        HeaderBg = Color.FromArgb(38, 38, 42),
        Fg = Color.FromArgb(244, 244, 246),
        Muted = Color.FromArgb(166, 168, 178),
        DisabledFg = Color.FromArgb(118, 120, 128),
        Border = Color.FromArgb(68, 68, 75),
        BorderStrong = Color.FromArgb(92, 92, 102),
        Accent = Color.FromArgb(0, 120, 212),
        AccentHover = Color.FromArgb(26, 138, 226),
        AccentPressed = Color.FromArgb(0, 102, 184),
        AccentDisabledBg = Color.FromArgb(46, 60, 74),
        AccentDisabledFg = Color.FromArgb(132, 148, 164),
        TrackBg = Color.FromArgb(56, 56, 62)
    };

    static readonly Palette DarkBluePalette = new()
    {
        Bg = Color.FromArgb(15, 23, 42),
        Surface = Color.FromArgb(24, 35, 58),
        SurfaceReadOnly = Color.FromArgb(19, 29, 49),
        SurfaceAlt = Color.FromArgb(30, 41, 66),
        SurfaceHover = Color.FromArgb(38, 53, 82),
        SurfacePressed = Color.FromArgb(20, 30, 50),
        SurfaceDisabled = Color.FromArgb(20, 29, 48),
        HeaderBg = Color.FromArgb(20, 31, 52),
        Fg = Color.FromArgb(226, 232, 240),
        Muted = Color.FromArgb(148, 163, 184),
        DisabledFg = Color.FromArgb(100, 116, 139),
        Border = Color.FromArgb(51, 65, 92),
        BorderStrong = Color.FromArgb(71, 88, 120),
        Accent = Color.FromArgb(0, 122, 204),
        AccentHover = Color.FromArgb(28, 151, 234),
        AccentPressed = Color.FromArgb(0, 98, 168),
        AccentDisabledBg = Color.FromArgb(30, 52, 76),
        AccentDisabledFg = Color.FromArgb(120, 150, 180),
        TrackBg = Color.FromArgb(38, 52, 78)
    };

    static readonly string BodyFontFamily = ResolveFontFamily("Segoe UI Variable Text", "Segoe UI");
    static readonly string SemiboldFontFamily = ResolveFontFamily("Segoe UI Variable Text Semibold", BodyFontFamily);
    static readonly string MonospaceFontFamily = ResolveFontFamily("Cascadia Mono", "Consolas");

    public static Font BodyFont { get; } = new(BodyFontFamily, 9.5f, FontStyle.Regular);
    public static Font SectionFont { get; } = SemiboldFontFamily == BodyFontFamily
        ? new(BodyFontFamily, 9.5f, FontStyle.Bold)
        : new(SemiboldFontFamily, 9.5f, FontStyle.Regular);
    public static Font MonospaceFont { get; } = new(MonospaceFontFamily, 9f, FontStyle.Regular);
    static readonly Font BoldBodyFont = new(BodyFontFamily, 9.5f, FontStyle.Bold);

    static readonly ConditionalWeakTable<Control, object> hooked = new();
    static readonly ConditionalWeakTable<Button, ButtonVisualState> buttonStates = new();
    static readonly ConditionalWeakTable<Control, List<NativeWindow>> nativeHooks = new();
    static readonly ConditionalWeakTable<ListView, string[]> highlightTerms = new();
    static readonly ConditionalWeakTable<ToolTip, object> hookedToolTips = new();
    static readonly List<WeakReference<ProgressBar>> marqueeBars = [];
    static System.Windows.Forms.Timer? marqueeTimer;

    static bool isDark;
    static Palette current = LightPalette;

    internal static bool IsDark => isDark;
    internal static Color CurrentBg => current.Bg;
    internal static Color CurrentSurface => current.Surface;
    internal static Color CurrentSurfaceAlt => current.SurfaceAlt;
    internal static Color CurrentSurfaceHover => current.SurfaceHover;
    internal static Color CurrentFg => current.Fg;
    internal static Color CurrentMuted => current.Muted;
    internal static Color CurrentBorder => current.Border;
    internal static Color CurrentBorderStrong => current.BorderStrong;
    internal static Color CurrentAccent => current.Accent;

    sealed class ButtonVisualState
    {
        public bool Hovered;
        public bool Pressed;
    }

    static string ResolveFontFamily(string preferred, string fallback)
    {
        try
        {
            foreach (var family in FontFamily.Families)
                if (string.Equals(family.Name, preferred, StringComparison.OrdinalIgnoreCase))
                    return family.Name;
        }
        catch { }
        return fallback;
    }

    public static void MarkSectionTitle(Label label) => label.Tag = "section-title";
    public static void MarkPrimaryLabel(Label label) => label.Tag = "primary-label";

    public static AppTheme ResolveTheme(AppTheme theme)
    {
        if (theme != AppTheme.System)
            return theme;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value)
                return value == 0 ? AppTheme.Dark : AppTheme.Light;
        }
        catch { }

        return AppTheme.Light;
    }

    public static AppTheme LoadPreference(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, "theme.txt");
            if (File.Exists(path) && Enum.TryParse<AppTheme>(File.ReadAllText(path).Trim(), true, out var saved))
                return saved;
        }
        catch { }
        return AppTheme.System;
    }

    public static void SavePreference(string dataDirectory, AppTheme theme)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(Path.Combine(dataDirectory, "theme.txt"), theme.ToString());
        }
        catch { }
    }

    public static void Apply(Form form, bool dark) => Apply(form, dark ? AppTheme.Dark : AppTheme.Light);

    public static void Apply(Form form, AppTheme theme)
    {
        theme = ResolveTheme(theme);
        bool dark = theme != AppTheme.Light;
        isDark = dark;
        current = theme switch
        {
            AppTheme.Dark => DarkPalette,
            AppTheme.DarkBlue => DarkBluePalette,
            _ => LightPalette
        };

        try
        {
            _ = SetPreferredAppMode(dark ? 2 : 3); // 2 = ForceDark, 3 = ForceLight
            FlushMenuThemes();
        }
        catch { }

        ApplyWindowFrame(form, dark);
        if (!hooked.TryGetValue(form, out _))
        {
            hooked.Add(form, new object());
            form.HandleCreated += (_, _) => ApplyWindowFrame(form, isDark);
        }

        form.SuspendLayout();
        try
        {
            form.BackColor = current.Bg;
            form.ForeColor = current.Fg;
            if (!Equals(form.Font, BodyFont))
                form.Font = BodyFont;

            foreach (Control c in form.Controls)
                ApplyControl(c, dark);
        }
        finally
        {
            form.ResumeLayout(true);
        }

        form.Invalidate(true);
        form.Update();
    }

    static void ApplyWindowFrame(Form form, bool dark)
    {
        if (!form.IsHandleCreated) return;
        try
        {
            int useDark = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));

            int captionColor = ColorTranslator.ToWin32(current.Bg);
            _ = DwmSetWindowAttribute(form.Handle, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));

            int textColor = ColorTranslator.ToWin32(current.Fg);
            _ = DwmSetWindowAttribute(form.Handle, DWMWA_TEXT_COLOR, ref textColor, sizeof(int));

            int borderColor = ColorTranslator.ToWin32(current.Border);
            _ = DwmSetWindowAttribute(form.Handle, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
        }
        catch { }
    }

    static void ApplyControl(Control c, bool dark)
    {
        switch (c)
        {
            case ProgressBar pb:
                ApplyProgressBar(pb);
                return;

            case Button b:
                ApplyButton(b, dark);
                return;

            case ComboBox cb:
                ApplyComboBox(cb, dark);
                return;

            case DateTimePicker dtp:
                ApplyDateTimePicker(dtp, dark);
                return;

            case ListView lv:
                ApplyListView(lv, dark);
                return;

            case NumericUpDown nud:
                ApplyNumericUpDown(nud, dark);
                return;

            case TextBox tb:
                ApplyTextBox(tb, dark);
                return;

            case TreeView tv:
                ApplyTreeView(tv, dark);
                return;

            case CheckedListBox clb:
                ApplyListBox(clb, dark);
                return;

            case ListBox lb:
                ApplyListBox(lb, dark);
                return;

            case CheckBox chk:
                ApplyCheckBox(chk, dark);
                return;

            case GroupBox gb:
                ApplyGroupBox(gb);
                break;

            case Label l:
                ApplyLabel(l);
                return;

            case SplitContainer sc:
                ApplySplitContainer(sc);
                break;

            default:
                if (c.IsHandleCreated)
                    _ = SetWindowTheme(c.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
                c.BackColor = current.Bg;
                c.ForeColor = current.Fg;
                break;
        }

        foreach (Control child in c.Controls)
            ApplyControl(child, dark);
    }

    static bool IsPrimaryButton(Button b) =>
        b.Text.StartsWith("Scan ", StringComparison.Ordinal)
        || b.Text.StartsWith("Accept", StringComparison.Ordinal)
        || string.Equals(b.Text, "Apply", StringComparison.Ordinal);

    static void ApplyButton(Button b, bool dark)
    {
        if (b.IsHandleCreated)
            _ = SetWindowTheme(b.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);

        b.FlatStyle = FlatStyle.Flat;
        b.UseVisualStyleBackColor = false;
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Color.Transparent;
        b.FlatAppearance.MouseDownBackColor = Color.Transparent;
        b.BackColor = b.Parent?.BackColor ?? current.Bg;
        b.ForeColor = IsPrimaryButton(b) ? Color.White : current.Fg;
        b.Padding = new Padding(12, 4, 12, 4);
        if (b.MinimumSize.Height < 30)
            b.MinimumSize = new Size(b.MinimumSize.Width, 30);

        if (!hooked.TryGetValue(b, out _))
        {
            hooked.Add(b, new object());
            var state = buttonStates.GetOrCreateValue(b);

            b.MouseEnter += (_, _) => { state.Hovered = true; b.Invalidate(); };
            b.MouseLeave += (_, _) => { state.Hovered = false; state.Pressed = false; b.Invalidate(); };
            b.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { state.Pressed = true; b.Invalidate(); } };
            b.MouseUp += (_, _) => { state.Pressed = false; b.Invalidate(); };
            b.GotFocus += (_, _) => b.Invalidate();
            b.LostFocus += (_, _) => b.Invalidate();
            b.EnabledChanged += (_, _) => b.Invalidate();
            b.TextChanged += (_, _) => b.Invalidate();

            b.Paint += (_, e) => PaintFluentButton(b, state, e.Graphics);
        }

        b.Invalidate();
    }

    static void PaintFluentButton(Button b, ButtonVisualState state, Graphics g)
    {
        var p = current;
        bool primary = IsPrimaryButton(b);

        Color fill, border, text;
        if (!b.Enabled)
        {
            fill = primary ? p.AccentDisabledBg : p.SurfaceDisabled;
            border = primary ? p.AccentDisabledBg : p.Border;
            text = primary ? p.AccentDisabledFg : p.DisabledFg;
        }
        else if (primary)
        {
            fill = state.Pressed ? p.AccentPressed : state.Hovered ? p.AccentHover : p.Accent;
            border = fill;
            text = Color.White;
        }
        else
        {
            fill = state.Pressed ? p.SurfacePressed : state.Hovered ? p.SurfaceHover : p.SurfaceAlt;
            border = state.Hovered ? p.BorderStrong : p.Border;
            text = p.Fg;
        }

        var parentBg = b.Parent?.BackColor ?? p.Bg;
        using (var clearBrush = new SolidBrush(parentBg))
            g.FillRectangle(clearBrush, b.ClientRectangle);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(0.5f, 0.5f, Math.Max(1f, b.Width - 1.5f), Math.Max(1f, b.Height - 1.5f));
        using var path = CreateRoundedRect(rect, 5f);
        using (var fillBrush = new SolidBrush(fill))
            g.FillPath(fillBrush, path);
        using (var borderPen = new Pen(border, 1f))
            g.DrawPath(borderPen, path);

        if (b.Focused && b.Enabled)
        {
            var focusRect = new RectangleF(2f, 2f, Math.Max(1f, b.Width - 4.5f), Math.Max(1f, b.Height - 4.5f));
            using var focusPath = CreateRoundedRect(focusRect, 3.5f);
            using var focusPen = new Pen(primary ? Color.FromArgb(200, Color.White) : p.Accent, 1f);
            g.DrawPath(focusPen, focusPath);
        }

        var textRect = new Rectangle(6, 0, Math.Max(0, b.Width - 12), b.Height);
        TextRenderer.DrawText(g, b.Text, b.Font, textRect, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    static void ApplyComboBox(ComboBox cb, bool dark)
    {
        cb.FlatStyle = FlatStyle.Flat;
        cb.BackColor = current.Surface;
        cb.ForeColor = current.Fg;
        cb.DrawMode = DrawMode.OwnerDrawFixed;
        cb.ItemHeight = Math.Max(20, (int)Math.Ceiling(cb.Font.GetHeight()) + 4);

        ApplyComboBoxNativeTheme(cb, dark);

        if (!hooked.TryGetValue(cb, out _))
        {
            hooked.Add(cb, new object());
            cb.DrawItem += (_, e) =>
            {
                if (e.Index < 0 || e.Index >= cb.Items.Count) return;
                bool selected = (e.State & DrawItemState.Selected) != 0;
                var bg = selected ? current.Accent : current.Surface;
                var fg = selected ? Color.White : current.Fg;
                using var bgBrush = new SolidBrush(bg);
                e.Graphics.FillRectangle(bgBrush, e.Bounds);
                var text = cb.GetItemText(cb.Items[e.Index]);
                var textRect = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 10), e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, text, cb.Font, textRect, fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            cb.HandleCreated += (_, _) => ApplyComboBoxNativeTheme(cb, isDark);
            AttachNativeHook(cb, new FluentComboBoxHook(cb));
        }

        cb.Invalidate();
    }

    static void ApplyComboBoxNativeTheme(ComboBox cb, bool dark)
    {
        if (!cb.IsHandleCreated) return;
        _ = SetWindowTheme(cb.Handle, dark ? "DarkMode_CFD" : "Explorer", null);
        var info = new COMBOBOXINFO { cbSize = Marshal.SizeOf<COMBOBOXINFO>() };
        if (GetComboBoxInfo(cb.Handle, ref info))
        {
            if (info.hwndList != IntPtr.Zero)
                _ = SetWindowTheme(info.hwndList, dark ? "DarkMode_Explorer" : "Explorer", null);
            if (info.hwndEdit != IntPtr.Zero)
                _ = SetWindowTheme(info.hwndEdit, dark ? "DarkMode_Explorer" : "Explorer", null);
        }
    }

    static void ApplyDateTimePicker(DateTimePicker dtp, bool dark)
    {
        if (dtp.IsHandleCreated)
            _ = SetWindowTheme(dtp.Handle, dark ? "DarkMode_CFD" : "Explorer", null);

        dtp.CalendarMonthBackground = current.Surface;
        dtp.CalendarForeColor = current.Fg;
        dtp.CalendarTitleBackColor = current.Accent;
        dtp.CalendarTitleForeColor = Color.White;
        dtp.CalendarTrailingForeColor = current.Muted;

        if (!hooked.TryGetValue(dtp, out _))
        {
            hooked.Add(dtp, new object());
            dtp.DropDown += (_, _) =>
            {
                if (!dtp.IsHandleCreated) return;
                IntPtr cal = SendMessage(dtp.Handle, DTM_GETMONTHCAL, IntPtr.Zero, IntPtr.Zero);
                if (cal != IntPtr.Zero)
                    _ = SetWindowTheme(cal, "", "");
            };
            AttachNativeHook(dtp, new FluentDateTimePickerHook(dtp));
        }

        dtp.Invalidate();
    }

    static void ApplyProgressBar(ProgressBar pb)
    {
        if (!hooked.TryGetValue(pb, out _))
        {
            hooked.Add(pb, new object());
            AttachNativeHook(pb, new FluentProgressBarHook(pb));
            marqueeBars.Add(new WeakReference<ProgressBar>(pb));
            EnsureMarqueeTimer();
        }
        pb.Invalidate();
    }

    static void EnsureMarqueeTimer()
    {
        if (marqueeTimer is not null) return;
        marqueeTimer = new System.Windows.Forms.Timer { Interval = 30 };
        marqueeTimer.Tick += (_, _) =>
        {
            for (int i = marqueeBars.Count - 1; i >= 0; i--)
            {
                if (!marqueeBars[i].TryGetTarget(out var bar) || bar.IsDisposed)
                {
                    marqueeBars.RemoveAt(i);
                    continue;
                }
                if (bar.Visible && bar.Style == ProgressBarStyle.Marquee && bar.IsHandleCreated)
                    bar.Invalidate();
            }
        };
        marqueeTimer.Start();
    }

    public static void SetHighlightTerms(ListView lv, IReadOnlyList<string>? terms)
    {
        string[] normalized = terms is null || terms.Count == 0
            ? []
            : terms
                .Where(static t => !string.IsNullOrWhiteSpace(t))
                .Select(static t => t.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(static t => t.Length)
                .ToArray();

        if (highlightTerms.TryGetValue(lv, out var existing)
            && existing.Length == normalized.Length
            && existing.SequenceEqual(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        highlightTerms.AddOrUpdate(lv, normalized);
        if (lv.IsHandleCreated && !lv.IsDisposed)
            lv.Invalidate();
    }

    public static void StyleToolTip(ToolTip tip)
    {
        tip.BackColor = current.SurfaceAlt;
        tip.ForeColor = current.Fg;
        tip.OwnerDraw = true;

        if (!hookedToolTips.TryGetValue(tip, out _))
        {
            hookedToolTips.Add(tip, new object());
            tip.Popup += (_, e) =>
            {
                var text = e.AssociatedControl is not null ? tip.GetToolTip(e.AssociatedControl) : "";
                if (string.IsNullOrEmpty(text)) return;
                var measured = TextRenderer.MeasureText(text, BodyFont, new Size(360, 0),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                e.ToolTipSize = new Size(measured.Width + 16, measured.Height + 10);
            };
            tip.Draw += (_, e) =>
            {
                var p = current;
                using var bg = new SolidBrush(p.SurfaceAlt);
                using var border = new Pen(p.BorderStrong, 1f);
                e.Graphics.FillRectangle(bg, e.Bounds);
                e.Graphics.DrawRectangle(border, e.Bounds.X, e.Bounds.Y,
                    Math.Max(0, e.Bounds.Width - 1), Math.Max(0, e.Bounds.Height - 1));
                var textRect = new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 5,
                    Math.Max(0, e.Bounds.Width - 16), Math.Max(0, e.Bounds.Height - 10));
                TextRenderer.DrawText(e.Graphics, e.ToolTipText ?? "", BodyFont, textRect, p.Fg,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            };
        }
    }

    public static void AttachTooltip(Control c, ToolTip tip, string text)
    {
        StyleToolTip(tip);
        tip.SetToolTip(c, text);
    }

    static void ApplyListView(ListView lv, bool dark)
    {
        lv.BackColor = current.Surface;
        lv.ForeColor = current.Fg;
        lv.BorderStyle = BorderStyle.FixedSingle;
        lv.OwnerDraw = true;

        ApplyListViewNativeTheme(lv, dark);

        if (!hooked.TryGetValue(lv, out _))
        {
            hooked.Add(lv, new object());
            lv.HandleCreated += (_, _) => ApplyListViewNativeTheme(lv, isDark);

            lv.DrawColumnHeader += (_, e) =>
            {
                using var hBg = new SolidBrush(current.HeaderBg);
                using var sep = new Pen(current.Border);
                e.Graphics.FillRectangle(hBg, e.Bounds);
                e.Graphics.DrawLine(sep, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                e.Graphics.DrawLine(sep, e.Bounds.Right - 1, e.Bounds.Top + 5, e.Bounds.Right - 1, e.Bounds.Bottom - 5);

                var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
                flags |= e.Header?.TextAlign switch
                {
                    HorizontalAlignment.Right => TextFormatFlags.Right,
                    HorizontalAlignment.Center => TextFormatFlags.HorizontalCenter,
                    _ => TextFormatFlags.Left
                };
                var textRect = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 14), e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", SectionFont, textRect, current.Muted, flags);
            };
            lv.DrawItem += (_, e) => e.DrawDefault = true;
            lv.DrawSubItem += (_, e) => DrawListViewSubItem(lv, e);

            AttachNativeHook(lv, new DarkBorderHook(lv));
            AttachNativeHook(lv, new ListViewHeaderHook(lv));
        }

        lv.Invalidate(true);
    }

    static void DrawListViewSubItem(ListView lv, DrawListViewSubItemEventArgs e)
    {
        if (e.ColumnIndex != 0
            || e.Item is null
            || !highlightTerms.TryGetValue(lv, out var terms)
            || terms.Length == 0)
        {
            e.DrawDefault = true;
            return;
        }

        string text = e.SubItem?.Text ?? e.Item.Text;
        if (string.IsNullOrEmpty(text))
        {
            e.DrawDefault = true;
            return;
        }

        var spans = FindHighlightSpans(text, terms);
        if (spans.Count == 0)
        {
            e.DrawDefault = true;
            return;
        }

        var p = current;
        bool selected = e.Item.Selected;
        bool focused = lv.Focused;

        Color rowBg = selected
            ? (focused ? p.Accent : p.SurfaceHover)
            : p.Surface;
        Color textFg = selected && focused ? Color.White : p.Fg;
        Color matchBg = selected && focused
            ? Color.FromArgb(75, Color.White)
            : Color.FromArgb(isDark ? 76 : 44, p.Accent);

        using (var bgBrush = new SolidBrush(rowBg))
            e.Graphics.FillRectangle(bgBrush, e.Bounds);

        var normalFont = lv.Font ?? BodyFont;
        Font? transientBold = null;
        Font boldFont;
        if (Equals(normalFont, BodyFont))
            boldFont = BoldBodyFont;
        else if (normalFont.Bold)
            boldFont = normalFont;
        else
            boldFont = transientBold = new Font(normalFont, FontStyle.Bold);

        var state = e.Graphics.Save();
        try
        {
            var clipRect = new Rectangle(e.Bounds.X + 2, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 4), e.Bounds.Height);
            e.Graphics.SetClip(clipRect);

            const TextFormatFlags measureFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            const TextFormatFlags drawFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;

            int x = e.Bounds.X + 6;
            int maxRight = e.Bounds.Right - 4;
            int pos = 0;

            using var matchBrush = new SolidBrush(matchBg);

            foreach (var (start, end) in spans)
            {
                if (x >= maxRight) break;

                if (start > pos)
                {
                    string prefix = text[pos..start];
                    var size = TextRenderer.MeasureText(e.Graphics, prefix, normalFont, new Size(int.MaxValue, e.Bounds.Height), measureFlags);
                    var segRect = new Rectangle(x, e.Bounds.Y, Math.Min(size.Width, maxRight - x), e.Bounds.Height);
                    TextRenderer.DrawText(e.Graphics, prefix, normalFont, segRect, textFg, drawFlags);
                    x += size.Width;
                }

                if (x >= maxRight) break;

                string match = text[start..end];
                var matchSize = TextRenderer.MeasureText(e.Graphics, match, boldFont, new Size(int.MaxValue, e.Bounds.Height), measureFlags);
                int drawWidth = Math.Min(matchSize.Width, maxRight - x);

                var pillRect = new RectangleF(x - 1f, e.Bounds.Y + 2f, Math.Max(1f, drawWidth + 2f), Math.Max(1f, e.Bounds.Height - 4f));
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pillPath = CreateRoundedRect(pillRect, 2.5f))
                    e.Graphics.FillPath(matchBrush, pillPath);

                var matchRect = new Rectangle(x, e.Bounds.Y, drawWidth, e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, match, boldFont, matchRect, textFg, drawFlags);
                x += matchSize.Width;
                pos = end;
            }

            if (pos < text.Length && x < maxRight)
            {
                string suffix = text[pos..];
                var suffixRect = new Rectangle(x, e.Bounds.Y, maxRight - x, e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, suffix, normalFont, suffixRect, textFg,
                    drawFlags | TextFormatFlags.EndEllipsis);
            }
        }
        finally
        {
            e.Graphics.Restore(state);
            transientBold?.Dispose();
        }
    }

    static List<(int Start, int End)> FindHighlightSpans(string text, string[] terms)
    {
        List<(int Start, int End)> raw = [];
        foreach (var term in terms)
        {
            int idx = 0;
            while (idx < text.Length)
            {
                int found = text.IndexOf(term, idx, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;
                raw.Add((found, found + term.Length));
                idx = found + term.Length;
            }
        }

        if (raw.Count <= 1) return raw;

        raw.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
        List<(int Start, int End)> merged = [raw[0]];
        for (int i = 1; i < raw.Count; i++)
        {
            var prev = merged[^1];
            var cur = raw[i];
            if (cur.Start <= prev.End)
                merged[^1] = (prev.Start, Math.Max(prev.End, cur.End));
            else
                merged.Add(cur);
        }
        return merged;
    }

    static void ApplyListViewNativeTheme(ListView lv, bool dark)
    {
        if (!lv.IsHandleCreated) return;
        _ = SetWindowTheme(lv.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
        IntPtr header = SendMessage(lv.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
        if (header != IntPtr.Zero)
            _ = SetWindowTheme(header, dark ? "DarkMode_ItemsView" : "Explorer", null);
    }

    static void ApplyTextBox(TextBox tb, bool dark)
    {
        if (tb.IsHandleCreated)
            _ = SetWindowTheme(tb.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
        tb.BackColor = tb.ReadOnly ? current.SurfaceReadOnly : current.Surface;
        tb.ForeColor = current.Fg;
        tb.BorderStyle = BorderStyle.FixedSingle;

        if (!hooked.TryGetValue(tb, out _))
        {
            hooked.Add(tb, new object());
            tb.HandleCreated += (_, _) => _ = SetWindowTheme(tb.Handle, isDark ? "DarkMode_Explorer" : "Explorer", null);
            AttachNativeHook(tb, new DarkBorderHook(tb));
        }

        tb.Invalidate();
    }

    static void ApplyTreeView(TreeView tv, bool dark)
    {
        if (tv.IsHandleCreated)
            _ = SetWindowTheme(tv.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
        tv.BackColor = current.Surface;
        tv.ForeColor = current.Fg;
        tv.LineColor = current.Border;
        tv.BorderStyle = BorderStyle.FixedSingle;

        if (!hooked.TryGetValue(tv, out _))
        {
            hooked.Add(tv, new object());
            tv.HandleCreated += (_, _) => _ = SetWindowTheme(tv.Handle, isDark ? "DarkMode_Explorer" : "Explorer", null);
            AttachNativeHook(tv, new DarkBorderHook(tv));
        }

        tv.Invalidate();
    }

    static void ApplyListBox(ListBox lb, bool dark)
    {
        if (lb.IsHandleCreated)
            _ = SetWindowTheme(lb.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
        lb.BackColor = current.Surface;
        lb.ForeColor = current.Fg;
        lb.BorderStyle = BorderStyle.FixedSingle;

        if (!hooked.TryGetValue(lb, out _))
        {
            hooked.Add(lb, new object());
            lb.HandleCreated += (_, _) => _ = SetWindowTheme(lb.Handle, isDark ? "DarkMode_Explorer" : "Explorer", null);
            AttachNativeHook(lb, new DarkBorderHook(lb));
        }

        lb.Invalidate();
    }

    static void ApplyNumericUpDown(NumericUpDown nud, bool dark)
    {
        if (nud.IsHandleCreated)
            _ = SetWindowTheme(nud.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
        nud.BackColor = current.Surface;
        nud.ForeColor = current.Fg;
        nud.BorderStyle = BorderStyle.FixedSingle;

        foreach (Control child in nud.Controls)
        {
            if (child.IsHandleCreated)
                _ = SetWindowTheme(child.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
            child.BackColor = current.Surface;
            child.ForeColor = current.Fg;
        }

        if (!hooked.TryGetValue(nud, out _))
        {
            hooked.Add(nud, new object());
            nud.HandleCreated += (_, _) => _ = SetWindowTheme(nud.Handle, isDark ? "DarkMode_Explorer" : "Explorer", null);
            AttachNativeHook(nud, new DarkBorderHook(nud));
            if (nud.Controls.Count > 0)
                AttachNativeHook(nud.Controls[0], new FluentUpDownButtonsHook(nud.Controls[0], nud));
        }

        nud.Invalidate(true);
    }

    static void ApplyCheckBox(CheckBox chk, bool dark)
    {
        if (chk.IsHandleCreated)
            _ = SetWindowTheme(chk.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
        chk.BackColor = chk.Parent?.BackColor ?? current.Bg;
        chk.ForeColor = chk.Enabled ? current.Fg : current.DisabledFg;

        if (!hooked.TryGetValue(chk, out _))
        {
            hooked.Add(chk, new object());
            chk.EnabledChanged += (_, _) =>
            {
                chk.ForeColor = chk.Enabled ? current.Fg : current.DisabledFg;
                chk.Invalidate();
            };
            chk.Paint += (_, e) =>
            {
                if (chk.Enabled) return;
                PaintDisabledCheckBox(chk, e.Graphics);
            };
        }

        chk.Invalidate();
    }

    static void PaintDisabledCheckBox(CheckBox chk, Graphics g)
    {
        var p = current;
        using (var bg = new SolidBrush(chk.Parent?.BackColor ?? p.Bg))
            g.FillRectangle(bg, chk.ClientRectangle);

        float scale = chk.DeviceDpi / 96f;
        int boxSize = Math.Max(13, (int)Math.Round(14 * scale));
        int boxY = (chk.Height - boxSize) / 2;
        var boxRect = new RectangleF(1f, boxY, boxSize - 1f, boxSize - 1f);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = CreateRoundedRect(boxRect, 3f);
        using (var fill = new SolidBrush(p.SurfaceDisabled))
            g.FillPath(fill, path);
        using (var pen = new Pen(p.Border, 1f))
            g.DrawPath(pen, path);

        if (chk.Checked)
            DrawCheckmark(g, new Rectangle(1, boxY, boxSize, boxSize), p.DisabledFg);

        var textRect = new Rectangle(boxSize + 6, 0, Math.Max(0, chk.Width - boxSize - 6), chk.Height);
        TextRenderer.DrawText(g, chk.Text, chk.Font, textRect, p.DisabledFg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    static void ApplyGroupBox(GroupBox gb)
    {
        gb.BackColor = current.Bg;
        gb.ForeColor = current.Fg;
        if (!Equals(gb.Font, SectionFont))
            gb.Font = SectionFont;

        if (!hooked.TryGetValue(gb, out _))
        {
            hooked.Add(gb, new object());
            gb.Paint += (_, e) =>
            {
                var p = current;
                using (var bg = new SolidBrush(gb.Parent?.BackColor ?? p.Bg))
                    e.Graphics.FillRectangle(bg, gb.ClientRectangle);

                var textSize = TextRenderer.MeasureText(e.Graphics, gb.Text, SectionFont);
                int topOffset = Math.Max(8, textSize.Height / 2);
                var rect = new RectangleF(0.5f, topOffset + 0.5f, Math.Max(1f, gb.Width - 1.5f), Math.Max(1f, gb.Height - topOffset - 1.5f));
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = CreateRoundedRect(rect, 6f);
                using (var pen = new Pen(p.Border, 1f))
                    e.Graphics.DrawPath(pen, path);

                var labelRect = new Rectangle(10, 0, textSize.Width + 6, textSize.Height + 2);
                using (var titleBg = new SolidBrush(gb.Parent?.BackColor ?? p.Bg))
                    e.Graphics.FillRectangle(titleBg, labelRect);
                TextRenderer.DrawText(e.Graphics, gb.Text, SectionFont, labelRect, p.Fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            };
        }

        gb.Invalidate();
    }

    static void ApplyLabel(Label l)
    {
        bool isSection = Equals(l.Tag, "section-title") || l.Font.Bold;
        bool isPrimary = Equals(l.Tag, "primary-label");
        if (isSection && !Equals(l.Font, SectionFont))
            l.Font = SectionFont;
        else if (!isSection && !Equals(l.Font, BodyFont))
            l.Font = BodyFont;

        l.BackColor = Color.Transparent;
        l.ForeColor = (isSection || isPrimary) ? current.Fg : current.Muted;
    }

    static void ApplySplitContainer(SplitContainer sc)
    {
        sc.BackColor = current.Bg;
        sc.Panel1.BackColor = current.Bg;
        sc.Panel2.BackColor = current.Bg;

        if (!hooked.TryGetValue(sc, out _))
        {
            hooked.Add(sc, new object());
            sc.Paint += (_, e) =>
            {
                using var pen = new Pen(current.Border, 1f);
                if (sc.Orientation == Orientation.Vertical)
                {
                    int x = sc.SplitterDistance + sc.SplitterWidth / 2;
                    e.Graphics.DrawLine(pen, x, 6, x, sc.Height - 6);
                }
                else
                {
                    int y = sc.SplitterDistance + sc.SplitterWidth / 2;
                    e.Graphics.DrawLine(pen, 6, y, sc.Width - 6, y);
                }
            };
        }

        sc.Invalidate();
    }

    static void AttachNativeHook(Control control, NativeWindow hook)
    {
        var list = nativeHooks.GetOrCreateValue(control);
        list.Add(hook);
    }

    internal static GraphicsPath CreateRoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(rect);
            return path;
        }
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    static void DrawChevronDown(Graphics g, float cx, float cy, float size, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float half = size * 0.5f;
        float quarter = size * 0.25f;
        g.DrawLines(pen, [new PointF(cx - half, cy - quarter), new PointF(cx, cy + quarter), new PointF(cx + half, cy - quarter)]);
    }

    static void DrawChevronUp(Graphics g, float cx, float cy, float size, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float half = size * 0.5f;
        float quarter = size * 0.25f;
        g.DrawLines(pen, [new PointF(cx - half, cy + quarter), new PointF(cx, cy - quarter), new PointF(cx + half, cy + quarter)]);
    }

    static void DrawCheckmark(Graphics g, Rectangle rect, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float x = rect.X, y = rect.Y, w = rect.Width, h = rect.Height;
        g.DrawLines(pen,
        [
            new PointF(x + w * 0.24f, y + h * 0.52f),
            new PointF(x + w * 0.44f, y + h * 0.72f),
            new PointF(x + w * 0.78f, y + h * 0.30f)
        ]);
    }

    sealed class DarkBorderHook : NativeWindow
    {
        readonly Control control;

        public DarkBorderHook(Control control)
        {
            this.control = control;
            control.HandleCreated += (_, _) => { if (control.IsHandleCreated) AssignHandle(control.Handle); };
            control.HandleDestroyed += (_, _) => ReleaseHandle();
            control.GotFocus += (_, _) => RedrawBorder();
            control.LostFocus += (_, _) => RedrawBorder();
            if (control.IsHandleCreated) AssignHandle(control.Handle);
        }

        void RedrawBorder()
        {
            if (Handle == IntPtr.Zero || control.IsDisposed) return;
            PaintBorder();
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg is WM_NCPAINT or WM_PAINT)
                PaintBorder();
        }

        void PaintBorder()
        {
            if (Handle == IntPtr.Zero || control.IsDisposed) return;
            IntPtr hdc = GetWindowDC(Handle);
            if (hdc == IntPtr.Zero) return;
            try
            {
                if (!GetWindowRect(Handle, out var rect)) return;
                using var g = Graphics.FromHdc(hdc);
                var borderColor = control.Focused && control.Enabled ? current.Accent : current.Border;
                using var pen = new Pen(borderColor, 1f);
                g.DrawRectangle(pen, 0, 0, Math.Max(0, rect.Width - 1), Math.Max(0, rect.Height - 1));
            }
            catch { }
            finally
            {
                _ = ReleaseDC(Handle, hdc);
            }
        }
    }

    sealed class ListViewHeaderHook : NativeWindow
    {
        readonly ListView listView;
        readonly HeaderBackgroundHook headerHook = new();

        public ListViewHeaderHook(ListView listView)
        {
            this.listView = listView;
            listView.HandleCreated += (_, _) => Attach();
            listView.HandleDestroyed += (_, _) => Detach();
            if (listView.IsHandleCreated) Attach();
        }

        void Attach()
        {
            if (!listView.IsHandleCreated) return;
            AssignHandle(listView.Handle);
            AttachHeader();
        }

        void AttachHeader()
        {
            IntPtr header = SendMessage(listView.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
            if (header != IntPtr.Zero && headerHook.Handle != header)
            {
                headerHook.ReleaseHandle();
                headerHook.AssignHandle(header);
            }
        }

        void Detach()
        {
            headerHook.ReleaseHandle();
            ReleaseHandle();
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_THEMECHANGED)
                AttachHeader();
        }

        sealed class HeaderBackgroundHook : NativeWindow
        {
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_ERASEBKGND && m.WParam != IntPtr.Zero)
                {
                    try
                    {
                        if (GetWindowRect(Handle, out var rect))
                        {
                            using var g = Graphics.FromHdc(m.WParam);
                            using var brush = new SolidBrush(current.HeaderBg);
                            using var pen = new Pen(current.Border);
                            g.FillRectangle(brush, 0, 0, rect.Width, rect.Height);
                            g.DrawLine(pen, 0, rect.Height - 1, rect.Width, rect.Height - 1);
                            m.Result = (IntPtr)1;
                            return;
                        }
                    }
                    catch { }
                }
                base.WndProc(ref m);
            }
        }
    }

    sealed class FluentComboBoxHook : NativeWindow
    {
        readonly ComboBox combo;
        bool hovered;

        public FluentComboBoxHook(ComboBox combo)
        {
            this.combo = combo;
            combo.HandleCreated += (_, _) => { if (combo.IsHandleCreated) AssignHandle(combo.Handle); };
            combo.HandleDestroyed += (_, _) => ReleaseHandle();
            combo.MouseEnter += (_, _) => { hovered = true; combo.Invalidate(); };
            combo.MouseLeave += (_, _) => { hovered = false; combo.Invalidate(); };
            combo.GotFocus += (_, _) => combo.Invalidate();
            combo.LostFocus += (_, _) => combo.Invalidate();
            combo.DropDown += (_, _) => combo.Invalidate();
            combo.DropDownClosed += (_, _) => combo.Invalidate();
            combo.EnabledChanged += (_, _) => combo.Invalidate();
            if (combo.IsHandleCreated) AssignHandle(combo.Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (combo.DropDownStyle == ComboBoxStyle.DropDownList)
            {
                if (m.Msg == WM_ERASEBKGND)
                {
                    m.Result = (IntPtr)1;
                    return;
                }
                if (m.Msg == WM_PAINT)
                {
                    IntPtr hdc = BeginPaint(m.HWnd, out var ps);
                    try
                    {
                        if (hdc != IntPtr.Zero && combo.Width > 0 && combo.Height > 0)
                        {
                            using var bmp = new Bitmap(combo.Width, combo.Height);
                            using (var g = Graphics.FromImage(bmp))
                                PaintDropDownList(g);
                            using var target = Graphics.FromHdc(hdc);
                            target.DrawImageUnscaled(bmp, 0, 0);
                        }
                    }
                    catch { }
                    finally
                    {
                        _ = EndPaint(m.HWnd, ref ps);
                    }
                    return;
                }
            }

            base.WndProc(ref m);

            if (m.Msg == WM_PAINT && combo.DropDownStyle != ComboBoxStyle.DropDownList)
                PaintDropDownChrome();
        }

        void PaintDropDownList(Graphics g)
        {
            var p = current;
            var parentBg = combo.Parent?.BackColor ?? p.Bg;
            using (var clear = new SolidBrush(parentBg))
                g.FillRectangle(clear, combo.ClientRectangle);

            var fill = !combo.Enabled ? p.SurfaceDisabled : hovered ? p.SurfaceHover : p.Surface;
            var border = combo.Focused && combo.Enabled ? p.Accent : hovered ? p.BorderStrong : p.Border;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new RectangleF(0.5f, 0.5f, Math.Max(1f, combo.Width - 1.5f), Math.Max(1f, combo.Height - 1.5f));
            using var path = CreateRoundedRect(rect, 4f);
            using (var brush = new SolidBrush(fill))
                g.FillPath(brush, path);
            using (var pen = new Pen(border, 1f))
                g.DrawPath(pen, path);

            string text = combo.SelectedIndex >= 0 ? combo.GetItemText(combo.SelectedItem) ?? "" : combo.Text;
            var textRect = new Rectangle(8, 0, Math.Max(0, combo.Width - 28), combo.Height);
            TextRenderer.DrawText(g, text, combo.Font, textRect, combo.Enabled ? p.Fg : p.DisabledFg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            DrawChevronDown(g, combo.Width - 13f, combo.Height / 2f, 7f, combo.Enabled ? p.Muted : p.DisabledFg);
        }

        void PaintDropDownChrome()
        {
            if (Handle == IntPtr.Zero || combo.IsDisposed || combo.Width <= 0 || combo.Height <= 0) return;
            try
            {
                var p = current;
                using var g = Graphics.FromHwnd(Handle);

                var info = new COMBOBOXINFO { cbSize = Marshal.SizeOf<COMBOBOXINFO>() };
                int buttonLeft = combo.Width - 20;
                if (GetComboBoxInfo(Handle, ref info) && info.rcButton.Width > 0)
                    buttonLeft = info.rcButton.Left;

                var buttonRect = new Rectangle(buttonLeft - 1, 1, combo.Width - buttonLeft, combo.Height - 2);
                using (var btnBrush = new SolidBrush(!combo.Enabled ? p.SurfaceDisabled : hovered ? p.SurfaceHover : p.Surface))
                    g.FillRectangle(btnBrush, buttonRect);

                DrawChevronDown(g, buttonRect.X + buttonRect.Width / 2f, combo.Height / 2f, 7f,
                    combo.Enabled ? p.Muted : p.DisabledFg);

                var border = combo.Focused && combo.Enabled ? p.Accent : hovered ? p.BorderStrong : p.Border;
                using (var innerPen = new Pen(p.Surface, 2f))
                    g.DrawRectangle(innerPen, 1, 1, Math.Max(0, combo.Width - 3), Math.Max(0, combo.Height - 3));
                using (var borderPen = new Pen(border, 1f))
                    g.DrawRectangle(borderPen, 0, 0, Math.Max(0, combo.Width - 1), Math.Max(0, combo.Height - 1));
            }
            catch { }
        }
    }

    sealed class FluentDateTimePickerHook : NativeWindow
    {
        readonly DateTimePicker picker;
        bool hovered;

        public FluentDateTimePickerHook(DateTimePicker picker)
        {
            this.picker = picker;
            picker.HandleCreated += (_, _) => { if (picker.IsHandleCreated) AssignHandle(picker.Handle); };
            picker.HandleDestroyed += (_, _) => ReleaseHandle();
            picker.MouseEnter += (_, _) => { hovered = true; picker.Invalidate(); };
            picker.MouseLeave += (_, _) => { hovered = false; picker.Invalidate(); };
            picker.GotFocus += (_, _) => picker.Invalidate();
            picker.LostFocus += (_, _) => picker.Invalidate();
            picker.ValueChanged += (_, _) => picker.Invalidate();
            picker.DropDown += (_, _) => picker.Invalidate();
            picker.CloseUp += (_, _) => picker.Invalidate();
            picker.EnabledChanged += (_, _) => picker.Invalidate();
            if (picker.IsHandleCreated) AssignHandle(picker.Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_ERASEBKGND)
            {
                m.Result = (IntPtr)1;
                return;
            }
            if (m.Msg == WM_PAINT)
            {
                IntPtr hdc = BeginPaint(m.HWnd, out var ps);
                try
                {
                    if (hdc != IntPtr.Zero && picker.Width > 0 && picker.Height > 0)
                    {
                        using var bmp = new Bitmap(picker.Width, picker.Height);
                        using (var g = Graphics.FromImage(bmp))
                            PaintPicker(g);
                        using var target = Graphics.FromHdc(hdc);
                        target.DrawImageUnscaled(bmp, 0, 0);
                    }
                }
                catch { }
                finally
                {
                    _ = EndPaint(m.HWnd, ref ps);
                }
                return;
            }

            base.WndProc(ref m);
        }

        void PaintPicker(Graphics g)
        {
            var p = current;
            var parentBg = picker.Parent?.BackColor ?? p.Bg;
            using (var clear = new SolidBrush(parentBg))
                g.FillRectangle(clear, picker.ClientRectangle);

            var fill = !picker.Enabled ? p.SurfaceDisabled : hovered ? p.SurfaceHover : p.Surface;
            var border = picker.Focused && picker.Enabled ? p.Accent : hovered ? p.BorderStrong : p.Border;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new RectangleF(0.5f, 0.5f, Math.Max(1f, picker.Width - 1.5f), Math.Max(1f, picker.Height - 1.5f));
            using var path = CreateRoundedRect(rect, 4f);
            using (var brush = new SolidBrush(fill))
                g.FillPath(brush, path);
            using (var pen = new Pen(border, 1f))
                g.DrawPath(pen, path);

            float scale = picker.DeviceDpi / 96f;
            int textX = (int)Math.Round(7 * scale);

            if (picker.ShowCheckBox)
            {
                int boxSize = Math.Max(13, (int)Math.Round(14 * scale));
                int boxX = (int)Math.Round(5 * scale);
                int boxY = (picker.Height - boxSize) / 2;
                var boxRect = new RectangleF(boxX, boxY, boxSize - 1f, boxSize - 1f);
                using var boxPath = CreateRoundedRect(boxRect, 3f);

                if (picker.Checked)
                {
                    using var cbFill = new SolidBrush(picker.Enabled ? p.Accent : p.AccentDisabledBg);
                    g.FillPath(cbFill, boxPath);
                    DrawCheckmark(g, new Rectangle(boxX, boxY, boxSize, boxSize), picker.Enabled ? Color.White : p.AccentDisabledFg);
                }
                else
                {
                    using var cbFill = new SolidBrush(p.Surface);
                    using var cbPen = new Pen(p.BorderStrong, 1f);
                    g.FillPath(cbFill, boxPath);
                    g.DrawPath(cbPen, boxPath);
                }

                textX = boxX + boxSize + (int)Math.Round(6 * scale);
            }

            int rightReserve = (int)Math.Round(22 * scale);
            var textRect = new Rectangle(textX, 0, Math.Max(0, picker.Width - textX - rightReserve), picker.Height);
            var textColor = picker.Enabled && (!picker.ShowCheckBox || picker.Checked) ? p.Fg : p.Muted;
            TextRenderer.DrawText(g, picker.Text, picker.Font, textRect, textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            DrawChevronDown(g, picker.Width - 12f * scale, picker.Height / 2f, 6.5f * scale,
                picker.Enabled ? p.Muted : p.DisabledFg);
        }
    }

    sealed class FluentProgressBarHook : NativeWindow
    {
        readonly ProgressBar bar;

        public FluentProgressBarHook(ProgressBar bar)
        {
            this.bar = bar;
            bar.HandleCreated += (_, _) => { if (bar.IsHandleCreated) AssignHandle(bar.Handle); };
            bar.HandleDestroyed += (_, _) => ReleaseHandle();
            if (bar.IsHandleCreated) AssignHandle(bar.Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_ERASEBKGND)
            {
                m.Result = (IntPtr)1;
                return;
            }
            if (m.Msg == WM_PAINT)
            {
                IntPtr hdc = BeginPaint(m.HWnd, out var ps);
                try
                {
                    if (hdc != IntPtr.Zero && bar.Width > 0 && bar.Height > 0)
                    {
                        using var bmp = new Bitmap(bar.Width, bar.Height);
                        using (var g = Graphics.FromImage(bmp))
                            PaintBar(g);
                        using var target = Graphics.FromHdc(hdc);
                        target.DrawImageUnscaled(bmp, 0, 0);
                    }
                }
                catch { }
                finally
                {
                    _ = EndPaint(m.HWnd, ref ps);
                }
                return;
            }

            base.WndProc(ref m);
        }

        void PaintBar(Graphics g)
        {
            var p = current;
            var parentBg = bar.Parent?.BackColor ?? p.Bg;
            using (var clear = new SolidBrush(parentBg))
                g.FillRectangle(clear, bar.ClientRectangle);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            float trackHeight = Math.Clamp(bar.Height - 8f, 5f, 8f);
            float trackY = (bar.Height - trackHeight) / 2f;
            var trackRect = new RectangleF(0.5f, trackY, Math.Max(1f, bar.Width - 1f), trackHeight);
            float radius = trackHeight / 2f;

            using var trackPath = CreateRoundedRect(trackRect, radius);
            using (var trackBrush = new SolidBrush(p.TrackBg))
                g.FillPath(trackBrush, trackPath);

            g.SetClip(trackPath);
            using var accentBrush = new SolidBrush(p.Accent);

            if (bar.Style == ProgressBarStyle.Marquee)
            {
                float pillWidth = Math.Max(24f, trackRect.Width * 0.32f);
                double cycle = (Environment.TickCount64 % 1400L) / 1400.0;
                float x = trackRect.X - pillWidth + (float)(cycle * (trackRect.Width + pillWidth));
                var pillRect = new RectangleF(x, trackRect.Y, pillWidth, trackRect.Height);
                using var pillPath = CreateRoundedRect(pillRect, radius);
                g.FillPath(accentBrush, pillPath);
            }
            else if (bar.Maximum > bar.Minimum && bar.Value > bar.Minimum)
            {
                double frac = Math.Clamp((double)(bar.Value - bar.Minimum) / (bar.Maximum - bar.Minimum), 0.0, 1.0);
                float fillWidth = Math.Max(trackHeight, (float)(trackRect.Width * frac));
                var fillRect = new RectangleF(trackRect.X, trackRect.Y, fillWidth, trackRect.Height);
                using var fillPath = CreateRoundedRect(fillRect, radius);
                g.FillPath(accentBrush, fillPath);
            }
        }
    }

    sealed class FluentUpDownButtonsHook : NativeWindow
    {
        readonly Control buttons;
        readonly NumericUpDown owner;

        public FluentUpDownButtonsHook(Control buttons, NumericUpDown owner)
        {
            this.buttons = buttons;
            this.owner = owner;
            buttons.HandleCreated += (_, _) => { if (buttons.IsHandleCreated) AssignHandle(buttons.Handle); };
            buttons.HandleDestroyed += (_, _) => ReleaseHandle();
            owner.EnabledChanged += (_, _) => buttons.Invalidate();
            if (buttons.IsHandleCreated) AssignHandle(buttons.Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_ERASEBKGND)
            {
                m.Result = (IntPtr)1;
                return;
            }
            if (m.Msg == WM_PAINT)
            {
                IntPtr hdc = BeginPaint(m.HWnd, out var ps);
                try
                {
                    if (hdc != IntPtr.Zero && buttons.Width > 0 && buttons.Height > 0)
                    {
                        using var bmp = new Bitmap(buttons.Width, buttons.Height);
                        using (var g = Graphics.FromImage(bmp))
                            PaintSpinButtons(g);
                        using var target = Graphics.FromHdc(hdc);
                        target.DrawImageUnscaled(bmp, 0, 0);
                    }
                }
                catch { }
                finally
                {
                    _ = EndPaint(m.HWnd, ref ps);
                }
                return;
            }

            base.WndProc(ref m);
        }

        void PaintSpinButtons(Graphics g)
        {
            var p = current;
            var bg = owner.Enabled ? p.SurfaceAlt : p.SurfaceDisabled;
            var fg = owner.Enabled ? p.Muted : p.DisabledFg;

            using (var fill = new SolidBrush(bg))
                g.FillRectangle(fill, 0, 0, buttons.Width, buttons.Height);

            int mid = buttons.Height / 2;
            using (var sep = new Pen(p.Border, 1f))
            {
                g.DrawLine(sep, 0, 0, 0, buttons.Height);
                g.DrawLine(sep, 0, mid, buttons.Width, mid);
            }

            float cx = buttons.Width / 2f;
            DrawChevronUp(g, cx, mid / 2f + 0.5f, 5.5f, fg);
            DrawChevronDown(g, cx, mid + (buttons.Height - mid) / 2f - 0.5f, 5.5f, fg);
        }
    }
}
