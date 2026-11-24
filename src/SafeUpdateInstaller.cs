using System.Diagnostics;
using System.IO.Compression;

namespace ChargeTray;

// Explicitly approved, local EXE replacement. The worker is the new ChargeTray
// executable extracted from the authenticated official release ZIP.
// Nothing is silently installed, no administrator rights are requested.
internal static class SafeUpdateInstaller {
    internal static bool PrepareAndLaunch(string zipPath, string targetExe) {
        string fullTarget = Path.GetFullPath(targetExe);
        if (!string.Equals(Path.GetFileName(fullTarget), "ChargeTray.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Неизвестная цель обновления");
        using var archive = ZipFile.OpenRead(zipPath);
        var entries = archive.Entries.Where(e =>
            e.FullName.Equals("ChargeTray.exe", StringComparison.OrdinalIgnoreCase) ||
            e.FullName.EndsWith("/ChargeTray.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entries.Length != 1 || entries[0].Length is < 100_000 or > 180_000_000)
            throw new InvalidDataException("Архив не содержит единственный допустимый ChargeTray.exe");
        var candidate = entries[0];
        string staging = Path.Combine(SettingsStore.DirectoryPath, "update-staging");
        Directory.CreateDirectory(staging);
        string worker = Path.Combine(staging, "ChargeTray.exe");
        // A previous worker may still be exiting; create a unique folder for each update.
        string slot = Path.Combine(staging, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(slot);
        worker = Path.Combine(slot, "ChargeTray.exe");
        using (var read = candidate.Open())
        using (var write = new FileStream(worker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            read.CopyTo(write);
        if (new FileInfo(worker).Length != candidate.Length)
            throw new InvalidDataException("Распакованный исполняемый файл повреждён");
        var process = new ProcessStartInfo(worker) {
            UseShellExecute = false,
            WorkingDirectory = slot
        };
        process.ArgumentList.Add("--apply-update");
        process.ArgumentList.Add(Environment.ProcessId.ToString());
        process.ArgumentList.Add(fullTarget);
        Process.Start(process);
        return true;
    }

    internal static void Apply(int parentPid, string target) {
        try {
            target = Path.GetFullPath(target);
            if (!Path.GetFileName(target).Equals("ChargeTray.exe", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(target) || parentPid <= 0) throw new InvalidDataException("Некорректная цель установки");
            try {
                using var parent = Process.GetProcessById(parentPid);
                if (!parent.WaitForExit(20_000))
                    throw new IOException("Старая версия ещё работает. Закройте ChargeTray.");
            } catch (ArgumentException) { /* Parent already exited */ }

            string source = Application.ExecutablePath;
            string next = Path.Combine(Path.GetDirectoryName(target)!, "ChargeTray.update.tmp");
            string backup = Path.Combine(Path.GetDirectoryName(target)!, "ChargeTray.previous.exe");
            File.Copy(source, next, true);
            try {
                File.Replace(next, target, backup, ignoreMetadataErrors: true);
            } finally {
                if (File.Exists(next)) try { File.Delete(next); } catch (IOException) { }
            }
            try {
                Process.Start(new ProcessStartInfo(target) {
                    WorkingDirectory = Path.GetDirectoryName(target)!,
                    UseShellExecute = true
                });
            } catch {
                if (File.Exists(backup)) File.Replace(backup, target, null);
                throw;
            }
        } catch (Exception error) {
            MessageBox.Show("Автоустановка не завершена: " + error.Message +
                "\nМожно установить архив вручную из папки «Загрузки».",
                "ChargeTray — ошибка обновления", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
