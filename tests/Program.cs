using ChargeTray;

int assertions = 0;
void Verify(bool ok, string scenario) {
    if (!ok) throw new Exception(scenario);
    assertions++;
    Console.WriteLine($"PASS  {scenario}");
}
var mouse = new Accessory("mouse", "Mouse", DeviceType.Mouse, "Bluetooth");
Verify((mouse with { Percent = 0 }).Low, "0% is a real reading and triggers an alert");
Verify((mouse with { Percent = 14 }).Low, "14% crosses the threshold");
Verify(!(mouse with { Percent = 15 }).Low, "15% does not cross the threshold");
Verify(!(mouse with { Percent = 10, Charging = true }).Low, "A charging device does not need an alert");
Verify(!mouse.Low && mouse.Reading == "Нет данных", "Missing readings never become zero");
Verify((mouse with { Level = Band.Low }).Low && !(mouse with { Level = Band.Low }).Reading.Contains('%'), "A coarse reading has no invented percentage");
Verify(!(mouse with { Level = Band.Low, Percent = 20 }).Low, "Exact readings take precedence");
Verify((mouse with { Percent = 100 }).Reading == "100%", "100% is accepted");
var alerts = new LowBatteryAlerts();
Verify(alerts.Check([mouse with { Percent = 12 }]).Length == 1, "First low reading alerts");
Verify(alerts.Check([mouse with { Percent = 11 }]).Length == 0, "Polling does not repeat an alert");
Verify(alerts.Check([]).Length == 0 && alerts.Check([mouse with { Percent = 10 }]).Length == 0, "Reconnect alone does not duplicate an alert");
Verify(alerts.Check([mouse with { Percent = 73 }]).Length == 0, "Recovered battery re-arms silently");
Verify(alerts.Check([mouse with { Percent = 13 }, mouse with { Key = "other", Percent = 3 }]).Length == 2, "Distinct devices alert independently");
var bt = new Accessory("bt-x", "Xbox Wireless Controller", DeviceType.Controller, "Bluetooth");
var raw = new Accessory("raw-x", "HID-совместимый игровой контроллер", DeviceType.Controller, "Windows Gaming Input", 100);
var xi = new Accessory("xi-x", "Геймпад 1", DeviceType.Controller, "XInput", Level: Band.Medium);
var merged = ControllerMerge.Merge([bt], [new(raw, 0x045e, 0x0B13)], []);
Verify(merged.Length == 1 && merged[0].Name == bt.Name && merged[0].Percent == 100, "One identifiable Xbox becomes one named card");
merged = ControllerMerge.Merge([bt], [new(raw, 0x045e, 0x0B13)], [xi]);
Verify(merged.Length == 1 && merged[0].Percent is null && merged[0].Level == Band.Medium, "XInput contradicting WGI 100% falls back to the status");
merged = ControllerMerge.Merge([bt with { Percent = 62 }], [new(raw, 0x045e, 0x0B13)], [xi]);
Verify(merged.Length == 1 && merged[0].Percent == 62, "Direct Bluetooth reading takes priority");
merged = ControllerMerge.Merge([bt], [], [xi]);
Verify(merged.Length == 1 && merged[0].Level == Band.Medium, "An unambiguous Xbox receives its XInput level");
Verify(ControllerMerge.Merge([bt], [new(raw, 0x1234, 0x0B13)], []).Length == 2, "Unrelated controller is not merged");
Verify(ControllerMerge.Merge([bt, bt with { Key = "bt-2" }], [], [xi]).All(a => a.Level is null), "No battery status assigned to an ambiguous Xbox");
Verify(ControllerMerge.CapacityPercent(false, 1000, 1000) is null, "A capacity report without battery status is ignored");
Verify(ControllerMerge.CapacityPercent(true, 200, 1000) == 20, "A valid battery report is converted to percent");
var request = RazerBatteryPacket.Query();
Verify(request.Length == 91 && request[0] == 0 && request[2] == 0xff &&
    request[6] == 2 && request[7] == 7 && request[8] == 0x80 && request[89] == 0x85, "Razer feature report layout and checksum");
Verify(RazerBatteryPacket.Percent(request) is null, "A request is not a successful Razer reply");
var reply = (byte[])request.Clone();
reply[1] = 2;
foreach (var (rawValue, percent) in new (byte, int)[] { (0,0), (128,50), (153,60), (255,100) }) {
    reply[10] = rawValue;
    Verify(RazerBatteryPacket.Percent(reply) == percent, $"Razer raw value {rawValue} scales to {percent}%");
}
reply[1] = 4;
Verify(RazerBatteryPacket.Percent(reply) is null, "A sleeping Razer device has no reading");
reply[1] = 2; reply[8] = 0x84;
Verify(RazerBatteryPacket.Percent(reply) is null, "An unrelated Razer reply is ignored");
Verify(RazerBatteryPacket.Percent(reply.AsSpan(1)) is null, "A truncated report is ignored");
var aula = AulaBatteryPacket.Query(65);
Verify(aula.Length == 65 && aula[0] == 0 && aula[1] == 0x20 && aula[2] == 1 && aula[32] == 0x21, "AULA Windows HID report ID and checksum");
var capture = new byte[33];
capture[1] = 0x20; capture[2] = 0x01; capture[4] = 0x64; capture[32] = 0x85;
Verify(AulaBatteryPacket.Percent(capture) == 100, "Exact AULA capture from the Windows receiver displays 100%");
Verify(AulaBatteryPacket.Percent(capture.AsSpan(1)) == 100, "AULA response without prepended report ID");
Verify(AulaBatteryPacket.Percent(capture.AsSpan(0, 32)) is null, "AULA truncated response is rejected");
Verify(AulaBatteryPacket.IsBatteryFrame(aula) && AulaBatteryPacket.Percent(aula) is null, "AULA echoed request is not a reading");
capture[4] = 60; capture[32] = 0x5d;
Verify(AulaBatteryPacket.Percent(capture) == 60, "AULA 60% comes from the charge field");
Verify(AulaBatteryPacket.StatusFingerprint(capture)?.Contains("AULA status[4..11]=0000000000000000") == true,
    "AULA diagnostic samples validated status bytes without guessing charging");
var capturedStatus = (byte[])capture.Clone();
capturedStatus[5] = 0x02;
capturedStatus[32] = 0x5f;
Verify(AulaBatteryPacket.StatusFingerprint(capturedStatus)?.Contains("0200000000000000") == true,
    "AULA status sample exposes potential cable changes for follow-up comparison");
capturedStatus[32] = 0;
Verify(AulaBatteryPacket.StatusFingerprint(capturedStatus) is null,
    "Bad checksum never leaks an unvalidated AULA status sample");
capture[4] = 1; capture[32] = 0x22;
Verify(AulaBatteryPacket.Percent(capture) == 1, "AULA 1% remains available for alerts");
capture[32] = 0xff;
Verify(AulaBatteryPacket.Percent(capture) is null, "Corrupted AULA checksum rejected");
capture[4] = 101; capture[32] = 0x86;
Verify(AulaBatteryPacket.Percent(capture) is null, "AULA value above 100 is rejected even with a good checksum");
capture[2] = 0x02; capture[4] = 60; capture[32] = 0x5e;
Verify(AulaBatteryPacket.Percent(capture) is null, "Unrelated AULA command rejected even with a good checksum");
Verify(WindowsBattery.Decode(128, 100) is null, "Windows desktop without a system battery is not shown");
Verify(WindowsBattery.Decode(255, 100) is null && WindowsBattery.Decode(0, 255) is null, "Unknown Windows battery does not become a percentage");
Verify(WindowsBattery.Decode(8, 95) is { Percent: 95, Charging: true, Type: DeviceType.SystemBattery }, "Charging laptop is identified separately");
Verify(WindowsBattery.Decode(4, 0) is { Low: true }, "Real low system battery is detected");
var model = RazerModels.Receivers.Single(d => d.Pid == 0x0083);
Verify(model.Transaction == 0xff && model.Matches(@"\\?\hid#vid_1532&pid_0083&mi_01#abc"), "Existing Basilisk receiver path stays supported");
Verify(!model.Matches(@"\\?\hid#vid_1532&pid_00830#abc") && !model.Matches(@"\\?\hid#vid_1234&pid_0083#abc"), "Vendor/product matching is exact");
Verify(RazerModels.Receivers.Select(r => r.Pid).Distinct().Count() == 31, "31 unique Razer receiver IDs");
Verify(RazerBatteryPacket.Query(0x3f)[2] == 0x3f && RazerBatteryPacket.Query(0x1f)[89] == 0x85, "Receiver-specific transaction selectors preserve payload checksum");
var now = new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc);
var aulaCache = new AulaReadingCache();
aulaCache.Record(78, now);
aulaCache.Record(null, now.AddSeconds(10));
Verify(aulaCache.Display(null, now.AddSeconds(10)) == (78, true), "AULA brief timeout retains explicitly stale reading");
Verify(aulaCache.Display(null, now.AddMinutes(3)) == (null, false), "AULA previous reading expires");
aulaCache.Record(79, now.AddSeconds(11));
Verify(aulaCache.Display(79, now.AddSeconds(11)) == (79, false) && aulaCache.Failures == 0, "AULA automatically recovers and resets backoff");
Verify(!(mouse with { Percent = 5, IsStale = true }).Low && !(mouse with { Percent = 5, IsStale = true }).HasCurrentReading, "Stale charge cannot cause alerts or enter live widgets");
var xentaConfirm = new XentaFrameConfirmation();
var time = new DateTime(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc);
byte[] initialXenta = [0x03, 0x55, 0x40, 0x01, 100];
byte[] realXenta = [0x03, 0x55, 0x40, 0x01, 62];
Verify(xentaConfirm.Observe(initialXenta, time) is null, "One transient Xenta 100% frame is ignored");
Verify(xentaConfirm.Observe(realXenta, time.AddSeconds(2)) is null, "First valid Xenta level awaits confirmation");
Verify(xentaConfirm.Observe(realXenta, time.AddSeconds(4)) == 62, "Second Xenta level confirms percent");
Verify(XentaBatteryPacket.Percent([0x03, 0x55, 0x01, 0x00, 0x00]) is null, "Xenta link status is not a battery frame");
Verify(XentaBatteryPacket.Percent([0x03, 0x55, 0x40, 0x01, 0x00]) is null, "Xenta unknown zero is not a low-battery alarm");
Verify(XentaBatteryPacket.Percent([0x03, 0x55, 0x40, 0x01, 101]) is null, "Xenta value above 100 is rejected");
Verify(xentaConfirm.Observe(realXenta, time.AddSeconds(30)) is null, "Separated Xenta frames need fresh confirmation");
Verify(xentaConfirm.Observe(realXenta, time.AddSeconds(32)) == 62, "Valid report after timeout restores the battery level");
Verify(AccentTheme.TryParse("#3479EF", out var customColor) && customColor.R == 0x34 &&
    customColor.G == 0x79 && customColor.B == 0xEF, "Any 6-digit RGB hex color parses correctly");
Verify(!AccentTheme.TryParse("#GGCC11", out _) && !AccentTheme.TryParse("123", out _),
    "Invalid accent color settings are rejected");
Verify(AccentTheme.Rainbow(0).ToArgb() == AccentTheme.Rainbow(9000).ToArgb(),
    "Animated RGB loop repeats after nine seconds");
var prefs = new SettingsStore();
var profile = prefs.Device("test2:keyboard");
profile.Name = "Клавиатура пользователя";
profile.Pinned = true;
profile.AlertThreshold = 8;
var applied = prefs.Apply([new Accessory("test2:mouse", "Мышь", DeviceType.Mouse, "USB"),
    new Accessory("test2:keyboard", "Оригинальная клавиатура", DeviceType.Keyboard, "Bluetooth", 70)]);
Verify(applied.Length == 2 && applied[0].Name == "Клавиатура пользователя",
    "Custom name and pinned device ordering");
Verify(prefs.Threshold("test2:keyboard") == 8 && prefs.Threshold("test2:mouse") == 15,
    "Per-device alert threshold and default");
Verify(DeviceCatalog.TryGet("1D57:FA60", out var profileF) &&
    profileF.Name.Contains("1D57:FA60"), "Generic dongle does not assume ARDOR brand");
Verify(!DeviceCatalog.TryGet("FFFF:9999", out _), "Unknown VID/PID has no invented known model");
Verify(ReleaseUpdates.IsNewer("v2.1.0", new Version(2, 0, 0)) &&
    !ReleaseUpdates.IsNewer("v1.4.0", new Version(2, 0, 0)),
    "GitHub release comparison detects only upgrades");
Verify(new BatteryHistory().Forecast("does-not-exist").Contains("Недостаточно"),
    "Battery lifetime estimation refuses insufficient data");
Verify(AccentTheme.CycleSeconds == 9, "Default RGB cycle speed stays compatible with earlier versions");
AccentTheme.SetCycleSeconds(5);
Verify(AccentTheme.CycleSeconds == 5 &&
    AccentTheme.Rainbow(0).ToArgb() == AccentTheme.Rainbow(5000).ToArgb(),
    "RGB preview remains smooth and periodic after selecting a custom cycle speed");
AccentTheme.SetCycleSeconds(9);
AccentTheme.SetSystemAccent(true);
Verify(AccentTheme.SystemAccentEnabled && !AccentTheme.RgbEnabled,
    "Windows accent option does not overwrite stored RGB color");
AccentTheme.Reset();
Verify(!AccentTheme.SystemAccentEnabled && !AccentTheme.RgbEnabled,
    "Reset restores previous default appearance");
var chargeTracker = new AulaPowerTracker();
var t0 = new DateTime(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc);
chargeTracker.Observe(35, false, t0);
chargeTracker.Observe(35, false, t0.AddSeconds(50));
Verify(!chargeTracker.LikelyCharging, "Constant battery level cannot prove charging");
chargeTracker.Observe(36, false, t0.AddMinutes(3));
Verify(!chargeTracker.LikelyCharging, "Single rise must not trigger charging estimate");
chargeTracker.Observe(37, false, t0.AddMinutes(6));
Verify(chargeTracker.LikelyCharging, "Two spaced rises allow cautious charging estimate");
chargeTracker.Observe(37, true, t0.AddMinutes(7));
Verify(chargeTracker.LikelyCharging, "Stale report cannot increase charging evidence");
chargeTracker.Observe(36, false, t0.AddMinutes(9));
Verify(!chargeTracker.LikelyCharging, "Decrease clears tentative charging estimate");
chargeTracker.Observe(37, false, t0.AddMinutes(10));
chargeTracker.Observe(38, false, t0.AddMinutes(12));
chargeTracker.Observe(38, false, t0.AddMinutes(45));
Verify(!chargeTracker.LikelyCharging, "Old increase evidence eventually expires");
chargeTracker.Reset();
Verify(!chargeTracker.LikelyCharging, "Disconnect resets power inference");
chargeTracker.Observe(100, false, t0.AddMinutes(46));
Verify(!chargeTracker.LikelyCharging, "Full battery is not proof of ongoing charging");
var usbKeyboard = new Accessory("aula:f108-pro", "AULA F108 Pro", DeviceType.Keyboard,
    "Донгл 2,4 ГГц", 98, Detail: "USB подключён · зарядка не подтверждена",
    IsStale: true, UsbConnected: true);
Verify(usbKeyboard.UsbConnected && !usbKeyboard.Charging && usbKeyboard.IsStale,
    "USB cable presence must not incorrectly claim charging");
var personalized = new SettingsStore().Apply([usbKeyboard]);
Verify(personalized.Length == 1 && personalized[0].UsbConnected && !personalized[0].Charging,
    "Custom device names preserve the independent USB status");
Verify(DeviceCatalog.TryGet("062A:38CC", out var combo) && 
    combo.Type == "Keyboard", "Generic 062A:38CC combo receiver is recognized");
Verify(DeviceCatalog.TryGet("A8A5:2255", out var yjx) &&
    yjx.Type == "Mouse", "YJX A8A5:2255 receiver is classified as mouse");
byte[] unknownXenta = [0x03, 0x00, 0x40, 0xF3, 0x0A];
Verify(XentaBatteryPacket.Percent(unknownXenta) is null,
    "Unverified Xenta F3 status never reports 10 percent");
Verify(XentaBatteryPacket.RejectionReason(unknownXenta).Contains("0xF3") &&
    XentaBatteryPacket.RejectionReason(unknownXenta).Contains("0x0A"),
    "Xenta diagnostic explains the unrecognized F3 status");
Verify(XentaBatteryPacket.RejectionReason([0x03, 0x55, 0x40, 0x01, 62])
    .Contains("валидный"), "Valid Xenta packet is recognized");
Console.WriteLine($"{assertions} checks passed.");
