using System.Collections.Concurrent;
using ModelContextProtocol;

namespace NexcoMcp;

/// <summary>
/// NEXCO 西日本とドライブコンパスの IC コードを読み替える。
/// 付け方（道路コード 4 桁 + 位置 3 桁）は同じだが、同じ IC でコードが違うことや、片方にしかない IC があるので、
/// コードと名前がそろうものを優先し、そろわなければ名前（括弧の前）と道路コードで同じ IC を探す。
/// </summary>
public sealed class CodeTranslator
{
    private readonly IcMaster _west;
    private readonly Dictionary<string, CentralIc> _centralByCode;
    private readonly ILookup<string, CentralIc> _centralByName;
    private readonly ILookup<string, Ic> _westByName;
    private readonly ConcurrentDictionary<string, string> _roads = new();

    public CodeTranslator(IcMaster west, IReadOnlyList<CentralIc> central)
    {
        _west = west;
        _centralByCode = central.GroupBy(c => c.Code).ToDictionary(g => g.Key, g => g.First());
        _centralByName = central.ToLookup(c => Key(c.Name));
        _westByName = west.Ics.ToLookup(i => Key(i.Name));
        Map = new CodeMap(ToWest, ToWestRoad);
    }

    /// <summary>ドライブコンパスの応答を NEXCO 西日本のコードに読み替える関数。</summary>
    public CodeMap Map { get; }

    private static string Key(string name) => IcMaster.Normalize(IcMaster.BaseName(name));

    private static string Road(string code) => code.Length >= 4 ? code[..4] : code;

    /// <summary>NEXCO 西日本の IC をドライブコンパスの IC コードにする。</summary>
    public string ToCentral(Ic ic)
    {
        if (_centralByCode.TryGetValue(ic.Code, out var same) && Key(same.Name) == Key(ic.Name)) return same.Code;
        var byName = _centralByName[Key(ic.Name)].ToList();
        if (byName.FirstOrDefault(c => c.Road == Road(ic.Code)) is { } onRoad) return onRoad.Code;
        if (byName.Count == 1) return byName[0].Code;
        throw new McpException($"ドライブコンパスで {ic.Name}（{ic.Code}）に当たる IC が見つかりませんでした。source=west で調べてください。");
    }

    /// <summary>IC の管轄の会社（101: 東日本, 102: 中日本, 103: 西日本 など）。分からなければ null。</summary>
    public string? CompanyOf(string? code, string? name)
    {
        if (code is null) return null;
        var hasSame = _centralByCode.TryGetValue(code, out var same);
        if (hasSame && (name is null || Key(same!.Name) == Key(name))) return same!.Company;
        if (name is not null && _centralByName[Key(name)].FirstOrDefault(c => c.Road == Road(code)) is { } onRoad) return onRoad.Company;
        return hasSame ? same!.Company : null;
    }

    private string ToWest(string code, string? name)
    {
        if (name is null) return code;
        if (_west.FindByCode(code) is { } same && Key(same.Name) == Key(name)) return code;
        var byName = _westByName[Key(name)].ToList();
        if (byName.FirstOrDefault(i => Road(i.Code) == Road(code)) is { } onRoad) return onRoad.Code;
        return byName.Count == 1 ? byName[0].Code : code;
    }

    // 道路コードも違うことがある（圏央道が 2140 と 214K など）ので、道路名で引き直す
    private string ToWestRoad(string code, string? name) =>
        name is null ? code : _roads.GetOrAdd(code + "|" + name, _ =>
        {
            try
            {
                return _west.ResolveRoad(name).Code;
            }
            catch (McpException)
            {
                return code;
            }
        });
}
