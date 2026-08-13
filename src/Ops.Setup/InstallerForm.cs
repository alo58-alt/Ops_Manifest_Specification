using System.Diagnostics;
using System.Drawing;

namespace CompanyOps.Setup;

internal sealed class InstallerForm : Form
{
    private readonly TextBox _installRoot = new() { Dock = DockStyle.Fill };
    private readonly TextBox _dataRoot = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _enableControlledUpdates = new()
    {
        AutoSize = true,
        Text = "启用受控项目更新（推荐）"
    };
    private readonly TextBox _approvedProjectRoots = new() { Dock = DockStyle.Fill };
    private readonly Button _installButton = new()
    {
        AutoSize = true,
        BackColor = Color.FromArgb(26, 115, 232),
        FlatStyle = FlatStyle.Flat,
        ForeColor = Color.White,
        Padding = new Padding(22, 9, 22, 9),
        Text = "安装并启动"
    };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee };
    private readonly Label _status = new()
    {
        AutoEllipsis = true,
        Dock = DockStyle.Fill,
        ForeColor = Color.FromArgb(70, 70, 70),
        Text = "请选择两个相互独立的本机目录。安装程序会完成其余工作。"
    };
    private bool _installing;

    public InstallerForm()
    {
        Text = "CompanyOps 安装程序";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(720, 545);
        ClientSize = new Size(760, 575);
        Font = new Font("Microsoft YaHei UI", 10F);
        BackColor = Color.White;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormClosing += OnFormClosing;

        var header = new Panel
        {
            BackColor = Color.FromArgb(245, 248, 255),
            Dock = DockStyle.Fill,
            Padding = new Padding(26, 20, 26, 16)
        };
        header.Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font(Font.FontFamily, 17F, FontStyle.Bold),
            ForeColor = Color.FromArgb(32, 33, 36),
            Location = new Point(24, 18),
            Text = "安装 CompanyOps"
        });
        header.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(95, 99, 104),
            Location = new Point(27, 61),
            Text = "选择程序目录和数据目录，然后点击一次安装。"
        });

        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(28, 20, 28, 24),
            RowCount = 11
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(FieldLabel("程序存放位置（自动新建 CompanyOps）"), 0, 0);
        layout.Controls.Add(CreatePathRow(_installRoot, "选择程序存放位置", "CompanyOps"), 0, 1);
        layout.Controls.Add(FieldLabel("数据存放位置（自动新建 CompanyOpsData）"), 0, 2);
        layout.Controls.Add(CreatePathRow(_dataRoot, "选择数据存放位置", "CompanyOpsData"), 0, 3);
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(110, 110, 110),
            Margin = new Padding(0, 8, 0, 0),
            Text = "可以选择同一个父文件夹，安装程序会创建两个独立子目录。"
        }, 0, 4);
        layout.Controls.Add(_enableControlledUpdates, 0, 5);
        layout.Controls.Add(CreateApprovedRootsRow(), 0, 6);
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(110, 110, 110),
            Margin = new Padding(0, 3, 0, 0),
            Text = "选择项目目录的父目录，例如 WebQuizBot 位于 D:\\project\\webquizbot，则选择 D:\\project。这里只配置一次。"
        }, 0, 7);
        layout.Controls.Add(_status, 0, 8);
        layout.Controls.Add(_progress, 0, 9);
        layout.Controls.Add(CreateActionRow(), 0, 10);

        _progress.Visible = false;
        _enableControlledUpdates.CheckedChanged += (_, _) =>
            _approvedProjectRoots.Enabled = _enableControlledUpdates.Checked;
        _approvedProjectRoots.Enabled = false;
        _installButton.Click += InstallAsync;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 105));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(layout, 0, 1);
        Controls.Add(root);

        try
        {
            var existing = InstallerEngine.DetectExistingInstallation();
            if (existing is not null)
            {
                _installRoot.Text = existing.InstallRoot;
                _dataRoot.Text = existing.DataRoot;
                _enableControlledUpdates.Checked = existing.EnableMutations;
                _approvedProjectRoots.Text = string.Join("; ", existing.AllowedProjectInstallRoots);
                _installButton.Text = "升级并启动";
                _status.Text = "已检测到现有 CompanyOps。可在本页完成一次性的受控更新授权，然后安全升级。";
            }
        }
        catch (Exception exception)
        {
            _status.ForeColor = Color.FromArgb(183, 28, 28);
            _status.Text = exception.Message;
        }
    }

    private static Label FieldLabel(string text) => new()
    {
        AutoSize = true,
        Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
        Margin = new Padding(0, 8, 0, 5),
        Text = text
    };

    private Control CreatePathRow(TextBox textBox, string dialogDescription, string childDirectoryName)
    {
        var browse = new Button
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(10, 0, 0, 0),
            Padding = new Padding(12, 5, 12, 5),
            Text = "选择…"
        };
        browse.Click += (_, _) => BrowseForFolder(textBox, dialogDescription, childDirectoryName);
        var row = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(textBox, 0, 0);
        row.Controls.Add(browse, 1, 0);
        return row;
    }

    private Control CreateApprovedRootsRow()
    {
        var browse = new Button
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(10, 0, 0, 0),
            Padding = new Padding(12, 5, 12, 5),
            Text = "选择项目父目录…"
        };
        browse.Click += (_, _) => BrowseForApprovedRoot();
        var row = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(_approvedProjectRoots, 0, 0);
        row.Controls.Add(browse, 1, 0);
        return row;
    }

    private void BrowseForApprovedRoot()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择允许 CompanyOps 管理的项目父目录",
            ShowNewFolderButton = false,
            UseDescriptionForTitle = true
        };
        var current = _approvedProjectRoots.Text.Split(';', StringSplitOptions.TrimEntries)[0];
        if (Directory.Exists(current))
        {
            dialog.SelectedPath = current;
        }
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            _approvedProjectRoots.Text = dialog.SelectedPath;
            _enableControlledUpdates.Checked = true;
        }
    }

    private Control CreateActionRow()
    {
        var close = new Button
        {
            AutoSize = true,
            Padding = new Padding(20, 9, 20, 9),
            Text = "关闭"
        };
        close.Click += (_, _) => Close();
        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        actions.Controls.Add(_installButton);
        actions.Controls.Add(close);
        return actions;
    }

    private static void BrowseForFolder(
        TextBox target,
        string description,
        string childDirectoryName)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = description,
            ShowNewFolderButton = true,
            UseDescriptionForTitle = true
        };
        var currentParent = Path.GetDirectoryName(target.Text);
        if (Directory.Exists(currentParent))
        {
            dialog.SelectedPath = currentParent;
        }

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            target.Text = Path.Combine(dialog.SelectedPath, childDirectoryName);
        }
    }

    private async void InstallAsync(object? sender, EventArgs eventArgs)
    {
        if (_installing)
        {
            _status.Text = "安装正在进行，请不要重复点击。";
            return;
        }

        _installing = true;
        _installButton.Enabled = false;
        _progress.Visible = true;
        _status.ForeColor = Color.FromArgb(70, 70, 70);
        _status.Text = "正在进行安全检查…";
        try
        {
            var progress = new Progress<string>(message => _status.Text = message);
            var result = await Task.Run(() =>
                new InstallerEngine().InstallOrUpdate(
                    _installRoot.Text,
                    _dataRoot.Text,
                    _enableControlledUpdates.Checked,
                    _approvedProjectRoots.Text,
                    progress));

            _progress.Visible = false;
            _installing = false;
            _status.ForeColor = Color.FromArgb(24, 128, 56);
            _status.Text = result.WasUpgrade
                ? "升级成功，CompanyOps Agent 和 Console 已启动。"
                : "安装成功，CompanyOps Agent 和 Console 已启动。";
            MessageBox.Show(
                $"CompanyOps {(result.WasUpgrade ? "升级" : "安装")}完成。\n\n程序目录：{result.InstallRoot}\n数据目录：{result.DataRoot}\n控制台：{result.ConsoleUrl}\n\n即将打开本机运维控制台。",
                result.WasUpgrade ? "升级成功" : "安装成功",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            try
            {
                var versionedConsoleUrl = $"{result.ConsoleUrl}?companyops-upgrade={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                Process.Start(new ProcessStartInfo(versionedConsoleUrl) { UseShellExecute = true });
            }
            catch
            {
                // Installation is already complete. The URL remains visible in the success message.
            }
            _installButton.Text = result.WasUpgrade ? "升级完成" : "安装完成";
        }
        catch (Exception exception)
        {
            _installing = false;
            _installButton.Enabled = true;
            _progress.Visible = false;
            _status.ForeColor = Color.FromArgb(183, 28, 28);
            _status.Text = exception.Message;
            MessageBox.Show(
                exception.Message,
                "CompanyOps 安装失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (!_installing)
        {
            return;
        }

        eventArgs.Cancel = true;
        _status.Text = "安装正在进行，完成或失败后才能关闭。";
    }
}
