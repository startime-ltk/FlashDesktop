using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FlashDesktop.Core;

/// <summary>
/// Minimal, event-driven update checker. No timer, no background poll: the only
/// network call happens when the user presses "检查更新" in the client.
/// Reads a plain JSON manifest: { "version": "0.5.0", "notes": "...", "url": "..." }
/// </summary>
public sealed record UpdateResult(
    bool Checked, bool HasUpdate, string Latest, string Notes, string Url, string Error)
{
    public static UpdateResult Fail(string error) => new(false, false, "", "", "", error);
}

public static class Updater
{
    public static string CurrentVersion
    {
        get
        {
            try
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
            }
            catch { return "0.0.0"; }
        }
    }

    public static async Task<UpdateResult> CheckAsync(string manifestUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl))
            return UpdateResult.Fail("还没填更新地址。填一个 version.json 的网址再试。");

        if (!Uri.TryCreate(manifestUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return UpdateResult.Fail("更新地址不是有效的 http/https 网址。");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("FlashDesktop-Updater/1.0");

            var json = await http.GetStringAsync(uri, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string latest = root.TryGetProperty("version", out var v) ? (v.GetString() ?? "") : "";
            string notes = root.TryGetProperty("notes", out var n) ? (n.GetString() ?? "") : "";
            string url = root.TryGetProperty("url", out var u) ? (u.GetString() ?? "") : "";

            if (string.IsNullOrWhiteSpace(latest))
                return UpdateResult.Fail("清单里没有 version 字段。");

            bool has = Version.TryParse(latest, out var lv) &&
                       Version.TryParse(CurrentVersion, out var cv) && lv > cv;

            return new UpdateResult(true, has, latest, notes, url, "");
        }
        catch (OperationCanceledException)
        {
            return UpdateResult.Fail("检查超时（10 秒）。网络可能不通。");
        }
        catch (Exception ex)
        {
            return UpdateResult.Fail("检查失败：" + ex.Message);
        }
    }
}
