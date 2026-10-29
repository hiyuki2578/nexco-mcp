using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace NexcoMcp;

/// <summary>ルート検索の条件。IC は IC コード、避ける道路は道路コード（rdM の番号）で持つ。</summary>
public sealed record RouteQuery(
    string FromCode,
    string ToCode,
    IReadOnlyList<string> ViaCodes,
    IReadOnlyList<string> AvoidRoadCodes,
    DateTime DateTime,
    bool IsArrival,
    int CarType,
    int Priority,
    int MaxResult);

/// <summary>
/// NEXCO 西日本の「高速料金・ルート検索」（search.w-nexco.co.jp）の内部 API を呼ぶクライアント。
/// IC・道路の一覧はサイトの JavaScript（route_master_vNNN.js）に埋め込まれているので、それを読んで覚えておく。
/// </summary>
public sealed partial class NexcoClient(IHttpClientFactory httpClientFactory, ILogger<NexcoClient> logger)
{
    public const string HttpClientName = "nexco";
    public const string BaseUrl = "https://search.w-nexco.co.jp/";

    private static readonly TimeZoneInfo Jst = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    // マスタは開通やサイト改修で変わるので、半日で読み直す
    private static readonly TimeSpan MasterLifetime = TimeSpan.FromHours(12);
    private readonly SemaphoreSlim _masterLock = new(1, 1);
    private IcMaster? _master;
    private DateTime _masterLoadedAt;

    public static DateTime NowJst() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Jst).DateTime;

    [GeneratedRegex(@"/js_navi/route_master_v\d+\.js[^""']*")]
    private static partial Regex MasterScriptPattern();

    public async Task<IcMaster> GetMasterAsync(CancellationToken ct)
    {
        if (_master is { } cached && DateTime.UtcNow - _masterLoadedAt < MasterLifetime) return cached;
        await _masterLock.WaitAsync(ct);
        try
        {
            if (_master is { } again && DateTime.UtcNow - _masterLoadedAt < MasterLifetime) return again;

            var http = httpClientFactory.CreateClient(HttpClientName);
            // ファイル名にバージョンが入るので、トップページから今のファイル名を拾う
            var top = await GetStringAsync(http, "", ct);
            var path = MasterScriptPattern().Match(top) is { Success: true } m
                ? m.Value
                : throw new McpException("IC の一覧（route_master_vNNN.js）の場所がトップページから見つかりませんでした。サイトが改修された可能性があります。");
            var script = await GetStringAsync(http, path.TrimStart('/'), ct);
            var master = IcMaster.Parse(script);
            logger.LogInformation("IC マスタを読み込みました: {Path}（IC {IcCount} 件、道路 {RoadCount} 件）", path, master.Ics.Count, master.Roads.Count);

            _master = master;
            _masterLoadedAt = DateTime.UtcNow;
            return master;
        }
        finally
        {
            _masterLock.Release();
        }
    }

    /// <summary>ルート検索（/route_ajax/mk_route.php）。応答は JSONP なので、コールバックの括弧をはがして JSON にする。</summary>
    public async Task<JsonElement> SearchRouteAsync(RouteQuery q, CancellationToken ct)
    {
        string Code(IReadOnlyList<string> list, int i) => i < list.Count ? list[i] : "";
        string Avoid(int i) => i < q.AvoidRoadCodes.Count ? "G" + q.AvoidRoadCodes[i] : "0";
        var p = new List<(string, string)>
        {
            ("val_from", q.FromCode),
            ("val_to", q.ToCode),
            ("val_date", q.DateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture)),
            ("val_time_hour", q.DateTime.Hour.ToString(CultureInfo.InvariantCulture)),
            ("val_time_min", q.DateTime.Minute.ToString(CultureInfo.InvariantCulture)),
            ("val_timekind", q.IsArrival ? "2" : "1"),
            ("val_cartype", q.CarType.ToString(CultureInfo.InvariantCulture)),
            ("val_pass1", Code(q.ViaCodes, 0)),
            ("val_pass2", Code(q.ViaCodes, 1)),
            ("val_pass3", Code(q.ViaCodes, 2)),
            ("val_detour1", Avoid(0)),
            ("val_detour2", Avoid(1)),
            ("val_roadtype", ""),
            ("val_maxresult", q.MaxResult.ToString(CultureInfo.InvariantCulture)),
            ("val_priority", q.Priority.ToString(CultureInfo.InvariantCulture)),
            ("val_latilongi", "2"),
            ("val_roaddisp", "2"),
        };
        var url = "route_ajax/mk_route.php?" + string.Join("&", p.Select(x => $"{x.Item1}={Uri.EscapeDataString(x.Item2)}"));

        var http = httpClientFactory.CreateClient(HttpClientName);
        var body = (await GetStringAsync(http, url, ct)).Trim();
        // 混んでいると本文が空で返ることがあるので、1 回だけ取り直す
        if (body.Length == 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            body = (await GetStringAsync(http, url, ct)).Trim();
        }
        var start = body.IndexOf('{');
        var end = body.LastIndexOf('}');
        if (start < 0 || end < start) throw new McpException("ルート検索の応答を読めませんでした。サイトが改修された可能性があります。");

        using var doc = JsonDocument.Parse(body.AsMemory(start, end - start + 1));
        var root = doc.RootElement.Clone();
        if (Json.Str(root, "ErrorMessage") is { Length: > 0 } error) throw new McpException(error);
        return root;
    }

    private async Task<string> GetStringAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var res = await http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode)
        {
            logger.LogWarning("{Url} が {Status} を返しました", url, (int)res.StatusCode);
            throw new McpException($"NEXCO 西日本のサイトが HTTP {(int)res.StatusCode} を返しました（{url.Split('?')[0]}）。しばらくしてから試してください。");
        }
        return await res.Content.ReadAsStringAsync(ct);
    }
}

/// <summary>応答の JSON は同じ項目が文字列だったり数値だったり null だったりするので、それを吸収する補助関数。</summary>
public static class Json
{
    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    public static int? Int(JsonElement e, string name) =>
        int.TryParse(Str(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>「8,170」のような桁区切り付きの金額を読む。</summary>
    public static int? Yen(JsonElement e, string name) =>
        int.TryParse(Str(e, name)?.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    public static double? Double(JsonElement e, string name) =>
        double.TryParse(Str(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

    public static JsonElement? Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    public static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray()
            : [];
}
