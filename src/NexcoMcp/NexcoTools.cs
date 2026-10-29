using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace NexcoMcp;

[McpServerToolType]
public sealed class NexcoTools(NexcoClient client, RouteSearcher searcher)
{
    [McpServerTool(Name = "search_highway_route", ReadOnly = true, Idempotent = true)]
    [Description("""
        NEXCO の料金・ルート検索で、IC から IC までの高速道路のルートを調べる（全国の高速道路・都市高速に対応）。
        既定（source=auto）では NEXCO 西日本のサイトで調べ、NEXCO 中日本・東日本の区間を通るときは、長く走る方の会社のサイト
        （中日本のドライブコンパス・東日本のドラぷら）で調べ直す（会社ごとの工事に伴う料金調整などは、その会社のサイトにしか入っていないことがある）。
        どのサイトの結果かは各ルートの site に入る。
        ルートごとに通常料金（現金）・ETC 料金・ETC2.0 料金、距離、所要時間（渋滞予測込みの時間も）、出発/到着時刻、通る道路と、
        料金所ごとの料金・適用される ETC 割引（休日・深夜など）、IC・JCT の通過時刻を返す。出張の高速代の見積もりや精算に使える。

        IC 名は日本語で渡す（例: 吹田, 広島, 東京, 福岡）。「IC」「インター」は付けても付けなくてもよい。
        同じ名前の IC が複数あると候補の一覧がエラーで返るので、その中の正確な名前（例: 吹田（名神高速））か IC コードで指定し直す。
        分からないときは先に search_ics で調べる。ETC 割引は出発の日時で決まるので、休日や深夜の料金を知りたいときは date / time を指定する。

        例: 「大阪から広島まで車でいくら？」→ from="吹田（名神高速）", to="広島"
            「明日 10 時に名古屋に着きたい」→ to="名古屋", date=明日, time="10:00", time_type="arrival"
            「中国道を通らずに」→ avoid_roads=["中国道"]
        """)]
    public async Task<object> SearchHighwayRoute(
        [Description("出発 IC 名（例: 吹田（名神高速）, 西宮）または IC コード")] string from,
        [Description("到着 IC 名（例: 広島, 福岡）または IC コード")] string to,
        [Description("通過する IC 名（最大 3 つ）")] string[]? via = null,
        [Description("通らない道路の名前（最大 2 つ。例: 中国道, 新名神, 一般道）")] string[]? avoid_roads = null,
        [Description("日付 (YYYY-MM-DD)。省略時は今日（日本時間）")] string? date = null,
        [Description("時刻 (HH:MM, 24 時間制)。省略時は現在時刻（日本時間）")] string? time = null,
        [Description("時刻の意味: departure=出発（既定）, arrival=到着")] string time_type = "departure",
        [Description("車種: kei=軽・自動二輪, normal=普通車（既定）, medium=中型車, large=大型車, extra_large=特大車")] string car_type = "normal",
        [Description("並び順: time=時間順（既定）, distance=距離順, toll=通常料金（現金）が安い順, etc_toll=ETC 料金が安い順")] string sort = "time",
        [Description("返すルートの数 (1〜5)。既定 3")] int count = 3,
        [Description("true なら区間ごとの SA・PA と通過時刻も返す")] bool include_sapa = false,
        [Description("検索するサイト: auto=NEXCO 中日本・東日本の区間を通るときはその会社のサイト、それ以外は NEXCO 西日本（既定）, west=NEXCO 西日本, central=NEXCO 中日本のドライブコンパス, east=NEXCO 東日本のドラぷら（central・east では avoid_roads は使えない。east は count が 3 まで）")] string source = "auto",
        [Description("source=auto での判定: any=NEXCO 西日本の候補のどれかが中日本・東日本を通れば、そのうち 1 番目の候補で決めたサイトで全体を調べ直す（既定）, first=1 番目の候補で決める, per_route=候補ごとに決め、そのサイトの同じ道路を通るルートに入れ替える")] string auto_rule = "any",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(from)) throw new McpException("from（出発 IC）を指定してください。");
        if (string.IsNullOrWhiteSpace(to)) throw new McpException("to（到着 IC）を指定してください。");
        var viaList = (via ?? []).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (viaList.Count > 3) throw new McpException("通過 IC は 3 つまでです。");
        var avoidList = (avoid_roads ?? []).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (avoidList.Count > 2) throw new McpException("通らない道路は 2 つまでです。");
        if (count is < 1 or > 5) throw new McpException("count は 1〜5 で指定してください。");

        var routeSource = RouteSearcher.ParseSource(source);
        var rule = RouteSearcher.ParseRule(auto_rule);

        var master = await client.GetMasterAsync(ct);
        var fromIc = master.Resolve(from, "出発 IC");
        var toIc = master.Resolve(to, "到着 IC");
        var viaIcs = viaList.Select(v => master.Resolve(v, "通過 IC")).ToList();
        var avoid = avoidList.Select(master.ResolveRoad).ToList();

        var query = new RouteQuery(
            fromIc.Code,
            toIc.Code,
            viaIcs.Select(i => i.Code).ToList(),
            avoid.Select(r => r.Code).ToList(),
            ParseDateTime(date, time),
            IsArrival: ParseOption(time_type, nameof(time_type), new Dictionary<string, bool>
            {
                ["departure"] = false,
                ["arrival"] = true,
            }),
            CarType: ParseOption(car_type, nameof(car_type), new Dictionary<string, int>
            {
                ["kei"] = 1,
                ["normal"] = 2,
                ["medium"] = 3,
                ["large"] = 4,
                ["extra_large"] = 5,
            }),
            Priority: ParseOption(sort, nameof(sort), new Dictionary<string, int>
            {
                ["distance"] = 1,
                ["time"] = 2,
                ["toll"] = 3,
                ["etc_toll"] = 9,
            }),
            MaxResult: count);

        RouteSearchResult result;
        try
        {
            result = await searcher.SearchAsync(query, routeSource, rule, ct);
        }
        catch (McpException ex) when (avoid.Count > 0 && routeSource != RouteSource.Central)
        {
            // 出発・到着 IC のある道路を避けると経路がなくなるので、そのことを添える
            throw new McpException(ex.Message + $"（通らない道路に {string.Join("・", avoid.Select(r => r.Name))} を指定しています。出発・到着・通過 IC がその道路にあると経路が見つかりません）");
        }

        var routes = result.Routes
            .Select((r, i) => ToRoute(i + 1, r.Route, r.Site, include_sapa))
            .ToList();
        var sites = result.Routes.Select(r => r.Site).Distinct().ToList();

        // 出発・到着 IC の注意書き（出入りできる方向の制限など）と、ルートにかかわるお知らせ
        var notices = result.Roots.SelectMany(root => Json.Array(root, "AttentionIC")
                .Select(a => Json.Str(a, "Message") is { } m ? $"{Json.Str(a, "Name")}: {IcMaster.StripHtml(m)}" : null)
                .Concat(Json.Array(root, "AttentionList").Select(a => IcMaster.StripHtml(Json.Str(a, "Message") ?? ""))))
            .Concat(result.Notes)
            .OfType<string>()
            .Where(n => n.Length > 0)
            .Distinct()
            .ToList();

        return new
        {
            From = fromIc.Name,
            To = toIc.Name,
            Via = viaIcs.Count > 0 ? viaIcs.Select(i => i.Name).ToList() : null,
            AvoidRoads = avoid.Count > 0 ? avoid.Select(r => r.Name).ToList() : null,
            Condition = $"{query.DateTime:yyyy-MM-dd HH:mm} {(query.IsArrival ? "到着" : "出発")}、{CarTypeName(query.CarType)}",
            Site = string.Join("・", sites),
            Routes = routes,
            Notices = notices.Count > 0 ? notices : null,
            Note = "料金は site のサイトの計算結果。ETC 料金には出発日時に応じた割引（休日・深夜など）が入っている。time は渋滞のない場合の所要時間、time_with_congestion は渋滞予測を入れた所要時間（違うときだけ）。到着時刻と IC の通過時刻は渋滞予測込み。",
        };
    }

    [McpServerTool(Name = "search_ics", ReadOnly = true, Idempotent = true)]
    [Description("""
        高速道路の IC（インターチェンジ・スマート IC・JCT など）を名前か読みで探す（全国）。
        search_highway_route に渡す IC の正確な名前や IC コード、出入りできる方向の制限（注意書き）を調べるのに使う。
        例: 「吹田」→ 吹田（名神高速）と吹田（近畿道）が見つかる。都道府県や道路名で絞り込める。
        """)]
    public async Task<object> SearchIcs(
        [Description("IC 名か読み（ひらがな・カタカナ）。一部でもよい（例: 吹田, すいた, 広島）")] string query,
        [Description("都道府県で絞り込む（例: 大阪府, 広島）")] string? prefecture = null,
        [Description("道路名で絞り込む（例: 山陽道, 名神）")] string? road = null,
        [Description("返す件数の上限 (1〜50)。既定 20")] int limit = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new McpException("query を指定してください。");
        if (limit is < 1 or > 50) throw new McpException("limit は 1〜50 で指定してください。");

        var master = await client.GetMasterAsync(ct);
        var hits = master.Search(query, prefecture, road);
        return new
        {
            Count = hits.Count,
            Ics = hits.Take(limit).Select(h => new
            {
                h.Ic.Name,
                h.Ic.Yomi,
                h.Ic.Code,
                h.Ic.Prefecture,
                h.Ic.Road,
                h.Ic.Note,
            }).ToList(),
        };
    }

    [McpServerTool(Name = "search_roads", ReadOnly = true, Idempotent = true)]
    [Description("""
        高速道路・有料道路の名前を探す（全国）。search_highway_route の avoid_roads（通らない道路）に渡す名前を調べるのに使う。
        query を省くと全道路を返す（約 270 件）。
        """)]
    public async Task<object> SearchRoads(
        [Description("道路名の一部（例: 中国, 新名神, E2）")] string? query = null,
        CancellationToken ct = default)
    {
        var master = await client.GetMasterAsync(ct);
        var roads = master.SearchRoads(query);
        return new { Count = roads.Count, Roads = roads.Select(r => r.Name).ToList() };
    }

    // ---- 応答の組み立て ----

    private static object ToRoute(int no, JsonElement route, string site, bool includeSapa)
    {
        var total = Json.Obj(route, "Total") ?? default;
        var depDate = DateTime.ParseExact(Json.Str(total, "DepDate") ?? "", "yyyyMMdd", CultureInfo.InvariantCulture);

        var points = new List<object>();
        var discounts = new List<string>();
        if (Json.Obj(route, "Detail") is { } detail)
        {
            foreach (var p in detail.EnumerateObject())
            {
                var ic = Json.Obj(p.Value, "LineIc") ?? default;
                var road = Json.Obj(p.Value, "LineRoad");

                object? toll = null;
                if (Json.Int(ic, "ChargeNormal") is { } cash)
                {
                    var etcName = Json.Str(ic, "ChargeSelectName") is { Length: > 0 } n1 ? n1 : null;
                    var etc2Name = Json.Str(ic, "Charge2SelectName") is { Length: > 0 } n2 ? n2 : null;
                    if (etcName is not null) discounts.Add(etcName);
                    if (etc2Name is not null) discounts.Add(etc2Name);
                    toll = new
                    {
                        // この料金がかかる区間の起点（ここまでの区間の料金をこの料金所で払う）
                        From = Json.Str(ic, "ChargeStartIcJct"),
                        Cash = cash,
                        Etc = Json.Int(ic, "ChargeSelect"),
                        EtcDiscount = etcName,
                        Etc2 = Json.Int(ic, "Charge2Select"),
                        Etc2Discount = etc2Name != etcName ? etc2Name : null,
                        Note = IcMaster.StripHtml(Json.Str(ic, "ChargeMessage") ?? ""),
                    };
                }

                object? next = null;
                if (road is { } r)
                {
                    var minutes = Json.Int(r, "intTime");
                    var delayed = Json.Int(r, "intDelayTime");
                    var sapa = Json.Array(r, "SaPaList").Select(s => $"{Json.Str(s, "Name")} {Json.Str(s, "Time")}").ToList();
                    next = new
                    {
                        Road = Json.Str(r, "Road"),
                        Direction = Json.Str(r, "DirectionName") is { } d && d != "未設定" ? d : null,
                        DistanceKm = Json.Double(r, "Dist"),
                        Minutes = minutes,
                        MinutesWithCongestion = delayed != minutes ? delayed : null,
                        SaPa = includeSapa && sapa.Count > 0 ? sapa : null,
                    };
                }

                points.Add(new
                {
                    Ic = Json.Str(ic, "Ic"),
                    Time = Json.Str(ic, "colonTime"),
                    Toll = toll,
                    Next = next,
                });
            }
        }

        var meishin = Json.Obj(total, "MeishinConst");
        var detour = Json.Obj(total, "TotalChargeAdjustItem");
        var time = Json.Str(total, "DispTotalTime");
        var delayedTime = Json.Str(total, "DispTotalDelayTime");

        return new
        {
            Route = no,
            Site = site,
            Roads = Json.Array(total, "RoadList").Select(x => x.GetString()).ToList(),
            DistanceKm = Json.Double(total, "DispTotalDist"),
            Time = time,
            TimeWithCongestion = delayedTime != time ? delayedTime : null,
            Departure = FormatMinutes(depDate, Json.Int(total, "DepTime")),
            Arrival = FormatMinutes(depDate, Json.Int(total, "ArrTime")),
            Toll = new
            {
                Cash = Json.Int(total, "TotalCharge"),
                Etc = Json.Int(total, "TotalChargeETC"),
                Etc2 = Json.Int(total, "TotalChargeETC2"),
                // 名神と新名神のように並行するルートで、ETC 料金を安いほうにそろえる調整
                // （名神のリニューアル工事の期間だけ付く。サイトの画面と同じく表示用の「1,234」形式の値を読む）
                EtcAdjusted = meishin is { } m && Json.Int(m, "Flag") is > 0 ? Json.Yen(m, "DispAdjustChargeETC") : null,
                Etc2Adjusted = meishin is { } m2 && Json.Int(m2, "Flag") is > 0 ? Json.Yen(m2, "DispAdjustCharge2ETC") : null,
                // 工事などでう回したときの ETC 料金
                EtcDetour = detour is { } a ? Json.Int(a, "Charge1") : null,
                Etc2Detour = detour is { } b ? Json.Int(b, "Charge2") : null,
            },
            EtcDiscounts = discounts.Count > 0 ? discounts.Distinct().ToList() : null,
            Points = points,
        };
    }

    private static string? FormatMinutes(DateTime date, int? minutes) =>
        minutes is { } m ? date.AddMinutes(m).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : null;

    internal static string CarTypeName(int carType) => carType switch
    {
        1 => "軽・自動二輪",
        2 => "普通車",
        3 => "中型車",
        4 => "大型車",
        _ => "特大車",
    };

    internal static DateTime ParseDateTime(string? date, string? time)
    {
        var now = NexcoClient.NowJst();
        var d = DateOnly.FromDateTime(now);
        if (!string.IsNullOrWhiteSpace(date) && !DateOnly.TryParseExact(date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
            throw new McpException($"date は YYYY-MM-DD で指定してください（受け取った値: {date}）。");
        var t = new TimeOnly(now.Hour, now.Minute);
        if (!string.IsNullOrWhiteSpace(time) && !TimeOnly.TryParseExact(time.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
            throw new McpException($"time は HH:MM で指定してください（受け取った値: {time}）。");
        return d.ToDateTime(t);
    }

    internal static T ParseOption<T>(string value, string name, Dictionary<string, T> options) =>
        options.TryGetValue(value.Trim().ToLowerInvariant(), out var v)
            ? v
            : throw new McpException($"{name} は {string.Join(", ", options.Keys)} のどれかで指定してください（受け取った値: {value}）。");
}
