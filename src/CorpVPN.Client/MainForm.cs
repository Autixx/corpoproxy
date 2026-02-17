using CorpVPN.Client.Models;
using CorpVPN.Client.Services;

namespace CorpVPN.Client;

public sealed class MainForm : Form
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly DelayProbeService _delayProbeService = new();
    private readonly SubscriptionService _subscriptionService;
    private readonly XrayService _xrayService;

    private readonly Panel _titleBar = new();
    private readonly Label _titleLabel = new();
    private readonly Button _minButton = new();
    private readonly Button _closeButton = new();
    private readonly Button _powerButton = new();
    private readonly CheckBox _tunToggle = new();
    private readonly Label _tunLabel = new();
    private readonly Label _statusLabel = new();
    private readonly Label _speedLabel = new();
    private readonly NotifyIcon _trayIcon = new();

    private readonly System.Windows.Forms.Timer _statsTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _healthTimer = new() { Interval = 15000 };

    private readonly SemaphoreSlim _sync = new(1, 1);

    private AppState _state = AppState.Default;
    private bool _connected;
    private VlessNode? _currentNode;
    private int _currentDelayMs;
    private double _lastTotalKbps;
    private DateTimeOffset _lastSpeedSample = DateTimeOffset.MinValue;

    private Point _dragOrigin;

    public MainForm()
    {
        _subscriptionService = new SubscriptionService(_httpClient);
        _xrayService = new XrayService(AppContext.BaseDirectory);

        BuildUi();

        _state = AppStateStore.Load();
        _tunToggle.Checked = _state.TunEnabled;
        UpdateTunLabel();

        _statsTimer.Tick += async (_, _) => await UpdateSpeedAsync();
        _healthTimer.Tick += async (_, _) => await MaintainConnectionAsync();

        FormClosing += OnFormClosing;
    }

    private void BuildUi()
    {
        SuspendLayout();

        BackColor = Color.FromArgb(46, 46, 46);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(250, 500);
        MaximizeBox = false;
        MinimizeBox = false;

        _titleBar.Dock = DockStyle.Top;
        _titleBar.Height = 34;
        _titleBar.BackColor = Color.FromArgb(58, 58, 58);
        _titleBar.MouseDown += (_, e) => _dragOrigin = e.Location;
        _titleBar.MouseMove += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                var p = PointToScreen(e.Location);
                Location = new Point(p.X - _dragOrigin.X, p.Y - _dragOrigin.Y);
            }
        };

        _titleLabel.Text = "CorpVPN";
        _titleLabel.ForeColor = Color.WhiteSmoke;
        _titleLabel.AutoSize = true;
        _titleLabel.Location = new Point(10, 9);

        _minButton.Text = "_";
        _minButton.FlatStyle = FlatStyle.Flat;
        _minButton.FlatAppearance.BorderSize = 0;
        _minButton.ForeColor = Color.WhiteSmoke;
        _minButton.BackColor = Color.FromArgb(58, 58, 58);
        _minButton.Size = new Size(30, 30);
        _minButton.Location = new Point(184, 2);
        _minButton.Click += (_, _) => HideToTray();

        _closeButton.Text = "X";
        _closeButton.FlatStyle = FlatStyle.Flat;
        _closeButton.FlatAppearance.BorderSize = 0;
        _closeButton.ForeColor = Color.WhiteSmoke;
        _closeButton.BackColor = Color.FromArgb(58, 58, 58);
        _closeButton.Size = new Size(30, 30);
        _closeButton.Location = new Point(216, 2);
        _closeButton.Click += async (_, _) => await ShutdownAsync();

        _powerButton.Text = "⏻";
        _powerButton.Font = new Font("Segoe UI Symbol", 28, FontStyle.Bold);
        _powerButton.ForeColor = Color.White;
        _powerButton.Size = new Size(120, 120);
        _powerButton.Location = new Point(65, 140);
        _powerButton.FlatStyle = FlatStyle.Flat;
        _powerButton.FlatAppearance.BorderSize = 0;
        _powerButton.BackColor = Color.FromArgb(185, 51, 51);
        _powerButton.Region = Region.FromHrgn(NativeMethods.CreateRoundRectRgn(0, 0, 120, 120, 120, 120));
        _powerButton.Click += async (_, _) => await ToggleConnectionAsync();

        _tunToggle.Appearance = Appearance.Button;
        _tunToggle.AutoSize = false;
        _tunToggle.Size = new Size(110, 30);
        _tunToggle.Location = new Point(70, 285);
        _tunToggle.FlatStyle = FlatStyle.Flat;
        _tunToggle.FlatAppearance.BorderSize = 0;
        _tunToggle.BackColor = Color.FromArgb(106, 106, 106);
        _tunToggle.CheckedChanged += async (_, _) => await OnTunChangedAsync();

        _tunLabel.AutoSize = false;
        _tunLabel.Size = new Size(200, 24);
        _tunLabel.Location = new Point(25, 320);
        _tunLabel.TextAlign = ContentAlignment.MiddleCenter;
        _tunLabel.ForeColor = Color.Gainsboro;

        _statusLabel.AutoSize = false;
        _statusLabel.Size = new Size(220, 44);
        _statusLabel.Location = new Point(15, 370);
        _statusLabel.ForeColor = Color.WhiteSmoke;
        _statusLabel.TextAlign = ContentAlignment.MiddleCenter;
        _statusLabel.Text = "Отключен";

        _speedLabel.AutoSize = false;
        _speedLabel.Size = new Size(220, 30);
        _speedLabel.Location = new Point(15, 420);
        _speedLabel.ForeColor = Color.Silver;
        _speedLabel.TextAlign = ContentAlignment.MiddleCenter;
        _speedLabel.Text = "0.00 Kbps";

        _trayIcon.Icon = SystemIcons.Application;
        _trayIcon.Text = "CorpVPN";
        _trayIcon.Visible = true;
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Выход", null, async (_, _) => await ShutdownAsync());
        _trayIcon.ContextMenuStrip = menu;

        _titleBar.Controls.AddRange([_titleLabel, _minButton, _closeButton]);
        Controls.AddRange([_titleBar, _powerButton, _tunToggle, _tunLabel, _statusLabel, _speedLabel]);

        ResumeLayout(false);
    }

    private async Task ToggleConnectionAsync()
    {
        await _sync.WaitAsync();
        try
        {
            if (_connected)
            {
                DisconnectCore();
                SetStatus("Отключен");
            }
            else
            {
                await ConnectCoreAsync();
            }

            UpdatePowerState();
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task ConnectCoreAsync()
    {
        SetStatus("Загрузка подписок...");
        var nodes = await _subscriptionService.LoadNodesAsync(CancellationToken.None);
        if (nodes.Count == 0)
        {
            SetStatus("Ноды не найдены в подписках");
            return;
        }

        SetStatus($"Проверка задержки ({nodes.Count} нод)...");
        var pick = await _delayProbeService.PickLeastDelayAsync(nodes, timeoutMs: 1800, CancellationToken.None);
        if (pick is null)
        {
            SetStatus("Нет доступных нод (delay timeout)");
            return;
        }

        var start = await _xrayService.StartAsync(pick.Node, _tunToggle.Checked, CancellationToken.None);
        if (!start.Ok)
        {
            SetStatus(start.Message);
            return;
        }

        try
        {
            SystemProxyService.SetEnabled(true);
        }
        catch (Exception ex)
        {
            _xrayService.Stop();
            SetStatus($"Proxy error: {ex.Message}");
            return;
        }

        _connected = true;
        _currentNode = pick.Node;
        _currentDelayMs = pick.DelayMs;
        _lastTotalKbps = 0;
        _lastSpeedSample = DateTimeOffset.MinValue;

        if (!_state.AutoStartConfigured)
        {
            if (AutoStartService.EnsureTask())
            {
                _state = _state with { AutoStartConfigured = true };
                AppStateStore.Save(_state);
            }
        }

        _statsTimer.Start();
        _healthTimer.Start();
        SetStatus($"Подключено: {pick.Node.Name} ({pick.DelayMs} ms)");
    }

    private void DisconnectCore()
    {
        _statsTimer.Stop();
        _healthTimer.Stop();

        try
        {
            SystemProxyService.SetEnabled(false);
        }
        catch
        {
            // ignore
        }

        _xrayService.Stop();
        _connected = false;
        _currentNode = null;
        _currentDelayMs = 0;
        _lastTotalKbps = 0;
        _lastSpeedSample = DateTimeOffset.MinValue;
        _speedLabel.Text = "0.00 Kbps";
    }

    private async Task UpdateSpeedAsync()
    {
        if (!_connected)
        {
            return;
        }

        try
        {
            var totalKbps = await _xrayService.QueryKbpsAsync(CancellationToken.None);
            var now = DateTimeOffset.UtcNow;
            if (_lastSpeedSample == DateTimeOffset.MinValue)
            {
                _lastSpeedSample = now;
                _lastTotalKbps = totalKbps;
                _speedLabel.Text = "0.00 Kbps";
                return;
            }

            var dt = Math.Max(0.001, (now - _lastSpeedSample).TotalSeconds);
            var delta = Math.Max(0, totalKbps - _lastTotalKbps);
            var speed = delta / dt;
            _speedLabel.Text = $"{speed:F2} Kbps";
            _lastTotalKbps = totalKbps;
            _lastSpeedSample = now;
        }
        catch
        {
            _speedLabel.Text = "0.00 Kbps";
        }
    }

    private async Task MaintainConnectionAsync()
    {
        if (!_connected)
        {
            return;
        }

        if (!_xrayService.IsRunning)
        {
            SetStatus("Потеряно ядро, fallback...");
            DisconnectCore();
            await ConnectCoreAsync();
            UpdatePowerState();
            return;
        }

        var currentNode = _currentNode;
        if (currentNode is null)
        {
            return;
        }

        var nodes = await _subscriptionService.LoadNodesAsync(CancellationToken.None);
        if (nodes.Count < 2)
        {
            return;
        }

        var best = await _delayProbeService.PickLeastDelayAsync(nodes, timeoutMs: 1400, CancellationToken.None);
        if (best is null)
        {
            return;
        }

        if (_currentNode is not null && best.Node.OriginalUri != _currentNode.OriginalUri && best.DelayMs + 50 < _currentDelayMs)
        {
            SetStatus($"Переключение fallback: {best.Node.Name}");
            DisconnectCore();
            var started = await _xrayService.StartAsync(best.Node, _tunToggle.Checked, CancellationToken.None);
            if (started.Ok)
            {
                SystemProxyService.SetEnabled(true);
                _connected = true;
                _currentNode = best.Node;
                _currentDelayMs = best.DelayMs;
                SetStatus($"Подключено: {best.Node.Name} ({best.DelayMs} ms)");
            }
            else
            {
                SetStatus(started.Message);
            }

            UpdatePowerState();
        }
    }

    private async Task OnTunChangedAsync()
    {
        UpdateTunLabel();
        _state = _state with { TunEnabled = _tunToggle.Checked };
        AppStateStore.Save(_state);

        if (_connected)
        {
            SetStatus("Перезапуск для применения TUN...");
            DisconnectCore();
            await ConnectCoreAsync();
            UpdatePowerState();
        }
    }

    private void UpdateTunLabel()
    {
        _tunLabel.Text = _tunToggle.Checked ? "TUN: ON" : "TUN: OFF";
        _tunToggle.Text = _tunToggle.Checked ? "     ON" : "OFF     ";
        _tunToggle.BackColor = _tunToggle.Checked ? Color.FromArgb(31, 162, 74) : Color.FromArgb(106, 106, 106);
        _tunToggle.ForeColor = Color.White;
    }

    private void UpdatePowerState()
    {
        _powerButton.BackColor = _connected ? Color.FromArgb(31, 162, 74) : Color.FromArgb(185, 51, 51);
    }

    private void SetStatus(string text)
    {
        _statusLabel.Text = text;
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    private void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private async Task ShutdownAsync()
    {
        DisconnectCore();
        _trayIcon.Visible = false;
        await Task.Delay(80);
        Close();
    }

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _trayIcon.Visible = false;
        DisconnectCore();
        await Task.CompletedTask;
    }
}

internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    public static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
}
