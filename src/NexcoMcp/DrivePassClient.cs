using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace NexcoMcp;

public sealed record DrivePass(string Site, string Name, string Url);

public sealed record DrivePassDetail(string Name, string Url, string Text, IReadOnlyList<(byte[] Data, string MimeType, string Alt)> Maps);

/// <summary>
/// NEXCO 3 社の周遊割引プランのサイト（みち旅・速旅・ドラ割）から、プランの一覧と詳細ページを読む。
/// 料金や条件は本文の文字から読めるが、乗り放題の範囲はほぼ地図画像だけなので、画像はそのまま返す。
/// </summary>
public sealed partial class DrivePassClient(IHttpClientFactory httpClientFactory, ILogger<DrivePassClient> logger)
{
    public const string HttpClientName = "drivepass";

    private sealed record Site(string Key, string Label, string ListUrl, Regex LinkPattern);

    private static readonly Site[] Sites =
    [
        new("michitabi", "みち旅（NEXCO 西日本）", "https://www.michitabi.com/drivepass/", MichitabiLink()),
        new("hayatabi", "速旅（NEXCO 中日本）", "https://hayatabi.c-nexco.co.jp/", HayatabiLink()),
        new("drawari", "ドラ割（NEXCO 東日本）", "https://www.driveplaza.com/etc/drawari/", DrawariLink()),
    ];

    public static IReadOnlyList<string> SiteKeys => Sites.Select(s => s.Key).ToList();

    [GeneratedRegex(@"^/drivepass/(?!freepass/)[a-z0-9_-]+/$")]
    private static partial Regex MichitabiLink();

    [GeneratedRegex(@"/(drive|setplan)/detail\.html\?id=\d+$")]
    private static partial Regex HayatabiLink();

    [GeneratedRegex(@"^/etc/drawari/[a-z0-9_-]+/$")]
    private static partial Regex DrawariLink();

    [GeneratedRegex(@"<a\b[^>]*href=""([^""]*)""[^>]*>(.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex AnchorPattern();

    [GeneratedRegex(@"alt=""([^""]*)""")]
    private static partial Regex AltPattern();

    [GeneratedRegex(@"<main\b.*?</main>", RegexOptions.Singleline)]
    private static partial Regex MainPattern();

    [GeneratedRegex(@"<(script|style|nav|header|footer)\b.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex NoisePattern();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacePattern();

    [GeneratedRegex(@"<h1\b[^>]*>(.*?)</h1>", RegexOptions.Singleline)]
    private static partial Regex H1Pattern();

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ImgPattern();

    [GeneratedRegex(@"src=""([^""]*)""")]
    private static partial Regex SrcPattern();

    // 乗り放題の範囲の地図（「対象エリア」「周遊エリア図」「周遊エリア詳細マップ」「_map_」「areamap」など）。アイコンやバナーは除く
    [GeneratedRegex(@"エリア|路線図|areamap|_map_|/map", RegexOptions.IgnoreCase)]
    private static partial Regex MapPattern();

    [GeneratedRegex(@"icon|banner|logo|\.svg", RegexOptions.IgnoreCase)]
    private static partial Regex NotMapPattern();

    private const int MaxTextLength = 8000;
    private const int MaxMaps = 4;
    private const int MaxImageBytes = 3 * 1024 * 1024;

    public async Task<List<DrivePass>> ListAsync(string? site, CancellationToken ct)
    {
        var targets = site is null ? Sites : Sites.Where(s => s.Key == site).ToArray();
        var lists = await Task.WhenAll(targets.Select(async s =>
        {
            try
            {
                return await ListSiteAsync(s, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or McpException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // 1 つのサイトが落ちていても、ほかのサイトの一覧は返す
                logger.LogWarning(ex, "{Site} の一覧を読めませんでした", s.Key);
                return [new DrivePass(s.Label, "（一覧を読めませんでした）", s.ListUrl)];
            }
        }));
        return lists.SelectMany(l => l).ToList();
    }

    private async Task<List<DrivePass>> ListSiteAsync(Site site, CancellationToken ct)
    {
        var html = await GetStringAsync(site.ListUrl, ct);
        var baseUri = new Uri(site.ListUrl);
        var result = new Dictionary<string, string>();
        foreach (Match m in AnchorPattern().Matches(html))
        {
            var href = WebUtility.HtmlDecode(m.Groups[1].Value);
            // Unix では「/a/b?id=1」も file: の絶対 URI として読まれて ? がエスケープされるので、http(s) のときだけ絶対 URL とみなす
            var path = href.StartsWith("http", StringComparison.Ordinal) && Uri.TryCreate(href, UriKind.Absolute, out var abs) ? abs.PathAndQuery : href;
            if (!site.LinkPattern.IsMatch(path)) continue;
            var url = new Uri(baseUri, href).ToString();

            var inner = m.Groups[2].Value;
            var name = CleanTitle(ToText(inner));
            if (name.Length == 0) name = CleanTitle(string.Join(" ", AltPattern().Matches(inner).Select(a => WebUtility.HtmlDecode(a.Groups[1].Value))));
            if (name.Length == 0) continue;
            result.TryAdd(url, name);
        }
        return result.Select(r => new DrivePass(site.Label, r.Value, r.Key)).ToList();
    }

    /// <summary>プランの詳細ページの本文と、乗り放題の範囲の地図画像を読む。plan は URL かプラン名（一部でもよい）。</summary>
    public async Task<DrivePassDetail> GetDetailAsync(string plan, CancellationToken ct)
    {
        string url;
        if (Uri.TryCreate(plan.Trim(), UriKind.Absolute, out var given) && given.Scheme is "http" or "https")
        {
            url = given.ToString();
        }
        else
        {
            var q = IcMaster.Normalize(plan);
            var hits = (await ListAsync(null, ct)).Where(p => IcMaster.Normalize(p.Name).Contains(q)).DistinctBy(p => p.Url).ToList();
            url = hits.Count switch
            {
                1 => hits[0].Url,
                0 => throw new McpException($"「{plan}」という名前のプランが見つかりません。list_drive_passes を plan なしで呼んで一覧を確かめてください。"),
                _ => throw new McpException($"「{plan}」に当てはまるプランが複数あります。次のどれかの URL を指定してください。\n"
                    + string.Join("\n", hits.Take(15).Select(h => $"- {h.Name}（{h.Site}）{h.Url}"))),
            };
        }

        var html = await GetStringAsync(url, ct);
        var main = MainPattern().Match(html) is { Success: true } m ? m.Value : html;
        var title = H1Pattern().Matches(html).Select(h => CleanTitle(ToText(h.Groups[1].Value))).LastOrDefault(t => t.Length > 0) ?? plan;

        var text = ToText(NoisePattern().Replace(main, " "));
        if (text.Length > MaxTextLength) text = text[..MaxTextLength] + "…（以下略）";

        var maps = new List<(byte[], string, string)>();
        foreach (Match img in ImgPattern().Matches(main))
        {
            if (maps.Count >= MaxMaps) break;
            var tag = img.Value;
            if (!MapPattern().IsMatch(tag) || NotMapPattern().IsMatch(tag)) continue;
            if (SrcPattern().Match(tag) is not { Success: true } src) continue;
            var alt = AltPattern().Match(tag) is { Success: true } a ? WebUtility.HtmlDecode(a.Groups[1].Value) : "";
            var imageUrl = new Uri(new Uri(url), WebUtility.HtmlDecode(src.Groups[1].Value));
            if (await TryGetImageAsync(imageUrl, ct) is { } image) maps.Add((image.Data, image.MimeType, alt));
        }

        return new DrivePassDetail(title, url, text, maps);
    }

    private async Task<(byte[] Data, string MimeType)?> TryGetImageAsync(Uri url, CancellationToken ct)
    {
        try
        {
            var http = httpClientFactory.CreateClient(HttpClientName);
            using var res = await http.GetAsync(url, ct);
            var mime = res.Content.Headers.ContentType?.MediaType;
            if (!res.IsSuccessStatusCode || mime is null || !mime.StartsWith("image/", StringComparison.Ordinal)) return null;
            var data = await res.Content.ReadAsByteArrayAsync(ct);
            return data.Length <= MaxImageBytes ? (data, mime) : null;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "地図画像 {Url} を読めませんでした", url);
            return null;
        }
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        using var res = await http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode)
            throw new McpException($"{url} が HTTP {(int)res.StatusCode} を返しました。プランが終わったか、サイトが改修された可能性があります。");
        return await res.Content.ReadAsStringAsync(ct);
    }

    private static string ToText(string html) =>
        SpacePattern().Replace(WebUtility.HtmlDecode(TagPattern().Replace(html, " ")), " ").Trim();

    // 一覧のリンクの文字には「NEW」「詳細はこちら」などが混ざるので落とす
    private static string CleanTitle(string s)
    {
        var t = SpacePattern().Replace(s.Replace("詳細はこちら", "").Replace("［ドラ割］", ""), " ").Trim();
        return t.StartsWith("NEW ", StringComparison.Ordinal) ? t[4..] : t;
    }
}
