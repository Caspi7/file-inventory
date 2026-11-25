using System.Runtime.InteropServices;

namespace FileInventory;

static class Theme
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? pszSubAppName, string? pszSubIdList);

    public static void Apply(Form form, bool dark)
    {
        if (form.IsHandleCreated)
        {
            int useDark = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(form.Handle, 20, ref useDark, sizeof(int));
        }

        var bg = dark ? Color.FromArgb(24, 24, 27) : Color.FromArgb(243, 244, 246);
        var surface = dark ? Color.FromArgb(39, 39, 42) : Color.White;
        var fg = dark ? Color.FromArgb(244, 244, 245) : Color.FromArgb(24, 24, 27);
        var muted = dark ? Color.FromArgb(161, 161, 170) : Color.FromArgb(113, 113, 122);
        var accent = dark ? Color.FromArgb(10, 132, 255) : Color.FromArgb(0, 103, 192);
        var border = dark ? Color.FromArgb(63, 63, 70) : Color.FromArgb(212, 212, 216);

        form.BackColor = bg;
        form.ForeColor = fg;
        form.Font = new Font("Segoe UI", 9.5f);

        foreach (Control c in form.Controls)
            ApplyControl(c, dark, bg, surface, fg, muted, accent, border);
    }

    static void ApplyControl(Control c, bool dark, Color bg, Color surface, Color fg, Color muted, Color accent, Color border)
    {
        if (c.IsHandleCreated)
            _ = SetWindowTheme(c.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);

        switch (c)
        {
            case Button b:
                b.FlatStyle = FlatStyle.Flat;
                b.UseVisualStyleBackColor = false;
                bool isPrimary = b.Text.StartsWith("Scan ", StringComparison.Ordinal);
                b.BackColor = isPrimary ? accent : surface;
                b.ForeColor = isPrimary ? Color.White : (b.Enabled ? fg : muted);
                b.FlatAppearance.BorderColor = isPrimary ? accent : border;
                b.FlatAppearance.BorderSize = 1;
                b.FlatAppearance.MouseOverBackColor = isPrimary
                    ? Color.FromArgb(Math.Min(255, accent.R + 20), Math.Min(255, accent.G + 20), Math.Min(255, accent.B + 20))
                    : (dark ? Color.FromArgb(55, 55, 60) : Color.FromArgb(232, 234, 238));
                b.Padding = new Padding(10, 4, 10, 4);
                break;
            case ComboBox cb:
                cb.FlatStyle = FlatStyle.Flat;
                cb.BackColor = surface;
                cb.ForeColor = fg;
                break;
            case DateTimePicker dtp:
                dtp.CalendarMonthBackground = surface;
                dtp.CalendarForeColor = fg;
                dtp.CalendarTitleBackColor = accent;
                dtp.CalendarTitleForeColor = Color.White;
                break;
            case TextBox or NumericUpDown or ListView or TreeView:
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
                c.BackColor = bg;
                c.ForeColor = fg;
                break;
        }

        foreach (Control child in c.Controls)
            ApplyControl(child, dark, bg, surface, fg, muted, accent, border);
    }
}
