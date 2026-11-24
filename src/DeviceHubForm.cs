using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ChargeTray;

// Lightweight WinForms Device Hub. No WebView/browser process.
internal sealed class DeviceHubForm : Form {
    private static readonly Color BackgroundInk = Color.FromArgb(19, 22, 29);
    private static readonly Color CardInk = Color.FromArgb(31, 36, 46);
    private static readonly Color TextInk = Color.FromArgb(238, 242, 250);
    private static readonly Color HintInk = Color.FromArgb(159, 170, 188);
    private readonly SettingsStore _settings;
    private readonly BatteryHistory _history;
    private readonly FlowLayoutPanel _cards = new();
    private readonly Label _status = new();
    private readonly TabControl _tabs = new();
    private readonly FlowLayoutPanel _historyCards = new();
    private Accessory[] _devices = [];
    private DateTime? _updated;
    internal event Action? RefreshRequested;
    internal event Action? PreferencesChanged;
    internal event Action<int>? MonitorChanged;

    internal DeviceHubForm(SettingsStore settings, BatteryHistory history) {
        _settings = settings;
        _history = history;
        Text = "ChargeTray 2.0 — Device Hub";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(820, 655);
        MinimumSize = new Size(650, 510);
        BackColor = BackgroundInk;
        ForeColor = TextInk;
        Font = new Font("Segoe UI", 10);
        Icon = SystemIcons.Application;

        var top = new Panel { Dock = DockStyle.Top, Height = 83, BackColor = BackgroundInk };
        Controls.Add(top);
        var heading = TextLabel("ChargeTray", 24, true);
        heading.Location = new Point(25, 10);
        heading.Size = new Size(250, 36);
        top.Controls.Add(heading);
        var sub = TextLabel("DEVICE HUB  •  WINDOWS", 9);
        sub.Location = new Point(27, 51);
        sub.Size = new Size(340, 20);
        top.Controls.Add(sub);
        var refresh = ActionButton("↻  Обновить", 128);
        refresh.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        refresh.Location = new Point(Width - 171, 22);
        top.Resize += (_, _) => refresh.Left = top.ClientSize.Width - 153;
        refresh.Click += (_, _) => RefreshRequested?.Invoke();
        top.Controls.Add(refresh);
        _tabs.Dock = DockStyle.Fill;
        _tabs.Font = new Font("Segoe UI", 10, FontStyle.Regular);
        _tabs.Padding = new Point(17, 8);
        _tabs.Appearance = TabAppearance.Normal;
        _tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        _tabs.SizeMode = TabSizeMode.Fixed;
        _tabs.ItemSize = new Size(158, 33);
        _tabs.DrawItem += (_, e) => {
            bool selected = e.Index == _tabs.SelectedIndex;
            var r = _tabs.GetTabRect(e.Index);
            using var back = new SolidBrush(selected ? Color.FromArgb(47, 55, 72) : BackgroundInk);
            e.Graphics.FillRectangle(back, r);
            TextRenderer.DrawText(e.Graphics, _tabs.TabPages[e.Index].Text,
                _tabs.Font, r, selected ? TextInk : HintInk,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (selected) using (var marker = new SolidBrush(AccentTheme.Accent))
                e.Graphics.FillRectangle(marker, r.X + 24, r.Bottom - 3, r.Width - 48, 3);
        };
        Controls.Add(_tabs);
        _tabs.BringToFront();

        var devicePage = Page("Устройства");
        var historyPage = Page("История");
        var settingsPage = Page("Настройки");
        _tabs.TabPages.AddRange([devicePage, historyPage, settingsPage]);

        _status.Dock = DockStyle.Top;
        _status.Height = 46;
        _status.Padding = new Padding(17, 15, 0, 0);
        _status.ForeColor = HintInk;
        devicePage.Controls.Add(_status);
        SetupFlow(_cards);
        devicePage.Controls.Add(_cards);
        _cards.BringToFront();
        _cards.Resize += (_, _) => ResizeCards(_cards);

        SetupFlow(_historyCards);
        historyPage.Controls.Add(_historyCards);
        _historyCards.Resize += (_, _) => ResizeCards(_historyCards);
        BuildSettings(settingsPage);
        FormClosed += (_, _) => { /* Form can be reopened; parent owns disposal. */ };
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        try {
            int yes = 1;
            DwmSetWindowAttribute(Handle, 20, ref yes, sizeof(int)); // dark title bar
            int round = 2;
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)); // rounded corners on Windows 11
        } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
    }

    internal void ShowHub() {
        if (!Visible) Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    internal void UpdateDevices(Accessory[] devices, DateTime? updated) {
        _devices = devices;
        _updated = updated;
        if (IsDisposed) return;
        if (!Visible) return;
        RefreshLists();
    }

    internal void RefreshLists() {
        if (IsDisposed) return;
        _status.Text = $"{_devices.Length} устройств  •  Обновлено: {_updated?.ToString("HH:mm:ss") ?? "—"}  •  Данные остаются на вашем компьютере";
        FillCards(_cards, history: false);
        FillCards(_historyCards, history: true);
    }

    private static TabPage Page(string name) => new(name) {
        BackColor = BackgroundInk,
        ForeColor = TextInk,
        Padding = new Padding(12)
    };
    private static void SetupFlow(FlowLayoutPanel flow) {
        flow.Dock = DockStyle.Fill;
        flow.AutoScroll = true;
        flow.FlowDirection = FlowDirection.TopDown;
        flow.WrapContents = false;
        flow.BackColor = BackgroundInk;
        flow.Padding = new Padding(6, 9, 10, 15);
    }
    private static void ResizeCards(FlowLayoutPanel flow) {
        int width = Math.Max(450, flow.ClientSize.Width - 31);
        foreach (Control child in flow.Controls) child.Width = width;
    }

    private void FillCards(FlowLayoutPanel flow, bool history) {
        flow.SuspendLayout();
        try {
            var oldControls = flow.Controls.Cast<Control>().ToArray();
            flow.Controls.Clear();
            foreach (var old in oldControls) old.Dispose();
            foreach (var device in _devices) {
                var card = MakeCard(device, history);
                card.Width = Math.Max(440, flow.ClientSize.Width - 31);
                flow.Controls.Add(card);
            }
            if (_devices.Length == 0) {
                var empty = TextLabel("Пока нет подключённых устройств. Проверьте Bluetooth или USB-ресивер.", 12);
                empty.Height = 60; empty.Width = 620;
                flow.Controls.Add(empty);
            }
        } finally { flow.ResumeLayout(); }
    }

    private Panel MakeCard(Accessory d, bool history) {
        var card = new Panel {
            Width = 680, Height = history ? 162 : 120,
            BackColor = CardInk, Margin = new Padding(0, 0, 0, 13),
            Padding = new Padding(16)
        };
        card.Paint += (_, e) => {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Color.FromArgb(85, AccentTheme.Accent), 1);
            e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
        };
        var name = TextLabel(d.Name, 13, true);
        name.Location = new Point(17, 10); name.Size = new Size(410, 27);
        card.Controls.Add(name);
        var info = TextLabel($"{d.Type}  ·  {d.Via}" + (d.Charging ? "  ·  ⚡ Заряжается" : ""), 9);
        info.Location = new Point(18, 38); info.Size = new Size(450, 23);
        card.Controls.Add(info);
        var percent = TextLabel(d.Reading, 17, true);
        percent.Location = new Point(555, 9); percent.Size = new Size(103, 30);
        percent.TextAlign = ContentAlignment.TopRight;
        card.Controls.Add(percent);
        var bar = new Panel { Location = new Point(18, 71), Height = 7, Width = 635, BackColor = Color.FromArgb(68, 74, 87) };
        bar.Paint += (_, e) => {
            if (d.Percent is not int value || d.IsStale || value <= 0) return;
            using var brush = new SolidBrush(d.Low ? Color.FromArgb(250, 119, 140) : AccentTheme.Accent);
            e.Graphics.FillRectangle(brush, new RectangleF(0, 0, bar.Width * value / 100f, bar.Height));
        };
        card.Controls.Add(bar);
        card.Resize += (_, _) => {
            percent.Left = card.ClientSize.Width - 122;
            bar.Width = card.ClientSize.Width - 36;
            name.Width = Math.Max(220, card.ClientSize.Width - 160);
        };
        if (!history) {
            var deviceSettings = _settings.Device(d.Key);
            var alias = TextLabel("Имя:", 9);
            alias.Location = new Point(18, 91); alias.Size = new Size(42, 24);
            card.Controls.Add(alias);
            var rename = new TextBox { Location = new Point(57, 89), Width = 195,
                Text = deviceSettings.Name, PlaceholderText = d.Name, BackColor = Color.FromArgb(51, 58, 72), ForeColor = TextInk,
                BorderStyle = BorderStyle.FixedSingle };
            rename.Leave += (_, _) => {
                deviceSettings.Name = rename.Text.Trim();
                _settings.Save(); PreferencesChanged?.Invoke();
            };
            card.Controls.Add(rename);
            var pin = new CheckBox { Text = "Закрепить", AutoSize = true, Left = 270, Top = 91,
                ForeColor = TextInk, Checked = deviceSettings.Pinned };
            pin.CheckedChanged += (_, _) => {
                deviceSettings.Pinned = pin.Checked;
                _settings.Save(); PreferencesChanged?.Invoke();
            };
            card.Controls.Add(pin);
            var threshold = new NumericUpDown {
                Minimum = 1, Maximum = 99, Value = Math.Clamp(deviceSettings.AlertThreshold, 1, 99),
                Width = 53, Left = 499, Top = 89, BackColor = Color.FromArgb(51, 58, 72), ForeColor = TextInk
            };
            threshold.ValueChanged += (_, _) => {
                deviceSettings.AlertThreshold = (int)threshold.Value; _settings.Save(); PreferencesChanged?.Invoke();
            };
            var caption = TextLabel("Порог:", 9); caption.SetBounds(440, 92, 58, 22);
            card.Controls.Add(caption); card.Controls.Add(threshold);
        } else {
            var samples = _history.Samples(d.Key);
            var forecast = TextLabel($"История: {samples.Length} точек за 7 дней. {_history.Forecast(d.Key)}", 9);
            forecast.SetBounds(18, 89, 620, 26);
            card.Controls.Add(forecast);
            var chart = new Panel { BackColor = Color.FromArgb(35, 40, 52), Location = new Point(18, 122), Size = new Size(635, 30) };
            chart.Paint += (_, e) => DrawMiniChart(e.Graphics, chart.ClientRectangle, samples);
            card.Controls.Add(chart);
            card.Resize += (_, _) => chart.Width = card.ClientSize.Width - 36;
        }
        return card;
    }
    private static void DrawMiniChart(Graphics graphics, Rectangle area, BatteryHistory.Sample[] samples) {
        if (samples.Length < 2) return;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = new GraphicsPath();
        double range = (samples[^1].Utc - samples[0].Utc).TotalSeconds;
        if (range <= 0) return;
        for (int i = 0; i < samples.Length; i++) {
            float x = 2 + (float)((samples[i].Utc - samples[0].Utc).TotalSeconds / range) * (area.Width - 4);
            float y = 2 + (100 - samples[i].Percent) / 100f * (area.Height - 4);
            if (i == 0) path.StartFigure();
            else {
                var prev = samples[i - 1];
                float px = 2 + (float)((prev.Utc - samples[0].Utc).TotalSeconds / range) * (area.Width - 4);
                float py = 2 + (100 - prev.Percent) / 100f * (area.Height - 4);
                path.AddLine(px, py, x, y);
            }
        }
        using var pen = new Pen(AccentTheme.Accent, 2);
        graphics.DrawPath(pen, path);
    }
    private void BuildSettings(TabPage page) {
        var panel = new FlowLayoutPanel {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, Padding = new Padding(15), BackColor = BackgroundInk
        };
        page.Controls.Add(panel);
        void Toggle(string name, bool selected, Action<bool> changed) {
            var box = new CheckBox { Text = name, Checked = selected, AutoSize = true,
                ForeColor = TextInk, Font = new Font("Segoe UI", 11), Margin = new Padding(4, 11, 0, 11) };
            box.CheckedChanged += (_, _) => { changed(box.Checked); _settings.Save(); PreferencesChanged?.Invoke(); };
            panel.Controls.Add(box);
        }
        Toggle("Уведомления при подключении / отключении", _settings.Values.ConnectionNotifications,
            x => _settings.Values.ConnectionNotifications = x);
        Toggle("Уведомления при низком заряде", _settings.Values.BatteryNotifications,
            x => _settings.Values.BatteryNotifications = x);
        Toggle("Сохранять историю заряда локально (до 30 дней)", _settings.Values.BatteryHistory,
            x => _settings.Values.BatteryHistory = x);
        Toggle("Проверять новые версии на GitHub", _settings.Values.CheckUpdates,
            x => _settings.Values.CheckUpdates = x);
        Toggle("Экономичный режим (останавливает RGB-анимацию)", _settings.Values.EconomyMode,
            x => _settings.Values.EconomyMode = x);
        var screenLabel = TextLabel("Монитор для Dynamic Island", 11, true);
        screenLabel.Margin = new Padding(4, 17, 0, 6);
        screenLabel.Size = new Size(510, 31);
        panel.Controls.Add(screenLabel);
        var screens = Screen.AllScreens;
        var select = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 400,
            BackColor = CardInk, ForeColor = TextInk };
        for (int i = 0; i < screens.Length; i++)
            select.Items.Add($"Монитор {i + 1}: {screens[i].Bounds.Width} × {screens[i].Bounds.Height}" +
                (screens[i].Primary ? " (основной)" : ""));
        if (screens.Length > 0) select.SelectedIndex = Math.Clamp(_settings.Values.IslandMonitor, 0, screens.Length - 1);
        select.SelectedIndexChanged += (_, _) => {
            _settings.Values.IslandMonitor = select.SelectedIndex;
            _settings.Save(); MonitorChanged?.Invoke(select.SelectedIndex);
        };
        panel.Controls.Add(select);
        var note = TextLabel("RGB-цвета и плавная смена оттенков сохранены из 1.4.\nВыбор цвета: правый клик по значку → Оформление и RGB.", 10);
        note.Size = new Size(630, 58); note.Margin = new Padding(4, 20, 0, 4);
        panel.Controls.Add(note);
        var colorButton = ActionButton("Выбрать RGB-цвет интерфейса", 325);
        colorButton.Margin = new Padding(4, 10, 0, 5);
        colorButton.Click += (_, _) => {
            using var dialog = new ColorDialog { Color = AccentTheme.Fixed, AnyColor = true, FullOpen = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) {
                AccentTheme.SetColor(dialog.Color);
                PreferencesChanged?.Invoke();
                RefreshLists();
            }
        };
        panel.Controls.Add(colorButton);
        var rgb = new CheckBox { Text = "Плавная RGB-анимация (как в 1.4)",
            AutoSize = true, Checked = AccentTheme.RgbEnabled, ForeColor = TextInk,
            Font = new Font("Segoe UI", 11), Margin = new Padding(4, 10, 0, 7) };
        rgb.CheckedChanged += (_, _) => {
            AccentTheme.SetRgb(rgb.Checked);
            PreferencesChanged?.Invoke();
            RefreshLists();
        };
        panel.Controls.Add(rgb);
        var update = ActionButton("Проверить обновления и скачать ZIP", 325);
        update.Margin = new Padding(4, 16, 0, 5);
        update.Click += async (_, _) => {
            update.Enabled = false;
            var original = update.Text;
            update.Text = "Проверяем GitHub…";
            try {
                var result = await ReleaseUpdates.Check();
                if (result is null) { MessageBox.Show("Не удалось получить данные Releases.", "ChargeTray"); return; }
                var current = typeof(Program).Assembly.GetName().Version ?? new Version(2,0);
                if (!ReleaseUpdates.IsNewer(result.Tag, current)) {
                    MessageBox.Show($"Установлена {current.ToString(3)}. Последний публичный релиз: {result.Tag}.", "ChargeTray");
                    return;
                }
                if (MessageBox.Show($"Доступен {result.Tag}. Скачать ZIP в папку Загрузки?\n\nУстановка выполняется вручную после закрытия приложения.",
                    "Новая версия", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
                update.Text = "Скачиваем…";
                var file = await ReleaseUpdates.Download(result);
                if (file is null) {
                    Process.Start(new ProcessStartInfo(result.Url) { UseShellExecute = true });
                    return;
                }
                ReleaseUpdates.OpenFolder(file);
                MessageBox.Show("Архив скачан. Закройте ChargeTray, распакуйте новую версию и запустите EXE.", "ChargeTray");
            } catch (Exception ex) {
                MessageBox.Show("Ошибка загрузки: " + ex.GetType().Name, "ChargeTray");
            } finally { update.Enabled = true; update.Text = original; }
        };
        panel.Controls.Add(update);
    }
    private static Label TextLabel(string value, float size, bool bold = false) => new() {
        Text = value, ForeColor = bold ? TextInk : HintInk, AutoSize = false,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular)
    };
    private static Button ActionButton(string text, int width) => new() {
        Text = text, Width = width, Height = 35, FlatStyle = FlatStyle.Flat,
        BackColor = CardInk, ForeColor = TextInk, Cursor = Cursors.Hand
    };
}
