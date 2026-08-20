using System.Diagnostics;
using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;
using Flipper.Core.Update;

namespace Flipper.Setup;

[SupportedOSPlatform("windows")]
internal sealed class InstallWizard : Form
{
    public int ExitCode { get; private set; }

    private readonly Label _title;
    private readonly Label _subtitle;
    private readonly Panel _licensePage;
    private readonly Panel _folderPage;
    private readonly Panel _progressPage;
    private readonly TextBox _licenseBox;
    private readonly CheckBox _accept;
    private readonly TextBox _folderBox;
    private readonly Label _folderError;
    private readonly Label _status;
    private readonly ProgressBar _bar;
    private readonly TextBox _log;
    private readonly Button _back;
    private readonly Button _next;
    private readonly Button _cancel;
    private readonly Button _open;
    private readonly Button _finish;
    private readonly FlowLayoutPanel _nav;
    private readonly FlowLayoutPanel _doneButtons;

    private enum Stage
    {
        License,
        Folder,
        Installing,
        Done,
        Failed
    }

    private Stage _stage = Stage.License;
    private bool _installing;
    private string _appPath = "";
    private string _targetDir = "";

    public InstallWizard()
    {
        Text = "Carousel Setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 420);
        Font = new Font("Segoe UI", 9F);

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 64,
            BackColor = SystemColors.Window,
            Padding = new Padding(16, 12, 16, 8)
        };
        _title = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 24,
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            Text = "License Agreement"
        };
        _subtitle = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
            Text = "Please read the following license agreement."
        };
        header.Controls.Add(_subtitle);
        header.Controls.Add(_title);

        var headerLine = new Panel
        {
            Dock = DockStyle.Top,
            Height = 1,
            BackColor = SystemColors.ControlDark
        };

        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            Padding = new Padding(12, 10, 12, 10)
        };
        var footerLine = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 1,
            BackColor = SystemColors.ControlDark
        };

        _back = MakeButton("Back", 88);
        _next = MakeButton("Next", 96);
        _cancel = MakeButton("Cancel", 88);
        _open = MakeButton("Finish and open Carousel", 180);
        _finish = MakeButton("Finish", 88);
        _back.Enabled = false;
        _next.Enabled = false;

        _nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        _nav.Controls.Add(_cancel);
        _nav.Controls.Add(_next);
        _nav.Controls.Add(_back);

        _doneButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Visible = false
        };
        _doneButtons.Controls.Add(_finish);
        _doneButtons.Controls.Add(_open);

        footer.Controls.Add(_nav);
        footer.Controls.Add(_doneButtons);

        var body = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16)
        };
        _licensePage = BuildLicensePage(out _licenseBox, out _accept);
        _folderPage = BuildFolderPage(out _folderBox, out _folderError);
        _progressPage = BuildProgressPage(out _status, out _bar, out _log);
        _folderPage.Visible = false;
        _progressPage.Visible = false;
        body.Controls.Add(_progressPage);
        body.Controls.Add(_folderPage);
        body.Controls.Add(_licensePage);

        Controls.Add(body);
        Controls.Add(headerLine);
        Controls.Add(header);
        Controls.Add(footerLine);
        Controls.Add(footer);

        AcceptButton = _next;
        CancelButton = _cancel;

        _accept.CheckedChanged += (_, _) =>
        {
            if (_stage == Stage.License)
            {
                _next.Enabled = _accept.Checked;
            }
        };
        _back.Click += (_, _) => ShowLicense();
        _next.Click += (_, _) => OnNext();
        _cancel.Click += (_, _) => Close();
        _finish.Click += (_, _) => Close();
        _open.Click += (_, _) =>
        {
            StartApp(_appPath, _targetDir);
            Close();
        };

        _licenseBox.Text = LoadLicense();
        _licenseBox.Select(0, 0);
        _folderBox.Text = FirstInstallPaths.DefaultTarget();
        Shown += (_, _) =>
        {
            ActiveControl = _accept;
            _licenseBox.Select(0, 0);
        };
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_installing)
        {
            e.Cancel = true;
            return;
        }

        if (_stage == Stage.Failed)
        {
            ExitCode = 4;
        }

        base.OnFormClosing(e);
    }

    private void OnNext()
    {
        if (_stage == Stage.License)
        {
            if (!_accept.Checked)
            {
                return;
            }

            ShowFolder();
            return;
        }

        if (_stage != Stage.Folder)
        {
            return;
        }

        if (!InstallSession.TryValidateTarget(_folderBox.Text, out var target, out var error))
        {
            _folderError.Text = error;
            _folderError.Visible = true;
            return;
        }

        if (InstallSession.IsCarouselRunning())
        {
            _folderError.Text = InstallSession.CloseCarouselMessage;
            _folderError.Visible = true;
            return;
        }

        _folderBox.Text = target;
        _folderError.Visible = false;
        StartInstall(target);
    }

    private void ShowLicense()
    {
        _stage = Stage.License;
        _title.Text = "License Agreement";
        _subtitle.Text = "Please read the following license agreement.";
        _licensePage.Visible = true;
        _folderPage.Visible = false;
        _progressPage.Visible = false;
        _back.Enabled = false;
        _next.Text = "Next";
        _next.Enabled = _accept.Checked;
        AcceptButton = _next;
        _licenseBox.Select(0, 0);
        ActiveControl = _accept;
    }

    private void ShowFolder()
    {
        _stage = Stage.Folder;
        _title.Text = "Install Location";
        _subtitle.Text = "Choose the folder where Setup will install Carousel.";
        _licensePage.Visible = false;
        _folderPage.Visible = true;
        _progressPage.Visible = false;
        _back.Enabled = true;
        _next.Text = "Install";
        _next.Enabled = true;
        AcceptButton = _next;
        _folderBox.Focus();
        _folderBox.Select(_folderBox.Text.Length, 0);
    }

    private async void StartInstall(string target)
    {
        _stage = Stage.Installing;
        _installing = true;
        _title.Text = "Installing";
        _subtitle.Text = "Please wait while Setup installs Carousel.";
        _licensePage.Visible = false;
        _folderPage.Visible = false;
        _progressPage.Visible = true;
        _back.Enabled = false;
        _next.Enabled = false;
        _cancel.Enabled = false;
        _bar.Style = ProgressBarStyle.Marquee;
        _status.Text = "Starting...";
        _log.Clear();

        var progress = new Progress<InstallStatus>(ApplyStatus);
        var outcome = await Task.Run(() => InstallSession.Run(target, progress));
        _installing = false;
        ShowFinished(outcome);
    }

    private void ApplyStatus(InstallStatus status)
    {
        _status.Text = status.Message;
        if (status.Percent is int percent)
        {
            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Value = Math.Clamp(percent, 0, 100);
        }
        else
        {
            _bar.Style = ProgressBarStyle.Marquee;
        }

        if (status.Log || status.IsIssue)
        {
            if (_log.TextLength > 0)
            {
                _log.AppendText(Environment.NewLine);
            }

            _log.AppendText(status.IsIssue ? "Issue: " + status.Message : status.Message);
        }
    }

    private void ShowFinished(InstallOutcome outcome)
    {
        _nav.Visible = false;
        _doneButtons.Visible = true;
        _appPath = outcome.AppPath;
        _targetDir = outcome.TargetDir;
        AcceptButton = outcome.Success ? _open : _finish;
        CancelButton = _finish;

        if (!outcome.Success)
        {
            _stage = Stage.Failed;
            ExitCode = 4;
            _title.Text = "Installation Failed";
            _subtitle.Text = "Setup could not install Carousel.";
            _status.Text = outcome.Message;
            _open.Visible = false;
            _finish.Text = "Close";
            return;
        }

        _stage = Stage.Done;
        ExitCode = 0;
        _bar.Style = ProgressBarStyle.Continuous;
        _bar.Value = 100;
        _open.Visible = File.Exists(outcome.AppPath);
        _finish.Text = "Finish";
        if (outcome.HasIssues)
        {
            _title.Text = "Completing the Carousel Setup Wizard";
            _subtitle.Text = "Setup installed Carousel with issues.";
            _status.Text = outcome.Message;
            return;
        }

        _title.Text = "Completing the Carousel Setup Wizard";
        _subtitle.Text = "Setup has finished installing Carousel.";
        _status.Text = "Completed successfully.";
    }

    private void BrowseFolder()
    {
        if (NativeFolderPicker.TryPick(_folderBox.Text, Handle, out var path))
        {
            _folderBox.Text = path;
            _folderError.Visible = false;
        }
    }

    private static void StartApp(string app, string targetDir)
    {
        if (string.IsNullOrWhiteSpace(app) || !File.Exists(app))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = app,
                UseShellExecute = true,
                WorkingDirectory = string.IsNullOrWhiteSpace(targetDir) ? Path.GetDirectoryName(app) : targetDir
            });
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string LoadLicense()
    {
        var assembly = typeof(InstallWizard).Assembly;
        using var stream = assembly.GetManifestResourceStream("LICENSE.txt");
        if (stream is null)
        {
            return "MIT License";
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Panel BuildLicensePage(out TextBox licenseBox, out CheckBox accept)
    {
        var page = new Panel { Dock = DockStyle.Fill };
        accept = new CheckBox
        {
            AutoSize = true,
            Dock = DockStyle.Bottom,
            Text = "I accept the terms of the license agreement",
            Padding = new Padding(0, 8, 0, 0)
        };
        licenseBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            BackColor = SystemColors.Window,
            HideSelection = true
        };
        page.Controls.Add(licenseBox);
        page.Controls.Add(accept);
        return page;
    }

    private Panel BuildFolderPage(out TextBox folderBox, out Label folderError)
    {
        var page = new Panel { Dock = DockStyle.Fill };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var intro = new Label
        {
            AutoSize = true,
            Text = "Setup will install Carousel in this folder.",
            Margin = new Padding(0, 0, 0, 12)
        };
        layout.Controls.Add(intro, 0, 0);
        layout.SetColumnSpan(intro, 2);

        folderBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 8, 0)
        };
        var browse = MakeButton("Browse...", 96);
        browse.Margin = new Padding(0);
        browse.Click += (_, _) => BrowseFolder();
        layout.Controls.Add(folderBox, 0, 1);
        layout.Controls.Add(browse, 1, 1);

        folderError = new Label
        {
            AutoSize = true,
            ForeColor = Color.Firebrick,
            Margin = new Padding(0, 8, 0, 0),
            Visible = false
        };
        layout.Controls.Add(folderError, 0, 2);
        layout.SetColumnSpan(folderError, 2);

        var note = new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Text = "This install does not request administrator rights.",
            Margin = new Padding(0, 16, 0, 0)
        };
        layout.Controls.Add(note, 0, 3);
        layout.SetColumnSpan(note, 2);

        page.Controls.Add(layout);
        return page;
    }

    private static Panel BuildProgressPage(out Label status, out ProgressBar bar, out TextBox log)
    {
        var page = new Panel { Dock = DockStyle.Fill };
        status = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 24,
            Text = "Starting..."
        };
        bar = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = 22,
            Style = ProgressBarStyle.Marquee
        };
        var spacer = new Panel { Dock = DockStyle.Top, Height = 12 };
        log = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            BackColor = SystemColors.Window
        };
        page.Controls.Add(log);
        page.Controls.Add(spacer);
        page.Controls.Add(bar);
        page.Controls.Add(status);
        return page;
    }

    private static Button MakeButton(string text, int width)
    {
        return new Button
        {
            Text = text,
            Width = width,
            Height = 28,
            Margin = new Padding(8, 0, 0, 0),
            UseVisualStyleBackColor = true
        };
    }
}
