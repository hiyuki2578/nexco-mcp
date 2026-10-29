using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace NexcoMcp;

/// <summary>
/// NEXCO 中日本の「ドライブコンパス」（dc.c-nexco.co.jp）で検索するクライアント。
/// 応答は XML なので、NEXCO 西日本の応答と同じ形の JSON に組み替えて返し、ツール側では同じように読めるようにする。
/// IC コードの付け方は NEXCO 西日本と同じだが、同じ IC でコードが違うことがある（大月が 1101076 と 1101066 など）ので、
/// 呼び出し側で <see cref="CodeMap"/> を渡して読み替える。
/// </summary>
/// <summary>ドライブコンパスの IC 一覧の 1 件。Company は管轄の会社（101: 東日本, 102: 中日本, 103: 西日本 など）。</summary>
public sealed record CentralIc(string Code, string Name, string Road, string Company);

/// <summary>応答の IC コード・道路コードを NEXCO 西日本のコードに読み替える関数（コード, 名前 → コード）。</summary>
public sealed record CodeMap(Func<string, string?, string> Ic, Func<string, string?, string> Road);

public sealed class CentralClient(IHttpClientFactory httpClientFactory, ILogger<CentralClient> logger)
{
    public const string HttpClientName = "central";
    public const string BaseUrl = "https://dc.c-nexco.co.jp/";
    public const string CentralCompany = "102";

    private static readonly TimeSpan IcsLifetime = TimeSpan.FromHours(12);
    private readonly SemaphoreSlim _icsLock = new(1, 1);
    private List<CentralIc>? _ics;
    private DateTime _icsLoadedAt;

    /// <summary>ドライブコンパスの IC 一覧。</summary>
    public async Task<IReadOnlyList<CentralIc>> GetIcsAsync(CancellationToken ct)
    {
        if (_ics is { } cached && DateTime.UtcNow - _icsLoadedAt < IcsLifetime) return cached;
        await _icsLock.WaitAsync(ct);
        try
        {
            if (_ics is { } again && DateTime.UtcNow - _icsLoadedAt < IcsLifetime) return again;

            var http = httpClientFactory.CreateClient(HttpClientName);
            using var doc = JsonDocument.Parse(await GetStringAsync(http, "json/icdata.json", ct));
            // 1 件は [道路コード, IC コード, 名前, 読み, 会社, ...] の形
            var ics = new List<CentralIc>();
            foreach (var ic in Json.Array(doc.RootElement, "icData"))
            {
                if (ic.GetArrayLength() < 5) continue;
                if (ic[0].GetString() is { } road && ic[1].GetString() is { } code && ic[2].GetString() is { } name && ic[4].GetString() is { } company)
                    ics.Add(new CentralIc(code, name, road, company));
            }
            if (ics.Count == 0) throw new McpException("ドライブコンパスの IC の一覧を読めませんでした。サイトが改修された可能性があります。");
            logger.LogInformation("ドライブコンパスの IC 一覧を読み込みました（{Count} 件）", ics.Count);

            _ics = ics;
            _icsLoadedAt = DateTime.UtcNow;
            return ics;
        }
        finally
        {
            _icsLock.Release();
        }
    }

    /// <summary>
    /// ルート検索。q の IC コードはドライブコンパスのコードで渡す。応答のコードは map で読み替える。
    /// 通らない道路の指定はサイトにないので受け付けない。
    /// </summary>
    public async Task<JsonElement> SearchRouteAsync(RouteQuery q, CodeMap map, CancellationToken ct)
    {
        if (q.AvoidRoadCodes.Count > 0) throw new McpException("ドライブコンパスでは通らない道路を指定できません。");

        var p = new List<(string, string)>
        {
            ("val_apiid", "routesearch"),
            ("val_from", q.FromCode),
        };
        // 経由 IC は val_to01〜 に順に並べ、最後に到着 IC を置く
        if (q.ViaCodes.Count > 0)
        {
            var stops = q.ViaCodes.Append(q.ToCode).ToList();
            for (var i = 0; i < stops.Count; i++) p.Add(($"val_to{i + 1:00}", stops[i]));
        }
        else
        {
            p.Add(("val_to", q.ToCode));
        }
        p.AddRange(
        [
            ("val_timekind", q.IsArrival ? "2" : "1"),
            ("val_date", q.DateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture)),
            ("val_time", (q.DateTime.Hour * 60 + q.DateTime.Minute).ToString(CultureInfo.InvariantCulture)),
            ("val_cartype", q.CarType.ToString(CultureInfo.InvariantCulture)),
        ]);
        // 並び順は 1: 距離, 2: 時間, 3: 料金（val_prioritycost で 1: 通常料金, 2: ETC 料金）
        p.AddRange(q.Priority switch
        {
            3 => [("val_priority", "3"), ("val_prioritycost", "1")],
            9 => [("val_priority", "3"), ("val_prioritycost", "2")],
            var n => [("val_priority", n.ToString(CultureInfo.InvariantCulture))],
        });
        p.AddRange(
        [
            ("val_latilongi", "2"),
            ("val_maxresult", q.MaxResult.ToString(CultureInfo.InvariantCulture)),
            ("val_stedreplace", "1"),
            ("val_outputType", "3"),
            ("val_lang", "0"),
        ]);
        var url = "cgi-bin/tn_linux.cgi?" + string.Join("&", p.Select(x => $"{x.Item1}={Uri.EscapeDataString(x.Item2)}"));

        var http = httpClientFactory.CreateClient(HttpClientName);
        XElement root;
        try
        {
            root = XDocument.Parse(await GetStringAsync(http, url, ct)).Root!;
        }
        catch (System.Xml.XmlException)
        {
            throw new McpException("ドライブコンパスの応答を読めませんでした。サイトが改修された可能性があります。");
        }
        if (root.Name.LocalName == "ErrorMessage") throw new McpException("ドライブコンパス: " + root.Value.Trim());
        if (root.Name.LocalName != "NexcoRoute") throw new McpException("ドライブコンパスの応答を読めませんでした。サイトが改修された可能性があります。");

        var json = ToWestShape(root, map);
        return JsonSerializer.SerializeToElement(json);
    }

    // ---- NEXCO 西日本の応答と同じ形への組み替え ----

    /// <summary>
    /// RouteItem の Detail は料金区間（ChargeSection）の並び。区間の中に IC・JCT が順に並び、それぞれにその先の道路が付く。
    /// 区間の料金は、次の区間の最初の地点（最後の区間なら到着 IC）で払う料金として載せる（NEXCO 西日本の応答と同じ）。
    /// </summary>
    private static JsonObject ToWestShape(XElement root, CodeMap map)
    {
        var routes = new JsonArray();
        foreach (var item in root.Elements("RouteItem"))
        {
            var total = item.Element("Total");
            var depDate = total?.Element("DepDate")?.Value ?? "";

            var points = new List<JsonObject>();
            var roadNames = new List<string>();
            JsonObject? pendingCharge = null;
            string? sectionStart = null;

            foreach (var section in item.Element("Detail")?.Elements("ChargeSection") ?? [])
            {
                var first = true;
                foreach (var e in section.Elements())
                {
                    if (e.Name.LocalName is "SA" or "PA")
                    {
                        if (points.LastOrDefault()?["LineRoad"] is JsonObject lastRoad)
                        {
                            var list = lastRoad["SaPaList"] as JsonArray ?? [];
                            list.Add(new JsonObject { ["Name"] = Name(e), ["Time"] = Clock(e.Element("ArrTime")?.Value) });
                            lastRoad["SaPaList"] = list;
                        }
                        continue;
                    }

                    var nodes = e.Name.LocalName == "Base"
                        ? e.Elements().Where(x => x.Name.LocalName is "IC" or "JCT").ToList()
                        : e.Name.LocalName is "IC" or "JCT" ? [e] : [];
                    foreach (var node in nodes)
                    {
                        var point = Point(node, map);
                        if (first)
                        {
                            // 前の区間の料金は、この区間の最初の地点で払う
                            if (pendingCharge is not null) Attach(point, pendingCharge, sectionStart);
                            sectionStart = Name(node);
                            first = false;
                        }
                        if (point["LineRoad"] is JsonObject road && node.Element("RoadType")?.Value != "4"
                            && road["Road"]?.GetValue<string>() is { } roadName && roadNames.LastOrDefault() != roadName)
                            roadNames.Add(roadName);
                        points.Add(point);
                    }
                    if (e.Name.LocalName == "Base") pendingCharge = Charge(e.Element("ChargeItem"));
                }
                if (first) pendingCharge = null;
            }
            // 到着 IC は道路を持たない最後の地点。最後の区間の料金をここで払う
            if (points.Count > 0 && points[^1]["LineRoad"] is null)
            {
                if (pendingCharge is not null) Attach(points[^1], pendingCharge, sectionStart);
                // 到着 IC には時刻がないので、ルート全体の到着時刻を使う
                points[^1]["LineIc"]!["colonTime"] ??= Clock(total?.Element("ArrTime")?.Value);
            }

            var detail = new JsonObject();
            for (var i = 0; i < points.Count; i++)
                detail[$"{i}_{points[i]["LineIc"]!["Code"]}"] = points[i];

            var totalCharge = total?.Element("ChargeItem");
            var etcTotal = totalCharge?.Elements("Value").FirstOrDefault(v => (string?)v.Attribute("type") == "etctotal");
            var minutes = Int(total?.Element("TotalTime")?.Value);
            var delayed = Int(total?.Element("TotalDelayTime")?.Value);
            routes.Add(new JsonObject
            {
                ["Total"] = new JsonObject
                {
                    ["DepDate"] = depDate,
                    ["DepTime"] = total?.Element("DepTime")?.Value,
                    ["ArrTime"] = total?.Element("ArrTime")?.Value,
                    ["TotalCharge"] = totalCharge?.Elements("Value").FirstOrDefault(v => (string?)v.Attribute("type") == "normal")?.Element("Charge")?.Value,
                    ["TotalChargeETC"] = TypedCharge(etcTotal, "1"),
                    ["TotalChargeETC2"] = TypedCharge(etcTotal, "2"),
                    ["DispTotalDist"] = Int(total?.Element("TotalDist")?.Value) is { } d ? d / 10.0 : null,
                    ["DispTotalTime"] = Duration(minutes),
                    ["DispTotalDelayTime"] = Duration(delayed),
                    ["RoadList"] = new JsonArray(roadNames.Select(n => (JsonNode)n).ToArray()),
                },
                ["Detail"] = detail,
            });
        }

        var attentions = new JsonArray();
        foreach (var a in root.Elements("Attention"))
            attentions.Add(new JsonObject { ["Code"] = a.Element("Code")?.Value is { } c ? map.Ic(c, a.Element("Name")?.Value) : null, ["Name"] = a.Element("Name")?.Value, ["Message"] = a.Element("Message")?.Value });

        return new JsonObject
        {
            ["ErrorMessage"] = "",
            ["Detail"] = routes,
            ["AttentionIC"] = attentions,
        };
    }

    private static JsonObject Point(XElement node, CodeMap map)
    {
        var ic = new JsonObject
        {
            ["Ic"] = Name(node),
            ["Code"] = node.Element("Code")?.Value is { } code ? map.Ic(code, Name(node)) : null,
            ["colonTime"] = Clock(node.Element("DepTime")?.Value ?? node.Element("ArrTime")?.Value),
        };
        var point = new JsonObject { ["LineIc"] = ic };
        // 到着 IC には道路がない（NextRoad に降りた先の一般道の名前だけがある）
        if (node.Element("Road")?.Value is { Length: > 0 } road)
        {
            point["LineRoad"] = new JsonObject
            {
                ["Road"] = road,
                ["Code"] = node.Element("RoadCode")?.Value is { } roadCode ? map.Road(roadCode, road) : null,
                ["DirectionName"] = node.Element("DirectionName")?.Value,
                ["Dist"] = Int(node.Element("Dist")?.Value) is { } d ? d / 10.0 : null,
                ["intTime"] = node.Element("Time")?.Value,
                ["intDelayTime"] = node.Element("DelayTime")?.Value,
            };
        }
        else
        {
            // 到着時刻は ArrTime にしかない
            ic["colonTime"] = Clock(node.Element("ArrTime")?.Value) ?? ic["colonTime"]?.GetValue<string>();
        }
        return point;
    }

    private static void Attach(JsonObject point, JsonObject charge, string? start)
    {
        var ic = (JsonObject)point["LineIc"]!;
        foreach (var (key, value) in charge) ic[key] = value?.DeepClone();
        ic["ChargeStartIcJct"] = start;
    }

    /// <summary>
    /// 区間の料金。Value の type 1 が通常料金（現金）で、selected が all / etc1 の Value が ETC、all / etc2 の Value が ETC2.0 で払う料金。
    /// </summary>
    private static JsonObject? Charge(XElement? item)
    {
        if (item is null) return null;
        var values = item.Elements("Value").ToList();
        var cash = values.FirstOrDefault(v => (string?)v.Attribute("type") == "1")?.Element("Charge")?.Value;
        if (cash is null) return null;

        var etc = values.FirstOrDefault(v => (string?)v.Attribute("selected") is "all" or "etc1");
        var etc2 = values.FirstOrDefault(v => (string?)v.Attribute("selected") is "all" or "etc2") ?? etc;
        return new JsonObject
        {
            ["ChargeNormal"] = cash,
            ["ChargeSelect"] = TypedCharge(etc, "1") ?? cash,
            ["ChargeSelectName"] = DiscountName(etc),
            ["Charge2Select"] = TypedCharge(etc2, "2") ?? TypedCharge(etc, "1") ?? cash,
            ["Charge2SelectName"] = DiscountName(etc2),
        };
    }

    // 通常料金（type 1）と、首都高などの ETC 料金（type 102 など）は割引ではないので名前を付けない
    private static string? DiscountName(XElement? value) =>
        value is null || (string?)value.Attribute("type") == "1" || value.Element("Name")?.Value is null or "ETC料金" ? null : value.Element("Name")?.Value;

    private static string? TypedCharge(XElement? value, string type) =>
        value?.Elements("Charge").FirstOrDefault(c => (string?)c.Attribute("type") == type)?.Value
        ?? value?.Elements("Charge").FirstOrDefault(c => c.Attribute("type") is null)?.Value;

    private static string? Name(XElement node) =>
        node.Elements("Name").FirstOrDefault(n => (string?)n.Attribute("dscr") == "kanji")?.Value ?? node.Element("Name")?.Value;

    private static int? Int(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    // 時刻はその日の 0 時からの分（日をまたぐと 1440 を超える）
    private static string? Clock(string? minutes) =>
        Int(minutes) is { } m ? $"{m / 60 % 24:00}:{m % 60:00}" : null;

    private static string? Duration(int? minutes) => minutes switch
    {
        null => null,
        < 60 => $"{minutes}分",
        { } m => $"{m / 60}時間{m % 60}分",
    };

    private async Task<string> GetStringAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var res = await http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode)
        {
            logger.LogWarning("{Url} が {Status} を返しました", url, (int)res.StatusCode);
            throw new McpException($"NEXCO 中日本のサイトが HTTP {(int)res.StatusCode} を返しました。しばらくしてから試してください。");
        }
        return await res.Content.ReadAsStringAsync(ct);
    }
}
