using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Security.Cryptography;

namespace ChargeTray;

// Only retrieves GitHub's public release metadata. Never silently replaces an EXE.
internal static class ReleaseUpdates {
    internal sealed record Release(string Tag, string Url, string? AssetUrl, string? AssetName, string? Digest);
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(22) };
    private const string Api = "https://api.github.com/repos/DrowzyX/ChargeTray/releases/latest";

    internal static async Task<Release?> Check(CancellationToken cancellation = default) {
        using var req = new HttpRequestMessage(HttpMethod.Get, Api);
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("ChargeTray", "2.0"));
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Client.SendAsync(req, cancellation);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation);
        var root = json.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var url = root.GetProperty("html_url").GetString() ?? "";
        string? assetUrl = null, name = null, digest = null;
        if (root.TryGetProperty("assets", out var assets))
            foreach (var asset in assets.EnumerateArray()) {
                string filename = asset.GetProperty("name").GetString() ?? "";
                if (!filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                    !filename.Contains("win-x64", StringComparison.OrdinalIgnoreCase)) continue;
                name = filename;
                assetUrl = asset.GetProperty("browser_download_url").GetString();
                digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() : null;
                break;
            }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var page) || page.Host != "github.com")
            return null;
        return new(tag, url, assetUrl, name, digest);
    }

    internal static bool IsNewer(string? tag, Version current) {
        string value = (tag ?? "").TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(value, out var remote) && remote > current;
    }

    internal static async Task<string?> Download(Release release, CancellationToken cancellation = default) {
        if (release.AssetUrl is null || release.AssetName is null) return null;
        var uri = new Uri(release.AssetUrl);
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" ||
            !uri.AbsolutePath.StartsWith("/DrowzyX/ChargeTray/releases/download/", StringComparison.Ordinal))
            return null;
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(folder);
        string filename = Path.GetFileName(release.AssetName);
        string dest = Path.Combine(folder, filename);
        string temp = dest + ".part";
        try {
            using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long len && len > 350_000_000)
                throw new IOException("Архив обновления слишком большой");
            await using var from = await response.Content.ReadAsStreamAsync(cancellation);
            await using (var to = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) {
                var buffer = new byte[65536];
                long count = 0;
                int read;
                while ((read = await from.ReadAsync(buffer, cancellation)) > 0) {
                    count += read;
                    if (count > 350_000_000) throw new IOException("Архив обновления слишком большой");
                    await to.WriteAsync(buffer.AsMemory(0, read), cancellation);
                }
            }
            if (release.Digest is { } expected && expected.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) {
                using var verify = File.OpenRead(temp);
                string actual = Convert.ToHexString(SHA256.HashData(verify));
                if (!actual.Equals(expected[7..], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA256 скачанного архива не совпадает с GitHub Release");
            }
            File.Move(temp, dest, overwrite: true);
            return dest;
        } finally {
            if (File.Exists(temp)) try { File.Delete(temp); } catch (IOException) { }
        }
    }
    internal static void OpenFolder(string file) {
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
    }
}
