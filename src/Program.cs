using System.Drawing.Drawing2D;
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
    private readonly Dictionary<string, (NotifyIcon Tray, Icon Icon, string Signature)> _icons = new();
    private readonly LowBatteryAlerts _alerts = new();
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
        _demo = demo;
        _panel = new BatteryPanel();
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
        _menu.Items.Add("Открыть панель", null, (_, _) => Open());
        _menu.Items.Add("Обновить", null, async (_, _) => await Refresh(true));
        _menu.Items.Add(_position);
        _menu.Items.Add(_extra);
        if (!demo) {
            _menu.Items.Add(_startupItem);
            _menu.Opening += (_, _) => SyncStartupMenu();
            _startupItem.Click += (_, _) => ToggleStartup();
            SyncStartupMenu();
        }
        if (demo) _menu.Items.Add("Проверить уведомление", null, (_, _) =>
            _main.ShowBalloonTip(5000, "Демо · пора зарядить мышь", "Logitech MX Master 3S — 12%", ToolTipIcon.Warning));
        _menu.Items.Add("Скопировать диагностику", null, (_, _) => CopyDiagnostics());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Выход", null, (_, _) => ExitThread());
        Hook(_main);
        RenderTray();
        _poll.Tick += async (_, _) => await Refresh(false);
        if (!demo) _poll.Start();
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
            _data = next;
            _updated = DateTime.Now;
            RenderTray();
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
        var low = _alerts.Check(_data);
        if (low.Length == 0) return;
        string lines = string.Join("\n", low.Take(3).Select(d => $"{Trim(d.Name, 36)} — {d.Reading}"));
        if (low.Length > 3) lines += $"\nЕщё устройств: {low.Length - 3}";
        _main.ShowBalloonTip(5000, "Пора зарядить устройство", lines, ToolTipIcon.Warning);
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

    private void CopyDiagnostics() {
        var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        var text = new StringBuilder($"ChargeTray {version}").AppendLine()
            .AppendLine(Environment.OSVersion.VersionString)
            .AppendLine($"Последний опрос: {_updated?.ToString("HH:mm:ss") ?? "—"}");
        foreach (var d in _data) text.AppendLine($"{d.Name} | {d.Model ?? "—"} | {d.Type} | {d.Via} | {d.Reading}");
        if (_hardware.Length > 0) {
            text.AppendLine().AppendLine("Windows: клавиатуры, мыши и геймпады (без серийных номеров)");
            foreach (var line in _hardware) text.AppendLine(line);
        }
        foreach (var e in _errors) text.AppendLine(e);
        try { Clipboard.SetText(text.ToString()); _main.ShowBalloonTip(3000, "ChargeTray", "Диагностика скопирована.", ToolTipIcon.Info); }
        catch (ExternalException) { _main.ShowBalloonTip(3000, "ChargeTray", "Буфер обмена занят. Попробуйте ещё раз.", ToolTipIcon.Info); }
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
