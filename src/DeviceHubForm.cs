using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ChargeTray;

// Native UI: no WebView and no background browser. Fluent-styled navigation and cards.
internal sealed class DeviceHubForm : Form {
    private readonly SettingsStore _settings;
    private readonly BatteryHistory _history;
    private readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = FluentStyle.Background };
    private readonly Panel _sidebar = new() { Dock = DockStyle.Left, Width = 192, BackColor = FluentStyle.Background };
    private readonly FluentHeader _header = new();
    private readonly Dictionary<string, Panel> _pages = new();
    private readonly Dictionary<string, FluentButton> _nav = new();
    private readonly FlowLayoutPanel _deviceList = NewFlow();
    private readonly FlowLayoutPanel _historyList = NewFlow();
    private readonly FlowLayoutPanel _styleList = NewFlow();
    private readonly FlowLayoutPanel _settingsList = NewFlow();
    private readonly FlowLayoutPanel _diagnosticsList = NewFlow();
    private readonly FlowLayoutPanel _labList = NewFlow();
    private readonly List<FluentColorChip> _colorChips = [];
    private readonly List<ProtocolSnapshot> _labSnapshots = [];
    private string[] _hardwareInfo = [];
    private TextBox? _labOutput;
    private readonly Label _title = UiElements.Label("Устройства", 23, true);
    private readonly Label _subtitle = UiElements.Label("Мониторинг подключённой периферии", 10, muted: true);
    private readonly Label _deviceStatus = UiElements.Label("", 9, muted: true);
    private readonly System.Windows.Forms.Timer _resizeDelay = new() { Interval = 180 };
    private Accessory[] _devices = [];
    private DateTime? _updated;
    private string _selected = "devices";
    private bool _refreshingLayout;
    private readonly Dictionary<string, FluentToggle> _toggles = new();

    internal event Action? RefreshRequested;
    internal event Action? PreferencesChanged;
    internal event Action<int>? MonitorChanged;
    internal event Action? CopySummaryRequested;
    internal event Action? CopyDeveloperRequested;
    internal event Action? CloseForUpdateRequested;

    internal DeviceHubForm(SettingsStore settings, BatteryHistory history) {
        _settings = settings;
        _history = history;
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "ChargeTray 2.1 — Device Hub";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 565);
        Size = new Size(1040, 725);
        BackColor = FluentStyle.Background;
        ForeColor = FluentStyle.Text;
        Font = FluentStyle.Font();
        DoubleBuffered = true;
        Controls.Add(_content);
        Controls.Add(_sidebar);
        Controls.Add(_header);

        _title.SetBounds(24, 9, 420, 34);
        _subtitle.SetBounds(26, 47, 490, 20);
        _header.Height = 85;
        _header.Controls.Add(_title);
        _header.Controls.Add(_subtitle);
        var refresh = new FluentButton("Обновить") { Symbol = "↻", Width = 142, Height = 38 };
        refresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        refresh.Location = new Point(Width - 190, 23);
        refresh.Click += (_, _) => RefreshRequested?.Invoke();
        _header.Controls.Add(refresh);
        _header.Resize += (_, _) => refresh.Left = _header.ClientSize.Width - refresh.Width - 26;

        var brand = UiElements.Label("CHARGE", 17, true);
        brand.SetBounds(19, 19, 152, 32);
        _sidebar.Controls.Add(brand);
        var brandSub = UiElements.Label("TRAY 2.1", 9, muted: true);
        brandSub.SetBounds(21, 50, 145, 23);
        _sidebar.Controls.Add(brandSub);
        AddNavigation("devices", "Устройства", "▣", 112);
        AddNavigation("history", "История", "◷", 161);
        AddNavigation("appearance", "Оформление", "◈", 210);
        AddNavigation("settings", "Настройки", "⚙", 259);
        AddNavigation("diagnostics", "Диагностика", "≡", 308);
        AddNavigation("lab", "Protocol Lab", "⚗", 357);
        var sidebarFooter = UiElements.Label("Локально · без аккаунта", 8, muted: true);
        sidebarFooter.Dock = DockStyle.Bottom;
        sidebarFooter.Height = 43;
        sidebarFooter.Padding = new Padding(19, 7, 0, 0);
        _sidebar.Controls.Add(sidebarFooter);

        // Give the status strip and the scrolling cards distinct layout rows.
        // A Top-docked label layered over a Fill-docked FlowLayoutPanel clipped
        // the first card on high-DPI Windows 11.
        var devicePage = Page("devices", new Panel { Dock = DockStyle.Fill });
        devicePage.Controls.Clear();
        var deviceLayout = new TableLayoutPanel {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            Margin = Padding.Empty, Padding = Padding.Empty,
            BackColor = FluentStyle.Background
        };
        deviceLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        deviceLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        deviceLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _deviceStatus.Dock = DockStyle.Fill;
        _deviceStatus.Height = 42;
        _deviceStatus.Padding = new Padding(20, 11, 0, 0);
        _deviceList.Dock = DockStyle.Fill;
        deviceLayout.Controls.Add(_deviceStatus, 0, 0);
        deviceLayout.Controls.Add(_deviceList, 0, 1);
        devicePage.Controls.Add(deviceLayout);
        Page("history", _historyList);
        Page("appearance", _styleList);
        Page("settings", _settingsList);
        Page("diagnostics", _diagnosticsList);
        Page("lab", _labList);
        BuildAppearance();
        BuildSettings();
        BuildDiagnostics();
        BuildProtocolLab();
        SelectPage("devices");

        _resizeDelay.Tick += (_, _) => { _resizeDelay.Stop(); SizeCards(); };
        Resize += (_, _) => {
            _resizeDelay.Stop();
            _resizeDelay.Start();
            if (Width < 850) _sidebar.Width = 160;
            else _sidebar.Width = 192;
        };
        Shown += (_, _) => RefreshLists();
    }

    private void AddNavigation(string id, string title, string icon, int top) {
        var button = new FluentButton(title) { Symbol = icon, Size = new Size(165, 42),
            Location = new Point(13, top), Anchor = AnchorStyles.Top | AnchorStyles.Left };
        button.Click += (_, _) => SelectPage(id);
        _sidebar.Controls.Add(button);
        _nav[id] = button;
        _sidebar.Resize += (_, _) => button.Width = _sidebar.Width - 27;
    }
    private Panel Page(string key, Control child) {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = FluentStyle.Background, Visible = false };
        page.Controls.Add(child);
        _content.Controls.Add(page);
        _pages[key] = page;
        return page;
    }
    private void SelectPage(string key) {
        _selected = key;
        foreach (var (id, page) in _pages) page.Visible = id == key;
        _pages[key].BringToFront();
        foreach (var (id, button) in _nav) {
            button.Selected = id == key;
            button.Invalidate();
        }
        _title.Text = key switch {
            "devices" => "Мои устройства", "history" => "История батареи",
            "appearance" => "Оформление", "settings" => "Настройки",
            "diagnostics" => "Диагностика", _ => "Protocol Lab"
        };
        _subtitle.Text = key switch {
            "devices" => "Все подключённые устройства и их заряд",
            "history" => "Локальные измерения за последние 30 дней",
            "appearance" => "Windows 11 Fluent · сохранили прежнюю RGB-подсветку",
            "settings" => "Уведомления, монитор, обновления",
            "diagnostics" => "Отчёты устройств и поддержка новых моделей",
            _ => "Сравнение состояний без отправки неизвестных HID-команд"
        };
        if (key is "devices" or "history") RefreshLists();
        SizeCards();
    }
    private static FlowLayoutPanel NewFlow() {
        var panel = new FlowLayoutPanel {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, BackColor = FluentStyle.Background,
            Padding = new Padding(18, 13, 19, 15)
        };
        return panel;
    }

    internal void ShowHub() {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
        RefreshLists();
    }
    internal void UpdateDevices(Accessory[] devices, DateTime? updated, string[]? hardware = null) {
        if (hardware is not null) _hardwareInfo = hardware;
        _devices = devices;
        _updated = updated;
        if (Visible && !IsDisposed && _selected is "devices" or "history") RefreshLists();
    }
    internal void RefreshLists() {
        if (IsDisposed || _refreshingLayout) return;
        _refreshingLayout = true;
        try {
            _deviceStatus.Text = $"{_devices.Length} устройств  ·  Проверено: {_updated?.ToString("HH:mm:ss") ?? "—"}  ·  Данные хранятся локально";
            if (_selected == "devices") PopulateDevices();
            if (_selected == "history") PopulateHistory();
        } finally { _refreshingLayout = false; }
    }
    private void PopulateDevices() {
        ReplaceCards(_deviceList, _devices.Select(DeviceCard));
        if (_devices.Length == 0) AddEmpty(_deviceList, "Устройства не обнаружены. Проверьте Bluetooth и USB-ресивер.");
    }
    private void PopulateHistory() {
        ReplaceCards(_historyList, _devices.Select(HistoryCard));
        if (_devices.Length == 0) AddEmpty(_historyList, "История появится после подключения устройств.");
    }
    private static void AddEmpty(FlowLayoutPanel flow, string message) {
        var label = UiElements.Label(message, 11, muted: true);
        label.Size = new Size(590, 64);
        flow.Controls.Add(label);
    }
    private void ReplaceCards(FlowLayoutPanel flow, IEnumerable<Control> cards) {
        flow.SuspendLayout();
        try {
            var old = flow.Controls.Cast<Control>().ToArray();
            flow.Controls.Clear();
            foreach (var card in cards) flow.Controls.Add(card);
            foreach (var control in old) control.Dispose();
            SizeCards(flow);
        } finally { flow.ResumeLayout(); }
    }
    private static FluentCard NewCard(int height) => new() { Size = new Size(640, height) };
    private static Label Label(string text, int x, int y, int w, int h, float size = 10,
        bool bold = false, bool muted = false) {
        var label = UiElements.Label(text, size, bold, muted);
        label.SetBounds(x, y, w, h);
        return label;
    }
    private FluentCard DeviceCard(Accessory d) {
        var card = NewCard(218);
        card.Controls.Add(new DeviceGlyphIcon(d.Type) { Left = 19, Top = 16, Width = 38, Height = 38 });
        var name = Label(d.Name, 73, 16, 410, 31, 13, true);
        card.Controls.Add(name);
        var connection = Label(d.Type + "  ·  " + d.Via + (d.UsbConnected ? "  ·  USB подключён" : ""), 74, 46, 510, 22, 9, muted: true);
        card.Controls.Add(connection);
        if (d.Charging || d.Key == "aula:f108-pro") {
            var status = Label(d.Charging ? "⚡ Заряжается (подтверждено устройством)" :
                (d.Detail ?? "Статус зарядки неизвестен"), 74, 68, 510, 20, 9, muted: true);
            status.ForeColor = d.Charging ? Color.FromArgb(115, 230, 166) :
                d.UsbConnected ? AccentTheme.Accent :
                d.Detail?.StartsWith("⚡?") == true ? AccentTheme.Accent : FluentStyle.Muted;
            card.Controls.Add(status);
            card.Resize += (_, _) => status.Width = Math.Max(180, card.ClientSize.Width - 95);
        }
        var percent = Label(d.Reading, 505, 17, 115, 33, 18, true);
        percent.TextAlign = ContentAlignment.TopRight;
        card.Controls.Add(percent);
        var bar = new BatteryProgress { Percent = d.IsStale ? null : d.Percent, Low = d.Low,
            Bounds = new Rectangle(22, 102, 590, 8) };
        card.Controls.Add(bar);
        var pref = _settings.Device(d.Key);
        card.Controls.Add(Label("Название", 22, 131, 72, 24, 9, muted: true));
        var rename = new TextBox { Left = 98, Top = 126, Width = 220, Height = 27,
            Text = pref.Name, PlaceholderText = d.Name, BackColor = FluentStyle.SurfaceHover,
            ForeColor = FluentStyle.Text, BorderStyle = BorderStyle.FixedSingle };
        rename.Leave += (_, _) => {
            if (pref.Name == rename.Text.Trim()) return;
            pref.Name = rename.Text.Trim();
            _settings.Save();
            PreferencesChanged?.Invoke();
            name.Text = string.IsNullOrWhiteSpace(pref.Name) ? d.Name : pref.Name;
        };
        card.Controls.Add(rename);
        var pin = new FluentToggle("Закрепить", pref.Pinned) { Left = 22, Top = 160, Width = 155, Height = 36 };
        pin.ValueChanged += value => {
            pref.Pinned = value; _settings.Save(); PreferencesChanged?.Invoke();
            if (!IsDisposed) BeginInvoke((Action)RefreshLists);
        };
        card.Controls.Add(pin);
        card.Controls.Add(Label("Порог", 211, 167, 55, 22, 9, muted: true));
        var threshold = new NumericUpDown { Left = 271, Top = 163, Width = 72, Height = 27,
            Minimum = 1, Maximum = 99, Value = Math.Clamp(pref.AlertThreshold, 1, 99),
            BackColor = FluentStyle.SurfaceHover, ForeColor = FluentStyle.Text };
        threshold.ValueChanged += (_, _) => {
            pref.AlertThreshold = (int)threshold.Value; _settings.Save(); PreferencesChanged?.Invoke();
        };
        card.Controls.Add(threshold);
        card.Resize += (_, _) => {
            percent.Left = card.ClientSize.Width - 150;
            bar.Width = card.ClientSize.Width - 46;
            name.Width = Math.Max(160, card.ClientSize.Width - 260);
            connection.Width = Math.Max(180, card.ClientSize.Width - 95);
            threshold.Left = 271;
        };
        return card;
    }
    private FluentCard HistoryCard(Accessory d) {
        var card = NewCard(173);
        card.Controls.Add(Label(d.Name, 21, 16, 510, 29, 13, true));
        card.Controls.Add(Label($"{d.Reading}   ·   {_history.Forecast(d.Key)}", 22, 47, 580, 29, 10, muted: true));
        var graph = new BatteryChart(_history.Samples(d.Key, 30)) { Left = 21, Top = 91,
            Size = new Size(580, 57) };
        card.Controls.Add(graph);
        card.Resize += (_, _) => graph.Width = card.ClientSize.Width - 45;
        return card;
    }
    private void SizeCards() {
        if (_selected == "devices") SizeCards(_deviceList);
        if (_selected == "history") SizeCards(_historyList);
        SizeCards(_styleList);
        SizeCards(_settingsList);
        SizeCards(_diagnosticsList);
        SizeCards(_labList);
    }
    private static void SizeCards(FlowLayoutPanel flow) {
        int width = Math.Max(450, flow.ClientSize.Width - 48);
        foreach (Control card in flow.Controls)
            if (card is FluentCard) card.Width = width;
    }

    private static FluentCard Group(string heading, string description, int height) {
        var card = NewCard(height);
        card.Controls.Add(Label(heading, 21, 13, 590, 32, 14, true));
        card.Controls.Add(Label(description, 22, 45, 590, 29, 9, muted: true));
        return card;
    }
    private FluentToggle Toggle(FluentCard card, string text, int top,
        bool initial, Action<bool> onChange) {
        var toggle = new FluentToggle(text, initial) { Left = 18, Top = top, Width = 600, Height = 43 };
        toggle.ValueChanged += value => {
            onChange(value);
            _settings.Save();
            PreferencesChanged?.Invoke();
        };
        card.Controls.Add(toggle);
        _toggles[text] = toggle;
        card.Resize += (_, _) => toggle.Width = card.ClientSize.Width - 38;
        return toggle;
    }
    private void BuildAppearance() {
        _styleList.Controls.Clear();
        var accent = Group("RGB и цветовой акцент", "Прежняя подсветка ChargeTray полностью сохранена.", 255);
        var picker = new FluentButton("Выбрать любой цвет…") { Symbol = "◈", Left = 20, Top = 88,
            Width = 254, Height = 40 };
        picker.Click += (_, _) => {
            using var dialog = new ColorDialog { Color = AccentTheme.Fixed, AnyColor = true, FullOpen = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            AccentTheme.SetColor(dialog.Color);
            UpdateThemeImmediately();
        };
        accent.Controls.Add(picker);
        var systemAccent = new FluentButton("Акцент Windows") {
            Left = 295, Top = 88, Width = 200, Height = 40
        };
        systemAccent.Click += (_, _) => {
            AccentTheme.SetSystemAccent(true);
            UpdateThemeImmediately();
        };
        accent.Controls.Add(systemAccent);
        var presets = new (string Name, Color Value)[] {
            ("Шампань", Color.FromArgb(226,197,160)), ("Лазурный", Color.FromArgb(70,174,245)),
            ("Фиолетовый", Color.FromArgb(185,129,245)), ("Розовый", Color.FromArgb(242,130,174)),
            ("Бирюзовый", Color.FromArgb(70,219,201)), ("Белый", Color.FromArgb(245,245,245))
        };
        var palette = new FlowLayoutPanel { Top = 137, Left = 20, Width = 610, Height = 90,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = true,
            BackColor = FluentStyle.Surface };
        foreach (var swatch in presets) {
            var b = new FluentColorChip(swatch.Name, swatch.Value) {
                Selected = !AccentTheme.RgbEnabled && !AccentTheme.SystemAccentEnabled &&
                    AccentTheme.Fixed.ToArgb() == swatch.Value.ToArgb()
            };
            var color = swatch.Value;
            b.Click += (_, _) => {
                AccentTheme.SetColor(color);
                foreach (var chip in _colorChips) {
                    chip.Selected = chip.BaseColor.ToArgb() == color.ToArgb();
                    chip.Invalidate();
                }
                UpdateThemeImmediately();
            };
            _colorChips.Add(b);
            palette.Controls.Add(b);
        }
        accent.Controls.Add(palette);
        accent.Resize += (_, _) => palette.Width = Math.Max(360, accent.Width - 41);
        _styleList.Controls.Add(accent);

        var animation = Group("Плавная подсветка", "Живая анимация оттенков без обновления батареи.", 227);
        var rgb = new FluentToggle("Плавная RGB-анимация", AccentTheme.RgbEnabled) {
            Left = 18, Top = 82, Width = 560 };
        rgb.ValueChanged += active => {
            AccentTheme.SetRgb(active); UpdateThemeImmediately();
        };
        animation.Controls.Add(rgb);
        animation.Resize += (_, _) => rgb.Width = animation.Width - 38;
        animation.Controls.Add(Label("Скорость смены оттенков", 23, 139, 280, 24, 10));
        var speed = new FluentButton(AccentTheme.CycleSeconds + " сек / цикл") {
            Left = 319, Top = 129, Width = 170 };
        speed.Click += (_, _) => {
            var menu = new ContextMenuStrip();
            menu.Closed += (_, _) => {
                // WinForms continues processing OnItemClicked after Closed.
                // Dispose on the next UI message, never inside the close event.
                if (IsHandleCreated && !IsDisposed && !Disposing) {
                    try {
                        BeginInvoke((Action)(() => menu.Dispose()));
                    } catch (InvalidOperationException) { menu.Dispose(); }
                } else {
                    menu.Dispose();
                }
            };
            foreach (int duration in new[] { 18, 9, 5 }) {
                int selected = duration;
                menu.Items.Add(duration + " секунд", null, (_, _) => {
                    AccentTheme.SetCycleSeconds(selected);
                    speed.Text = selected + " сек / цикл";
                    UpdateThemeImmediately();
                });
            }
            menu.Show(speed, new Point(0, speed.Height));
        };
        animation.Controls.Add(speed);
        var preview = new BatteryProgress { Percent = 75, Low = false,
            Bounds = new Rectangle(23, 188, 560, 10) };
        animation.Controls.Add(preview);
        animation.Resize += (_, _) => preview.Width = animation.Width - 48;
        _styleList.Controls.Add(animation);

        var other = Group("Режим оформления", "Оформление сохраняется при следующем запуске.", 160);
        var themeText = _settings.Values.ThemeMode switch {
            "light" => "Светлая — Windows 11",
            "system" => "Автоматически по Windows",
            _ => "Тёмная тема Windows 11"
        };
        var theme = new FluentButton(themeText) { Left = 20, Top = 86, Width = 276 };
        theme.Click += (_, _) => {
            var menu = new ContextMenuStrip();
            menu.Closed += (_, _) => {
                // WinForms continues processing OnItemClicked after Closed.
                // Dispose on the next UI message, never inside the close event.
                if (IsHandleCreated && !IsDisposed && !Disposing) {
                    try {
                        BeginInvoke((Action)(() => menu.Dispose()));
                    } catch (InvalidOperationException) { menu.Dispose(); }
                } else {
                    menu.Dispose();
                }
            };
            menu.Items.Add("Тёмная — системный графит", null, (_, _) => ThemeChoice("dark", theme));
            menu.Items.Add("Светлая — Windows 11", null, (_, _) => ThemeChoice("light", theme));
            menu.Items.Add("Автоматически по Windows", null, (_, _) => ThemeChoice("system", theme));
            menu.Show(theme, new Point(0, theme.Height));
        };
        other.Controls.Add(theme);
        _styleList.Controls.Add(other);
    }
    private void ThemeChoice(string choice, FluentButton label) {
        _settings.Values.ThemeMode = choice;
        label.Text = choice switch { "light" => "Светлая — Windows 11",
            "system" => "Автоматически по Windows", _ => "Тёмная тема Windows 11" };
        _settings.Save();
        FluentStyle.SetTheme(choice);
        ApplyThemeToWindow();
        PreferencesChanged?.Invoke();
    }
    private void UpdateThemeImmediately() {
        ThemeFrame();
        PreferencesChanged?.Invoke();
    }

    private void BuildSettings() {
        _settingsList.Controls.Clear();
        var notify = Group("Уведомления", "Сообщения о важных событиях и уровне заряда.", 191);
        Toggle(notify, "Подключение и отключение устройств", 81, _settings.Values.ConnectionNotifications,
            v => _settings.Values.ConnectionNotifications = v);
        Toggle(notify, "Низкий заряд по индивидуальным порогам", 129, _settings.Values.BatteryNotifications,
            v => _settings.Values.BatteryNotifications = v);
        _settingsList.Controls.Add(notify);

        var vendor = Group("Vendor Bridge · Device Intelligence",
            "Опциональный локальный JSON от проверенной утилиты производителя.", 195);
        Toggle(vendor, "Разрешить локальный Vendor Bridge", 82, _settings.Values.EnableVendorBridge,
            v => _settings.Values.EnableVendorBridge = v);
        var bridgePath = Label(@"%LOCALAPPDATA%\ChargeTray\vendor-bridge.json", 23, 135, 585, 25, 9, muted: true);
        vendor.Controls.Add(bridgePath);
        _settingsList.Controls.Add(vendor);

        var battery = Group("История и производительность", "Сохраняем статистику только на вашем компьютере.", 192);
        Toggle(battery, "Локальная история до 30 дней", 83, _settings.Values.BatteryHistory,
            v => _settings.Values.BatteryHistory = v);
        Toggle(battery, "Экономичный режим анимации", 131, _settings.Values.EconomyMode,
            v => _settings.Values.EconomyMode = v);
        _settingsList.Controls.Add(battery);

        var monitor = Group("Dynamic Island 2.1", "Монитор и отступ островка от верхнего края.", 215);
        var selected = Math.Clamp(_settings.Values.IslandMonitor, 0, Math.Max(0, Screen.AllScreens.Length - 1));
        var chooser = new FluentButton(ScreenLabel(selected)) { Left = 22, Top = 92,
            Width = 338, Height = 40 };
        chooser.Click += (_, _) => {
            var menu = new ContextMenuStrip();
            menu.Closed += (_, _) => {
                // WinForms continues processing OnItemClicked after Closed.
                // Dispose on the next UI message, never inside the close event.
                if (IsHandleCreated && !IsDisposed && !Disposing) {
                    try {
                        BeginInvoke((Action)(() => menu.Dispose()));
                    } catch (InvalidOperationException) { menu.Dispose(); }
                } else {
                    menu.Dispose();
                }
            };
            for (int i = 0; i < Screen.AllScreens.Length; i++) {
                int index = i;
                menu.Items.Add(ScreenLabel(index), null, (_, _) => {
                    chooser.Text = ScreenLabel(index);
                    _settings.Values.IslandMonitor = index;
                    _settings.Save();
                    MonitorChanged?.Invoke(index);
                });
            }
            menu.Show(chooser, new Point(0, chooser.Height));
        };
        monitor.Controls.Add(chooser);
        monitor.Controls.Add(Label("Отступ сверху (px)", 25, 149, 184, 24, 10));
        var offset = new NumericUpDown {
            Left = 212, Top = 143, Width = 86, Minimum = 0, Maximum = 120,
            Value = Math.Clamp(_settings.Values.IslandTopOffset, 0, 120),
            BackColor = FluentStyle.SurfaceHover, ForeColor = FluentStyle.Text
        };
        offset.ValueChanged += (_, _) => {
            _settings.Values.IslandTopOffset = (int)offset.Value;
            _settings.Save();
            PreferencesChanged?.Invoke();
        };
        monitor.Controls.Add(offset);
        _settingsList.Controls.Add(monitor);

        var updates = Group("Обновления", "Скачивание только из официального GitHub-репозитория.", 196);
        Toggle(updates, "Проверять наличие новых версий", 77, _settings.Values.CheckUpdates,
            v => _settings.Values.CheckUpdates = v);
        var download = new FluentButton("Проверить обновления") { Symbol = "↓", Left = 20,
            Top = 140, Width = 260, Height = 39 };
        download.Click += async (_, _) => await DownloadRelease(download);
        updates.Controls.Add(download);
        _settingsList.Controls.Add(updates);
    }
    private static string ScreenLabel(int index) {
        var screens = Screen.AllScreens;
        if (screens.Length == 0) return "Монитор недоступен";
        var screen = screens[Math.Clamp(index, 0, screens.Length - 1)];
        return $"Монитор {index + 1} — {screen.Bounds.Width} × {screen.Bounds.Height}" +
            (screen.Primary ? " · основной" : "");
    }
    private async Task DownloadRelease(FluentButton button) {
        button.Enabled = false;
        button.Text = "Проверка GitHub…";
        try {
            var release = await ReleaseUpdates.Check();
            if (release is null) {
                MessageBox.Show(this, "GitHub Releases недоступен. Проверьте соединение.", "ChargeTray");
                return;
            }
            var local = typeof(Program).Assembly.GetName().Version ?? new Version(2, 1);
            if (!ReleaseUpdates.IsNewer(release.Tag, local)) {
                MessageBox.Show(this, $"Установлено {local.ToString(3)}, последний релиз {release.Tag}.",
                    "ChargeTray — обновления");
                return;
            }
            if (MessageBox.Show(this, $"Доступна версия {release.Tag}. Скачать ZIP в папку «Загрузки»?\n\n" +
                "Новая версия будет установлена вручную после закрытия ChargeTray.",
                "ChargeTray — обновление", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            button.Text = "Загрузка ZIP…";
            var path = await ReleaseUpdates.Download(release);
            if (path is null) Process.Start(new ProcessStartInfo(release.Url) { UseShellExecute = true });
            else {
                if (release.Digest is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) &&
                    MessageBox.Show(this, "ZIP загружен и проверен по SHA256 GitHub.\n\n" +
                        "Установить обновление автоматически с резервной копией предыдущей версии? " +
                        "ChargeTray закроется и перезапустится. Если установлен в защищённую папку, " +
                        "может понадобиться ручная установка.",
                        "ChargeTray — установка", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) {
                    SafeUpdateInstaller.PrepareAndLaunch(path, Application.ExecutablePath);
                    CloseForUpdateRequested?.Invoke();
                } else {
                    ReleaseUpdates.OpenFolder(path);
                    MessageBox.Show(this, "Архив сохранён. Для установки закройте ChargeTray, " +
                        "распакуйте ZIP и запустите EXE.", "ChargeTray");
                }
            }
        } catch (Exception e) {
            MessageBox.Show(this, "Обновление не удалось: " + e.GetType().Name, "ChargeTray");
        } finally {
            if (!button.IsDisposed) { button.Enabled = true; button.Text = "Проверить обновления"; }
        }
    }
    private void BuildDiagnostics() {
        _diagnosticsList.Controls.Clear();
        var report = Group("Диагностика устройств", "Компактный и технический отчёты для совместимости HID.", 205);
        var summary = new FluentButton("Скопировать краткий отчёт") {
            Left = 20, Top = 89, Width = 290, Height = 43 };
        summary.Click += (_, _) => CopySummaryRequested?.Invoke();
        report.Controls.Add(summary);
        var detailed = new FluentButton("Скопировать HID-отчёт") {
            Left = 20, Top = 145, Width = 290, Height = 43 };
        detailed.Click += (_, _) => CopyDeveloperRequested?.Invoke();
        report.Controls.Add(detailed);
        _diagnosticsList.Controls.Add(report);

        var support = Group("Поддержка нового устройства",
            "Обнаружение VID:PID не гарантирует наличие данных о батарее.", 164);
        var issue = new FluentButton("Сообщить об устройстве на GitHub") {
            Left = 20, Top = 91, Width = 332, Height = 41 };
        issue.Click += (_, _) => {
            try {
                Process.Start(new ProcessStartInfo("https://github.com/DrowzyX/ChargeTray/issues/new") {
                    UseShellExecute = true
                });
            } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) {
                MessageBox.Show(this, "Не удалось открыть GitHub.", "ChargeTray");
            }
        };
        support.Controls.Add(issue);
        _diagnosticsList.Controls.Add(support);
    }

    private void BuildProtocolLab() {
        _labList.Controls.Clear();
        var intro = Group("Protocol Lab · безопасный режим",
            "Снимки состояния до и после подключения USB или запуска фирменного ПО.", 200);
        var baseline = new FluentButton("1 · Снять состояние ДО") {
            Left = 18, Top = 86, Width = 268, Height = 42
        };
        baseline.Click += (_, _) => {
            _labSnapshots.Clear();
            _labSnapshots.Add(ProtocolLab.Capture(_devices, _hardwareInfo, DateTime.UtcNow));
            if (_labOutput is not null) _labOutput.Text = "Снимок ДО сохранён. Измените состояние устройства, нажмите «Обновить», затем сделайте снимок ПОСЛЕ.";
        };
        intro.Controls.Add(baseline);
        var after = new FluentButton("2 · Снять состояние ПОСЛЕ") {
            Left = 301, Top = 86, Width = 280, Height = 42
        };
        after.Click += (_, _) => {
            if (_labSnapshots.Count == 0) {
                MessageBox.Show(this, "Сначала снимите состояние ДО.", "Protocol Lab"); return;
            }
            var sample = ProtocolLab.Capture(_devices, _hardwareInfo, DateTime.UtcNow);
            if (_labSnapshots.Count == 1) _labSnapshots.Add(sample);
            else _labSnapshots[1] = sample;
            if (_labOutput is not null) _labOutput.Text = string.Join(Environment.NewLine,
                ProtocolLab.Compare(_labSnapshots[0], _labSnapshots[1]).Changes);
        };
        intro.Controls.Add(after);
        var save = new FluentButton("Экспортировать сравнение JSON") {
            Left = 18, Top = 142, Width = 326, Height = 40
        };
        save.Click += (_, _) => {
            if (_labSnapshots.Count < 2) {
                MessageBox.Show(this, "Для экспорта нужны два снимка.", "Protocol Lab"); return;
            }
            using var dialog = new SaveFileDialog {
                Filter = "JSON (*.json)|*.json", FileName = "ChargeTray-ProtocolLab.json",
                Title = "Экспорт обезличенного сравнения"
            };
            if (dialog.ShowDialog(this) == DialogResult.OK) {
                try { File.WriteAllText(dialog.FileName,
                    ProtocolLab.ExportJson(ProtocolLab.Compare(_labSnapshots[0], _labSnapshots[1]))); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                    MessageBox.Show(this, "Не удалось записать файл: " + e.Message, "Protocol Lab");
                }
            }
        };
        intro.Controls.Add(save);
        _labList.Controls.Add(intro);

        var output = Group("Изменения между снимками",
            "Показываются только уже доступные Windows/HID сведения; неизвестные команды не отправляются.", 355);
        _labOutput = new TextBox {
            Left = 20, Top = 88, Width = 590, Height = 234,
            ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None, Font = new Font("Consolas", 10),
            BackColor = FluentStyle.SurfaceHover, ForeColor = FluentStyle.Text,
            Text = "1. Сделайте первый снимок.\r\n2. Подключите зарядку или запустите OEM-приложение.\r\n3. Нажмите «Обновить».\r\n4. Снимите состояние после изменения."
        };
        output.Controls.Add(_labOutput);
        output.Resize += (_, _) => _labOutput.Width = Math.Max(250, output.Width - 41);
        _labList.Controls.Add(output);
    }

    internal void ThemeFrame() {
        if (!Visible || IsDisposed || !AccentTheme.RgbEnabled || _settings.Values.EconomyMode) return;
        foreach (var button in _nav.Values) button.Invalidate();
        var flow = _selected switch {
            "devices" => _deviceList, "history" => _historyList,
            "appearance" => _styleList, "settings" => _settingsList,
            "diagnostics" => _diagnosticsList, _ => _labList
        };
        foreach (Control c in flow.Controls) {
            if (c is not FluentCard card || card.Bottom < 0 || card.Top > flow.ClientSize.Height) continue;
            card.Invalidate();
            foreach (Control sub in card.Controls) {
                if (sub is BatteryProgress or BatteryChart or FluentToggle or FluentButton) sub.Invalidate();
            }
        }
    }
    private void ApplyThemeToWindow() {
        BackColor = FluentStyle.Background;
        try {
            int dark = FluentStyle.IsDark ? 1 : 0;
            _ = DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
        _sidebar.BackColor = FluentStyle.Background;
        _title.ForeColor = FluentStyle.Text;
        _subtitle.ForeColor = FluentStyle.Muted;
        foreach (Control sidebarChild in _sidebar.Controls)
            if (sidebarChild is Label label) label.ForeColor = FluentStyle.Text;
        _header.BackColor = FluentStyle.Background;
        _content.BackColor = FluentStyle.Background;
        foreach (var page in _pages.Values) page.BackColor = FluentStyle.Background;
        foreach (var flow in new[] {_deviceList,_historyList,_styleList,_settingsList,_diagnosticsList,_labList}) {
            flow.BackColor = FluentStyle.Background;
            foreach (Control card in flow.Controls) {
                if (card is FluentCard) {
                    card.BackColor = FluentStyle.Background;
                    foreach (Control child in card.Controls) {
                        if (child is Label label) label.ForeColor = FluentStyle.Text;
                        if (child is TextBox box) { box.BackColor = FluentStyle.SurfaceHover; box.ForeColor = FluentStyle.Text; }
                        if (child is NumericUpDown number) { number.BackColor = FluentStyle.SurfaceHover; number.ForeColor = FluentStyle.Text; }
                    }
                }
                foreach (Control nested in card.Controls) {
                    if (nested is FlowLayoutPanel palettePanel) {
                        palettePanel.BackColor = FluentStyle.Surface;
                        foreach (Control chip in palettePanel.Controls) chip.Invalidate();
                    }
                }
                card.Invalidate(true);
            }
        }
        foreach (var chip in _colorChips) chip.Invalidate();
        Invalidate(true);
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        try {
            int dark = FluentStyle.IsDark ? 1 : 0;
            _ = DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
            int corners = 2;
            _ = DwmSetWindowAttribute(Handle, 33, ref corners, sizeof(int));
            int backdrop = 2; // DWMSBT_MAINWINDOW; supported Windows 11 builds only
            _ = DwmSetWindowAttribute(Handle, 38, ref backdrop, sizeof(int));
        } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
    }
    protected override void Dispose(bool disposing) {
        if (disposing) _resizeDelay.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class BatteryProgress : Control {
    internal int? Percent;
    internal bool Low;
    internal BatteryProgress() {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 8;
    }
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        FluentStyle.DrawRounded(e.Graphics, ClientRectangle, Color.FromArgb(70, 76, 90), 4, false);
        if (Percent is not int n || n <= 0) return;
        var bounds = new Rectangle(0, 0, Math.Max(5, (int)Math.Round(Width * n / 100d)), Height);
        FluentStyle.DrawRounded(e.Graphics, bounds,
            Low ? Color.FromArgb(244, 123, 139) : AccentTheme.Accent, 4, false);
    }
}
internal sealed class BatteryChart : Control {
    private readonly BatteryHistory.Sample[] _samples;
    internal BatteryChart(BatteryHistory.Sample[] samples) {
        _samples = samples;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        FluentStyle.DrawRounded(e.Graphics, ClientRectangle, Color.FromArgb(39, 44, 55), 8, false);
        if (_samples.Length < 2) return;
        double range = (_samples[^1].Utc - _samples[0].Utc).TotalSeconds;
        if (range <= 0) return;
        using var path = new GraphicsPath();
        PointF? previous = null;
        foreach (var sample in _samples) {
            var x = 7 + (float)((sample.Utc - _samples[0].Utc).TotalSeconds / range) * Math.Max(1, Width - 14);
            var y = 5 + (100 - sample.Percent) / 100f * Math.Max(1, Height - 10);
            var current = new PointF(x, y);
            if (previous is PointF start) path.AddLine(start, current);
            previous = current;
        }
        using var pen = new Pen(AccentTheme.Accent, 2);
        e.Graphics.DrawPath(pen, path);
    }
}
