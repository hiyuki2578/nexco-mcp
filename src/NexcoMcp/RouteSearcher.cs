using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;

namespace NexcoMcp;

/// <summary>どのサイトで検索するか。</summary>
public enum RouteSource
{
    /// <summary>NEXCO 中日本・東日本の区間を通るときはその会社のサイト、それ以外は NEXCO 西日本。</summary>
    Auto,
    West,
    Central,
    East,
}

/// <summary>source=auto のとき、NEXCO 西日本のどの候補を見て、検索し直すサイトを決めるか。</summary>
public enum AutoRule
{
    /// <summary>中日本・東日本を通る候補のうち 1 番目のもので決め、全体をそのサイトで検索し直す。</summary>
    Any,
    /// <summary>1 番目の候補で決め、全体をそのサイトで検索し直す。</summary>
    First,
    /// <summary>候補ごとに決め、そのサイトの同じ道路を通るルートに入れ替える。</summary>
    PerRoute,
}

/// <summary>検索結果のルート 1 本と、それを返したサイト。</summary>
public sealed record SourcedRoute(JsonElement Route, string Site);

public sealed record RouteSearchResult(IReadOnlyList<SourcedRoute> Routes, IReadOnlyList<JsonElement> Roots, IReadOnlyList<string> Notes);

/// <summary>NEXCO 西日本・中日本（ドライブコンパス）・東日本（ドラぷら）を、指定やルートの区間に応じて使い分けて検索する。</summary>
public sealed partial class RouteSearcher(NexcoClient west, CentralClient central, EastClient east)
{
    public const string WestSite = "NEXCO 西日本";
    public const string CentralSite = "NEXCO 中日本（ドライブコンパス）";
    public const string EastSite = "NEXCO 東日本（ドラぷら）";

    public static RouteSource ParseSource(string value) => NexcoTools.ParseOption(value, "source", new Dictionary<string, RouteSource>
    {
        ["auto"] = RouteSource.Auto,
        ["west"] = RouteSource.West,
        ["central"] = RouteSource.Central,
        ["east"] = RouteSource.East,
    });

    public static AutoRule ParseRule(string value) => NexcoTools.ParseOption(value, "auto_rule", new Dictionary<string, AutoRule>
    {
        ["any"] = AutoRule.Any,
        ["first"] = AutoRule.First,
        ["per_route"] = AutoRule.PerRoute,
    });

    private static string SiteName(RouteSource source) => source switch
    {
        RouteSource.Central => CentralSite,
        RouteSource.East => EastSite,
        _ => WestSite,
    };

    public async Task<RouteSearchResult> SearchAsync(RouteQuery q, RouteSource source, AutoRule rule, CancellationToken ct)
    {
        if (source is RouteSource.Central or RouteSource.East)
        {
            if (q.AvoidRoadCodes.Count > 0)
                throw new McpException($"{SiteName(source)}（source={source.ToString().ToLowerInvariant()}）では avoid_roads を使えません。source=west にしてください。");
            var r = await SearchOtherAsync(q, source, await TranslatorAsync(ct), ct);
            return new(Routes(r, SiteName(source)), [r], []);
        }

        var w = await west.SearchRouteAsync(q, ct);
        if (source == RouteSource.West) return new(Routes(w, WestSite), [w], []);

        var westRoutes = Json.Array(w, "Detail").ToList();
        var translator = await TranslatorAsync(ct);
        var targets = westRoutes.Select(r => Target(r, translator)).ToList();
        var target = rule switch
        {
            AutoRule.First => targets.FirstOrDefault(),
            _ => targets.FirstOrDefault(t => t != RouteSource.West),
        };
        if (target == RouteSource.West && !(rule == AutoRule.PerRoute && targets.Any(t => t != RouteSource.West)))
            return new(Routes(w, WestSite), [w], []);

        if (q.AvoidRoadCodes.Count > 0)
            return new(Routes(w, WestSite), [w], [$"NEXCO 中日本・東日本の区間を通るが、{SiteName(target)}では通らない道路を指定できないので、NEXCO 西日本の結果を返した。"]);

        if (rule != AutoRule.PerRoute)
        {
            var r = await SearchOtherAsync(q, target, translator, ct);
            return new(Routes(r, SiteName(target)), [r], []);
        }

        // 候補ごとに、そのサイトの候補のうち同じ有料道路を同じ順に通るものに入れ替える（サイトごとに 1 回だけ検索する）
        var roots = new List<JsonElement> { w };
        var others = new Dictionary<RouteSource, List<JsonElement>>();
        foreach (var t in targets.Where(t => t != RouteSource.West).Distinct())
        {
            var r = await SearchOtherAsync(q, t, translator, ct);
            roots.Add(r);
            others[t] = Json.Array(r, "Detail").ToList();
        }
        var used = new HashSet<(RouteSource, int)>();
        var result = new List<SourcedRoute>();
        var notes = new List<string>();
        for (var i = 0; i < westRoutes.Count; i++)
        {
            if (targets[i] == RouteSource.West)
            {
                result.Add(new(westRoutes[i], WestSite));
                continue;
            }
            var key = RoadKey(westRoutes[i]);
            var names = RoadNameKey(westRoutes[i]);
            var match = others[targets[i]].FindIndex(r => RoadKey(r) == key || RoadNameKey(r) == names);
            if (match >= 0 && used.Add((targets[i], match)))
            {
                result.Add(new(others[targets[i]][match], SiteName(targets[i])));
            }
            else
            {
                result.Add(new(westRoutes[i], WestSite));
                notes.Add($"ルート {i + 1} は {SiteName(targets[i])}で調べ直す区間を通るが、同じ道路を通るルートがなかったので、NEXCO 西日本の結果のまま。");
            }
        }
        return new(result, roots, notes);
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

    private async Task<JsonElement> SearchOtherAsync(RouteQuery q, RouteSource source, CodeTranslator translator, CancellationToken ct)
    {
        var master = await west.GetMasterAsync(ct);
        if (source == RouteSource.East) return await east.SearchRouteAsync(q, master, translator.Map, ct);
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
    /// ルートをどのサイトで調べ直すか。地点から次の地点までの道路ごとに、どちらかの端がその道路上（IC コードの先頭 4 桁が道路コード）の
    /// IC なら、その IC の管轄の会社の道路とみなして距離を足し、中日本・東日本のうち長く走る方のサイトにする。どちらも走らなければ NEXCO 西日本。
    /// </summary>
    internal static RouteSource Target(JsonElement route, CodeTranslator translator)
    {
        var points = Points(route);
        double centralKm = 0, eastKm = 0;
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var road = points[i].Road;
            if (road is null) continue;
            var company = Company(points[i], road) ?? Company(points[i + 1], road);
            if (company == CentralClient.CentralCompany) centralKm += points[i].Km ?? 0.1;
            else if (company == EastClient.EastCompany) eastKm += points[i].Km ?? 0.1;
        }
        return centralKm == 0 && eastKm == 0 ? RouteSource.West
            : centralKm >= eastKm ? RouteSource.Central
            : RouteSource.East;

        string? Company((string? Code, string? Name, string? Road, double? Km) p, string road) =>
            p.Code is { Length: 7 } code && code.StartsWith(road, StringComparison.Ordinal) ? translator.CompanyOf(code, p.Name) : null;
    }

    private static List<(string? Code, string? Name, string? Road, double? Km)> Points(JsonElement route) =>
        Json.Obj(route, "Detail") is { } detail
            ? detail.EnumerateObject().Select(p => (
                Json.Str(Json.Obj(p.Value, "LineIc") ?? default, "Code"),
                Json.Str(Json.Obj(p.Value, "LineIc") ?? default, "Ic"),
                Json.Obj(p.Value, "LineRoad") is { } r ? Json.Str(r, "Code") : null,
                Json.Obj(p.Value, "LineRoad") is { } r2 ? Json.Double(r2, "Dist") : null)).ToList()
            : [];

    // 首都高などは道路コードがサイトによって違うので、道路名でも比べる。
    // 「E4 東北自動車道」と「E4 東北道」、「首都高速中央環状線」と「首都高中央環状線」のような表記の違いはそろえる
    private static string RoadNameKey(JsonElement route)
    {
        var roads = new List<string>();
        // 一般道（道路コードが 9 で始まる）は除く
        var names = Json.Obj(route, "Detail") is { } detail
            ? detail.EnumerateObject().Select(p => Json.Obj(p.Value, "LineRoad")).OfType<JsonElement>()
                .Where(r => Json.Str(r, "Code") is not { } c || !c.StartsWith('9'))
                .Select(r => Json.Str(r, "Road") ?? "")
            : [];
        foreach (var name in names)
        {
            var n = IcMaster.Normalize(RoadNumberPattern().Replace(name, ""))
                .Replace("自動車道", "道").Replace("高速道路", "高速").Replace("首都高速", "首都高");
            if (n.Length > 0 && roads.LastOrDefault() != n) roads.Add(n);
        }
        return string.Join(",", roads);
    }

    [GeneratedRegex(@"^\s*[A-Z]\d+[A-Z]?\s+")]
    private static partial Regex RoadNumberPattern();

    // 一般道（道路コードが 9 で始まる）を除いた、通る道路の並び
    private static string RoadKey(JsonElement route)
    {
        var roads = new List<string>();
        foreach (var (_, _, road, _) in Points(route))
            if (road is { Length: > 0 } r && !r.StartsWith('9') && roads.LastOrDefault() != r) roads.Add(r);
        return string.Join(",", roads);
    }
}
