using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ai4Sts2.Workbench;

public sealed class ValueHead(IReadOnlyList<string> names, double[] wp, double bp, double[] wh, double bh)
{
    private readonly Dictionary<string, int> _index = names
        .Select((n, i) => (n, i))
        .ToDictionary(x => x.n, x => x.i, StringComparer.Ordinal);

    public int Count => _index.Count;

    public (double P, double H) Predict(IReadOnlyList<KeyValuePair<string, double>> features)
    {
        var zp = bp;
        var zh = bh;
        foreach (var (name, value) in features)
        {
            if (_index.TryGetValue(name, out var j))
            {
                zp += wp[j] * value;
                zh += wh[j] * value;
            }
        }
        return (1.0 / (1.0 + Math.Exp(-Math.Clamp(zp, -30, 30))), zh);
    }

    public IEnumerable<string> Names => _index.Keys;
}

public sealed class ValueModel
{
    private static readonly Dictionary<string, ValueModel> _loaded = new(StringComparer.Ordinal);

    public required string Hash { get; init; }

    public required string? Game { get; init; }

    public required IReadOnlyList<string> TrainedRuns { get; init; }

    public required Dictionary<string, ValueHead> Phases { get; init; }

    public static ValueModel? Current =>
        Tuning.Value is { } key && key != "hand" && _loaded.TryGetValue(key, out var model) ? model : null;

    public static ValueModel Load(string path)
    {
        var text = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var hash = root.TryGetProperty("hash", out var h)
            ? h.GetString()!
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();
        var phases = new Dictionary<string, ValueHead>(StringComparer.Ordinal);
        foreach (var phase in root.GetProperty("phases").EnumerateObject())
        {
            var p = phase.Value;
            var names = p.GetProperty("names").EnumerateArray().Select(n => n.GetString()!).ToList();
            var wp = p.GetProperty("wp").EnumerateArray().Select(v => v.GetDouble()).ToArray();
            var wh = p.GetProperty("wh").EnumerateArray().Select(v => v.GetDouble()).ToArray();
            if (wp.Length != names.Count || wh.Length != names.Count || wp.Concat(wh).Any(double.IsNaN))
            {
                throw new InvalidDataException($"value model phase {phase.Name} is malformed");
            }
            phases[phase.Name] = new ValueHead(
                names,
                wp,
                p.GetProperty("bp").GetDouble(),
                wh,
                p.GetProperty("bh").GetDouble()
            );
        }
        var model = new ValueModel
        {
            Hash = hash,
            Game = root.TryGetProperty("game", out var g) ? g.GetString() : null,
            TrainedRuns = root.TryGetProperty("trainedRuns", out var t)
                ? t.EnumerateArray().Select(r => r.GetString()!).ToList()
                : [],
            Phases = phases,
        };
        _loaded[hash] = model;
        return model;
    }

    public double Value(string phase, IReadOnlyList<KeyValuePair<string, double>> features, double hp, double lossHp)
    {
        if (!Phases.TryGetValue(phase, out var head))
        {
            head = Phases.Values.First();
        }
        var (p, h) = head.Predict(features);
        var kept = hp - Math.Clamp(h, 0, hp);
        return (p * kept) - ((1 - p) * lossHp);
    }
}
