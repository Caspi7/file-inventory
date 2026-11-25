using System.Runtime.InteropServices;

namespace FileInventory;

static class Theme
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static void Apply(Form form, bool dark)
    {
        if (form.IsHandleCreated)
        {
            int useDark = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(form.Handle, 20, ref useDark, sizeof(int));
        }

        var bg = dark ? Color.FromArgb(28, 28, 30) : Color.FromArgb(243, 244, 246);
        var surface = dark ? Color.FromArgb(44, 44, 46) : Color.White;
        var fg = dark ? Color.FromArgb(242, 242, 247) : Color.FromArgb(28, 28, 30);
        var muted = dark ? Color.FromArgb(174, 174, 178) : Color.FromArgb(99, 99, 102);
        var accent = dark ? Color.FromArgb(10, 132, 255) : Color.FromArgb(0, 103, 192);
        var border = dark ? Color.FromArgb(68, 68, 70) : Color.FromArgb(216, 218, 222);

        form.BackColor = bg;
        form.ForeColor = fg;
        form.Font = new Font("Segoe UI", 9.5f);

        foreach (Control c in form.Controls)
            ApplyControl(c, bg, surface, fg, muted, accent, border);
    }

    static void ApplyControl(Control c, Color bg, Color surface, Color fg, Color muted, Color accent, Color border)
    {
        switch (c)
        {
            case Button b:
                b.FlatStyle = FlatStyle.Flat;
                b.UseVisualStyleBackColor = false;
                bool isPrimary = b.Text.StartsWith("Scan ", StringComparison.Ordinal);
                b.BackColor = isPrimary ? accent : surface;
                b.ForeColor = isPrimary ? Color.White : fg;
                b.FlatAppearance.BorderColor = isPrimary ? accent : border;
                b.FlatAppearance.BorderSize = 1;
                b.Padding = new Padding(8, 3, 8, 3);
                break;
            case TextBox or ComboBox or NumericUpDown or ListView or TreeView:
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
            ApplyControl(child, bg, surface, fg, muted, accent, border);
    }
}
