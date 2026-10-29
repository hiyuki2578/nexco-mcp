using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol;

namespace NexcoMcp;

public sealed record Road(string Code, string Name);

public sealed record Ic(string Name, string Yomi, string Code, string Prefecture, string Road, string? Note);

/// <summary>
/// サイトの route_master_vNNN.js に入っている IC・道路・都道府県の一覧。
/// icM の 1 件は [名前, 読み, IC コード, 都道府県コード, 道路（rdM の添字）, 注意書き（HTML）, 表示順] の形。
/// </summary>
public sealed partial class IcMaster
{
    public IReadOnlyList<Ic> Ics { get; }
    public IReadOnlyList<Road> Roads { get; }

    private readonly Dictionary<string, Ic> _byCode;

    private IcMaster(IReadOnlyList<Ic> ics, IReadOnlyList<Road> roads)
    {
        Ics = ics;
        Roads = roads;
        _byCode = ics.GroupBy(i => i.Code).ToDictionary(g => g.Key, g => g.First());
    }

    public static IcMaster Parse(string script)
    {
        var rdM = JsLiteral.ParseArrayVariable(script, "rdM");
        var icM = JsLiteral.ParseArrayVariable(script, "icM");
        var preM = JsLiteral.ParseArrayVariable(script, "preM");

        var roads = rdM.Select(r => (List<object>)r).Select(r => new Road((string)r[0], (string)r[1])).ToList();
        var prefectures = preM.Select(p => (List<object>)p).ToDictionary(p => (string)p[0], p => (string)p[1]);
        var ics = icM.Select(i => (List<object>)i).Select(i =>
        {
            var roadIndex = int.TryParse((string)i[4], CultureInfo.InvariantCulture, out var n) ? n : -1;
            return new Ic(
                Name: (string)i[0],
                Yomi: (string)i[1],
                Code: (string)i[2],
                Prefecture: prefectures.GetValueOrDefault((string)i[3], ""),
                Road: roadIndex >= 0 && roadIndex < roads.Count ? roads[roadIndex].Name : "",
                Note: StripHtml((string)i[5]));
        }).ToList();

        if (ics.Count == 0 || roads.Count == 0) throw new McpException("IC の一覧を読めませんでした。サイトが改修された可能性があります。");
        return new IcMaster(ics, roads);
    }

    public Ic? FindByCode(string code) => _byCode.GetValueOrDefault(code);

    /// <summary>IC 名（または IC コード）を 1 つの IC に決める。決められなければ候補を添えてエラーにする。</summary>
    public Ic Resolve(string query, string role)
    {
        var q = query.Trim();
        if (FindByCode(q) is { } byCode) return byCode;

        var candidates = Search(q, null, null);
        if (candidates.Count == 0)
            throw new McpException($"{role}「{query}」という IC が見つかりません。search_ics で名前を調べてください（NEXCO 西日本のサイトにない IC は使えません）。");

        // いちばん強い一致の段階に 1 件だけなら、それに決める
        var best = candidates.Where(c => c.Rank == candidates[0].Rank).ToList();
        if (best.Count == 1) return best[0].Ic;

        var list = string.Join("\n", best.Take(10).Select(c => "- " + Describe(c.Ic)));
        throw new McpException($"{role}「{query}」に当てはまる IC が複数あります。次のどれかの名前か IC コードを指定してください。\n{list}");
    }

    public static string Describe(Ic ic) =>
        $"{ic.Name}（{ic.Code}、{ic.Prefecture}、{ic.Road}）" + (ic.Note is { Length: > 0 } note ? $" {note}" : "");

    /// <summary>名前・読みで IC を探し、一致の強い順に並べる（0: 完全一致, 1: 括弧の前・読みが一致, 2: 前方一致, 3: 部分一致）。</summary>
    public List<(Ic Ic, int Rank)> Search(string query, string? prefecture, string? road)
    {
        var q = NormalizeIcQuery(query);
        if (q.Length == 0) return [];
        var qYomi = ToHiragana(q);
        var pref = prefecture is { Length: > 0 } ? Normalize(prefecture) : null;
        var roadQ = road is { Length: > 0 } ? NormalizeRoad(road) : null;

        var result = new List<(Ic, int)>();
        foreach (var ic in Ics)
        {
            if (pref is not null && !Normalize(ic.Prefecture).Contains(pref)) continue;
            if (roadQ is not null && !NormalizeRoad(ic.Road).Contains(roadQ)) continue;

            var name = Normalize(ic.Name);
            var baseName = Normalize(BaseName(ic.Name));
            int? rank =
                name == q ? 0
                : baseName == q || ic.Yomi == qYomi ? 1
                : name.StartsWith(q, StringComparison.Ordinal) || ic.Yomi.StartsWith(qYomi, StringComparison.Ordinal) ? 2
                : name.Contains(q, StringComparison.Ordinal) ? 3
                : null;
            if (rank is { } r) result.Add((ic, r));
        }
        return result.OrderBy(x => x.Item2).ThenBy(x => x.Item1.Yomi, StringComparer.Ordinal).ToList();
    }

    /// <summary>道路名を道路コードに決める。「一般道」は 9999。</summary>
    public Road ResolveRoad(string query)
    {
        var q = NormalizeRoad(query);
        if (q is "一般道" or "一般道路") return new Road("9999", "一般道");
        if (Roads.FirstOrDefault(r => r.Code == q) is { } byCode) return byCode;

        var hits = Roads.Where(r => NormalizeRoad(r.Name).Contains(q)).ToList();
        // 「E2 山陽自動車道」に対する「山陽道」のように、番号を除いた名前が一致するものを優先する
        var exact = hits.Where(r => NormalizeRoad(RoadNumberPattern().Replace(r.Name, "")) == q || NormalizeRoad(r.Name) == q).ToList();
        if (exact.Count == 1) return exact[0];
        // 「名神」と「E1 名神高速道路」のように、末尾の「高速」「道」を除いた名前でも比べる（「新名神」とは区別できる）
        var core = RoadCore(q);
        var coreHits = hits.Where(r => RoadCore(NormalizeRoad(RoadNumberPattern().Replace(r.Name, ""))) == core).ToList();
        if (coreHits.Count == 1) return coreHits[0];
        if (hits.Count == 1) return hits[0];
        if (hits.Count == 0)
            throw new McpException($"「{query}」という道路が見つかりません。search_roads で名前を調べてください。");
        var list = string.Join("\n", hits.Take(15).Select(r => $"- {r.Name}（{r.Code}）"));
        throw new McpException($"「{query}」に当てはまる道路が複数あります。次のどれかを指定してください。\n{list}");
    }

    public List<Road> SearchRoads(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Roads.ToList();
        var q = NormalizeRoad(query);
        return Roads.Where(r => NormalizeRoad(r.Name).Contains(q) || r.Code == q).ToList();
    }

    // ---- 表記ゆれの吸収 ----

    [GeneratedRegex(@"^[A-Z]\d+[A-Z]?\s*")]
    private static partial Regex RoadNumberPattern();

    [GeneratedRegex(@"(IC|インターチェンジ|インター)$")]
    private static partial Regex IcSuffixPattern();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex HtmlTagPattern();

    /// <summary>全角半角をそろえ（NFKC）、空白を除き、英字を大文字にする。</summary>
    public static string Normalize(string s)
    {
        var n = s.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(n.Length);
        foreach (var c in n) if (!char.IsWhiteSpace(c)) sb.Append(char.ToUpperInvariant(c));
        return sb.ToString();
    }

    private static string NormalizeIcQuery(string s) => IcSuffixPattern().Replace(Normalize(s), "");

    private static string NormalizeRoad(string s) =>
        Normalize(s).Replace("自動車道", "道").Replace("高速道路", "高速");

    private static string RoadCore(string normalized)
    {
        var s = normalized;
        if (s.EndsWith("高速", StringComparison.Ordinal)) s = s[..^2];
        else if (s.EndsWith('道') && s.Length > 1) s = s[..^1];
        return s;
    }

    internal static string BaseName(string name)
    {
        var n = name.Normalize(NormalizationForm.FormKC);
        var i = n.IndexOf('(');
        return i > 0 ? n[..i] : n;
    }

    private static string ToHiragana(string s) =>
        new(s.Select(c => c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c).ToArray());

    public static string? StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var text = WebUtility.HtmlDecode(HtmlTagPattern().Replace(html.Replace("<br>", " "), "")).Trim();
        return text.Length > 0 ? text : null;
    }
}

/// <summary>JavaScript の配列リテラル（文字列・数値・入れ子の配列だけ）を読む。</summary>
internal static class JsLiteral
{
    public static List<object> ParseArrayVariable(string script, string name)
    {
        var marker = $"var {name}=";
        var start = script.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) throw new McpException($"IC の一覧に {name} が見つかりません。サイトが改修された可能性があります。");
        var pos = start + marker.Length;
        return (List<object>)ParseValue(script, ref pos);
    }

    private static object ParseValue(string s, ref int pos)
    {
        SkipSpace(s, ref pos);
        var c = s[pos];
        if (c == '[')
        {
            pos++;
            var list = new List<object>();
            while (true)
            {
                SkipSpace(s, ref pos);
                if (s[pos] == ']') { pos++; return list; }
                list.Add(ParseValue(s, ref pos));
                SkipSpace(s, ref pos);
                if (s[pos] == ',') pos++;
            }
        }
        if (c is '\'' or '"')
        {
            pos++;
            var sb = new StringBuilder();
            while (s[pos] != c)
            {
                if (s[pos] == '\\') pos++;
                sb.Append(s[pos++]);
            }
            pos++;
            return sb.ToString();
        }
        // 数値などは文字列として持つ
        var begin = pos;
        while (pos < s.Length && s[pos] is not (',' or ']')) pos++;
        return s[begin..pos].Trim();
    }

    private static void SkipSpace(string s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
    }
}
