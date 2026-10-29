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

    [Description("範囲の端の IC（その道路上の IC）。from と to の間が範囲。道路全体なら from も to も省く")]
    public string? From { get; set; }

    [Description("範囲のもう一方の端の IC（その道路上の IC）")]
    public string? To { get; set; }
}

[McpServerToolType]
public sealed class DrivePassTools(NexcoClient client, DrivePassClient passes)
{
    [McpServerTool(Name = "list_drive_passes", ReadOnly = true, Idempotent = true)]
    [Description("""
        NEXCO 3 社の周遊割引プラン（高速道路の乗り放題パス）を調べる。みち旅（NEXCO 西日本のドライブパス）、
        速旅（NEXCO 中日本のドライブプラン）、ドラ割（NEXCO 東日本のフリーパス・周遊プラン）が対象。

        plan を省くとプランの一覧（名前・URL）を返す。plan にプラン名か URL を渡すと、詳細ページの本文
        （料金・利用日数・利用できない期間・申込条件など）と、乗り放題の範囲の地図画像を返す。
        範囲は地図でしか示されていないことが多いので、画像を見て道路と端の IC を読み取り、compare_drive_pass の area に渡す。
        """)]
    public async Task<CallToolResult> ListDrivePasses(
        [Description("プラン名（一部でもよい）または詳細ページの URL。省略時は一覧を返す")] string? plan = null,
        [Description("一覧を絞り込むサイト: michitabi=みち旅（西日本）, hayatabi=速旅（中日本）, drawari=ドラ割（東日本）。省略時は全部")] string? site = null,
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

        var detail = await passes.GetDetailAsync(plan, ct);
        var text = $"""
            # {detail.Name}
            {detail.Url}

            {detail.Text}

            乗り放題の範囲の地図: {(detail.Maps.Count > 0 ? string.Join("、", detail.Maps.Select(m => m.Alt is { Length: > 0 } alt ? alt : "（説明なし）")) + "（この後に画像で続く）" : "見つからなかった。詳細ページを開いて確かめること")}
            """;
        var content = new List<ContentBlock> { new TextContentBlock { Text = text } };
        content.AddRange(detail.Maps.Select(m => ImageContentBlock.FromBytes(m.Data, m.MimeType)));
        return new CallToolResult { Content = content };
    }

    [McpServerTool(Name = "compare_drive_pass", ReadOnly = true, Idempotent = true)]
    [Description("""
        旅程（IC から IC への区間の並び）について、通常の ETC 料金と、周遊割引プラン（乗り放題パス）を使った場合の料金を比べる。

        通常料金は区間ごとに search_highway_route と同じ検索で出す（その日の休日割引・深夜割引も入る）。
        プランを使った場合は、プラン料金に、乗り放題の範囲外を走る分の料金を足す。範囲の外から範囲内へ入る（出る）区間は、
        プランの決まりに合わせて、範囲内でいちばん安くなる IC までの料金を別途かかる分として計算する。

        area には乗り放題の範囲を道路と端の IC で渡す。範囲は地図でしか示されていないことが多いので、
        先に list_drive_passes でプランの地図画像を見て読み取る。
        例: 関西エリア周遊ドライブパス（京都・兵庫）→ pass_price=6300, pass_days=2,
            area=[{road:"中国道", from:"中国吹田", to:"佐用"}, {road:"舞鶴若狭道"}, {road:"京都縦貫道"},
                  {road:"山陰近畿道", from:"与謝天橋立", to:"京丹後大宮"}, {road:"名神", from:"吹田（名神高速）", to:"八日市"}]
        """)]
    public async Task<object> CompareDrivePass(
        [Description("旅程の区間（高速道路に乗る区間ごと。一般道だけの移動は入れない）")] TripLeg[] legs,
        [Description("プランの料金（その車種の販売価格、円）")] int pass_price,
        [Description("乗り放題の範囲")] PassArea[] area,
        [Description("プランの名前（結果の表示用）")] string? pass_name = null,
        [Description("プランの利用日数（連続する最大日数）。旅程の日数がこれを超えると注意を出す")] int? pass_days = null,
        [Description("車種: kei=軽・自動二輪, normal=普通車（既定）, medium=中型車, large=大型車, extra_large=特大車")] string car_type = "normal",
        [Description("区間ごとのルートの選び方: etc_toll=ETC 料金が安いルート（既定）, time=時間が短いルート, distance=距離が短いルート")] string sort = "etc_toll",
        CancellationToken ct = default)
    {
        if (legs is not { Length: > 0 }) throw new McpException("legs（旅程の区間）を 1 つ以上指定してください。");
        if (area is not { Length: > 0 }) throw new McpException("area（乗り放題の範囲）を 1 つ以上指定してください。");
        if (pass_price < 0) throw new McpException("pass_price は 0 以上で指定してください。");

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

        var master = await client.GetMasterAsync(ct);
        var rules = area.Select(a => AreaRule.Resolve(master, a)).ToList();
        bool Inside(string? code) => code is { Length: 7 } && rules.Any(r => r.Contains(code));

        var notes = new List<string>();
        var legResults = new List<object>();
        int normalTotal = 0, extraTotal = 0, coveredTotal = 0;
        var dates = new HashSet<DateOnly>();

        foreach (var leg in legs)
        {
            var fromIc = master.Resolve(leg.From, "出発 IC");
            var toIc = master.Resolve(leg.To, "到着 IC");
            var viaIcs = (leg.Via ?? []).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => master.Resolve(v, "通過 IC")).ToList();
            if (string.IsNullOrWhiteSpace(leg.Date)) throw new McpException($"{fromIc.Name} → {toIc.Name} の date を指定してください。");
            var dateTime = NexcoTools.ParseDateTime(leg.Date, leg.Time ?? "09:00");
            dates.Add(DateOnly.FromDateTime(dateTime));

            var result = await client.SearchRouteAsync(new RouteQuery(
                fromIc.Code, toIc.Code, viaIcs.Select(i => i.Code).ToList(), [], dateTime, false, carType, priority, 1), ct);
            var route = Json.Array(result, "Detail").FirstOrDefault();
            var total = Json.Obj(route, "Total") ?? default;
            var points = Json.Obj(route, "Detail") is { } detail
                ? detail.EnumerateObject().Select(p => Json.Obj(p.Value, "LineIc") ?? default).ToList()
                : [];

            var tolls = new List<object>();
            for (var i = 0; i < points.Count; i++)
            {
                var point = points[i];
                if (Json.Int(point, "ChargeSelect") is not { } etc) continue;

                // 料金がかかる区間の起点は名前だけで来るので、ルートのそれより前の地点から IC コードを引く
                var startName = Json.Str(point, "ChargeStartIcJct") ?? "";
                var startCode = points.Take(i).LastOrDefault(p => Json.Str(p, "Ic") == startName) is { ValueKind: JsonValueKind.Object } sp
                    ? Json.Str(sp, "Code")
                    : null;
                var endName = Json.Str(point, "Ic") ?? "";
                var endCode = Json.Str(point, "Code");
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
                    var (cost, via) = await CheapestToAreaAsync(master, rules, outsideCode, outsideIsStart: !startIn,
                        points.Skip(points.FindIndex(p => Json.Str(p, "Code") == startCode)).Take(i + 1).Select(p => Json.Str(p, "Code")).Where(Inside).OfType<string>(),
                        dateTime, carType, ct);
                    if (cost is { } c)
                    {
                        payable = Math.Min(c, etc);
                        detailNote = $"範囲外の分は {(startIn ? $"{via} → {endName}" : $"{startName} → {via}")} の料金で計算";
                        coveredTotal += etc - payable;
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
                Roads = Json.Array(total, "RoadList").Select(x => x.GetString()).ToList(),
                DistanceKm = Json.Double(total, "DispTotalDist"),
                Time = Json.Str(total, "DispTotalTime"),
                NormalEtc = Json.Int(total, "TotalChargeETC"),
                Tolls = tolls,
            });
        }

        var passTotal = pass_price + extraTotal;
        var diff = passTotal - normalTotal;
        if (pass_days is { } days && dates.Count > 0 && (dates.Max().DayNumber - dates.Min().DayNumber + 1) > days)
            notes.Add($"旅程が {dates.Max().DayNumber - dates.Min().DayNumber + 1} 日にわたり、プランの利用日数（{days} 日）を超えている。超えた日の走行は通常料金になるので、プランを使う日だけで比べ直すこと。");
        notes.Add("範囲の判定は area に渡した道路と IC で行った。プランの地図や条件と合っているか確かめること。");
        notes.Add("通常料金と、範囲外の分の料金には、その日の ETC 割引（休日・深夜など）が入っている。プランを使う区間には他の割引は付かず、走った料金がプラン料金に満たなくても差額は返らない。");
        notes.Add("利用できない期間（GW・お盆・3 連休など）や申込回数の上限は、list_drive_passes の詳細で確かめること。");

        return new
        {
            Pass = pass_name,
            PassPrice = pass_price,
            Area = rules.Select(r => r.Description).ToList(),
            CarType = NexcoTools.CarTypeName(carType),
            Legs = legResults,
            NormalTotal = normalTotal,
            PassTotal = passTotal,
            PassExtra = extraTotal,
            Result = diff switch
            {
                > 0 => $"通常の方が {diff:N0} 円安い（範囲内の通常料金があと {diff:N0} 円分多ければプランと同額）",
                < 0 => $"プランの方が {-diff:N0} 円安い",
                _ => "同額",
            },
            CoveredNormalTotal = coveredTotal,
            Notes = notes,
        };
    }

    /// <summary>
    /// 範囲の外の IC と範囲内の IC の間でいちばん安い料金を探す（プランの「範囲内でいちばん安い IC までの料金は別途」に合わせる）。
    /// 候補は範囲の端の IC と、そのルートで範囲内にある地点のうち IC として引けるもの。
    /// </summary>
    private async Task<(int? Cost, string? Via)> CheapestToAreaAsync(
        IcMaster master, List<AreaRule> rules, string? outsideCode, bool outsideIsStart, IEnumerable<string> routeInsideCodes,
        DateTime dateTime, int carType, CancellationToken ct)
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
            try
            {
                var q = outsideIsStart
                    ? new RouteQuery(outsideCode, c.Code, [], [], dateTime, false, carType, 9, 1)
                    : new RouteQuery(c.Code, outsideCode, [], [], dateTime, false, carType, 9, 1);
                var result = await client.SearchRouteAsync(q, ct);
                var total = Json.Obj(Json.Array(result, "Detail").FirstOrDefault(), "Total") ?? default;
                if (Json.Int(total, "TotalChargeETC") is { } cost && (best.Cost is null || cost < best.Cost))
                    best = (cost, c.Name);
            }
            catch (McpException)
            {
                // 経路が見つからない候補は飛ばす
            }
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
            var bounds = new[] { a.From, a.To }
                .Where(b => !string.IsNullOrWhiteSpace(b))
                .Select(b => master.Resolve(b!, $"{road.Name} の範囲の端"))
                .ToList();
            foreach (var b in bounds)
            {
                if (!b.Code.StartsWith(road.Code, StringComparison.Ordinal))
                    throw new McpException($"{b.Name}（{b.Road}）は {road.Name} 上の IC ではありません。範囲の端にはその道路上の IC を指定してください。");
            }

            // 端が片方だけだと、どちら向きに道路の終わりまでなのかが決まらない
            if (bounds.Count == 1)
                throw new McpException($"{road.Name} の範囲は、端の IC を from と to の両方で指定するか、道路全体なら両方とも省いてください。");

            var positions = bounds.Select(b => int.Parse(b.Code[4..])).ToList();
            return bounds.Count == 2
                ? new AreaRule(road.Code, $"{road.Name}（{bounds[0].Name}〜{bounds[1].Name}）", positions.Min(), positions.Max(), bounds)
                : new AreaRule(road.Code, $"{road.Name}（全線）", null, null, bounds);
        }
    }
}
