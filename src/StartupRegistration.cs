using Microsoft.Win32;

namespace ChargeTray;

internal enum StartupState { Off, Current, OtherPath }

internal static class StartupRegistration {
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "ChargeTray";

    internal static StartupState GetState() {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        if (run?.GetValue(Name) is not string command || string.IsNullOrWhiteSpace(command))
            return StartupState.Off;
        return string.Equals(command, Command, StringComparison.OrdinalIgnoreCase) ?
            StartupState.Current : StartupState.OtherPath;
    }

    internal static void SetEnabled(bool enabled) {
        if (!enabled) {
            using var existing = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            existing?.DeleteValue(Name, throwOnMissingValue: false);
            return;
        }
        string command = Command;
        if (command.Length > 260)
            throw new IOException("Путь к ChargeTray слишком длинный. Распакуйте приложение в папку с более коротким путём.");
        using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        run.SetValue(Name, command, RegistryValueKind.String);
    }

    private static string Command => $"\"{Application.ExecutablePath}\" --startup";
}
