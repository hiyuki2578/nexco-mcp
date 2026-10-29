using System.Text.Json;
using ModelContextProtocol;

namespace NexcoMcp;

/// <summary>どのサイトで検索するか。</summary>
public enum RouteSource
{
    /// <summary>NEXCO 中日本の区間を通るときだけドライブコンパス、それ以外は NEXCO 西日本。</summary>
    Auto,
    West,
    Central,
}

/// <summary>source=auto のとき、何を見て中日本の区間を通ると判定するか。</summary>
public enum AutoRule
{
    /// <summary>NEXCO 西日本の候補のどれか 1 つでも通れば、全体をドライブコンパスで検索し直す。</summary>
    Any,
    /// <summary>NEXCO 西日本の 1 番目の候補が通れば、全体をドライブコンパスで検索し直す。</summary>
    First,
    /// <summary>通る候補だけ、ドライブコンパスの同じ道路を通るルートに入れ替える。</summary>
    PerRoute,
}

/// <summary>検索結果のルート 1 本と、それを返したサイト。</summary>
public sealed record SourcedRoute(JsonElement Route, string Site);

public sealed record RouteSearchResult(IReadOnlyList<SourcedRoute> Routes, IReadOnlyList<JsonElement> Roots, IReadOnlyList<string> Notes);

/// <summary>NEXCO 西日本とドライブコンパスを、指定やルートの区間に応じて使い分けて検索する。</summary>
public sealed class RouteSearcher(NexcoClient west, CentralClient central)
{
    public const string WestSite = "NEXCO 西日本";
    public const string CentralSite = "NEXCO 中日本（ドライブコンパス）";

    public static RouteSource ParseSource(string value) => NexcoTools.ParseOption(value, "source", new Dictionary<string, RouteSource>
    {
        ["auto"] = RouteSource.Auto,
        ["west"] = RouteSource.West,
        ["central"] = RouteSource.Central,
    });

    public static AutoRule ParseRule(string value) => NexcoTools.ParseOption(value, "auto_rule", new Dictionary<string, AutoRule>
    {
        ["any"] = AutoRule.Any,
        ["first"] = AutoRule.First,
        ["per_route"] = AutoRule.PerRoute,
    });

    public async Task<RouteSearchResult> SearchAsync(RouteQuery q, RouteSource source, AutoRule rule, CancellationToken ct)
    {
        if (source == RouteSource.Central)
        {
            if (q.AvoidRoadCodes.Count > 0) throw new McpException("ドライブコンパス（source=central）では avoid_roads を使えません。source=west にしてください。");
            var c = await SearchCentralAsync(q, await TranslatorAsync(ct), ct);
            return new(Routes(c, CentralSite), [c], []);
        }

        var w = await west.SearchRouteAsync(q, ct);
        if (source == RouteSource.West) return new(Routes(w, WestSite), [w], []);

        var westRoutes = Json.Array(w, "Detail").ToList();
        var translator = await TranslatorAsync(ct);
        var touches = westRoutes.Select(r => TouchesCentral(r, translator)).ToList();
        var needCentral = rule switch
        {
            AutoRule.First => touches.FirstOrDefault(),
            _ => touches.Any(t => t),
        };
        if (!needCentral) return new(Routes(w, WestSite), [w], []);

        if (q.AvoidRoadCodes.Count > 0)
            return new(Routes(w, WestSite), [w], ["NEXCO 中日本の区間を通るが、ドライブコンパスでは通らない道路を指定できないので、NEXCO 西日本の結果を返した。"]);

        var c2 = await SearchCentralAsync(q, translator, ct);
        if (rule != AutoRule.PerRoute) return new(Routes(c2, CentralSite), [c2], []);

        // 中日本を通る候補だけ、ドライブコンパスの候補のうち同じ有料道路を同じ順に通るものに入れ替える
        var centralRoutes = Json.Array(c2, "Detail").ToList();
        var used = new HashSet<int>();
        var result = new List<SourcedRoute>();
        var notes = new List<string>();
        for (var i = 0; i < westRoutes.Count; i++)
        {
            if (!touches[i])
            {
                result.Add(new(westRoutes[i], WestSite));
                continue;
            }
            var key = RoadKey(westRoutes[i]);
            var match = centralRoutes.FindIndex(r => RoadKey(r) == key);
            if (match >= 0 && used.Add(match))
            {
                result.Add(new(centralRoutes[match], CentralSite));
            }
            else
            {
                result.Add(new(westRoutes[i], WestSite));
                notes.Add($"ルート {i + 1} は NEXCO 中日本の区間を通るが、ドライブコンパスに同じ道路を通るルートがなかったので、NEXCO 西日本の結果のまま。");
            }
        }
        return new(result, [w, c2], notes);
    }

    private CodeTranslator? _translator;
    private (IcMaster, IReadOnlyList<CentralIc>)? _translatorSource;

    private async Task<CodeTranslator> TranslatorAsync(CancellationToken ct)
    {
        var master = await west.GetMasterAsync(ct);
        var ics = await central.GetIcsAsync(ct);
        // どちらかの一覧を読み直したときだけ作り直す
        if (_translator is null || _translatorSource is not { } src || !ReferenceEquals(src.Item1, master) || !ReferenceEquals(src.Item2, ics))
        {
            _translator = new CodeTranslator(master, ics);
            _translatorSource = (master, ics);
        }
        return _translator;
    }

    private async Task<JsonElement> SearchCentralAsync(RouteQuery q, CodeTranslator translator, CancellationToken ct)
    {
        var master = await west.GetMasterAsync(ct);
        string ToCentral(string code) => master.FindByCode(code) is { } ic ? translator.ToCentral(ic) : code;
        var cq = q with
        {
            FromCode = ToCentral(q.FromCode),
            ToCode = ToCentral(q.ToCode),
            ViaCodes = q.ViaCodes.Select(ToCentral).ToList(),
        };
        return await central.SearchRouteAsync(cq, translator.Map, ct);
    }

    private static List<SourcedRoute> Routes(JsonElement root, string site) =>
        Json.Array(root, "Detail").Select(r => new SourcedRoute(r, site)).ToList();

    /// <summary>
    /// ルートが NEXCO 中日本の道路を走るか。地点から次の地点までの道路について、
    /// どちらかの端がその道路上（IC コードの先頭 4 桁が道路コード）の中日本の IC なら、中日本の区間を走るとみなす。
    /// </summary>
    internal static bool TouchesCentral(JsonElement route, CodeTranslator translator)
    {
        var points = Points(route);
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var road = points[i].Road;
            if (road is null) continue;
            if (OnRoad(points[i], road) || OnRoad(points[i + 1], road)) return true;
        }
        return false;

        bool OnRoad((string? Code, string? Name, string? Road) p, string road) =>
            p.Code is { Length: 7 } code && code.StartsWith(road, StringComparison.Ordinal)
            && translator.CompanyOf(code, p.Name) == CentralClient.CentralCompany;
    }

    private static List<(string? Code, string? Name, string? Road)> Points(JsonElement route) =>
        Json.Obj(route, "Detail") is { } detail
            ? detail.EnumerateObject().Select(p => (
                Json.Str(Json.Obj(p.Value, "LineIc") ?? default, "Code"),
                Json.Str(Json.Obj(p.Value, "LineIc") ?? default, "Ic"),
                Json.Obj(p.Value, "LineRoad") is { } r ? Json.Str(r, "Code") : null)).ToList()
            : [];

    // 一般道（道路コードが 9 で始まる）を除いた、通る道路の並び
    private static string RoadKey(JsonElement route)
    {
        var roads = new List<string>();
        foreach (var (_, _, road) in Points(route))
            if (road is { Length: > 0 } r && !r.StartsWith('9') && roads.LastOrDefault() != r) roads.Add(r);
        return string.Join(",", roads);
    }
}
