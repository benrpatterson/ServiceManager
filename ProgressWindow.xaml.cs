using System.ComponentModel;
using System.Windows;

namespace ServiceManager;

/// <summary>Modal-looking progress popup shown while services start, stop or restart in the background.</summary>
public partial class ProgressWindow : Window
{
    private readonly int _total;
    private int _done;
    private bool _finished;

    public ProgressWindow(Window owner, string heading, int total)
    {
        InitializeComponent();
        Owner = owner;
        _total = total;
        Heading.Text = heading;
        Bar.Maximum = total;
        // One service has no meaningful percentage, so just show activity.
        Bar.IsIndeterminate = total <= 1;
        CountText.Text = total <= 1 ? "Please wait; some services take a while to change state." : $"0 of {total} done";
        Title = heading;
        owner.IsEnabled = false;
    }

    /// <summary>Call on the UI thread after one service finishes.</summary>
    public void Report(string line, bool ok)
    {
        _done++;
        Bar.Value = _done;
        if (_total > 1)
        {
            CountText.Text = $"{_done} of {_total} done";
        }
        Log.Items.Add((ok ? "OK    " : "FAILED  ") + line);
        Log.ScrollIntoView(Log.Items[^1]);
    }

    public void Finish()
    {
        _finished = true;
        if (Owner != null)
        {
            Owner.IsEnabled = true;
        }
        Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // The work can't be cancelled midway, so don't let the window be closed early.
        if (!_finished)
        {
            e.Cancel = true;
        }
    }
}
