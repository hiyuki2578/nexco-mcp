using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace NexcoMcp;

public sealed record DrivePass(string Site, string Name, string Url);

public sealed record DrivePassDetail(
    string Name,
    string Url,
    string Text,
    IReadOnlyList<string> Sections,
    IReadOnlyList<(byte[] Data, string MimeType, string Alt)> Figures);

/// <summary>
/// NEXCO 3 社の周遊割引プランのサイト（みち旅・速旅・ドラ割）から、プランの一覧と詳細ページを読む。
/// 料金や条件は本文の文字から読めるが、乗り放題の範囲（と速旅の料金表）は画像だけなので、画像はそのまま返す。
/// </summary>
public sealed partial class DrivePassClient(IHttpClientFactory httpClientFactory, ILogger<DrivePassClient> logger)
{
    public const string HttpClientName = "drivepass";

    private sealed record Site(string Key, string Label, string ListUrl, Regex LinkPattern, Regex? HubPattern = null);

    private static readonly Site[] Sites =
    [
        new("michitabi", "みち旅（NEXCO 西日本）", "https://www.michitabi.com/drivepass/", MichitabiLink()),
        // 速旅は、SA・PA 電子お買物券付プランのように入口ページ（/page/?id=）から出発エリア別のページに分かれているものがある
        new("hayatabi", "速旅（NEXCO 中日本）", "https://hayatabi.c-nexco.co.jp/", HayatabiLink(), HayatabiHub()),
        new("drawari", "ドラ割（NEXCO 東日本）", "https://www.driveplaza.com/etc/drawari/", DrawariLink()),
    ];

    public static IReadOnlyList<string> SiteKeys => Sites.Select(s => s.Key).ToList();

    [GeneratedRegex(@"^/drivepass/(?!freepass/)[a-z0-9_-]+/$")]
    private static partial Regex MichitabiLink();

    [GeneratedRegex(@"/(drive|setplan)/detail\.html\?id=\d+$")]
    private static partial Regex HayatabiLink();

    [GeneratedRegex(@"^/page/\?id=\d+$")]
    private static partial Regex HayatabiHub();

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

    // 乗り放題の範囲の地図と料金表（「対象エリア」「周遊エリア図」「周遊エリア詳細マップ」「料金表」「利用例」「_map_」「areamap」など）
    [GeneratedRegex(@"エリア|路線図|料金表|利用例|areamap|_map_|/map", RegexOptions.IgnoreCase)]
    private static partial Regex FigurePattern();

    [GeneratedRegex(@"icon|banner|バナー|logo|矢印|ボタン|\.svg|\.gif", RegexOptions.IgnoreCase)]
    private static partial Regex NotFigurePattern();

    // 速旅のページ内の小さなプランの見出し（例:「③ - M8 〈発着付き〉首都圏⇒愛知プラン3日間」「BN ＜発着なし＞北陸・尾張・岐阜周遊プラン」）
    [GeneratedRegex(@"(?:[①-⑳]\s*-\s*)?([A-Z]{1,2}\d{0,2})\s*[〈＜<]\s*発着[^〉＞>]{0,6}[〉＞>]\s*(\S[^※。]{1,40}?(?:プラン|パス)\S{0,12})")]
    private static partial Regex SectionHeadingPattern();

    [GeneratedRegex(@"実施を終了|本プランは終了しました")]
    private static partial Regex EndedPattern();

    private const int MaxTextLength = 8000;
    private const int MaxFigures = 6;
    private const int MaxImageBytes = 3 * 1024 * 1024;
    // 説明（alt）のない画像は、矢印などの小さな飾りを除くため、これより大きいものだけを料金表・地図とみなす
    private const int MinUnlabeledImageBytes = 20 * 1024;

    // 速旅の一覧は入口ページと各プランのページを読んで作るので、しばらく覚えておく
    private static readonly TimeSpan ListLifetime = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, (DateTime At, List<DrivePass> List)> _listCache = new();

    public async Task<List<DrivePass>> ListAsync(string? site, CancellationToken ct)
    {
        var targets = site is null ? Sites : Sites.Where(s => s.Key == site).ToArray();
        var lists = await Task.WhenAll(targets.Select(async s =>
        {
            if (_listCache.TryGetValue(s.Key, out var cached) && DateTime.UtcNow - cached.At < ListLifetime) return cached.List;
            try
            {
                var list = await ListSiteAsync(s, ct);
                _listCache[s.Key] = (DateTime.UtcNow, list);
                return list;
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
        var links = FindLinks(html, site.ListUrl, site.LinkPattern);
        if (site.HubPattern is null)
            return links.Select(r => new DrivePass(site.Label, r.Value, r.Key)).ToList();

        // 入口ページの先にあるプランのページも集める
        var hubs = FindLinks(html, site.ListUrl, site.HubPattern).Keys;
        foreach (var hubLinks in await ForEachLimitedAsync(hubs, async hub => FindLinks(await GetStringAsync(hub, ct), hub, site.LinkPattern), ct))
        {
            foreach (var (url, name) in hubLinks) links.TryAdd(url, name);
        }

        // 一覧のリンクの文字は「当日申込OK」のように名前になっていないことがあり、終わったプランへのリンクも残っているので、
        // 各ページを開いて見出しを名前にし、終わったものは除く
        var pages = await ForEachLimitedAsync(links, async link =>
        {
            var page = await GetStringAsync(link.Key, ct);
            if (EndedPattern().IsMatch(page)) return null;
            var title = H1Pattern().Matches(page).Select(h => CleanTitle(ToText(h.Groups[1].Value))).LastOrDefault(t => t.Length > 0);
            return new DrivePass(site.Label, title ?? link.Value, link.Key);
        }, ct);
        return pages.OfType<DrivePass>().ToList();
    }

    private static Dictionary<string, string> FindLinks(string html, string pageUrl, Regex pattern)
    {
        var baseUri = new Uri(pageUrl);
        var result = new Dictionary<string, string>();
        foreach (Match m in AnchorPattern().Matches(html))
        {
            var href = WebUtility.HtmlDecode(m.Groups[1].Value);
            // Unix では「/a/b?id=1」も file: の絶対 URI として読まれて ? がエスケープされるので、http(s) のときだけ絶対 URL とみなす
            var path = href.StartsWith("http", StringComparison.Ordinal) && Uri.TryCreate(href, UriKind.Absolute, out var abs) ? abs.PathAndQuery : href;
            if (!pattern.IsMatch(path)) continue;
            var url = new Uri(baseUri, href).ToString();

            var inner = m.Groups[2].Value;
            var name = CleanTitle(ToText(inner));
            if (name.Length == 0) name = CleanTitle(string.Join(" ", AltPattern().Matches(inner).Select(a => WebUtility.HtmlDecode(a.Groups[1].Value))));
            if (result.TryGetValue(url, out var existing) && existing.Length > 0) continue;
            result[url] = name;
        }
        return result;
    }

    /// <summary>
    /// プランの詳細ページの本文と、乗り放題の範囲の地図・料金表の画像を読む。plan は URL かプラン名（一部でもよい）。
    /// section（例: M8, AN）を渡すと、ページの中のその小さなプランの部分だけを読む。
    /// </summary>
    public async Task<DrivePassDetail> GetDetailAsync(string plan, string? section, CancellationToken ct)
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
        var mainText = ToText(NoisePattern().Replace(main, " "));

        var sections = SectionHeadingPattern().Matches(mainText)
            .Select(s => $"{s.Groups[1].Value}: {s.Value.Trim()}")
            .DistinctBy(s => s.Split(':')[0])
            .ToList();

        var body = main;
        if (!string.IsNullOrWhiteSpace(section))
        {
            body = FindSection(main, section.Trim())
                ?? throw new McpException($"このページに「{section}」というプランが見つかりません。"
                    + (sections.Count > 0 ? "このページのプラン:\n" + string.Join("\n", sections.Select(s => "- " + s)) : "このページは小さなプランに分かれていません。section を省いてください。"));
            title = $"{title}（{section.Trim()}）";
        }

        var text = ToText(NoisePattern().Replace(body, " "));
        if (text.Length > MaxTextLength) text = text[..MaxTextLength] + "…（以下略）";

        var figures = new List<(byte[], string, string)>();
        foreach (Match img in ImgPattern().Matches(body))
        {
            if (figures.Count >= MaxFigures) break;
            var tag = img.Value;
            if (NotFigurePattern().IsMatch(tag) || SrcPattern().Match(tag) is not { Success: true } src) continue;
            var alt = AltPattern().Match(tag) is { Success: true } a ? WebUtility.HtmlDecode(a.Groups[1].Value) : "";
            // 小さなプランを指定したときは、説明のない画像も料金表のことがあるので（速旅の一部のページ）、大きさで飾りと見分ける
            var labeled = FigurePattern().IsMatch(tag);
            if (!labeled && section is null) continue;
            var imageUrl = new Uri(new Uri(url), WebUtility.HtmlDecode(src.Groups[1].Value));
            if (await TryGetImageAsync(imageUrl, ct) is not { } image) continue;
            if (!labeled && image.Data.Length < MinUnlabeledImageBytes) continue;
            if (figures.Any(f => f.Item1.AsSpan().SequenceEqual(image.Data))) continue;
            figures.Add((image.Data, image.MimeType, alt));
        }

        return new DrivePassDetail(title, url, text, section is null ? sections : [], figures);
    }

    /// <summary>
    /// 速旅のページから、見出しが「M8 〈発着付き〉…」のような小さなプランの部分を切り出す。
    /// 部分の終わりは「高速道路周遊パスの選択に戻る」のリンク。
    /// </summary>
    private static string? FindSection(string main, string code)
    {
        var codePattern = new Regex($@"(?<![A-Za-z0-9]){Regex.Escape(code)}(?![0-9])", RegexOptions.IgnoreCase);
        foreach (Match m in codePattern.Matches(main))
        {
            var following = ToText(main.Substring(m.Index, Math.Min(600, main.Length - m.Index)));
            var afterCode = following.Length > code.Length ? following[code.Length..].TrimStart() : "";
            if (!(afterCode.StartsWith('〈') || afterCode.StartsWith('＜') || afterCode.StartsWith('<'))) continue;

            var end = main.IndexOf("高速道路周遊パスの選択に戻る", m.Index, StringComparison.Ordinal);
            return main[m.Index..(end > 0 ? end : Math.Min(main.Length, m.Index + 60000))];
        }
        return null;
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
            logger.LogWarning(ex, "画像 {Url} を読めませんでした", url);
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

    /// <summary>サイトに負担をかけないよう、同時に 6 件までにして順に読む。読めなかったものは飛ばす。</summary>
    private async Task<List<TResult>> ForEachLimitedAsync<TSource, TResult>(IEnumerable<TSource> items, Func<TSource, Task<TResult>> body, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(6);
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync(ct);
            try
            {
                return (Ok: true, Value: await body(item));
            }
            catch (Exception ex) when (ex is HttpRequestException or McpException)
            {
                logger.LogWarning(ex, "{Item} を読めませんでした", item);
                return (Ok: false, Value: default(TResult)!);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        return (await Task.WhenAll(tasks)).Where(r => r.Ok).Select(r => r.Value).ToList();
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
