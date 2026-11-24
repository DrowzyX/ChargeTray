using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ChargeTray;

static class Program {
    [STAThread]
    private static void Main(string[] args) {
        bool demo = args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        bool autoStarted = args.Contains("--startup", StringComparer.OrdinalIgnoreCase);
        using var single = new Mutex(true, demo ? @"Local\ChargeTray.Demo.v1" : @"Local\ChargeTray.Live.v1", out bool first);
        if (!first) {
            if (autoStarted) return;
            MessageBox.Show("ChargeTray уже работает. Найдите значок батареи рядом с часами.", "ChargeTray",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp(demo));
    }
}

sealed class TrayApp : ApplicationContext {
    private readonly NotifyIcon _main = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _startupItem = new("Запускать вместе с Windows");
    private readonly ToolStripMenuItem _position = new("Положение индикатора");
    private readonly ToolStripMenuItem _islandItem = new("Островок сверху");
    private readonly ToolStripMenuItem _stripItem = new("Полоска рядом с часами");
    private readonly ToolStripMenuItem _trayOnlyItem = new("Только трей");
    private readonly ChargeStrip _strip = new();
    private readonly ChargeIsland _island = new();
    private readonly ToolStripMenuItem _extra = new("Отдельный значок для каждого устройства");
    private readonly ToolStripMenuItem _colors = new("Оформление и RGB");
    private readonly ToolStripMenuItem _rgbItem = new("Плавная RGB-анимация");
    private readonly System.Windows.Forms.Timer _themeTimer = new() { Interval = 16 };
    private readonly Dictionary<string, (NotifyIcon Tray, Icon Icon, string Signature)> _icons = new();
    private readonly HashSet<string> _customAlerted = new(StringComparer.OrdinalIgnoreCase);
    private readonly SettingsStore _prefs = new();
    private readonly BatteryHistory _history = new();
    private DeviceHubForm? _hub;
    private Accessory[] _rawData = [];
    private bool _seenFirstScan;
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 4 * 60 * 60 * 1000 };
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 20_000 };
    private readonly BatteryReader _reader = new();
    private readonly BatteryPanel _panel;
    private readonly bool _demo;
    private Icon? _mainIcon;
    private string? _iconSignature;
    private Accessory[] _data = [];
    private bool _busy, _exiting;
    private DateTime? _updated;
    private string[] _errors = [];
    private string[] _hardware = [];
    private static readonly string Settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChargeTray", "single-icon-v2");

    private static readonly string HideStripSettings = Path.Combine(Path.GetDirectoryName(Settings)!, "hide-strip-v3");
    private static readonly string PositionSettings = Path.Combine(Path.GetDirectoryName(Settings)!, "indicator-mode-v9");
    private string _positionMode = "top";

    public TrayApp(bool demo) {
        AccentTheme.Load();
        FluentStyle.SetTheme(_prefs.Values.ThemeMode);
        _demo = demo;
        _panel = new BatteryPanel();
        _island.MonitorIndex = _prefs.Values.IslandMonitor;
        _island.TopOffset = _prefs.Values.IslandTopOffset;
        _updateTimer.Tick += async (_, _) => await CheckUpdatesQuietly();
        _panel.RefreshRequested += async () => await Refresh(true);
        _strip.OpenPanel += () => Open(44);
        _island.OpenPanel += point => {
            _panel.OpenAt(point, 48, underIsland: true);
            if (!_demo && _updated is not null && DateTime.Now - _updated > TimeSpan.FromSeconds(6))
                _ = Refresh(false);
        };
        _island.ContextMenuStrip = _menu;
        if (!demo) {
            try {
                _positionMode = File.Exists(PositionSettings) ? File.ReadAllText(PositionSettings).Trim() :
                    File.Exists(HideStripSettings) ? "off" : "top";
                if (_positionMode is not ("top" or "bottom" or "off")) _positionMode = "top";
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        ApplyPosition(_positionMode, save: false);
        _islandItem.Click += (_, _) => ApplyPosition("top");
        _stripItem.Click += (_, _) => ApplyPosition("bottom");
        _trayOnlyItem.Click += (_, _) => ApplyPosition("off");
        _position.DropDownItems.AddRange([_islandItem, _stripItem, _trayOnlyItem]);
        _extra.Checked = _demo || !File.Exists(Settings);
        _extra.Click += (_, _) => {
            _extra.Checked = !_extra.Checked;
            if (!_demo) try {
                Directory.CreateDirectory(Path.GetDirectoryName(Settings)!);
                if (_extra.Checked) File.Delete(Settings);
                else File.WriteAllText(Settings, "1");
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
            RenderTray();
        };
        _menu.Items.Add("Центр устройств (Windows 11)", null, (_, _) => OpenHub());
        _menu.Items.Add("Открыть панель", null, (_, _) => Open());
        _menu.Items.Add("Обновить", null, async (_, _) => await Refresh(true));
        _menu.Items.Add(_position);
        _menu.Items.Add(_extra);
        SetupThemeMenu();
        _menu.Items.Add(_colors);
        _themeTimer.Tick += (_, _) => {
            if (_panel.Visible) _panel.Invalidate();
            if (_island.Visible) _island.Invalidate();
            if (_strip.Visible) _strip.Invalidate();
            if (_hub?.Visible == true) _hub.ThemeFrame();
        };
        _themeTimer.Enabled = AccentTheme.RgbEnabled && !_prefs.Values.EconomyMode;
        if (!demo) {
            _menu.Items.Add(_startupItem);
            _menu.Opening += (_, _) => SyncStartupMenu();
            _startupItem.Click += (_, _) => ToggleStartup();
            SyncStartupMenu();
        }
        if (demo) _menu.Items.Add("Проверить уведомление", null, (_, _) =>
            _main.ShowBalloonTip(5000, "Демо · пора зарядить мышь", "Logitech MX Master 3S — 12%", ToolTipIcon.Warning));
        var diagnostics = new ToolStripMenuItem("Диагностика и поддержка");
        diagnostics.DropDownItems.Add("Скопировать краткий отчёт", null, (_, _) => CopyDiagnostics());
        diagnostics.DropDownItems.Add("Скопировать отчёт для разработчика", null,
            async (_, _) => await CopyDeveloperDiagnostics());
        diagnostics.DropDownItems.Add(new ToolStripSeparator());
        diagnostics.DropDownItems.Add("Сообщить о новом устройстве (GitHub)", null, (_, _) => {
            try {
                Process.Start(new ProcessStartInfo("https://github.com/DrowzyX/ChargeTray/issues/new") {
                    UseShellExecute = true
                });
            } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) {
                _main.ShowBalloonTip(4000, "ChargeTray", "Не удалось открыть GitHub.", ToolTipIcon.Warning);
            }
        });
        _menu.Items.Add(diagnostics);
        _menu.Items.Add("Скопировать диагностику", null, (_, _) => CopyDiagnostics());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Выход", null, (_, _) => ExitThread());
        Hook(_main);
        RenderTray();
        _poll.Tick += async (_, _) => await Refresh(false);
        if (!demo) {
            _poll.Start();
            if (_prefs.Values.CheckUpdates) {
                _updateTimer.Start();
            }
        }
        // The first tick must run after Windows Forms establishes its UI context.
        Application.Idle += FirstIdle;
    }

    private void ApplyPosition(string mode, bool save = true) {
        _positionMode = mode;
        _islandItem.Checked = mode == "top";
        _stripItem.Checked = mode == "bottom";
        _trayOnlyItem.Checked = mode == "off";
        if (!save) return;
        if (!_demo) try {
            Directory.CreateDirectory(Path.GetDirectoryName(PositionSettings)!);
            File.WriteAllText(PositionSettings, mode);
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
        RenderTray();
    }

    private void SyncStartupMenu() {
        try {
            var state = StartupRegistration.GetState();
            _startupItem.Checked = state == StartupState.Current;
            _startupItem.Enabled = true;
            _startupItem.Text = state == StartupState.OtherPath ?
                "Автозагрузка: обновить путь" : "Запускать вместе с Windows";
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) {
            _startupItem.Checked = false;
            _startupItem.Enabled = false;
            _startupItem.Text = "Автозагрузка недоступна";
        }
    }

    private void ToggleStartup() {
        try {
            bool enable = StartupRegistration.GetState() != StartupState.Current;
            StartupRegistration.SetEnabled(enable);
            _main.ShowBalloonTip(2500, "ChargeTray", enable ?
                "Запуск вместе с Windows включён." : "Запуск вместе с Windows выключен.", ToolTipIcon.Info);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) {
            _main.ShowBalloonTip(4000, "ChargeTray", "Не удалось изменить автозагрузку. " + e.Message, ToolTipIcon.Warning);
        } finally { SyncStartupMenu(); }
    }

    private async void FirstIdle(object? sender, EventArgs args) {
        Application.Idle -= FirstIdle;
        await Refresh(false);
        if (_demo && !_exiting) Open();
        if (!_demo && _prefs.Values.CheckUpdates) _ = CheckUpdatesQuietly();
    }

    private void Hook(NotifyIcon tray) {
        tray.ContextMenuStrip = _menu;
        tray.MouseClick += (_, e) => {
            if (e.Button != MouseButtons.Left) return;
            if (_panel.Visible && _panel.ClickedOutsideJustNow) return;
            Open();
        };
        tray.BalloonTipClicked += (_, _) => Open();
    }

    private void Open(int clearance = 0) {
        _panel.OpenAt(Cursor.Position, clearance);
        if (!_demo && _updated is not null && DateTime.Now - _updated > TimeSpan.FromSeconds(6))
            _ = Refresh(false);
    }

    private async Task Refresh(bool force) {
        if (_busy || _exiting) return;
        _busy = true;
        _panel.UpdateData(_data, true, _updated, _demo, _errors);
        try {
            var next = _demo ? Samples() : await Task.Run(() => _reader.Read(force));
            if (_exiting) return;
            _errors = _demo ? [] : _reader.Errors.ToArray();
            _hardware = _demo ? [] : _reader.HardwareDiagnostics;
            int interval = !_demo && _reader.RetryAulaSoon ? 8_000 : 20_000;
            if (_poll.Interval != interval) _poll.Interval = interval;
            _rawData = next;
            var oldKeys = _data.Select(d => d.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var oldReadings = _data.ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);
            _data = _prefs.Apply(next);
            if (_seenFirstScan && _prefs.Values.ConnectionNotifications) {
                var charging = _data.Where(d => d.Charging &&
                    (!oldReadings.TryGetValue(d.Key, out var prior) || !prior.Charging)).Take(2).ToArray();
                if (charging.Length > 0)
                    _main.ShowBalloonTip(3500, "ChargeTray · зарядка",
                        string.Join("\n", charging.Select(d => "⚡ Заряжается: " + Trim(d.Name, 35))),
                        ToolTipIcon.Info);
                var newKeys = _data.Select(d => d.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var added = _data.Where(d => !oldKeys.Contains(d.Key)).Take(3).ToArray();
                var removed = oldKeys.Where(k => !newKeys.Contains(k)).Take(3).ToArray();
                if (added.Length > 0 || removed.Length > 0) {
                    var summary = string.Join("\n", added.Select(d => "Подключено: " + Trim(d.Name, 40))
                        .Concat(removed.Select(k => "Отключено: " + Trim(k, 32))));
                    _main.ShowBalloonTip(4000, "ChargeTray · устройства", summary, ToolTipIcon.Info);
                    _island.Pulse();
                }
            }
            _seenFirstScan = true;
            if (_prefs.Values.BatteryHistory) _history.Observe(_data);
            _updated = DateTime.Now;
            RenderTray();
            if (_hub?.Visible == true) _hub.UpdateDevices(_data, _updated);
            if (!_demo) NotifyLow();
        } catch (Exception e) {
            if (_exiting) return;
            _errors = [$"Ошибка чтения: {e.GetType().Name}"];
            _data = [];
            _hardware = [];
            RenderTray();
        } finally {
            _busy = false;
            if (!_exiting) _panel.UpdateData(_data, false, _updated, _demo, _errors);
        }
    }

    private void NotifyLow() {
        if (!_prefs.Values.BatteryNotifications) return;
        var low = new List<Accessory>();
        foreach (var d in _data) {
            if (d.IsStale || d.Charging) continue;
            int threshold = _prefs.Threshold(d.Key);
            bool lowNow = d.Percent is int p ? p < threshold : d.Level is Band.Low or Band.Empty;
            if (lowNow && _customAlerted.Add(d.Key)) low.Add(d);
            if (!lowNow) _customAlerted.Remove(d.Key);
        }
        if (low.Count == 0) return;
        var description = string.Join("\n", low.Take(3).Select(d =>
            Trim(d.Name, 34) + " — " + d.Reading + $" (порог {_prefs.Threshold(d.Key)}%)"));
        _main.ShowBalloonTip(4500, "Пора зарядить устройство", description, ToolTipIcon.Warning);
        _island.Pulse();
    }

    private void OpenHub() {
        if (_hub is null || _hub.IsDisposed) {
            _hub = new DeviceHubForm(_prefs, _history);
            _hub.CopySummaryRequested += CopyDiagnostics;
            _hub.CopyDeveloperRequested += async () => await CopyDeveloperDiagnostics();
            _hub.RefreshRequested += async () => await Refresh(true);
            _hub.MonitorChanged += index => {
                _island.MonitorIndex = index;
                _island.TopOffset = _prefs.Values.IslandTopOffset;
                RenderTray();
            };
            _hub.PreferencesChanged += () => {
                _data = _prefs.Apply(_rawData);
                _island.TopOffset = _prefs.Values.IslandTopOffset;
                _island.UpdateIsland(_positionMode == "top", _data);
                _themeTimer.Enabled = AccentTheme.RgbEnabled && !_prefs.Values.EconomyMode;
                _updateTimer.Enabled = _prefs.Values.CheckUpdates && !_demo;
                RenderTray();
            };
        }
        _hub.ShowHub();
        _hub.UpdateDevices(_data, _updated);
    }

    private async Task CheckUpdatesQuietly() {
        if (_exiting || _demo || !_prefs.Values.CheckUpdates) return;
        try {
            var release = await ReleaseUpdates.Check();
            var version = typeof(Program).Assembly.GetName().Version ?? new Version(2,0);
            if (release is not null && ReleaseUpdates.IsNewer(release.Tag, version))
                _main.ShowBalloonTip(4500, "Доступно обновление ChargeTray",
                    release.Tag + " — откройте Центр устройств → Настройки для скачивания.", ToolTipIcon.Info);
        } catch (Exception) { /* Offline mode: no unnecessary notification */ }
    }

    private void RenderTray() {
        bool light = TrayArt.IsLightTaskbar();
        int size = TrayArt.IconSize();
        _strip.UpdateStrip(_positionMode == "bottom", light, _data);
        _island.UpdateIsland(_positionMode == "top", _data);
        bool trayOnly = _positionMode == "off";
        _extra.Enabled = trayOnly;
        var measured = _data.Where(d => d.HasCurrentReading).ToArray();
        var lowest = measured.Where(d => d.Percent.HasValue).MinBy(d => d.Percent);
        bool separate = trayOnly && _extra.Checked;
        var mainDevice = !trayOnly ? null : separate ? measured.FirstOrDefault()
            : lowest ?? measured.FirstOrDefault(d => d.Low) ?? measured.FirstOrDefault();
        string signature = Signature(mainDevice, light, size);
        if (_iconSignature != signature) {
            var icon = TrayArt.Make(Label(mainDevice), mainDevice?.Low ?? false, light, size, mainDevice?.Type ?? DeviceType.Other);
            _main.Icon = icon;
            _mainIcon?.Dispose();
            _mainIcon = icon;
            _iconSignature = signature;
        }
        _main.Text = Trim(_data.Length == 0 ? "ChargeTray · нет данных об устройствах" :
            separate && mainDevice is not null ? mainDevice.Name + " · " + mainDevice.Reading :
            "ChargeTray\n" + string.Join("\n", _data.Select(d => d.Name + " · " + d.Reading)), 120);
        _main.Visible = true;
        var shown = separate ? measured.Skip(1).ToArray() : [];
        var ids = shown.Select(d => d.Key).ToHashSet();
        foreach (var key in _icons.Keys.Where(k => !ids.Contains(k)).ToArray()) {
            var old = _icons[key]; old.Tray.Dispose(); old.Icon.Dispose(); _icons.Remove(key);
        }
        foreach (var d in shown) {
            string s = Signature(d, light, size);
            if (!_icons.TryGetValue(d.Key, out var entry)) {
                var tray = new NotifyIcon();
                Hook(tray);
                var art = TrayArt.Make(Label(d), d.Low, light, size, d.Type);
                entry = (tray, art, s);
                tray.Icon = art;
                _icons.Add(d.Key, entry);
            } else if (entry.Signature != s) {
                var art = TrayArt.Make(Label(d), d.Low, light, size, d.Type);
                entry.Tray.Icon = art;
                entry.Icon.Dispose();
                _icons[d.Key] = entry = (entry.Tray, art, s);
            }
            entry.Tray.Text = Trim(d.Name + " · " + d.Reading, 120);
            entry.Tray.Visible = true;
        }
    }

    private static string? Label(Accessory? d) => d?.Percent is int p ? p.ToString() + "%" : d?.Low == true ? "!" : null;
    private static string Signature(Accessory? d, bool light, int size) => $"{d?.Type}:{Label(d)}:{d?.Low}:{light}:{size}";

    private static string Version => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    private void CopyDiagnostics() =>
        CopyText(DiagnosticReport.Summary(Version, Environment.OSVersion.VersionString, _updated, _data, _errors));

    private async Task CopyDeveloperDiagnostics() {
        try {
            _main.ShowBalloonTip(1500, "ChargeTray", "Собираем сведения HID без серийных номеров…", ToolTipIcon.Info);
            var items = await Task.Run(() => {
                var inputs = HidInput.Read();
                var ids = inputs.Select(x => x.Hardware).Where(x =>
                    x.Length == 9 && x[4] == ':').Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var paths = RazerReceiver.PresentInterfaces(p => ids.Any(id =>
                    p.Contains("vid_" + id[..4] + "&pid_" + id[5..], StringComparison.OrdinalIgnoreCase)));
                var caps = paths.Take(72).Select(path => {
                    using var handle = RazerReceiver.Open(path, 0);
                    return (Path: path, Caps: RazerReceiver.GetCaps(handle));
                }).ToArray();
                return DiagnosticReport.Developer(Version, Environment.OSVersion.VersionString,
                    _updated, _data, _hardware, _errors, inputs, caps);
            });
            if (!_exiting) CopyText(items);
        } catch (Exception e) {
            if (!_exiting) _main.ShowBalloonTip(4000, "ChargeTray",
                "Отчёт HID недоступен: " + e.GetType().Name, ToolTipIcon.Warning);
        }
    }

    private void CopyText(string value) {
        try {
            Clipboard.SetText(value);
            _main.ShowBalloonTip(2500, "ChargeTray", "Диагностика скопирована.", ToolTipIcon.Info);
        } catch (ExternalException) {
            _main.ShowBalloonTip(3000, "ChargeTray", "Буфер обмена занят. Попробуйте ещё раз.", ToolTipIcon.Warning);
        }
    }

    private void SetupThemeMenu() {
        _colors.DropDownItems.Add("Выбрать любой цвет…", null, (_, _) => {
            using var picker = new ColorDialog { Color = AccentTheme.Fixed, FullOpen = true, AnyColor = true };
            if (picker.ShowDialog() == DialogResult.OK) {
                AccentTheme.SetColor(picker.Color);
                RefreshTheme();
            }
        });
        var presets = new (string Label, Color Value)[] {
            ("Шампань", Color.FromArgb(226, 197, 160)),
            ("Лазурный", Color.FromArgb(70, 174, 245)),
            ("Фиолетовый", Color.FromArgb(185, 129, 245)),
            ("Розовый", Color.FromArgb(242, 130, 174)),
            ("Бирюзовый", Color.FromArgb(70, 219, 201)),
            ("Белый", Color.FromArgb(245, 245, 245))
        };
        foreach (var preset in presets) {
            var color = preset.Value;
            _colors.DropDownItems.Add(preset.Label, null, (_, _) => {
                AccentTheme.SetColor(color);
                RefreshTheme();
            });
        }
        _colors.DropDownItems.Add(new ToolStripSeparator());
        _colors.DropDownItems.Add(_rgbItem);
        _rgbItem.Click += (_, _) => {
            AccentTheme.SetRgb(!AccentTheme.RgbEnabled);
            RefreshTheme();
        };
        _colors.DropDownItems.Add("Сбросить оформление", null, (_, _) => {
            AccentTheme.Reset();
            RefreshTheme();
        });
        _colors.DropDownOpening += (_, _) => _rgbItem.Checked = AccentTheme.RgbEnabled;
    }

    private void RefreshTheme() {
        _themeTimer.Enabled = AccentTheme.RgbEnabled && !_prefs.Values.EconomyMode;
        _panel.Invalidate();
        _strip.Invalidate();
        _island.Invalidate();
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
    private static Accessory[] Samples() => [
        new("kb", "Logitech MX Keys Mini", DeviceType.Keyboard, "Bluetooth", 78, Model: "MX Keys Mini"),
        new("ms", "Logitech MX Master 3S", DeviceType.Mouse, "Bluetooth", 12, Model: "MX Master 3S"),
        new("hp", "Move WLH 500", DeviceType.Headphones, "Bluetooth", 60),
        new("ctrl", "Xbox Wireless Controller", DeviceType.Controller, "XInput", Level: Band.Medium)
    ];

    protected override void ExitThreadCore() {
        _exiting = true;
        Application.Idle -= FirstIdle;
        _poll.Dispose();
        _themeTimer.Dispose();
        _updateTimer.Dispose();
        _hub?.Dispose();
        _reader.Dispose();
        foreach (var i in _icons.Values) { i.Tray.Dispose(); i.Icon.Dispose(); }
        _main.Dispose(); _mainIcon?.Dispose();
        _island.Dispose(); _menu.Dispose(); _strip.Dispose(); _panel.Dispose();
        base.ExitThreadCore();
    }
}

static class TrayArt {
    [DllImport("user32.dll", ExactSpelling = true)] private static extern bool DestroyIcon(IntPtr handle);
    private static readonly string[] Digits = [
        "111101101101111", "010110010010111", "111001111100111", "111001111001111",
        "101101111001001", "111100111001111", "111100111101111", "111001001010010",
        "111101111101111", "111101111001111"
    ];
    internal static bool IsLightTaskbar() {
        try {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        } catch (Exception) { return false; }
    }

    internal static int IconSize() => Math.Clamp(SystemInformation.SmallIconSize.Width, 16, 48);

    internal static void Battery(Graphics g, RectangleF bounds, Color ink, bool active = true) {
        var state = g.Save();
        g.TranslateTransform(bounds.X, bounds.Y);
        g.ScaleTransform(bounds.Width / 16f, bounds.Height / 16f);
        using var line = new Pen(ink, 1.22f);
        using var body = Shapes.Round(new RectangleF(1.12f, 3.55f, 12.35f, 8.9f), 1.55f);
        g.DrawPath(line, body);
        using var fill = new SolidBrush(ink);
        using var terminal = Shapes.Round(new RectangleF(14.0f, 6.0f, 1.65f, 4.0f), .7f);
        g.FillPath(fill, terminal);
        if (active) {
            using var cell = Shapes.Round(new RectangleF(3.04f, 5.38f, 8.52f, 5.25f), .65f);
            g.FillPath(fill, cell);
        }
        g.Restore(state);
    }

    internal static Icon Make(string? label, bool low, bool light, int size, DeviceType type = DeviceType.Other) {
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        var ink = light ? Color.FromArgb(23, 23, 27) : Color.FromArgb(253, 253, 254);
        if (label is null && type == DeviceType.Other) Battery(g, new RectangleF(0, 0, size, size), ink);
        else if (label is null) Shapes.Glyph(g, type, new RectangleF(0, 0, size, size), ink, 2.2f);
        else {
            g.ScaleTransform(size / 48f, size / 48f);
            var tint = !low ? ink : light ? Color.FromArgb(165, 34, 43) : Color.FromArgb(255, 173, 173);
            using var brush = new SolidBrush(tint);
            g.SmoothingMode = SmoothingMode.None;
            int x = (16 - (label.Length * 4 - 1)) / 2;
            foreach (char c in label) {
                string pixels = c switch {
                    >= '0' and <= '9' => Digits[c - '0'], '%' => "101001010100101",
                    '!' => "010010010000010", _ => "000000000000000"
                };
                for (int i = 0; i < 15; i++)
                    if (pixels[i] == '1') g.FillRectangle(brush, (x + i % 3) * 3, (2 + (i / 3) * 2) * 3, 3, 5);
                x += 4;
            }
        }
        var handle = bmp.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }
}
