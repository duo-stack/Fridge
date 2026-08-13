using System.Security.Cryptography;

namespace Fridge;

internal sealed class MainForm : Form
{
    private static readonly Color WindowBackground = Color.FromArgb(244, 246, 248);
    private static readonly Color Surface = Color.White;
    private static readonly Color Border = Color.FromArgb(214, 220, 226);
    private static readonly Color TextPrimary = Color.FromArgb(27, 35, 43);
    private static readonly Color TextMuted = Color.FromArgb(92, 104, 116);
    private static readonly Color Primary = Color.FromArgb(25, 103, 87);
    private static readonly Color PrimaryHover = Color.FromArgb(19, 83, 70);
    private static readonly Color Secondary = Color.FromArgb(49, 68, 86);
    private static readonly Color Danger = Color.FromArgb(174, 55, 55);

    private readonly TextBox _serverAddress = CreateTextBox("example.com");
    private readonly TextBox _sshPort = CreatePortInput(22);
    private readonly TextBox _sshUser = CreateTextBox("root");
    private readonly TextBox _sshPassword = CreateTextBox();
    private readonly TextBox _frpsPort = CreatePortInput(7000);
    private readonly TextBox _rdpPort = CreatePortInput(3389);
    private readonly TextBox _token = CreateTextBox();
    private readonly RichTextBox _log = new();
    private readonly Label _status = new();
    private readonly Label _resourceStatus = new();
    private readonly ErrorProvider _errors = new();
    private readonly Dictionary<Control, RoundedPanel> _fieldHosts = new();
    private readonly Dictionary<Control, Func<string?>> _fieldValidators = new();
    private readonly HashSet<Control> _editedFields = new();
    private readonly Button _deployServerButton;
    private readonly Button _deployLocalButton;
    private readonly Button _cancelButton;
    private readonly ClickOutsideFocusFilter _clickOutsideFocusFilter;
    private CancellationTokenSource? _operationCancellation;
    private bool _focusValidationEnabled;

    public MainForm()
    {
        Text = "Fridge - FRP 远程桌面部署工具";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1100, 820);
        MinimumSize = new Size(960, 740);
        BackColor = WindowBackground;
        ForeColor = TextPrimary;
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        AutoScaleMode = AutoScaleMode.Dpi;
        Padding = new Padding(28, 22, 28, 20);

        _errors.ContainerControl = this;
        _errors.BlinkStyle = ErrorBlinkStyle.NeverBlink;

        _sshPassword.UseSystemPasswordChar = true;
        _token.UseSystemPasswordChar = true;
        _token.Text = GenerateToken();

        _deployServerButton = CreateButton("部署服务器", Primary, PrimaryHover);
        _deployLocalButton = CreateButton("部署本机被控端", Secondary, Color.FromArgb(37, 53, 67));
        _cancelButton = CreateButton("取消当前操作", Color.FromArgb(235, 238, 241), Color.FromArgb(222, 227, 231), TextPrimary);
        _cancelButton.Enabled = false;

        BuildLayout();
        WireEvents();
        _clickOutsideFocusFilter = new ClickOutsideFocusFilter(HandleGlobalPointerDown);
        Application.AddMessageFilter(_clickOutsideFocusFilter);
        UpdateResourceStatus();
        Shown += (_, _) =>
        {
            ClearActiveFocus();
            _focusValidationEnabled = true;
        };
        FormClosed += (_, _) => Application.RemoveMessageFilter(_clickOutsideFocusFilter);
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = WindowBackground,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 252));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 162));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));

        root.Controls.Add(CreateHeader(), 0, 0);
        root.Controls.Add(CreateSettingsPanel(), 0, 1);
        root.Controls.Add(CreateActionsPanel(), 0, 2);
        root.Controls.Add(CreateLogPanel(), 0, 3);
        root.Controls.Add(CreateFooter(), 0, 4);
        Controls.Add(root);
    }

    private Control CreateHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        var title = new Label
        {
            Text = "Fridge",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 22F, FontStyle.Bold),
            ForeColor = TextPrimary,
            Location = new Point(0, 0)
        };
        var subtitle = new Label
        {
            Text = "FRP + Windows 远程桌面部署工具",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9.5F),
            ForeColor = TextMuted,
            Location = new Point(3, 43)
        };
        _resourceStatus.AutoSize = false;
        _resourceStatus.Dock = DockStyle.Right;
        _resourceStatus.TextAlign = ContentAlignment.MiddleRight;
        _resourceStatus.Font = new Font("Microsoft YaHei UI", 9F);
        _resourceStatus.Size = new Size(390, 72);
        panel.Controls.Add(title);
        panel.Controls.Add(subtitle);
        panel.Controls.Add(_resourceStatus);
        return panel;
    }

    private Control CreateSettingsPanel()
    {
        var surface = CreateSurface();
        surface.Padding = new Padding(20, 16, 20, 16);
        surface.Margin = new Padding(0, 0, 0, 14);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 5,
            BackColor = Surface,
            Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
        for (var i = 1; i < 5; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        var heading = new Label
        {
            Text = "连接配置",
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
            ForeColor = TextPrimary,
            TextAlign = ContentAlignment.TopLeft
        };
        layout.Controls.Add(heading, 0, 0);
        layout.SetColumnSpan(heading, 4);

        AddField(layout, "服务器地址", CreateInputHost(_serverAddress, ValidateServerAddress), 1, 0);
        AddField(layout, "SSH 端口", CreateInputHost(_sshPort, () => ValidatePort(_sshPort, "SSH 端口")), 1, 2);
        AddField(layout, "SSH 用户名", CreateInputHost(_sshUser, ValidateSshUser), 2, 0);
        AddField(layout, "SSH 密码", CreatePasswordBox(), 2, 2);
        AddField(layout, "FRPS 端口", CreateInputHost(_frpsPort, () => ValidatePort(_frpsPort, "FRPS 端口", true)), 3, 0);
        AddField(layout, "公网 RDP 端口", CreateInputHost(_rdpPort, () => ValidatePort(_rdpPort, "公网 RDP 端口", true)), 3, 2);
        AddField(layout, "认证 Token", CreateTokenBox(), 4, 0);
        layout.SetColumnSpan(layout.GetControlFromPosition(1, 4)!, 3);

        surface.Controls.Add(layout);
        return surface;
    }

    private Control CreatePasswordBox()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Surface,
            Margin = Padding.Empty
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var passwordHost = CreateInputHost(_sshPassword);
        var show = new CheckBox
        {
            Text = "显示",
            Dock = DockStyle.Fill,
            ForeColor = TextMuted,
            Margin = new Padding(8, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft
        };
        show.CheckedChanged += (_, _) => _sshPassword.UseSystemPasswordChar = !show.Checked;
        panel.Controls.Add(passwordHost, 0, 0);
        panel.Controls.Add(show, 1, 0);
        return panel;
    }

    private Control CreateTokenBox()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Surface,
            Margin = Padding.Empty
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var tokenHost = CreateInputHost(_token, ValidateToken);
        var regenerate = CreateSmallButton("重新生成");
        var show = new CheckBox
        {
            Text = "显示",
            Dock = DockStyle.Fill,
            ForeColor = TextMuted,
            Margin = new Padding(8, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft
        };
        regenerate.Click += (_, _) =>
        {
            _token.Text = GenerateToken();
            ValidateField(_token);
        };
        show.CheckedChanged += (_, _) => _token.UseSystemPasswordChar = !show.Checked;
        panel.Controls.Add(tokenHost, 0, 0);
        panel.Controls.Add(regenerate, 1, 0);
        panel.Controls.Add(show, 2, 0);
        return panel;
    }

    private Control CreateActionsPanel()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = WindowBackground,
            Margin = new Padding(0, 0, 0, 14)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.Controls.Add(CreateActionSurface(
            "1  部署云服务器",
            "通过 SSH 上传内嵌 FRPS，写入配置并启动 systemd 服务。",
            "支持 root 或免密 sudo 账号",
            _deployServerButton), 0, 0);
        var local = CreateActionSurface(
            "2  部署当前电脑",
            "释放 FRPC、启用 RDP，并注册 SYSTEM 开机任务。",
            "需要 Windows 专业版或可用的 RDP 服务",
            _deployLocalButton);
        local.Margin = new Padding(7, 0, 0, 0);
        layout.Controls.Add(local, 1, 0);
        return layout;
    }

    private static Control CreateActionSurface(string title, string description, string note, Button action)
    {
        var surface = CreateSurface();
        surface.Margin = new Padding(0, 0, 7, 0);
        surface.Padding = new Padding(20, 14, 20, 14);
        var titleLabel = new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold),
            ForeColor = TextPrimary,
            Location = new Point(20, 15)
        };
        var descriptionLabel = new Label
        {
            Text = description,
            AutoEllipsis = true,
            ForeColor = TextMuted,
            Location = new Point(20, 46),
            Size = new Size(430, 23)
        };
        var noteLabel = new Label
        {
            Text = note,
            AutoEllipsis = true,
            ForeColor = Color.FromArgb(111, 91, 47),
            Location = new Point(20, 72),
            Size = new Size(430, 21)
        };
        action.Location = new Point(20, 101);
        action.Size = new Size(164, 40);
        surface.Controls.Add(titleLabel);
        surface.Controls.Add(descriptionLabel);
        surface.Controls.Add(noteLabel);
        surface.Controls.Add(action);
        surface.Resize += (_, _) =>
        {
            descriptionLabel.Width = Math.Max(100, surface.ClientSize.Width - 40);
            noteLabel.Width = Math.Max(100, surface.ClientSize.Width - 40);
        };
        return surface;
    }

    private Control CreateLogPanel()
    {
        var surface = CreateSurface();
        surface.Padding = new Padding(1);
        surface.Margin = Padding.Empty;
        var header = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = Surface };
        var heading = new Label
        {
            Text = "执行日志",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            ForeColor = TextPrimary,
            Location = new Point(18, 12)
        };
        var clear = CreateSmallButton("清空");
        clear.Size = new Size(66, 29);
        clear.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        clear.Location = new Point(surface.Width - 82, 7);
        clear.Click += (_, _) => _log.Clear();
        header.Resize += (_, _) => clear.Left = Math.Max(0, header.ClientSize.Width - clear.Width - 14);
        header.Controls.Add(heading);
        header.Controls.Add(clear);

        _log.Dock = DockStyle.Fill;
        _log.BorderStyle = BorderStyle.None;
        _log.BackColor = Color.FromArgb(250, 251, 252);
        _log.ForeColor = Color.FromArgb(43, 52, 61);
        _log.Font = new Font("Cascadia Mono", 9F);
        _log.ReadOnly = true;
        _log.TabStop = false;
        _log.DetectUrls = false;
        _log.WordWrap = true;
        _log.Margin = Padding.Empty;
        _log.Text = "等待部署操作。请先确认服务器地址和端口配置。\n";

        surface.Controls.Add(_log);
        surface.Controls.Add(header);
        return surface;
    }

    private Control CreateFooter()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = WindowBackground,
            Padding = new Padding(0, 10, 0, 0),
            Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 10));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 134));
        _status.Text = "就绪";
        _status.Dock = DockStyle.Fill;
        _status.ForeColor = TextMuted;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _cancelButton.Dock = DockStyle.Fill;
        layout.Controls.Add(_status, 0, 0);
        layout.Controls.Add(_cancelButton, 2, 0);
        return layout;
    }

    private void WireEvents()
    {
        _deployServerButton.Click += async (_, _) => await RunOperationAsync(
            "正在部署服务器",
            true,
            token => new ServerDeployer(AppendLog, ConfirmFingerprint).DeployAsync(ReadSettings(), token));
        _deployLocalButton.Click += async (_, _) => await RunOperationAsync(
            "正在部署本机",
            false,
            token => new LocalDeployer(AppendLog).DeployAsync(ReadSettings(), token));
        _cancelButton.Click += (_, _) => _operationCancellation?.Cancel();

        _sshPort.TextChanged += (_, _) => ValidatePortFieldsWhenNeeded();
        _frpsPort.TextChanged += (_, _) => ValidatePortFieldsWhenNeeded();
        _rdpPort.TextChanged += (_, _) => ValidatePortFieldsWhenNeeded();
        _sshPassword.TextChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_sshPassword.Text)) SetFieldError(_sshPassword, null);
        };
    }

    private async Task RunOperationAsync(
        string busyText,
        bool requireSshPassword,
        Func<CancellationToken, Task> operation)
    {
        if (_operationCancellation is not null) return;
        ClearActiveFocus();
        if (!ValidateAllInputs(requireSshPassword)) return;

        try
        {
            var settings = ReadSettings();
            settings.Validate();
            _operationCancellation = new CancellationTokenSource();
            SetBusy(true, busyText);
            AppendLog(string.Empty);
            AppendLog($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {busyText} =====");
            await operation(_operationCancellation.Token);
            _status.Text = "部署完成";
            _status.ForeColor = Primary;
            AppendLog("操作成功完成。");
        }
        catch (OperationCanceledException)
        {
            _status.Text = "操作已取消";
            _status.ForeColor = TextMuted;
            AppendLog("操作已取消。远程命令如果已经开始，仍需检查服务器最终状态。");
        }
        catch (Exception exception)
        {
            _status.Text = "部署失败";
            _status.ForeColor = Danger;
            AppendLog("错误：" + exception.Message);
            ShowError(exception.Message);
        }
        finally
        {
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            SetBusy(false, "就绪");
        }
    }

    private DeploymentSettings ReadSettings()
    {
        return new DeploymentSettings(
            _serverAddress.Text.Trim(),
            ParsePort(_sshPort.Text),
            _sshUser.Text.Trim(),
            _sshPassword.Text,
            ParsePort(_frpsPort.Text),
            ParsePort(_rdpPort.Text),
            _token.Text.Trim());
    }

    private bool ConfirmFingerprint(string fingerprint)
    {
        if (InvokeRequired)
        {
            return (bool)Invoke(() => ConfirmFingerprint(fingerprint));
        }

        var message = "即将连接的 SSH 主机指纹为：\n\n" + fingerprint +
                      "\n\n请与服务器控制台显示的指纹核对。确认信任该服务器吗？";
        return MessageBox.Show(this, message, "确认 SSH 主机", MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    private void SetBusy(bool busy, string text)
    {
        _deployServerButton.Enabled = !busy;
        _deployLocalButton.Enabled = !busy;
        _cancelButton.Enabled = busy;
        _status.Text = text;
        _status.ForeColor = busy ? Secondary : TextMuted;
        UseWaitCursor = busy;
    }

    private void AppendLog(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(message));
            return;
        }

        _log.AppendText(message + Environment.NewLine);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private void UpdateResourceStatus()
    {
        var clientReady = EmbeddedFiles.Exists("frpc.exe");
        var serverReady = EmbeddedFiles.Exists("frps-linux-amd64.tar.gz") &&
                          EmbeddedFiles.Exists("frps-linux-arm64.tar.gz");
        _resourceStatus.Text = $"内嵌资源  Windows：{ToState(clientReady)}   Linux：{ToState(serverReady)}";
        _resourceStatus.ForeColor = clientReady && serverReady ? Primary : Color.FromArgb(148, 92, 30);
    }

    private static string ToState(bool ready) => ready ? "已就绪" : "未打包";

    private RoundedPanel CreateInputHost(Control input, Func<string?>? validator = null)
    {
        var host = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Surface,
            BorderColor = Border,
            FocusBorderColor = Primary,
            ErrorBorderColor = Danger,
            CornerRadius = 6,
            BorderThickness = 1,
            TabStop = false,
            Tag = "input-host"
        };

        input.BackColor = Surface;
        input.ForeColor = TextPrimary;
        input.Margin = Padding.Empty;
        host.Controls.Add(input);

        void CenterInput()
        {
            var horizontalPadding = 10;
            var preferredHeight = Math.Min(input.PreferredSize.Height, Math.Max(1, host.ClientSize.Height - 8));
            input.Bounds = new Rectangle(
                horizontalPadding,
                Math.Max(0, (host.ClientSize.Height - preferredHeight) / 2),
                Math.Max(1, host.ClientSize.Width - horizontalPadding * 2),
                preferredHeight);
        }

        host.Resize += (_, _) => CenterInput();
        host.MouseDown += (_, _) => input.Focus();
        input.Enter += (_, _) =>
        {
            host.IsFocused = true;
        };
        if (input is TextBox textBox)
        {
            textBox.TextChanged += (_, _) =>
            {
                if (_focusValidationEnabled) _editedFields.Add(input);
                if (host.HasError) SetFieldError(input, null);
            };
        }
        input.Leave += (_, _) =>
        {
            NormalizeField(input);
            BeginInvoke(() =>
            {
                host.IsFocused = host.ContainsFocus;
                if (!host.ContainsFocus && _editedFields.Contains(input))
                {
                    ValidateField(input, validateEmpty: false);
                }
            });
        };

        _fieldHosts[input] = host;
        if (validator is not null) _fieldValidators[input] = validator;
        _errors.SetIconAlignment(host, ErrorIconAlignment.MiddleRight);
        _errors.SetIconPadding(host, 3);
        CenterInput();
        return host;
    }

    private string? ValidateServerAddress()
    {
        return DeploymentSettings.IsHostOrIpv4(_serverAddress.Text.Trim())
            ? null
            : "请输入有效的 IPv4 地址或主机名。";
    }

    private string? ValidateSshUser()
    {
        return string.IsNullOrWhiteSpace(_sshUser.Text) ? "请输入 SSH 用户名。" : null;
    }

    private string? ValidateToken()
    {
        return DeploymentSettings.IsValidToken(_token.Text.Trim())
            ? null
            : "Token 需要包含 16-128 个字母、数字、点、下划线或连字符。";
    }

    private string? ValidatePort(TextBox input, string name, bool checkConflict = false)
    {
        var value = input.Text.Trim();
        if (!int.TryParse(value, out var port) || port is < 1 or > 65535)
        {
            return $"{name}必须是 1-65535 之间的数字。";
        }

        if (checkConflict &&
            int.TryParse(_frpsPort.Text.Trim(), out var frpsPort) &&
            int.TryParse(_rdpPort.Text.Trim(), out var rdpPort) &&
            frpsPort == rdpPort)
        {
            return "FRPS 端口和公网 RDP 端口不能相同。";
        }

        return null;
    }

    private void NormalizeField(Control input)
    {
        if (input == _serverAddress)
        {
            var value = _serverAddress.Text.Trim();
            _serverAddress.Text = Uri.CheckHostName(value) == UriHostNameType.Dns
                ? value.TrimEnd('.').ToLowerInvariant()
                : value;
        }
        else if (input == _sshUser)
        {
            _sshUser.Text = _sshUser.Text.Trim();
        }
        else if (input == _token)
        {
            _token.Text = _token.Text.Trim();
        }
        else if (input == _sshPort || input == _frpsPort || input == _rdpPort)
        {
            input.Text = input.Text.Trim();
        }
    }

    private bool ValidateField(Control input, bool validateEmpty = true)
    {
        if (!validateEmpty && IsInputEmpty(input))
        {
            SetFieldError(input, null);
            return true;
        }

        if (!_fieldValidators.TryGetValue(input, out var validator))
        {
            SetFieldError(input, null);
            return true;
        }

        var error = validator();
        SetFieldError(input, error);
        return error is null;
    }

    private static bool IsInputEmpty(Control input)
    {
        return input is TextBoxBase textBox && string.IsNullOrWhiteSpace(textBox.Text);
    }

    private void SetFieldError(Control input, string? error)
    {
        if (!_fieldHosts.TryGetValue(input, out var host)) return;
        host.HasError = error is not null;
        _errors.SetError(host, error ?? string.Empty);

        if (error is not null && _operationCancellation is null)
        {
            _status.Text = error;
            _status.ForeColor = Danger;
        }
        else if (_operationCancellation is null && _fieldHosts.Values.All(value => !value.HasError))
        {
            _status.Text = "就绪";
            _status.ForeColor = TextMuted;
        }
    }

    private bool ValidateAllInputs(bool requireSshPassword)
    {
        var inputs = new List<Control> { _serverAddress, _sshPort, _frpsPort, _rdpPort, _token };
        if (requireSshPassword) inputs.Add(_sshUser);

        foreach (var input in inputs) NormalizeField(input);
        var valid = true;
        foreach (var input in inputs)
        {
            valid &= ValidateField(input);
        }

        var passwordError = requireSshPassword && string.IsNullOrWhiteSpace(_sshPassword.Text)
            ? "请输入 SSH 密码。"
            : null;
        SetFieldError(_sshPassword, passwordError);
        valid &= passwordError is null;

        if (!requireSshPassword) SetFieldError(_sshUser, null);
        if (valid) return true;

        var firstInvalid = _fieldHosts.FirstOrDefault(item => item.Value.HasError).Key;
        firstInvalid?.Focus();
        return false;
    }

    private void ValidatePortFieldsWhenNeeded()
    {
        if (!_fieldHosts.TryGetValue(_sshPort, out var sshHost) ||
            !_fieldHosts.TryGetValue(_frpsPort, out var frpsHost) ||
            !_fieldHosts.TryGetValue(_rdpPort, out var rdpHost) ||
            (!sshHost.HasError && !frpsHost.HasError && !rdpHost.HasError))
        {
            return;
        }

        ValidateField(_sshPort, validateEmpty: false);
        ValidateField(_frpsPort, validateEmpty: false);
        ValidateField(_rdpPort, validateEmpty: false);
    }

    private void HandleGlobalPointerDown(IntPtr targetHandle)
    {
        Control? focusedRegion = _fieldHosts.Values.FirstOrDefault(host => host.ContainsFocus);
        if (focusedRegion is null && _log.ContainsFocus)
        {
            focusedRegion = _log;
        }

        if (focusedRegion is null || ClickOutsideFocusFilter.IsHandleInside(focusedRegion, targetHandle))
        {
            return;
        }

        ClearActiveFocus();
    }

    private void ClearActiveFocus()
    {
        ActiveControl = null;
    }

    private void ShowError(string message)
    {
        var lines = message.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var summary = string.Join(Environment.NewLine, lines.Take(2));
        MessageBox.Show(this, summary, "Fridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static void AddField(TableLayoutPanel layout, string labelText, Control control, int row, int column)
    {
        var label = new Label
        {
            Text = labelText,
            Dock = DockStyle.Fill,
            ForeColor = TextMuted,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 2, 10, 2)
        };
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(0, 4, column == 0 ? 24 : 0, 4);
        layout.Controls.Add(label, column, row);
        layout.Controls.Add(control, column + 1, row);
    }

    private static RoundedPanel CreateSurface()
    {
        return new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Surface,
            BorderColor = Border,
            CornerRadius = 8,
            Margin = Padding.Empty
        };
    }

    private static TextBox CreateTextBox(string placeholder = "")
    {
        return new TextBox
        {
            BorderStyle = BorderStyle.None,
            Font = new Font("Microsoft YaHei UI", 9.5F),
            PlaceholderText = placeholder,
            BackColor = Surface,
            ForeColor = TextPrimary
        };
    }

    private static TextBox CreatePortInput(int value)
    {
        var input = new TextBox
        {
            Text = value.ToString(),
            BorderStyle = BorderStyle.None,
            Font = new Font("Microsoft YaHei UI", 9.5F),
            MaxLength = 5,
            TextAlign = HorizontalAlignment.Left
        };
        input.KeyPress += (_, eventArgs) =>
        {
            if (!char.IsControl(eventArgs.KeyChar) && !char.IsDigit(eventArgs.KeyChar))
            {
                eventArgs.Handled = true;
            }
        };
        return input;
    }

    private static int ParsePort(string value)
    {
        return int.TryParse(value.Trim(), out var port) ? port : 0;
    }

    private static Button CreateButton(string text, Color background, Color hover, Color? foreground = null)
    {
        var button = new RoundedButton
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = background,
            ForeColor = foreground ?? Color.White,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            TabStop = true
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = hover;
        button.FlatAppearance.MouseDownBackColor = hover;
        return button;
    }

    private static Button CreateSmallButton(string text)
    {
        var button = CreateButton(text, Color.FromArgb(235, 238, 241), Color.FromArgb(222, 227, 231), TextPrimary);
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(7, 0, 0, 0);
        button.Font = new Font("Microsoft YaHei UI", 8.5F);
        return button;
    }

    private static string GenerateToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    }
}
