using System.Drawing.Drawing2D;

namespace FileInventory;

record CommandAction(string Title, string Category, string Shortcut, Action Execute);

sealed class CommandPalette : Form
{
    readonly IReadOnlyList<CommandAction> allActions;
    readonly List<CommandAction> filtered = [];
    readonly TextBox searchBox;
    readonly ListBox listBox;
    readonly Label emptyLabel;
    readonly Label hintLabel;

    CommandAction? selectedAction;
    int hoveredIndex = -1;

    CommandPalette(AppTheme theme, IReadOnlyList<CommandAction> actions)
    {
        allActions = actions;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        KeyPreview = true;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(560, 410);
        Padding = new Padding(12);

        searchBox = new TextBox
        {
            Dock = DockStyle.Top,
            PlaceholderText = "Type a command...",
            Font = Theme.BodyFont,
            Height = 28
        };

        var searchSpacer = new Panel
        {
            Dock = DockStyle.Top,
            Height = 8
        };

        hintLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 22,
            Text = "Up/Down Navigate   Enter Run   Esc Close",
            TextAlign = ContentAlignment.BottomLeft
        };

        var listHost = new Panel
        {
            Dock = DockStyle.Fill
        };

        listBox = new ListBox
        {
            Dock = DockStyle.Fill,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = Math.Max(34, (int)Math.Ceiling(Theme.BodyFont.GetHeight()) + 16),
            IntegralHeight = false,
            BorderStyle = BorderStyle.FixedSingle
        };

        emptyLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "No matching commands",
            TextAlign = ContentAlignment.MiddleCenter,
            Visible = false
        };

        listHost.Controls.Add(emptyLabel);
        listHost.Controls.Add(listBox);

        Controls.Add(listHost);
        Controls.Add(searchSpacer);
        Controls.Add(searchBox);
        Controls.Add(hintLabel);

        listBox.DrawItem += OnDrawItem;
        listBox.MouseMove += OnListMouseMove;
        listBox.MouseLeave += (_, _) =>
        {
            if (hoveredIndex != -1)
            {
                hoveredIndex = -1;
                listBox.Invalidate();
            }
        };
        listBox.MouseUp += OnListMouseUp;
        searchBox.TextChanged += (_, _) => ApplyFilter();

        Theme.Apply(this, theme);
        ApplyFilter();

        Shown += (_, _) =>
        {
            searchBox.Focus();
            searchBox.SelectAll();
        };
    }

    public static void Show(Form owner, AppTheme theme, IReadOnlyList<CommandAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);

        CommandAction? toExecute = null;
        using (var palette = new CommandPalette(theme, actions))
        {
            if (owner is not null && !owner.IsDisposed)
            {
                palette.StartPosition = FormStartPosition.Manual;
                var ownerBounds = owner.Bounds;
                var workArea = Screen.FromControl(owner).WorkingArea;

                int x = ownerBounds.Left + (ownerBounds.Width - palette.Width) / 2;
                int y = ownerBounds.Top + Math.Max(48, ownerBounds.Height / 6);

                x = Math.Clamp(x, workArea.Left + 8, Math.Max(workArea.Left + 8, workArea.Right - palette.Width - 8));
                y = Math.Clamp(y, workArea.Top + 8, Math.Max(workArea.Top + 8, workArea.Bottom - palette.Height - 8));
                palette.Location = new Point(x, y);

                if (palette.ShowDialog(owner) == DialogResult.OK)
                    toExecute = palette.selectedAction;
            }
            else
            {
                palette.StartPosition = FormStartPosition.CenterScreen;
                if (palette.ShowDialog() == DialogResult.OK)
                    toExecute = palette.selectedAction;
            }
        }

        toExecute?.Execute();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.CurrentBorderStrong, 1f);
        e.Graphics.DrawRectangle(pen, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape:
                DialogResult = DialogResult.Cancel;
                Close();
                return true;

            case Keys.Enter:
                CommitSelection();
                return true;

            case Keys.Down:
                MoveSelection(1);
                return true;

            case Keys.Up:
                MoveSelection(-1);
                return true;

            case Keys.PageDown:
                MoveSelection(Math.Max(1, listBox.ClientSize.Height / Math.Max(1, listBox.ItemHeight)));
                return true;

            case Keys.PageUp:
                MoveSelection(-Math.Max(1, listBox.ClientSize.Height / Math.Max(1, listBox.ItemHeight)));
                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    void ApplyFilter()
    {
        string query = searchBox.Text.Trim();
        filtered.Clear();

        if (query.Length == 0)
        {
            filtered.AddRange(allActions);
        }
        else
        {
            var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            List<(CommandAction Action, int Rank, int Index)> scored = [];

            for (int i = 0; i < allActions.Count; i++)
            {
                var action = allActions[i];
                if (!MatchesAllTerms(action, terms))
                    continue;

                int rank = action.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0
                    : action.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ? 1
                    : action.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2
                    : 3;

                scored.Add((action, rank, i));
            }

            scored.Sort(static (a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.Index.CompareTo(b.Index));
            foreach (var item in scored)
                filtered.Add(item.Action);
        }

        hoveredIndex = -1;
        listBox.BeginUpdate();
        try
        {
            listBox.Items.Clear();
            foreach (var action in filtered)
                listBox.Items.Add(action);

            if (filtered.Count > 0)
                listBox.SelectedIndex = 0;
        }
        finally
        {
            listBox.EndUpdate();
        }

        bool hasItems = filtered.Count > 0;
        listBox.Visible = hasItems;
        emptyLabel.Visible = !hasItems;
    }

    static bool MatchesAllTerms(CommandAction action, string[] terms)
    {
        foreach (var term in terms)
        {
            bool match = action.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
                || action.Category.Contains(term, StringComparison.OrdinalIgnoreCase)
                || action.Shortcut.Contains(term, StringComparison.OrdinalIgnoreCase);
            if (!match)
                return false;
        }
        return true;
    }

    void MoveSelection(int delta)
    {
        if (filtered.Count == 0) return;

        int current = listBox.SelectedIndex;
        int next = current < 0
            ? 0
            : (current + delta % filtered.Count + filtered.Count) % filtered.Count;

        listBox.SelectedIndex = next;
    }

    void CommitSelection()
    {
        if (listBox.SelectedIndex < 0 || listBox.SelectedIndex >= filtered.Count)
            return;

        selectedAction = filtered[listBox.SelectedIndex];
        DialogResult = DialogResult.OK;
        Close();
    }

    void OnListMouseMove(object? sender, MouseEventArgs e)
    {
        int idx = listBox.IndexFromPoint(e.Location);
        if (idx >= filtered.Count) idx = -1;
        if (idx != hoveredIndex)
        {
            hoveredIndex = idx;
            listBox.Invalidate();
        }
    }

    void OnListMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        int idx = listBox.IndexFromPoint(e.Location);
        if (idx >= 0 && idx < filtered.Count)
        {
            listBox.SelectedIndex = idx;
            CommitSelection();
        }
    }

    void OnDrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= filtered.Count) return;

        var g = e.Graphics;
        var action = filtered[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;
        bool hovered = !selected && e.Index == hoveredIndex;

        using (var baseBg = new SolidBrush(Theme.CurrentSurface))
            g.FillRectangle(baseBg, e.Bounds);

        if (selected || hovered)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rowRect = new RectangleF(e.Bounds.X + 3f, e.Bounds.Y + 2f,
                Math.Max(1f, e.Bounds.Width - 6f), Math.Max(1f, e.Bounds.Height - 4f));
            using var rowPath = Theme.CreateRoundedRect(rowRect, 4f);
            using var rowBrush = new SolidBrush(selected ? Theme.CurrentAccent : Theme.CurrentSurfaceHover);
            g.FillPath(rowBrush, rowPath);
        }

        int left = e.Bounds.X + 10;
        int right = e.Bounds.Right - 10;

        // Right-aligned shortcut hint
        if (!string.IsNullOrWhiteSpace(action.Shortcut))
        {
            var shortcutSize = TextRenderer.MeasureText(g, action.Shortcut, Theme.MonospaceFont,
                new Size(int.MaxValue, e.Bounds.Height), TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            int pillW = shortcutSize.Width + 10;
            int pillH = Math.Min(e.Bounds.Height - 10, shortcutSize.Height + 4);
            int pillX = Math.Max(left, right - pillW);
            int pillY = e.Bounds.Y + (e.Bounds.Height - pillH) / 2;

            var pillRect = new RectangleF(pillX, pillY, pillW, pillH);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pillPath = Theme.CreateRoundedRect(pillRect, 3.5f))
            using (var pillBg = new SolidBrush(selected ? Color.FromArgb(55, Color.White) : Theme.CurrentSurfaceAlt))
            using (var pillBorder = new Pen(selected ? Color.FromArgb(90, Color.White) : Theme.CurrentBorder, 1f))
            {
                g.FillPath(pillBg, pillPath);
                g.DrawPath(pillBorder, pillPath);
            }

            var shortcutRect = new Rectangle(pillX, pillY, pillW, pillH);
            var shortcutFg = selected ? Color.FromArgb(240, Color.White) : Theme.CurrentMuted;
            TextRenderer.DrawText(g, action.Shortcut, Theme.MonospaceFont, shortcutRect, shortcutFg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

            right = pillX - 8;
        }

        // Left category badge
        if (!string.IsNullOrWhiteSpace(action.Category))
        {
            var catSize = TextRenderer.MeasureText(g, action.Category, Theme.SectionFont,
                new Size(140, e.Bounds.Height), TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            int badgeW = Math.Min(120, catSize.Width + 12);
            int badgeH = Math.Min(e.Bounds.Height - 10, catSize.Height + 4);
            int badgeY = e.Bounds.Y + (e.Bounds.Height - badgeH) / 2;

            var badgeRect = new RectangleF(left, badgeY, badgeW, badgeH);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var badgePath = Theme.CreateRoundedRect(badgeRect, 3.5f))
            using (var badgeBg = new SolidBrush(selected
                ? Color.FromArgb(55, Color.White)
                : Color.FromArgb(Theme.IsDark ? 55 : 28, Theme.CurrentAccent)))
            {
                g.FillPath(badgeBg, badgePath);
            }

            var catRect = new Rectangle(left + 4, badgeY, Math.Max(0, badgeW - 8), badgeH);
            var catFg = selected ? Color.White : Theme.CurrentAccent;
            TextRenderer.DrawText(g, action.Category, Theme.SectionFont, catRect, catFg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            left += badgeW + 10;
        }

        // Title
        if (right > left)
        {
            var titleRect = new Rectangle(left, e.Bounds.Y, right - left, e.Bounds.Height);
            var titleFg = selected ? Color.White : Theme.CurrentFg;
            TextRenderer.DrawText(g, action.Title, Theme.BodyFont, titleRect, titleFg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }
}
