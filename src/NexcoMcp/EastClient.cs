using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace NexcoMcp;

/// <summary>
/// NEXCO 東日本の「ドラぷら」（www.driveplaza.com）で検索するクライアント。
/// 結果はサーバーで組み立てた HTML なので、そこから地点・道路・料金を読み、NEXCO 西日本の応答と同じ形の JSON に組み替える。
/// IC は名前で渡す。IC コードの付け方は同じだが、同じ IC でコードが違うことがある（大月が 1101076 と 1100066 など）ので、
/// サイトの IC 候補から名前と路線番号で同じ IC を探す。
/// </summary>
public sealed partial class EastClient(IHttpClientFactory httpClientFactory, ILogger<EastClient> logger)
{
    public const string HttpClientName = "east";
    public const string BaseUrl = "https://www.driveplaza.com/";
    public const string EastCompany = "101";

    private sealed record EastIc(string Code, string Name, string RoadName);

    private readonly ConcurrentDictionary<string, EastIc> _icCache = new();

    /// <summary>ルート検索。通らない道路の指定はサイトにないので受け付けない。応答のコードは map で読み替える。</summary>
    public async Task<JsonElement> SearchRouteAsync(RouteQuery q, IcMaster master, CodeMap map, CancellationToken ct)
    {
        if (q.AvoidRoadCodes.Count > 0) throw new McpException("ドラぷらでは通らない道路を指定できません。");
        if (q.ViaCodes.Count > 5) throw new McpException("ドラぷらの経由 IC は 5 つまでです。");

        var from = await ResolveAsync(master, q.FromCode, ct);
        var to = await ResolveAsync(master, q.ToCode, ct);
        var via = new List<EastIc>();
        foreach (var code in q.ViaCodes) via.Add(await ResolveAsync(master, code, ct));

        var p = new List<(string, string)>
        {
            ("startPlaceKana", from.Name),
            ("arrivePlaceKana", to.Name),
        };
        for (var i = 0; i < via.Count; i++) p.Add((i == 0 ? "keiyuPlaceKana" : $"keiyuPlaceKana{i + 1}", via[i].Name));
        p.AddRange(
        [
            ("searchYear", q.DateTime.Year.ToString(CultureInfo.InvariantCulture)),
            ("searchMonth", q.DateTime.Month.ToString(CultureInfo.InvariantCulture)),
            ("searchDay", q.DateTime.Day.ToString(CultureInfo.InvariantCulture)),
            ("searchHour", q.DateTime.Hour.ToString(CultureInfo.InvariantCulture)),
            ("searchMinute", q.DateTime.Minute.ToString(CultureInfo.InvariantCulture)),
            ("kind", q.IsArrival ? "2" : "1"),
            // 車種は 0: 軽自動車等, 1: 普通車, 2: 中型車, 3: 大型車, 4: 特大車
            ("carType", (q.CarType - 1).ToString(CultureInfo.InvariantCulture)),
            // 並び順は 1: 距離, 2: 時間, 3: 料金（通常料金と ETC 料金の区別はない）
            ("priority", q.Priority switch { 1 => "1", 3 or 9 => "3", _ => "2" }),
        ]);
        var url = "dp/SearchQuick?" + string.Join("&", p.Select(x => $"{x.Item1}={Uri.EscapeDataString(x.Item2)}"));

        var http = httpClientFactory.CreateClient(HttpClientName);
        var page = await GetStringAsync(http, url, ct);

        // 名前で渡した IC が、選んだ IC として受け取られたかを確かめる
        var startIc = InitIcPattern().Matches(page).FirstOrDefault(m => m.Groups[1].Value == "StartIc")?.Groups[2].Value;
        if (startIc is null)
            throw new McpException($"ドラぷらで {from.Name} → {to.Name} を検索できませんでした（IC 名が受け付けられませんでした）。source=west で調べてください。");
        if (startIc != from.Code)
            logger.LogWarning("ドラぷらの出発 IC が {Expected} ではなく {Actual} になりました", from.Code, startIc);

        var json = ToWestShape(page, q, map);
        if (((JsonArray)json["Detail"]!).Count == 0)
            throw new McpException($"ドラぷらで {from.Name} → {to.Name} のルートが見つかりませんでした。");
        return JsonSerializer.SerializeToElement(json);
    }

    /// <summary>NEXCO 西日本の IC を、ドラぷらの IC（名前とコード）に決める。</summary>
    private async Task<EastIc> ResolveAsync(IcMaster master, string westCode, CancellationToken ct)
    {
        if (_icCache.TryGetValue(westCode, out var cached)) return cached;
        var ic = master.FindByCode(westCode) ?? throw new McpException($"IC コード {westCode} が見つかりません。");

        var baseName = IcMaster.BaseName(ic.Name).Trim();
        var http = httpClientFactory.CreateClient(HttpClientName);
        var xml = await GetStringAsync(http, "community/icsearch_api.php?val_word=" + Uri.EscapeDataString(baseName), ct);
        List<EastIc> candidates;
        try
        {
            candidates = XDocument.Parse(xml).Root?.Elements("IcItem")
                .Select(e => new EastIc(e.Element("Code")?.Value ?? "", e.Element("Name")?.Value ?? "", e.Element("RoadName")?.Value ?? ""))
                .Where(e => e.Code.Length > 0 && e.Name.Length > 0)
                .ToList() ?? [];
        }
        catch (System.Xml.XmlException)
        {
            throw new McpException("ドラぷらの IC 候補を読めませんでした。サイトが改修された可能性があります。");
        }

        static string Key(string name) => IcMaster.Normalize(IcMaster.BaseName(name));
        var exact = candidates.Where(c => IcMaster.Normalize(c.Name) == IcMaster.Normalize(ic.Name)).ToList();
        var byBase = candidates.Where(c => Key(c.Name) == Key(ic.Name)).ToList();
        // 同じ名前の IC が複数あるときは、路線番号（E1 など）で選ぶ
        var westNumber = RoadNumberPattern().Match(IcMaster.Normalize(ic.Road)).Value;
        var byRoad = byBase.Where(c => westNumber.Length > 0 && RoadNumberPattern().Match(IcMaster.Normalize(c.RoadName)).Value == westNumber).ToList();
        var chosen = exact.Count == 1 ? exact[0]
            : byBase.Count == 1 ? byBase[0]
            : byRoad.Count >= 1 ? byRoad[0]
            : throw new McpException(byBase.Count == 0
                ? $"ドラぷらで {ic.Name} に当たる IC が見つかりませんでした。source=west で調べてください。"
                : $"ドラぷらで {ic.Name} に当たる IC を 1 つに決められませんでした（候補: {string.Join("、", byBase.Select(c => c.Name))}）。source=west で調べてください。");
        _icCache[westCode] = chosen;
        return chosen;
    }

    // ---- NEXCO 西日本の応答と同じ形への組み替え ----

    private sealed record Section(string Road, bool General, double? Dist, int? Minutes, int? DelayMinutes);

    private sealed record Fee(int Cash, int Etc, int Etc2, string? EtcName, string? Etc2Name);

    private sealed record Point(string Code, string Name, string? Time, Section? Next, Fee? Fee, List<(string Name, string Time)> SaPa);

    /// <summary>
    /// ルートごとに、合計（price-wrap・times-wrap）のあとに地点の並び（route-flow）が続く。
    /// 区間料金は料金のかかり始める地点（料金所や入口）に付いているので、その区間の終わりの地点で払う料金として載せ直す（NEXCO 西日本の応答と同じ）。
    /// </summary>
    private static JsonObject ToWestShape(string page, RouteQuery q, CodeMap map)
    {
        var routes = new JsonArray();
        foreach (var block in page.Split("<div class=\"price-wrap\">").Skip(1).Take(q.MaxResult))
        {
            var flowAt = block.IndexOf("<div class=\"route-flow\">", StringComparison.Ordinal);
            if (flowAt < 0) continue;
            var summary = block[..flowAt];
            var points = ParsePoints(block[flowAt..]);
            if (points.Count == 0) continue;

            var prices = PricePattern().Matches(summary[..Math.Max(0, summary.IndexOf("times-wrap", StringComparison.Ordinal))])
                .Select(m => Yen(m.Groups[1].Value)).ToList();
            var normalTime = TagText(summary, "li-normal");
            var congestionTime = TagText(summary, "li-congestion");
            var distance = TagText(summary, "li-distance")?.Replace("km", "");

            // 時刻は日付のない「15:10」なので、検索した日時から出発・到着の日付を決める
            var dep = ClockMinutes(points[0].Time) ?? 0;
            var arr = ClockMinutes(points[^1].Time) ?? dep;
            if (arr < dep) arr += 1440;
            var depDate = q.DateTime.Date;
            if (q.IsArrival && dep > q.DateTime.Hour * 60 + q.DateTime.Minute) depDate = depDate.AddDays(-1);

            var detail = new JsonObject();
            var roadNames = new List<string>();
            var charges = SectionEnds(points);
            for (var i = 0; i < points.Count; i++)
            {
                var pt = points[i];
                var ic = new JsonObject
                {
                    ["Ic"] = pt.Name,
                    ["Code"] = map.Ic(pt.Code, pt.Name),
                    ["colonTime"] = pt.Time,
                };
                if (charges.TryGetValue(i, out var charge))
                {
                    ic["ChargeNormal"] = charge.Fee.Cash.ToString(CultureInfo.InvariantCulture);
                    ic["ChargeSelect"] = charge.Fee.Etc.ToString(CultureInfo.InvariantCulture);
                    ic["ChargeSelectName"] = charge.Fee.EtcName;
                    ic["Charge2Select"] = charge.Fee.Etc2.ToString(CultureInfo.InvariantCulture);
                    ic["Charge2SelectName"] = charge.Fee.Etc2Name;
                    ic["ChargeStartIcJct"] = charge.Start;
                }
                var point = new JsonObject { ["LineIc"] = ic };
                if (pt.Next is { } next)
                {
                    var road = new JsonObject
                    {
                        ["Road"] = next.Road,
                        ["Code"] = next.General ? "9999" : map.Road(pt.Code.Length >= 4 ? pt.Code[..4] : pt.Code, next.Road),
                        ["Dist"] = next.Dist,
                        ["intTime"] = next.Minutes?.ToString(CultureInfo.InvariantCulture),
                        ["intDelayTime"] = (next.DelayMinutes ?? next.Minutes)?.ToString(CultureInfo.InvariantCulture),
                    };
                    if (pt.SaPa.Count > 0)
                        road["SaPaList"] = new JsonArray(pt.SaPa.Select(s => (JsonNode)new JsonObject { ["Name"] = s.Name, ["Time"] = s.Time }).ToArray());
                    point["LineRoad"] = road;
                    if (!next.General && roadNames.LastOrDefault() != next.Road) roadNames.Add(next.Road);
                }
                detail[$"{i}_{pt.Code}"] = point;
            }

            routes.Add(new JsonObject
            {
                ["Total"] = new JsonObject
                {
                    ["DepDate"] = depDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                    ["DepTime"] = dep.ToString(CultureInfo.InvariantCulture),
                    ["ArrTime"] = arr.ToString(CultureInfo.InvariantCulture),
                    ["TotalCharge"] = prices.ElementAtOrDefault(0)?.ToString(CultureInfo.InvariantCulture),
                    ["TotalChargeETC"] = prices.ElementAtOrDefault(1)?.ToString(CultureInfo.InvariantCulture),
                    ["TotalChargeETC2"] = prices.ElementAtOrDefault(2)?.ToString(CultureInfo.InvariantCulture),
                    ["DispTotalDist"] = double.TryParse(distance, NumberStyles.Float, CultureInfo.InvariantCulture, out var km) ? km : null,
                    ["DispTotalTime"] = normalTime,
                    ["DispTotalDelayTime"] = congestionTime ?? normalTime,
                    ["RoadList"] = new JsonArray(roadNames.Select(n => (JsonNode)n).ToArray()),
                },
                ["Detail"] = detail,
            });
        }

        var notices = new JsonArray();
        if (NoticePattern().Match(page) is { Success: true } notice)
        {
            var text = WebUtility.HtmlDecode(TagPattern().Replace(notice.Groups[1].Value.Replace("<br>", "\n"), "")).Trim();
            foreach (var line in text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("ご注意ください", StringComparison.Ordinal) && !l.StartsWith("ご指定いただいたIC", StringComparison.Ordinal)))
                notices.Add(new JsonObject { ["Message"] = line });
        }

        return new JsonObject
        {
            ["ErrorMessage"] = "",
            ["Detail"] = routes,
            ["AttentionList"] = notices,
        };
    }

    /// <summary>
    /// 料金のかかり始める地点ごとに、その区間の終わりの地点を決める。終わりは、一般道に降りる地点、到着地、
    /// または次の料金の地点の手前（次の料金の地点が同じ道路の本線料金所なら、その手前の地点。道路が変わる JCT なら、その JCT）。
    /// </summary>
    private static Dictionary<int, (Fee Fee, string Start)> SectionEnds(List<Point> points)
    {
        var result = new Dictionary<int, (Fee, string)>();
        var feeAt = Enumerable.Range(0, points.Count).Where(i => points[i].Fee is not null).ToList();
        for (var k = 0; k < feeAt.Count; k++)
        {
            var s = feeAt[k];
            var j = k + 1 < feeAt.Count ? feeAt[k + 1] : points.Count;
            var end = -1;
            for (var i = s + 1; i < j && end < 0; i++)
                if (points[i].Next is null || points[i].Next!.General) end = i;
            if (end < 0)
            {
                end = j >= points.Count ? points.Count - 1
                    : j - 1 > s && points[j - 1].Next?.Road == points[j].Next?.Road ? j - 1
                    : j;
            }
            result[end] = (points[s].Fee!, points[s].Name);
        }
        return result;
    }

    private static List<Point> ParsePoints(string flow)
    {
        var matches = PointPattern().Matches(flow);
        var points = new List<Point>();
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : flow.Length;
            var body = flow[start..end];
            var code = matches[i].Groups[1].Value;

            var name = PlacePattern().Match(body) is { Success: true } pm
                ? WebUtility.HtmlDecode(TagPattern().Replace(EmPattern().Replace(pm.Groups[1].Value, ""), "")).Trim()
                : code;
            var time = TimePattern().Match(body) is { Success: true } tm ? tm.Groups[1].Value : null;

            Section? next = null;
            if (DistancePattern().Match(body) is { Success: true } dm)
            {
                var raw = CommentPattern().Replace(dm.Groups[1].Value, "");
                var general = raw.Contains("general-road", StringComparison.Ordinal);
                var text = Regex.Replace(WebUtility.HtmlDecode(TagPattern().Replace(raw, " ")), @"\s+", " ").Trim();
                var km = KmPattern().Match(text);
                var roadText = (km.Success ? text[..km.Index] : text).Trim();
                if (general && roadText.StartsWith("一般道", StringComparison.Ordinal)) roadText = roadText["一般道".Length..].Trim();
                roadText = RoadTagPattern().Replace(roadText, m => m.Groups[1].Value.Any(char.IsLetter) ? m.Groups[1].Value + " " : "").Trim().TrimEnd('　').Trim();
                var after = km.Success ? text[(km.Index + km.Length)..] : "";
                // 「渋滞 9 分 （通常 7 分）」なら渋滞込みと通常、「19 分」ならどちらも同じ
                int? minutes, delayed;
                var normalAt = after.IndexOf("通常", StringComparison.Ordinal);
                if (after.Contains("渋滞", StringComparison.Ordinal) && normalAt >= 0)
                {
                    delayed = Duration(after[..normalAt]);
                    minutes = Duration(after[normalAt..]);
                }
                else
                {
                    minutes = delayed = Duration(after);
                }
                next = new Section(general ? (roadText.Length > 0 ? roadText : "一般道") : roadText, general,
                    km.Success && double.TryParse(km.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
                    minutes, delayed);
            }

            Fee? fee = null;
            if (FeeTablePattern().Match(body) is { Success: true } fm)
            {
                var values = PricePattern().Matches(fm.Groups[1].Value).Select(m => Yen(m.Groups[1].Value)).ToList();
                if (values.Count >= 3 && values[0] is { } cash && values[1] is { } etc && values[2] is { } etc2)
                {
                    var etcNo = SelectedPattern().Matches(body).FirstOrDefault(m => m.Groups[1].Value == "selectedEtc1No")?.Groups[2].Value;
                    var etc2No = SelectedPattern().Matches(body).FirstOrDefault(m => m.Groups[1].Value == "selectedEtcNoETC")?.Groups[2].Value;
                    fee = new Fee(cash, etc, etc2, DiscountName(body, "setSectionFee", etcNo), DiscountName(body, "setSectionFeeETC2", etc2No));
                }
            }

            var sapa = SaPaPattern().Matches(body)
                .Select(m => (WebUtility.HtmlDecode(m.Groups[2].Value).Trim(), m.Groups[1].Value))
                .Distinct()
                .ToList();
            points.Add(new Point(code, name, time, next, fee, sapa));
        }
        return points;
    }

    // 通常料金（1）と、首都高などの ETC 料金（102 など）は割引ではないので名前を付けない
    private static string? DiscountName(string body, string function, string? no)
    {
        if (no is null or "1") return null;
        var name = Regex.Matches(body, Regex.Escape(function) + @"\(\d+, '([^']*)','[^']*', '[^']*', (\d+)\)")
            .FirstOrDefault(m => m.Groups[2].Value == no)?.Groups[1].Value;
        return name is "ETC料金" or "通常料金" ? null : name;
    }

    private static string? TagText(string html, string cls) =>
        Regex.Match(html, $@"<dl class=""{cls}"">.*?<dd>(.*?)</dd>", RegexOptions.Singleline) is { Success: true } m
            ? TagPattern().Replace(m.Groups[1].Value, "").Trim()
            : null;

    private static int? Yen(string s) => int.TryParse(s.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static int? ClockMinutes(string? clock) =>
        clock is not null && TimeOnly.TryParseExact(clock, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t.Hour * 60 + t.Minute
            : null;

    private static int? Duration(string text)
    {
        var m = DurationPattern().Match(text);
        if (!m.Success) return null;
        var hours = m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        return hours * 60 + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"var init(\w+Ic\d?) = ""(\w*)""")]
    private static partial Regex InitIcPattern();

    [GeneratedRegex(@"<dl class=""js-accordion[^""]*"" data-iccode=""(\w+)"">")]
    private static partial Regex PointPattern();

    [GeneratedRegex(@"<span class=""txt-place"">(.*?)</span>", RegexOptions.Singleline)]
    private static partial Regex PlacePattern();

    [GeneratedRegex(@"<span class=""txt-time"">([\d:]+)</span>")]
    private static partial Regex TimePattern();

    [GeneratedRegex(@"<p class=""txt-distance"">(.*?)</p>", RegexOptions.Singleline)]
    private static partial Regex DistancePattern();

    [GeneratedRegex(@"<dl class=""table-price"">(.*?)</dl>", RegexOptions.Singleline)]
    private static partial Regex FeeTablePattern();

    [GeneratedRegex(@"<dd><em>(?:<span[^>]*>)?([\d,]+)")]
    private static partial Regex PricePattern();

    [GeneratedRegex(@"id=""(selectedEtc1No|selectedEtcNoETC)\d+"">(\d+)<")]
    private static partial Regex SelectedPattern();

    [GeneratedRegex(@"<p class=""txt-time"">([\d:]+)\s*<span[^>]*>[^<]*</span></p>\s*<p class=""txt-sa"">\d+\s*([^<]+)</p>")]
    private static partial Regex SaPaPattern();

    [GeneratedRegex(@"<div class=""c-blockSkin03 c-mb40 c-pcmb32 "">\s*<p class=""is-fsizeSmall"">(.*?)</p>", RegexOptions.Singleline)]
    private static partial Regex NoticePattern();

    [GeneratedRegex(@"([\d.]+)\s*km")]
    private static partial Regex KmPattern();

    [GeneratedRegex(@"(?:(\d+)\s*時間)?\s*(\d+)\s*分")]
    private static partial Regex DurationPattern();

    // 「【E4】東北自動車道」の路線番号。首都高の「【4】」のような数字だけの番号は外す
    [GeneratedRegex(@"【([^】]*)】")]
    private static partial Regex RoadTagPattern();

    [GeneratedRegex(@"[A-Z]\d+[A-Z]?")]
    private static partial Regex RoadNumberPattern();

    [GeneratedRegex(@"<em>[^<]*!</em>")]
    private static partial Regex EmPattern();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentPattern();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex TagPattern();

    private async Task<string> GetStringAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var res = await http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode)
        {
            logger.LogWarning("{Url} が {Status} を返しました", url.Split('?')[0], (int)res.StatusCode);
            throw new McpException($"NEXCO 東日本のサイトが HTTP {(int)res.StatusCode} を返しました。しばらくしてから試してください。");
        }
        return await res.Content.ReadAsStringAsync(ct);
    }
}
