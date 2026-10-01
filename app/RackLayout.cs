using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace Dissonancia;

/// The LIVE rack: a toolbar ("EDIT RACK") and the module rows. Half-width modules fill two
/// columns, each going to the shorter one, until a full-width module starts a new row.
public sealed class RackView : DockPanel
{
    readonly IReadOnlyDictionary<ModuleKind, RackModule> _modules;
    readonly StackPanel _rows = new() { Margin = new Thickness(8, 0, 8, 8) };
    readonly StackPanel _pinned = new() { Margin = new Thickness(8, 0, 8, 8) };   // Transport + Status, never scrolled away
    readonly ToggleButton _editButton = new() { Content = "EDIT RACK", Focusable = false, Padding = new Thickness(10, 3) };
    readonly ComboBox _catalog = new() { MinWidth = 260, Focusable = false };
    readonly StackPanel _editTools;
    readonly TextBlock _hint = new() { Foreground = Ui.Label, VerticalAlignment = VerticalAlignment.Center };
    List<RackEntry> _entries = [];
    bool _locked;

    public AppMode Mode { get; private set; } = (AppMode)(-1);
    public bool Editing => _editButton.IsChecked == true;
    public IEnumerable<RackModule> Visible => _entries.Select(e => _modules[e.Module]);

    public RackView(IReadOnlyDictionary<ModuleKind, RackModule> modules)
    {
        _modules = modules;
        var add = new Button { Content = "ADD", Focusable = false };
        add.Click += (_, _) => { if (_catalog.SelectedItem is ComboBoxItem { Tag: ModuleKind k, IsEnabled: true }) Edit(l => l.Insert(l.FindIndex(e => RackCatalog.Pinned(e.Module)), new RackEntry(k, false, RackCatalog.Units(k).Min))); };
        var reset = new Button { Content = "RESET TO DEFAULT", Focusable = false };
        reset.Click += (_, _) => { RackCatalog.Reset(Mode); _entries = RackCatalog.Default(Mode); Rebuild(); };
        _editTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false, Children = { _catalog, add, reset } };
        _editButton.IsCheckedChanged += (_, _) => { _editTools.IsVisible = Editing; Rebuild(); };
        foreach (var c in new Control[] { _editButton, add, reset, _catalog })
            if (c is TemplatedControl t) { t.Foreground = Ui.LabelBright; t.Background = Ui.Face; t.BorderBrush = Ui.Label; t.BorderThickness = new Thickness(1); }
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8, 6), Children = { _editButton, _editTools, _hint } };
        SetDock(bar, Dock.Top);
        SetDock(_pinned, Dock.Bottom);
        Children.Add(bar);
        Children.Add(_pinned);
        Children.Add(new ScrollViewer { Content = _rows });
    }

    public void Load(AppMode mode)
    {
        Mode = mode;
        _entries = RackCatalog.Load(mode);
        _editButton.IsChecked = false;
        Rebuild();
    }

    public void ToggleEdit() { if (!_locked) _editButton.IsChecked = !Editing; }

    /// Not while REC is armed or running (no mis-clicks mid-take).
    public void SetLocked(bool locked)
    {
        if (locked == _locked) return;
        _locked = locked;
        _editButton.IsEnabled = !locked;
        if (locked) _editButton.IsChecked = false;
        _hint.Text = locked ? "rack locked during REC" : Editing ? $"preset saved for {Mode} as JSON" : "";
    }

    void Edit(Action<List<RackEntry>> change)
    {
        change(_entries);
        RackCatalog.Save(Mode, _entries);
        Rebuild();
    }

    void Rebuild()
    {
        foreach (var m in _modules.Values)
            if (m.Parent is Panel p) p.Children.Remove(m);
        _rows.Children.Clear();
        _pinned.Children.Clear();
        StackPanel? left = null, right = null;
        double lh = 0, rh = 0;
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            var cell = Cell(e, i);
            if (RackCatalog.Pinned(e.Module)) { _pinned.Children.Add(cell); continue; }
            if (!e.Half) { left = right = null; _rows.Children.Add(cell); continue; }
            if (left is null || right is null)
            {
                left = new StackPanel();
                right = new StackPanel();
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
                Grid.SetColumn(right, 1);
                grid.Children.Add(left);
                grid.Children.Add(right);
                _rows.Children.Add(grid);
                lh = rh = 0;
            }
            if (lh <= rh) { left.Children.Add(cell); lh += RackCatalog.Height(e); }
            else { right.Children.Add(cell); rh += RackCatalog.Height(e); }
        }
        _catalog.ItemsSource = Enum.GetValues<ModuleKind>().Where(k => !RackCatalog.Pinned(k)).Select(k => new ComboBoxItem
        {
            Content = RackCatalog.Name(k) + (RackCatalog.Applies(k, Mode) ? "" : "  (not in this mode)"),
            Tag = k,
            IsEnabled = RackCatalog.Applies(k, Mode) && _entries.All(x => x.Module != k),
        }).ToList();
        _hint.Text = Editing ? $"preset saved for {Mode} as JSON" : "";
    }

    Control Cell(RackEntry e, int i)
    {
        var m = _modules[e.Module];
        m.Height = RackCatalog.Height(e);
        if (!Editing) return m;
        var (min, max) = RackCatalog.Units(e.Module);
        bool pinned = RackCatalog.Pinned(e.Module);
        Button B(string text, string tip, bool enabled, Action<List<RackEntry>> change)
        {
            var b = new Button { Content = text, IsEnabled = enabled, Focusable = false, Padding = new Thickness(7, 1), FontSize = 12 };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => Edit(change);
            return b;
        }
        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(0, 4, 26, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Children =
            {
                B("▲", "move up", !pinned && i > 0, l => (l[i - 1], l[i]) = (l[i], l[i - 1])),
                B("▼", "move down", !pinned && i + 1 < _entries.Count && !RackCatalog.Pinned(_entries[i + 1].Module), l => (l[i + 1], l[i]) = (l[i], l[i + 1])),
                B(e.Half ? "▭" : "½", e.Half ? "full width" : "half width", !pinned, l => l[i] = e with { Half = !e.Half }),
                B("U−", "shorter", e.HeightU > min, l => l[i] = e with { HeightU = e.HeightU - 1 }),
                B("U+", "taller", e.HeightU < max, l => l[i] = e with { HeightU = e.HeightU + 1 }),
                B("✕", pinned ? "pinned: always on screen" : "remove", !pinned, l => l.RemoveAt(i)),
            },
        };
        return new Panel { Children = { m, new Border { BorderBrush = Ui.Amber, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(4), IsHitTestVisible = false }, tools } };
    }
}
