using System.Runtime.InteropServices;

namespace FileInventory;

static class Theme
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? pszSubAppName, string? pszSubIdList);

    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    static extern int SetPreferredAppMode(int appMode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    static extern void FlushMenuThemes();

    static readonly HashSet<Control> hooked = [];
    static bool isDark;
    static Color currentSurface, currentFg, currentMuted, currentBorder, currentHeaderBg;

    public static void Apply(Form form, bool dark)
    {
        isDark = dark;
        try
        {
            _ = SetPreferredAppMode(dark ? 2 : 3); // 2 = ForceDark, 3 = ForceLight
            FlushMenuThemes();
        }
        catch { }

        if (form.IsHandleCreated)
        {
            int useDark = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(form.Handle, 20, ref useDark, sizeof(int));
        }

        var bg = dark ? Color.FromArgb(24, 24, 27) : Color.FromArgb(243, 244, 246);
        var surface = dark ? Color.FromArgb(36, 36, 40) : Color.White;
        var headerBg = dark ? Color.FromArgb(44, 44, 48) : Color.FromArgb(236, 238, 242);
        var fg = dark ? Color.FromArgb(244, 244, 245) : Color.FromArgb(24, 24, 27);
        var muted = dark ? Color.FromArgb(161, 161, 170) : Color.FromArgb(100, 100, 110);
        var accent = dark ? Color.FromArgb(10, 132, 255) : Color.FromArgb(0, 103, 192);
        var border = dark ? Color.FromArgb(63, 63, 70) : Color.FromArgb(212, 212, 216);

        currentSurface = surface;
        currentHeaderBg = headerBg;
        currentFg = fg;
        currentMuted = muted;
        currentBorder = border;

        form.BackColor = bg;
        form.ForeColor = fg;
        form.Font = new Font("Segoe UI", 9.5f);

        foreach (Control c in form.Controls)
            ApplyControl(c, dark, bg, surface, fg, muted, accent, border);

        form.Invalidate(true);
    }

    static void ApplyControl(Control c, bool dark, Color bg, Color surface, Color fg, Color muted, Color accent, Color border)
    {
        switch (c)
        {
            case ProgressBar pb:
                if (pb.IsHandleCreated) _ = SetWindowTheme(pb.Handle, null, null);
                break;

            case Button b:
                if (b.IsHandleCreated) _ = SetWindowTheme(b.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
                b.FlatStyle = FlatStyle.Flat;
                b.UseVisualStyleBackColor = false;
                bool isPrimary = b.Text.StartsWith("Scan ", StringComparison.Ordinal) || b.Text.StartsWith("Accept", StringComparison.Ordinal);
                b.BackColor = isPrimary ? accent : surface;
                b.ForeColor = isPrimary ? Color.White : fg;
                b.FlatAppearance.BorderColor = isPrimary ? accent : border;
                b.FlatAppearance.BorderSize = 1;
                b.FlatAppearance.MouseOverBackColor = isPrimary
                    ? Color.FromArgb(Math.Min(255, accent.R + 20), Math.Min(255, accent.G + 20), Math.Min(255, accent.B + 20))
                    : (dark ? Color.FromArgb(55, 55, 62) : Color.FromArgb(230, 233, 238));
                b.Padding = new Padding(10, 4, 10, 4);
                if (hooked.Add(b))
                {
                    b.Paint += (_, e) =>
                    {
                        if (b.Enabled) return;
                        using var bgBrush = new SolidBrush(isDark ? Color.FromArgb(32, 32, 36) : Color.FromArgb(235, 236, 240));
                        using var pen = new Pen(currentBorder);
                        e.Graphics.FillRectangle(bgBrush, b.ClientRectangle);
                        e.Graphics.DrawRectangle(pen, 0, 0, b.Width - 1, b.Height - 1);
                        TextRenderer.DrawText(e.Graphics, b.Text, b.Font, b.ClientRectangle, currentMuted,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    };
                }
                break;

            case ComboBox cb:
                cb.FlatStyle = FlatStyle.Standard;
                if (cb.IsHandleCreated) _ = SetWindowTheme(cb.Handle, dark ? "DarkMode_CFD" : "Explorer", null);
                cb.BackColor = surface;
                cb.ForeColor = fg;
                break;

            case DateTimePicker dtp:
                if (dtp.IsHandleCreated) _ = SetWindowTheme(dtp.Handle, dark ? "DarkMode_CFD" : "Explorer", null);
                dtp.CalendarMonthBackground = surface;
                dtp.CalendarForeColor = fg;
                dtp.CalendarTitleBackColor = accent;
                dtp.CalendarTitleForeColor = Color.White;
                break;

            case ListView lv:
                if (lv.IsHandleCreated) _ = SetWindowTheme(lv.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
                lv.BackColor = surface;
                lv.ForeColor = fg;
                lv.OwnerDraw = true;
                if (hooked.Add(lv))
                {
                    lv.DrawColumnHeader += (_, e) =>
                    {
                        using var hBg = new SolidBrush(currentHeaderBg);
                        using var sep = new Pen(currentBorder);
                        e.Graphics.FillRectangle(hBg, e.Bounds);
                        e.Graphics.DrawLine(sep, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                        e.Graphics.DrawLine(sep, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 4);
                        var textRect = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 10), e.Bounds.Height);
                        TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", lv.Font, textRect, currentFg,
                            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                    };
                    lv.DrawItem += (_, e) => e.DrawDefault = true;
                    lv.DrawSubItem += (_, e) => e.DrawDefault = true;
                }
                break;

            case TextBox or NumericUpDown or TreeView:
                if (c.IsHandleCreated) _ = SetWindowTheme(c.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
                c.BackColor = surface;
                c.ForeColor = fg;
                break;

            case Label l:
                l.ForeColor = l.Font.Bold ? fg : muted;
                break;

            case SplitContainer sc:
                sc.BackColor = border;
                sc.Panel1.BackColor = bg;
                sc.Panel2.BackColor = bg;
                break;

            default:
                if (c.IsHandleCreated) _ = SetWindowTheme(c.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
                c.BackColor = bg;
                c.ForeColor = fg;
                break;
        }

        foreach (Control child in c.Controls)
            ApplyControl(child, dark, bg, surface, fg, muted, accent, border);
    }
}
