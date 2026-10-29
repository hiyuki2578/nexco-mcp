using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace NexcoMcp;

public sealed class TripLeg
{
    [Description("出発 IC 名または IC コード")]
    public string From { get; set; } = "";

    [Description("到着 IC 名または IC コード")]
    public string To { get; set; } = "";

    [Description("日付 (YYYY-MM-DD)")]
    public string Date { get; set; } = "";

    [Description("出発時刻 (HH:MM)。省略時は 09:00")]
    public string? Time { get; set; }

    [Description("通過する IC 名（最大 3 つ）")]
    public string[]? Via { get; set; }
}

public sealed class PassArea
{
    [Description("乗り放題の範囲に入る道路名（例: 中国道, 舞鶴若狭道, 京都縦貫道, 名神）")]
    public string Road { get; set; } = "";

    [Description("範囲の端の IC（その道路上の IC）。from と to の間が範囲。道路全体なら from も to も省く。"
        + "範囲が JCT で終わるときは、道路の「起点」（キロポスト 0 km 側。IC コードの小さい側）か「終点」と書く")]
    public string? From { get; set; }

    [Description("範囲のもう一方の端の IC（その道路上の IC）か、「起点」「終点」")]
    public string? To { get; set; }
}

[McpServerToolType]
public sealed class DrivePassTools(NexcoClient client, DrivePassClient passes, RouteSearcher searcher)
{
    [McpServerTool(Name = "list_drive_passes", ReadOnly = true, Idempotent = true)]
    [Description("""
        NEXCO 3 社の周遊割引プラン（高速道路の乗り放題パス）を調べる。みち旅（NEXCO 西日本のドライブパス）、
        速旅（NEXCO 中日本のドライブプラン）、ドラ割（NEXCO 東日本のフリーパス・周遊プラン）が対象。

        plan を省くとプランの一覧（名前・URL）を返す。速旅は SA・PA 電子お買物券付プランのような、出発エリア別のページも含む。
        plan にプラン名か URL を渡すと、詳細ページの本文（料金・利用日数・利用できない期間・申込条件など）と、
        乗り放題の範囲の地図・料金表の画像を返す。範囲や速旅の料金は画像でしか示されていないことが多いので、
        画像を見て料金と、道路と端の IC を読み取り、compare_drive_pass に渡す。

        速旅のページは「M8 〈発着付き〉首都圏⇒愛知プラン3日間」のような小さなプランに分かれていることがある。
        section を省くとページの中のプランの一覧を返すので、section に記号（例: M8, AN）を渡してそのプランの料金表と地図を読む。
        """)]
    public async Task<CallToolResult> ListDrivePasses(
        [Description("プラン名（一部でもよい）または詳細ページの URL。省略時は一覧を返す")] string? plan = null,
        [Description("一覧を絞り込むサイト: michitabi=みち旅（西日本）, hayatabi=速旅（中日本）, drawari=ドラ割（東日本）。省略時は全部")] string? site = null,
        [Description("ページの中の小さなプランの記号（例: M8, AN）。速旅のページで使う")] string? section = null,
        CancellationToken ct = default)
    {
        if (site is not null && !DrivePassClient.SiteKeys.Contains(site))
            throw new McpException($"site は {string.Join(", ", DrivePassClient.SiteKeys)} のどれかで指定してください（受け取った値: {site}）。");

        if (string.IsNullOrWhiteSpace(plan))
        {
            var list = await passes.ListAsync(site, ct);
            var sb = new StringBuilder();
            foreach (var group in list.GroupBy(p => p.Site))
            {
                sb.AppendLine($"## {group.Key}");
                foreach (var p in group) sb.AppendLine($"- {p.Name} {p.Url}");
            }
            sb.AppendLine();
            sb.AppendLine("料金や乗り放題の範囲は、plan にプラン名か URL を渡して詳細を読むこと。");
            return new CallToolResult { Content = [new TextContentBlock { Text = sb.ToString() }] };
        }

        var detail = await passes.GetDetailAsync(plan, section, ct);
        var sb2 = new StringBuilder();
        sb2.AppendLine($"# {detail.Name}");
        sb2.AppendLine(detail.Url);
        sb2.AppendLine();
        if (detail.Sections.Count > 0)
        {
            // 小さなプランに分かれたページは、本文より先にプランの一覧を見せる
            sb2.AppendLine("このページのプラン（料金表と地図は section に記号を渡して読む）:");
            foreach (var s in detail.Sections) sb2.AppendLine($"- {s}");
            sb2.AppendLine();
        }
        sb2.AppendLine(detail.Text);
        sb2.AppendLine();
        sb2.AppendLine("地図・料金表の画像: " + (detail.Figures.Count > 0
            ? string.Join("、", detail.Figures.Select(m => m.Alt is { Length: > 0 } alt ? alt : "（説明なし）")) + "（この後に画像で続く）"
            : detail.Sections.Count > 0 ? "section を指定すると返す" : "見つからなかった。詳細ページを開いて確かめること"));
        var content = new List<ContentBlock> { new TextContentBlock { Text = sb2.ToString() } };
        content.AddRange(detail.Figures.Select(m => ImageContentBlock.FromBytes(m.Data, m.MimeType)));
        return new CallToolResult { Content = content };
    }

    [McpServerTool(Name = "compare_drive_pass", ReadOnly = true, Idempotent = true)]
    [Description("""
        旅程（IC から IC への区間の並び）について、通常の ETC 料金と、周遊割引プラン（乗り放題パス）を使った場合の料金を比べる。

        通常料金は区間ごとに search_highway_route と同じ検索で出す（その日の休日割引・深夜割引も入る）。
        プランを使った場合は、プラン料金に、乗り放題の範囲外を走る分の料金を足す。範囲の外から範囲内へ入る（出る）区間の
        範囲外の分は、outside_rule で決める。
        - route_edge（既定。速旅など）: ルート上の範囲の端の IC から範囲外の IC までの料金
        - cheapest_ic（みち旅）: 範囲内でいちばん安くなる IC から範囲外の IC までの料金
        速旅の「発着付き」プランは、往路は周遊エリア内のどこかの IC で一度出る・復路は周遊エリア内の IC から入る決まりがあり、
        範囲の外まで通しで走るとプラン全体が適用されない。範囲の外まで走る区間があると結果の注意に出るので、必ず伝える。

        SA・PA 電子お買物券付プランや観光パスポート付プランのように、セット料金にお買物券などが含まれるときは、
        pass_price にセット料金、included_value に実際に使う分の金額を渡す（その分を差し引いた実質の料金でも比べる）。

        area には乗り放題の範囲を道路と端の IC で渡す。範囲は地図でしか示されていないことが多いので、
        先に list_drive_passes でプランの地図画像を見て読み取る。
        例: 関西エリア周遊ドライブパス（京都・兵庫）→ pass_price=6300, pass_days=2, outside_rule="cheapest_ic",
            area=[{road:"中国道", from:"中国吹田", to:"佐用"}, {road:"舞鶴若狭道"}, {road:"京都縦貫道"},
                  {road:"山陰近畿道", from:"与謝天橋立", to:"京丹後大宮"}, {road:"名神", from:"吹田（名神高速）", to:"八日市"}]
        例: 速旅 SA・PA 電子お買物券付 M8（首都圏⇒愛知）→ pass_price=16000, included_value=5000, pass_days=3,
            area=[{road:"東名"}, {road:"新東名"}, {road:"伊勢湾岸自動車道"}, {road:"伊勢湾岸道路"},
                  {road:"東名阪", from:"名古屋西", to:"四日市東"}, {road:"新名神", from:"起点", to:"菰野"},
                  {road:"名神", from:"小牧", to:"彦根"}]
        """)]
    public async Task<object> CompareDrivePass(
        [Description("旅程の区間（高速道路に乗る区間ごと。一般道だけの移動は入れない）")] TripLeg[] legs,
        [Description("プランの料金（その車種のセット料金・販売価格、円）")] int pass_price,
        [Description("乗り放題の範囲")] PassArea[] area,
        [Description("プランの名前（結果の表示用）")] string? pass_name = null,
        [Description("プランの利用日数（連続する最大日数）。旅程の日数がこれを超えると注意を出す")] int? pass_days = null,
        [Description("セット料金に含まれるお買物券・観光パスポートなどのうち、実際に使う分の金額（円）。その分を差し引いた実質の料金でも比べる")] int included_value = 0,
        [Description("範囲の外から入る（外へ出る）区間の範囲外の分の決め方: route_edge=ルート上の範囲の端の IC から（既定。速旅など）, cheapest_ic=範囲内でいちばん安い IC から（みち旅）")] string outside_rule = "route_edge",
        [Description("車種: kei=軽・自動二輪, normal=普通車（既定）, medium=中型車, large=大型車, extra_large=特大車")] string car_type = "normal",
        [Description("区間ごとのルートの選び方: etc_toll=ETC 料金が安いルート（既定）, time=時間が短いルート, distance=距離が短いルート")] string sort = "etc_toll",
        [Description("料金を調べるサイト: auto=NEXCO 中日本・東日本の区間を通る区間はその会社のサイト、それ以外は NEXCO 西日本（既定）, west=NEXCO 西日本, central=NEXCO 中日本のドライブコンパス, east=NEXCO 東日本のドラぷら")] string source = "auto",
        CancellationToken ct = default)
    {
        if (legs is not { Length: > 0 }) throw new McpException("legs（旅程の区間）を 1 つ以上指定してください。");
        if (area is not { Length: > 0 }) throw new McpException("area（乗り放題の範囲）を 1 つ以上指定してください。");
        if (pass_price < 0) throw new McpException("pass_price は 0 以上で指定してください。");
        if (included_value < 0 || included_value > pass_price) throw new McpException("included_value は 0 以上、pass_price 以下で指定してください。");

        var routeEdge = NexcoTools.ParseOption(outside_rule, nameof(outside_rule), new Dictionary<string, bool>
        {
            ["route_edge"] = true,
            ["cheapest_ic"] = false,
        });
        var carType = NexcoTools.ParseOption(car_type, nameof(car_type), new Dictionary<string, int>
        {
            ["kei"] = 1,
            ["normal"] = 2,
            ["medium"] = 3,
            ["large"] = 4,
            ["extra_large"] = 5,
        });
        var priority = NexcoTools.ParseOption(sort, nameof(sort), new Dictionary<string, int>
        {
            ["etc_toll"] = 9,
            ["time"] = 2,
            ["distance"] = 1,
        });

        var routeSource = RouteSearcher.ParseSource(source);

        var master = await client.GetMasterAsync(ct);
        var rules = area.Select(a => AreaRule.Resolve(master, a)).ToList();
        bool Inside(string? code) => code is { Length: 7 } && rules.Any(r => r.Contains(code));

        var notes = new List<string>();
        var legResults = new List<object>();
        int normalTotal = 0, extraTotal = 0, coveredTotal = 0;
        var dates = new HashSet<DateOnly>();
        // 範囲の外まで通しで走る区間で、範囲外の分の起点にした範囲の端の IC
        var edgeIcs = new List<string>();

        foreach (var leg in legs)
        {
            var fromIc = master.Resolve(leg.From, "出発 IC");
            var toIc = master.Resolve(leg.To, "到着 IC");
            var viaIcs = (leg.Via ?? []).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => master.Resolve(v, "通過 IC")).ToList();
            if (string.IsNullOrWhiteSpace(leg.Date)) throw new McpException($"{fromIc.Name} → {toIc.Name} の date を指定してください。");
            var dateTime = NexcoTools.ParseDateTime(leg.Date, leg.Time ?? "09:00");
            dates.Add(DateOnly.FromDateTime(dateTime));

            // 1 本だけ調べるので、auto の判定は any でも first でも同じ
            var searched = await searcher.SearchAsync(new RouteQuery(
                fromIc.Code, toIc.Code, viaIcs.Select(i => i.Code).ToList(), [], dateTime, false, carType, priority, 1), routeSource, AutoRule.Any, ct);
            var route = searched.Routes.Count > 0 ? searched.Routes[0].Route : default;
            var total = Json.Obj(route, "Total") ?? default;
            // 地点ごとの IC と、その地点から次の地点までの道路
            var points = Json.Obj(route, "Detail") is { } detail
                ? detail.EnumerateObject().Select(p => new RoutePoint(
                    Json.Obj(p.Value, "LineIc") ?? default,
                    Json.Obj(p.Value, "LineRoad") is { } road ? Json.Str(road, "Code") : null)).ToList()
                : [];

            var tolls = new List<object>();
            for (var i = 0; i < points.Count; i++)
            {
                var point = points[i].Ic;
                if (Json.Int(point, "ChargeSelect") is not { } etc) continue;

                // 料金がかかる区間の起点は名前だけで来るので、ルートのそれより前の地点から IC コードを引く
                var startName = Json.Str(point, "ChargeStartIcJct") ?? "";
                var startIndex = points.FindLastIndex(i, p => Json.Str(p.Ic, "Ic") == startName);
                var startCode = startIndex >= 0 ? points[startIndex].Code : null;
                var endName = Json.Str(point, "Ic") ?? "";
                var endCode = points[i].Code;
                var startIn = Inside(startCode);
                var endIn = Inside(endCode);

                normalTotal += etc;
                string status;
                int payable;
                string? detailNote = null;
                if (startIn && endIn)
                {
                    status = "範囲内";
                    payable = 0;
                    coveredTotal += etc;
                }
                else if (!startIn && !endIn)
                {
                    // 範囲を通り抜けるだけの区間も、プランの対象にならない
                    status = "範囲外";
                    payable = etc;
                }
                else
                {
                    status = "範囲の外と中にまたがる";
                    var outsideCode = startIn ? endCode : startCode;
                    var segment = points.Skip(Math.Max(startIndex, 0)).Take(i - Math.Max(startIndex, 0) + 1).ToList();
                    var edge = routeEdge ? FindRouteEdge(master, rules, segment, exiting: startIn) : null;
                    var (cost, via) = edge is not null
                        ? (await TollAsync(outsideCode, edge, outsideIsStart: !startIn, dateTime, carType, routeSource, ct), edge.Name)
                        : await CheapestToAreaAsync(master, rules, outsideCode, outsideIsStart: !startIn,
                            segment.Select(p => p.Code).Where(Inside).OfType<string>(), dateTime, carType, routeSource, ct);
                    if (cost is { } c)
                    {
                        payable = Math.Min(c, etc);
                        detailNote = $"範囲外の分は {(startIn ? $"{via} → {endName}" : $"{startName} → {via}")} の料金で計算"
                            + (routeEdge && edge is null ? "（ルート上の範囲の端を決められなかったので、範囲内でいちばん安い IC からとした）" : "");
                        coveredTotal += etc - payable;
                        if (via is not null) edgeIcs.Add(via);
                    }
                    else
                    {
                        payable = etc;
                        detailNote = "範囲内の IC までの料金を出せなかったので、全額を範囲外として計算";
                    }
                }
                extraTotal += payable;
                tolls.Add(new
                {
                    Section = $"{startName} → {endName}",
                    Etc = etc,
                    EtcDiscount = Json.Str(point, "ChargeSelectName") is { Length: > 0 } d ? d : null,
                    Status = status,
                    PayableWithPass = payable,
                    Note = detailNote,
                });
            }

            legResults.Add(new
            {
                Leg = $"{fromIc.Name} → {toIc.Name}",
                Departure = $"{dateTime:yyyy-MM-dd HH:mm}",
                Site = searched.Routes.Count > 0 ? searched.Routes[0].Site : null,
                Roads = Json.Array(total, "RoadList").Select(x => x.GetString()).ToList(),
                DistanceKm = Json.Double(total, "DispTotalDist"),
                Time = Json.Str(total, "DispTotalTime"),
                NormalEtc = Json.Int(total, "TotalChargeETC"),
                Tolls = tolls,
            });
        }

        var passTotal = pass_price + extraTotal;
        var effectiveTotal = passTotal - included_value;
        if (pass_days is { } days && dates.Count > 0 && (dates.Max().DayNumber - dates.Min().DayNumber + 1) > days)
            notes.Add($"旅程が {dates.Max().DayNumber - dates.Min().DayNumber + 1} 日にわたり、プランの利用日数（{days} 日）を超えている。超えた日の走行は通常料金になるので、プランを使う日だけで比べ直すこと。");
        if (edgeIcs.Count > 0)
        {
            notes.Add($"範囲の外まで走る区間がある。範囲外の分は {string.Join("・", edgeIcs.Distinct())} からの料金で計算した。"
                + "速旅の「発着付き」プランなどは、往路は周遊エリア内のどこかの IC で一度出て、復路は周遊エリア内の IC から入る必要があり"
                + "（乗り直す IC は範囲内ならどこでもよい）、範囲の外まで通しで走ると往路・復路として認められず、プラン全体が適用されない（すべて通常料金になる）。"
                + "プランの利用方法の決まりを確かめること。");
        }
        if (included_value > 0)
            notes.Add($"プランの料金は、セットに含まれるお買物券などを {included_value:N0} 円分使う前提で、その分を差し引いた実質の料金でも比べた。使わなければ実質の料金は上がる。");
        notes.Add("範囲の判定は area に渡した道路と IC で行った。area の範囲内の IC がプランの地図と合っているか確かめること。");
        notes.Add("通常料金と、範囲外の分の料金には、その日の ETC 割引（休日・深夜など）が入っている。プランを使う区間には他の割引は付かず、走った料金がプラン料金に満たなくても差額は返らない。");
        notes.Add("利用できない期間（GW・お盆・3 連休など）や申込回数の上限は、list_drive_passes の詳細で確かめること。");

        static string Compare(int diff) => diff switch
        {
            > 0 => $"通常の方が {diff:N0} 円安い（範囲内の通常料金があと {diff:N0} 円分多ければプランと同額）",
            < 0 => $"プランの方が {-diff:N0} 円安い",
            _ => "同額",
        };

        return new
        {
            Pass = pass_name,
            PassPrice = pass_price,
            IncludedValue = included_value > 0 ? included_value : (int?)null,
            Area = rules.Select(r => r.Description).ToList(),
            CarType = NexcoTools.CarTypeName(carType),
            Legs = legResults,
            NormalTotal = normalTotal,
            PassTotal = passTotal,
            PassEffectiveTotal = included_value > 0 ? effectiveTotal : (int?)null,
            PassExtra = extraTotal,
            Result = included_value > 0
                ? $"お買物券などを使う場合（実質 {effectiveTotal:N0} 円）: {Compare(effectiveTotal - normalTotal)}。使わない場合（{passTotal:N0} 円）: {Compare(passTotal - normalTotal)}"
                : Compare(passTotal - normalTotal),
            CoveredNormalTotal = coveredTotal,
            Notes = notes,
        };
    }

    private sealed record RoutePoint(JsonElement Ic, string? NextRoad)
    {
        public string? Code => Json.Str(Ic, "Code");
    }

    /// <summary>
    /// ルート上で範囲の境目になる IC を探す（速旅の「周遊エリア外の IC から周遊エリア末端の IC まで」）。
    /// IC コードの先頭 4 桁が道路、残り 3 桁が道路上の位置なので、境目の道路の上で、範囲外の側にいちばん近い範囲内の IC を選ぶ。
    /// </summary>
    private static Ic? FindRouteEdge(IcMaster master, List<AreaRule> rules, List<RoutePoint> segment, bool exiting)
    {
        // 出る区間は進む向きに、入る区間は逆向きに見ると、どちらも「範囲内 → 範囲外」の並びになる
        var seq = exiting ? segment : Enumerable.Reverse(segment).ToList();
        var last = seq.FindLastIndex(p => p.Code is { Length: 7 } c && rules.Any(r => r.Contains(c)));
        if (last < 0 || last + 1 >= seq.Count) return null;
        var outsidePoint = seq[last + 1];

        // 境目をまたぐ区間の道路（出るときは範囲内の地点の次の道路、入るときは範囲外の地点の次の道路）
        var crossingRoad = exiting ? seq[last].NextRoad : outsidePoint.NextRoad;
        if (crossingRoad is not null && InsideIcs(master, rules, crossingRoad) is { Count: > 0 } onCrossing
            && Position(outsidePoint.Code, crossingRoad) is { } outsidePos)
        {
            // 範囲の外の地点がその道路の上にあれば、そこにいちばん近い範囲内の IC が端
            return onCrossing.MinBy(ic => Math.Abs(Position(ic.Code, crossingRoad)!.Value - outsidePos));
        }

        // 範囲内の道路から JCT で範囲外の道路に移るときは、手前の道路を進む向きにいちばん先の範囲内の IC が端
        var approachRoad = exiting ? (last > 0 ? seq[last - 1].NextRoad : null) : seq[last].NextRoad;
        if (approachRoad is null || InsideIcs(master, rules, approachRoad) is not { Count: > 0 } onApproach) return null;
        var positions = seq.Take(last + 1).Select(p => Position(p.Code, approachRoad)).OfType<int>().ToList();
        if (positions.Count < 2 || positions[^1] == positions[0]) return null;
        // 入る区間は逆向きに並べているので、実際の進む向きとは逆になるが、「範囲外に近い側」を選ぶことは同じ
        return positions[^1] > positions[0]
            ? onApproach.MaxBy(ic => Position(ic.Code, approachRoad))
            : onApproach.MinBy(ic => Position(ic.Code, approachRoad));
    }

    private static List<Ic> InsideIcs(IcMaster master, List<AreaRule> rules, string roadCode) =>
        master.Ics.Where(ic => ic.Code.StartsWith(roadCode, StringComparison.Ordinal) && rules.Any(r => r.Contains(ic.Code))).ToList();

    private static int? Position(string? code, string roadCode) =>
        code is { Length: 7 } && code.StartsWith(roadCode, StringComparison.Ordinal) && int.TryParse(code[4..], out var pos) ? pos : null;

    private async Task<int?> TollAsync(string? outsideCode, Ic edge, bool outsideIsStart, DateTime dateTime, int carType, RouteSource source, CancellationToken ct)
    {
        if (outsideCode is null) return null;
        try
        {
            var q = outsideIsStart
                ? new RouteQuery(outsideCode, edge.Code, [], [], dateTime, false, carType, 9, 1)
                : new RouteQuery(edge.Code, outsideCode, [], [], dateTime, false, carType, 9, 1);
            RouteSearchResult result;
            try
            {
                result = await searcher.SearchAsync(q, source, AutoRule.Any, ct);
            }
            catch (McpException) when (source != RouteSource.West)
            {
                // 本線料金所のように、ドライブコンパス・ドラぷらでは検索に使えない地点もあるので、NEXCO 西日本で調べ直す
                result = await searcher.SearchAsync(q, RouteSource.West, AutoRule.Any, ct);
            }
            return result.Routes.Count > 0 ? Json.Int(Json.Obj(result.Routes[0].Route, "Total") ?? default, "TotalChargeETC") : null;
        }
        catch (McpException)
        {
            return null;
        }
    }

    /// <summary>
    /// 範囲の外の IC と範囲内の IC の間でいちばん安い料金を探す（みち旅の「範囲内でいちばん安い IC までの料金は別途」に合わせる）。
    /// 候補は範囲の端の IC と、そのルートで範囲内にある地点のうち IC として引けるもの。
    /// </summary>
    private async Task<(int? Cost, string? Via)> CheapestToAreaAsync(
        IcMaster master, List<AreaRule> rules, string? outsideCode, bool outsideIsStart, IEnumerable<string> routeInsideCodes,
        DateTime dateTime, int carType, RouteSource source, CancellationToken ct)
    {
        if (outsideCode is null || master.FindByCode(outsideCode) is null) return (null, null);

        var candidates = rules.SelectMany(r => r.Bounds)
            .Concat(routeInsideCodes.Select(master.FindByCode).OfType<Ic>())
            .DistinctBy(c => c.Code)
            .Take(8)
            .ToList();

        (int? Cost, string? Via) best = (null, null);
        foreach (var c in candidates)
        {
            if (await TollAsync(outsideCode, c, outsideIsStart, dateTime, carType, source, ct) is { } cost && (best.Cost is null || cost < best.Cost))
                best = (cost, c.Name);
        }
        return best;
    }

    /// <summary>乗り放題の範囲の 1 本の道路。IC コードの先頭 4 桁が道路、残り 3 桁が道路上の位置なので、それで範囲内かを判定する。</summary>
    private sealed record AreaRule(string RoadCode, string Description, int? Min, int? Max, IReadOnlyList<Ic> Bounds)
    {
        public bool Contains(string code)
        {
            if (!code.StartsWith(RoadCode, StringComparison.Ordinal) || !int.TryParse(code[4..], out var pos)) return false;
            return (Min is null || pos >= Min) && (Max is null || pos <= Max);
        }

        public static AreaRule Resolve(IcMaster master, PassArea a)
        {
            if (string.IsNullOrWhiteSpace(a.Road)) throw new McpException("area の road を指定してください。");
            var road = master.ResolveRoad(a.Road);
            var given = new[] { a.From, a.To }.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b!.Trim()).ToList();

            // 端が片方だけだと、どちら向きに道路の終わりまでなのかが決まらない
            if (given.Count == 1)
                throw new McpException($"{road.Name} の範囲は、端を from と to の両方で指定するか（JCT で終わる側は「起点」「終点」）、道路全体なら両方とも省いてください。");
            if (given.Count == 0)
                return new AreaRule(road.Code, $"{road.Name}（全線）", null, null, []);

            var bounds = new List<Ic>();
            var positions = new List<int>();
            var names = new List<string>();
            foreach (var b in given)
            {
                // 「起点」「終点」は道路の端（IC コードの位置の最小・最大の外側）
                if (b is "起点" or "終点")
                {
                    positions.Add(b == "起点" ? 0 : 999);
                    names.Add(b);
                    continue;
                }
                var ic = master.Resolve(b, $"{road.Name} の範囲の端");
                if (!ic.Code.StartsWith(road.Code, StringComparison.Ordinal))
                    throw new McpException($"{ic.Name}（{ic.Road}）は {road.Name} 上の IC ではありません。範囲の端にはその道路上の IC か「起点」「終点」を指定してください。");
                bounds.Add(ic);
                positions.Add(int.Parse(ic.Code[4..]));
                names.Add(ic.Name);
            }

            int min = positions.Min(), max = positions.Max();
            // 指定の間違いに気づけるよう、範囲に入る IC を並べておく
            var inside = master.Ics
                .Where(ic => ic.Code.StartsWith(road.Code, StringComparison.Ordinal) && int.Parse(ic.Code[4..]) is var p && p >= min && p <= max)
                .OrderBy(ic => ic.Code, StringComparer.Ordinal)
                .Select(ic => ic.Name)
                .ToList();
            var list = inside.Count <= 8 ? string.Join("・", inside) : $"{string.Join("・", inside.Take(4))} … {string.Join("・", inside.TakeLast(3))}（{inside.Count} IC）";
            return new AreaRule(road.Code, $"{road.Name}（{names[0]}〜{names[1]}。範囲内の IC: {list}）", min, max, bounds);
        }
    }
}
