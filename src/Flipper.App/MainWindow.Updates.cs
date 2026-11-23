using System.Diagnostics;
using Flipper.App.Services;
using Flipper.App.Views;
using Flipper.Core.Update;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Flipper.App;

public sealed partial class MainWindow
{
    private readonly CancellationTokenSource _updateCancellation = new();
    private bool _updateInProgress;

    private async void CheckForStartupUpdate(object sender, RoutedEventArgs args)
    {
        WindowRoot.Loaded -= CheckForStartupUpdate;
        var cancellationToken = _updateCancellation.Token;
        try
        {
            var (offer, status) = await UpdateClient.CheckForUpdateAsync(cancellationToken);
            if (offer is null || cancellationToken.IsCancellationRequested || _updateInProgress)
            {
                return;
            }

            var version = offer.Version.ToString();
            var app = App.Current;
            if (app.Settings.SkippedUpdateVersions.Contains(version))
            {
                return;
            }

            var progress = new UpdateProgressView();
            progress.ShowStatus(status);
            var dialog = new ContentDialog
            {
                XamlRoot = WindowRoot.XamlRoot,
                Title = "Carousel update",
                Content = progress,
                PrimaryButtonText = "Update",
                SecondaryButtonText = "Skip version",
                CloseButtonText = "Not now",
                DefaultButton = ContentDialogButton.Close
            };
            dialog.SecondaryButtonClick += (_, click) =>
            {
                app.Settings.SkippedUpdateVersions.Add(version);
                try
                {
                    app.PersistSettings();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    app.Settings.SkippedUpdateVersions.Remove(version);
                    progress.ShowStatus("Could not save the skipped version.");
                    click.Cancel = true;
                }
            };
            dialog.PrimaryButtonClick += async (_, click) =>
            {
                var deferral = click.GetDeferral();
                try
                {
                    var error = await InstallUpdateAsync(offer, progress.ShowProgress);
                    if (!cancellationToken.IsCancellationRequested && error is not null)
                    {
                        progress.ShowStatus(error);
                        click.Cancel = true;
                    }
                }
                finally
                {
                    deferral.Complete();
                }
            };

            if (await Ui.ShowDialogAsync(dialog, cancellationToken) == ContentDialogResult.Primary
                && !cancellationToken.IsCancellationRequested)
            {
                Close();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Trace.TraceError("Carousel startup update check failed: {0}", error);
        }
    }

    internal async Task<string?> InstallUpdateAsync(UpdateOffer offer, Action<InstallProgress> showProgress)
    {
        if (_updateInProgress)
        {
            return "An update is already in progress";
        }

        _updateInProgress = true;
        var setupStarted = false;
        try
        {
            showProgress(new InstallProgress(0, 0, "Downloading..."));
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            var error = await new UpdateClient(http).InstallAsync(
                offer, new Progress<InstallProgress>(showProgress), _updateCancellation.Token);
            setupStarted = error is null;
            return error;
        }
        catch (Exception error)
        {
            Trace.TraceError("Carousel update failed: {0}", error);
            return "Could not download";
        }
        finally
        {
            _updateInProgress = setupStarted;
        }
    }
}
