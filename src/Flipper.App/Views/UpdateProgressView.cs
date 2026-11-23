using Flipper.Core.Update;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Flipper.App.Views;

internal sealed class UpdateProgressView : StackPanel
{
    private readonly TextBlock _status = Ui.Body();
    private readonly ProgressBar _bar = new() { Visibility = Visibility.Collapsed };

    public UpdateProgressView()
    {
        Spacing = 12;
        Children.Add(_status);
        Children.Add(_bar);
        Visibility = Visibility.Collapsed;
    }

    public void ShowStatus(string message)
    {
        _status.Text = message;
        _bar.Visibility = Visibility.Collapsed;
        Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void ShowProgress(InstallProgress progress)
    {
        _status.Text = progress.Message;
        _bar.IsIndeterminate = progress.Total <= 0;
        if (progress.Total > 0)
        {
            _bar.Maximum = progress.Total;
            _bar.Value = Math.Clamp(progress.Current, 0, progress.Total);
        }

        _bar.Visibility = Visibility.Visible;
        Visibility = Visibility.Visible;
    }
}
