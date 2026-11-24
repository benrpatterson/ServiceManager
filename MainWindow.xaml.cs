using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace ServiceManager;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<string> _servers = new();
    private readonly ObservableCollection<ServiceRow> _rows = new();
    private readonly Dictionary<string, List<ServiceRow>> _byServer = new();
    private readonly Dictionary<string, string> _errors = new();
    private readonly ICollectionView _view;

    public MainWindow()
    {
        InitializeComponent();
        foreach (var s in ServerStore.Load())
        {
            _servers.Add(s);
        }
        ServerList.ItemsSource = _servers;
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = Matches;
        Grid.ItemsSource = _view;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F5)
            {
                Refresh_Click(this, new RoutedEventArgs());
            }
        };
        Loaded += async (_, _) => await RefreshServersAsync(_servers.ToList());
    }

    /// <summary>Servers highlighted in the list; all servers when none are.</summary>
    private List<string> Scope => ServerList.SelectedItems.Count > 0 ? ServerList.SelectedItems.Cast<string>().ToList() : _servers.ToList();

    // ---- servers ----

    private async void AddServer_Click(object sender, RoutedEventArgs e) => await AddServerAsync();

    private async void NewServer_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await AddServerAsync();
        }
    }

    private async Task AddServerAsync()
    {
        var name = NewServerBox.Text.Trim().TrimStart('\\');
        if (name.Length == 0)
        {
            return;
        }
        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || name == "127.0.0.1")
        {
            name = ".";
        }
        var existing = _servers.FirstOrDefault(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            ServerList.SelectedItem = existing;
            return;
        }
        _servers.Add(name);
        ServerStore.Save(_servers);
        NewServerBox.Clear();
        await RefreshServersAsync(new List<string> { name });
    }

    private void RemoveServer_Click(object sender, RoutedEventArgs e)
    {
        foreach (var s in ServerList.SelectedItems.Cast<string>().ToList())
        {
            _servers.Remove(s);
            _byServer.Remove(s);
            _errors.Remove(s);
        }
        ServerStore.Save(_servers);
        Rebuild();
    }

    private void ServerList_SelectionChanged(object sender, SelectionChangedEventArgs e) => Rebuild();

    // ---- loading ----

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshServersAsync(Scope);

    private async Task RefreshServersAsync(List<string> servers)
    {
        StatusText.Text = "Loading " + string.Join(", ", servers.Select(ServiceOps.Display)) + "...";
        var tasks = servers.Select(async s =>
        {
            try
            {
                var rows = await Task.Run(() => ServiceOps.List(s));
                return (s, rows, error: (string?)null);
            }
            catch (Exception ex)
            {
                return (s, rows: new List<ServiceRow>(), error: Describe(ex));
            }
        }).ToList();
        foreach (var t in tasks)
        {
            var (s, rows, error) = await t;
            if (!_servers.Contains(s))
            {
                continue;
            }
            if (error == null)
            {
                _byServer[s] = rows;
                _errors.Remove(s);
            }
            else
            {
                _byServer.Remove(s);
                _errors[s] = error;
            }
            Rebuild();
        }
        var failed = _errors.Where(kv => servers.Contains(kv.Key)).ToList();
        StatusText.Text = failed.Count == 0
            ? "Ready"
            : string.Join("  |  ", failed.Select(kv => ServiceOps.Display(kv.Key) + ": " + kv.Value));
    }

    private static string Describe(Exception ex)
    {
        var inner = ex.InnerException ?? ex;
        return inner.Message.Replace("\r", " ").Replace("\n", " ");
    }

    private void Rebuild()
    {
        var selected = Grid.SelectedItems.Cast<ServiceRow>().Select(r => (r.Server, r.Name)).ToHashSet();
        _rows.Clear();
        foreach (var srv in Scope)
        {
            if (_byServer.TryGetValue(srv, out var list))
            {
                foreach (var r in list.OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase))
                {
                    _rows.Add(r);
                }
            }
        }
        foreach (var r in _rows.Where(r => selected.Contains((r.Server, r.Name))))
        {
            Grid.SelectedItems.Add(r);
        }
        UpdateCount();
    }

    private void UpdateCount()
    {
        CountText.Text = _view.Cast<object>().Count() + " services";
    }

    // ---- filter ----

    private bool Matches(object o)
    {
        var r = (ServiceRow)o;
        var state = (StatusFilter.SelectedItem as ComboBoxItem)?.Content as string;
        if (state != null && state != "All states" && r.Status != state)
        {
            return false;
        }
        var text = FilterBox.Text.Trim();
        return text.Length == 0
            || r.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            || r.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase)
            || r.Server.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private void Filter_Changed(object sender, EventArgs e)
    {
        if (_view == null)
        {
            return;
        }
        _view.Refresh();
        UpdateCount();
    }

    // ---- actions ----

    private void Start_Click(object sender, RoutedEventArgs e) => _ = Act("Starting", ServiceOps.Start);
    private void Stop_Click(object sender, RoutedEventArgs e) => _ = Act("Stopping", ServiceOps.Stop);
    private void Restart_Click(object sender, RoutedEventArgs e) => _ = Act("Restarting", ServiceOps.Restart);
    private void Pause_Click(object sender, RoutedEventArgs e) => _ = Act("Pausing", ServiceOps.Pause);
    private void Resume_Click(object sender, RoutedEventArgs e) => _ = Act("Resuming", ServiceOps.Resume);

    private void StartType_Click(object sender, RoutedEventArgs e)
    {
        var mode = (string)((MenuItem)sender).Tag;
        _ = Act("Changing startup type of", r => ServiceOps.SetStartType(r, mode));
    }

    private void Grid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Like services.msc, double-click opens Properties (only for a click on a row, not the header).
        if (e.OriginalSource is DependencyObject d && FindParent<DataGridRow>(d) != null)
        {
            Properties_Click(sender, e);
        }
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t)
            {
                return t;
            }
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private async void Properties_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not ServiceRow r)
        {
            return;
        }
        var dlg = new PropertiesWindow(r) { Owner = this };
        dlg.ShowDialog();
        if (dlg.Saved)
        {
            await RefreshServersAsync(new List<string> { r.Server });
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "CSV (*.csv)|*.csv|Tab-separated text (*.txt)|*.txt", FileName = "services.csv" };
        if (dlg.ShowDialog(this) != true)
        {
            return;
        }
        var sep = dlg.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? "\t" : ",";
        string Q(string s) => sep == "," ? "\"" + s.Replace("\"", "\"\"") + "\"" : s.Replace("\t", " ");
        var lines = new List<string> { string.Join(sep, "Server", "Name", "Display name", "Description", "Status", "Startup type", "Log on as") };
        foreach (var r in _view.Cast<ServiceRow>())
        {
            lines.Add(string.Join(sep, new[] { r.Server, r.Name, r.DisplayName, r.Description, r.Status, r.StartType, r.LogOnAs }.Select(Q)));
        }
        try
        {
            System.IO.File.WriteAllLines(dlg.FileName, lines, new System.Text.UTF8Encoding(true));
            StatusText.Text = "Exported " + (lines.Count - 1) + " services";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Service Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CopyName_Click(object sender, RoutedEventArgs e)
    {
        var names = Grid.SelectedItems.Cast<ServiceRow>().Select(r => r.Name).ToList();
        if (names.Count > 0)
        {
            Clipboard.SetText(string.Join(Environment.NewLine, names));
        }
    }

    private async Task Act(string verb, Action<ServiceRow> op)
    {
        var targets = Grid.SelectedItems.Cast<ServiceRow>().ToList();
        if (targets.Count == 0)
        {
            return;
        }
        if (targets.Count > 1 && MessageBox.Show(this, $"{verb} {targets.Count} services?", "Service Manager", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }
        StatusText.Text = $"{verb} {targets.Count} service(s)...";
        var progress = new ProgressWindow(this, $"{verb} {(targets.Count == 1 ? targets[0].DisplayName : targets.Count + " services")}", targets.Count);
        progress.Show();
        var errors = new List<string>();
        try
        {
            var results = await Task.WhenAll(targets.Select(async t =>
            {
                var label = $"{ServiceOps.Display(t.Server)}\\{t.Name}";
                string? error = null;
                try
                {
                    await Task.Run(() => op(t));
                }
                catch (Exception ex)
                {
                    error = $"{label}: {Describe(ex)}";
                }
                progress.Report(error ?? label, error == null);
                return error;
            }));
            errors.AddRange(results.OfType<string>());
            await RefreshServersAsync(targets.Select(t => t.Server).Distinct().ToList());
        }
        finally
        {
            progress.Finish();
        }
        if (errors.Count > 0)
        {
            StatusText.Text = errors.Count + " failed";
            MessageBox.Show(this, string.Join(Environment.NewLine, errors), "Service Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
