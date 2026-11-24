using System.Windows;
using System.Windows.Controls;

namespace ServiceManager;

public partial class PropertiesWindow : Window
{
    private static readonly string[] Starts = { "auto", "delayed", "demand", "disabled" };
    private static readonly (RecoveryType Type, string Text)[] Responses =
    {
        (RecoveryType.None, "Take No Action"),
        (RecoveryType.Restart, "Restart the Service"),
        (RecoveryType.RunCommand, "Run a Program"),
        (RecoveryType.Reboot, "Restart the Computer")
    };

    private readonly ServiceRow _row;
    private ServiceConfig _cfg = new();
    private string _originalDescription = "";
    private bool _loading = true;
    private bool _recoveryLoaded;
    private bool _dirty;
    private bool _startModeDirty;
    private bool _accountDirty;
    private bool _recoveryDirty;

    /// <summary>True when something was saved, so the caller should refresh.</summary>
    public bool Saved { get; private set; }

    public PropertiesWindow(ServiceRow row)
    {
        InitializeComponent();
        _row = row;
        Title = $"{row.DisplayName} Properties ({ServiceOps.Display(row.Server)})";
        foreach (var s in Starts)
        {
            StartTypeBox.Items.Add(ServiceOps.StartModeText(s));
        }
        foreach (var box in new[] { First, Second, Third })
        {
            foreach (var r in Responses)
            {
                box.Items.Add(r.Text);
            }
        }
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _cfg = await Task.Run(() => ServiceOps.ReadConfig(_row.Server, _row.Name));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Service Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        _originalDescription = _cfg.Description;
        NameBox.Text = _cfg.Name;
        DisplayBox.Text = _cfg.DisplayName;
        DescriptionBox.Text = _cfg.Description;
        PathBox.Text = _cfg.BinaryPath;
        var idx = Array.IndexOf(Starts, _cfg.StartMode);
        if (idx < 0)
        {
            // Boot/System drivers: show but don't allow changing.
            StartTypeBox.Items.Add(ServiceOps.StartModeText(_cfg.StartMode));
            idx = StartTypeBox.Items.Count - 1;
            StartTypeBox.IsEnabled = false;
        }
        StartTypeBox.SelectedIndex = idx;

        var system = _cfg.Account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase);
        LocalSystemRadio.IsChecked = system;
        AccountRadio.IsChecked = !system;
        AccountBox.Text = system ? "" : _cfg.Account;
        InteractBox.IsChecked = _cfg.Interactive;

        // Recovery actions: the API may return fewer than three; pad with "no action".
        var boxes = new[] { First, Second, Third };
        for (var i = 0; i < 3; i++)
        {
            var a = i < _cfg.Recovery.Count ? _cfg.Recovery[i] : (i > 0 && _cfg.Recovery.Count > 0 ? _cfg.Recovery[^1] : new RecoveryAction());
            boxes[i].SelectedIndex = Array.FindIndex(Responses, r => r.Type == a.Type);
        }
        ResetDays.Text = (_cfg.ResetPeriodSeconds / 86400).ToString();
        var restart = _cfg.Recovery.FirstOrDefault(a => a.Type == RecoveryType.Restart);
        RestartMinutes.Text = restart == null ? "1" : (restart.DelayMs / 60000).ToString();
        CommandBox.Text = _cfg.RecoveryCommand;
        RebootMsgBox.Text = _cfg.RebootMessage;
        _recoveryLoaded = true;
        UpdateRecoveryPanels();

        try
        {
            var (on, by) = await Task.Run(() => ServiceOps.Dependencies(_row.Server, _row.Name));
            DependsOnList.ItemsSource = on;
            DependentsList.ItemsSource = by;
        }
        catch
        {
            // Dependencies are informational only.
        }
        await UpdateStatusAsync();
        _loading = false;
    }

    private async Task UpdateStatusAsync()
    {
        string status;
        try
        {
            status = await Task.Run(() => ServiceOps.Status(_row.Server, _row.Name));
        }
        catch
        {
            status = "Unknown";
        }
        StatusLabel.Text = status;
        StartBtn.IsEnabled = status == "Stopped";
        StopBtn.IsEnabled = status is "Running" or "Paused";
        PauseBtn.IsEnabled = status == "Running";
        ResumeBtn.IsEnabled = status == "Paused";
    }

    private void Changed(object sender, EventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _dirty = true;
        if (sender == StartTypeBox)
        {
            _startModeDirty = true;
        }
        else if (sender == LocalSystemRadio || sender == AccountRadio || sender == InteractBox || sender == AccountBox || sender == PasswordBox || sender == ConfirmBox)
        {
            _accountDirty = true;
        }
        else if (sender == ResetDays || sender == RestartMinutes || sender == CommandBox || sender == RebootMsgBox)
        {
            _recoveryDirty = true;
        }
        if (sender == LocalSystemRadio || sender == AccountRadio)
        {
            var acct = AccountRadio.IsChecked == true;
            AccountBox.IsEnabled = acct;
            PasswordBox.IsEnabled = acct;
            ConfirmBox.IsEnabled = acct;
            InteractBox.IsEnabled = !acct;
        }
        ApplyButton.IsEnabled = true;
    }

    private void Recovery_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_recoveryLoaded)
        {
            return;
        }
        UpdateRecoveryPanels();
        if (!_loading)
        {
            _recoveryDirty = true;
            _dirty = true;
            ApplyButton.IsEnabled = true;
        }
    }

    private void UpdateRecoveryPanels()
    {
        var types = new[] { First, Second, Third }.Select(b => Responses[Math.Max(0, b.SelectedIndex)].Type).ToList();
        RunBox.IsEnabled = types.Contains(RecoveryType.RunCommand);
        RebootBox.IsEnabled = types.Contains(RecoveryType.Reboot);
        RestartMinutes.IsEnabled = types.Contains(RecoveryType.Restart);
    }

    private bool Save()
    {
        if (!_dirty)
        {
            return true;
        }
        var cfg = new ServiceConfig
        {
            Name = _cfg.Name,
            DisplayName = DisplayBox.Text.Trim(),
            Description = DescriptionBox.Text,
            ServiceType = _cfg.ServiceType,
            StartMode = StartTypeBox.SelectedIndex is >= 0 and < 4 ? Starts[StartTypeBox.SelectedIndex] : _cfg.StartMode
        };
        if (cfg.DisplayName.Length == 0)
        {
            MessageBox.Show(this, "The display name can't be empty.", "Service Manager");
            return false;
        }

        string? password = null;
        if (_accountDirty)
        {
            if (AccountRadio.IsChecked == true)
            {
                cfg.Account = AccountBox.Text.Trim();
                cfg.Interactive = false;
                if (cfg.Account.Length == 0)
                {
                    MessageBox.Show(this, "Enter the account name.", "Service Manager");
                    return false;
                }
                if (PasswordBox.Password != ConfirmBox.Password)
                {
                    MessageBox.Show(this, "The passwords don't match.", "Service Manager");
                    return false;
                }
                password = PasswordBox.Password;
            }
            else
            {
                cfg.Account = "LocalSystem";
                cfg.Interactive = InteractBox.IsChecked == true;
            }
        }

        if (_recoveryDirty)
        {
            if (!uint.TryParse(ResetDays.Text, out var days) || !uint.TryParse(RestartMinutes.Text, out var minutes))
            {
                MessageBox.Show(this, "Reset days and restart minutes must be whole numbers.", "Service Manager");
                return false;
            }
            cfg.ResetPeriodSeconds = days * 86400;
            cfg.RecoveryCommand = CommandBox.Text.Trim();
            cfg.RebootMessage = RebootMsgBox.Text.Trim();
            foreach (var b in new[] { First, Second, Third })
            {
                var type = Responses[Math.Max(0, b.SelectedIndex)].Type;
                cfg.Recovery.Add(new RecoveryAction { Type = type, DelayMs = type == RecoveryType.None ? 0 : minutes * 60000 });
            }
        }

        try
        {
            ServiceOps.SaveConfig(_row.Server, cfg, password, _accountDirty, _startModeDirty, _recoveryDirty, _originalDescription);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Service Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        _originalDescription = cfg.Description;
        _dirty = _startModeDirty = _accountDirty = _recoveryDirty = false;
        PasswordBox.Clear();
        ConfirmBox.Clear();
        ApplyButton.IsEnabled = false;
        Saved = true;
        return true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Save())
        {
            Close();
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => Save();

    private async Task Run(Action action)
    {
        var progress = new ProgressWindow(this, "Updating " + _row.DisplayName, 1);
        progress.Show();
        string? error = null;
        try
        {
            await Task.Run(action);
            Saved = true;
        }
        catch (Exception ex)
        {
            error = (ex.InnerException ?? ex).Message;
        }
        finally
        {
            progress.Finish();
        }
        if (error != null)
        {
            MessageBox.Show(this, error, "Service Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        await UpdateStatusAsync();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var args = ParamsBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        await Run(() => ServiceOps.Start(_row, args));
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) => await Run(() => ServiceOps.Stop(_row));
    private async void Pause_Click(object sender, RoutedEventArgs e) => await Run(() => ServiceOps.Pause(_row));
    private async void Resume_Click(object sender, RoutedEventArgs e) => await Run(() => ServiceOps.Resume(_row));
}
