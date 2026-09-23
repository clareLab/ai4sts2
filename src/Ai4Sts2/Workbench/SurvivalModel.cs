using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ai4Sts2.Workbench;

public sealed class SurvivalModel
{
    private static readonly Dictionary<string, SurvivalModel> _loaded = new(StringComparer.Ordinal);

    private readonly Dictionary<string, int> _index;
    private readonly double[] _hazard;
    private readonly double[] _delta;

    private SurvivalModel(IReadOnlyList<string> names, double[] hazard, double[] delta, string hash)
    {
        _index = names.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i, StringComparer.Ordinal);
        _hazard = hazard;
        _delta = delta;
        Hash = hash;
    }

    public string Hash { get; }

    public int Count => _index.Count;

    public static SurvivalModel? Current =>
        Tuning.Survival is { } key && key != "hand" && _loaded.TryGetValue(key, out var model) ? model : null;

    public static SurvivalModel Load(string path)
    {
        var text = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var names = root.GetProperty("names").EnumerateArray().Select(n => n.GetString()!).ToList();
        var hazard = root.GetProperty("w").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        var delta = root.GetProperty("delta").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        if (hazard.Length != names.Count || delta.Length != names.Count || hazard.Concat(delta).Any(double.IsNaN))
        {
            throw new InvalidDataException("survival model is malformed");
        }
        var hash = root.TryGetProperty("hash", out var h)
            ? h.GetString()!
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();
        var model = new SurvivalModel(names, hazard, delta, hash);
        _loaded[hash] = model;
        return model;
    }

    public double Hazard(RunFeatures features, string room, double hpFraction, int floor) =>
        1.0 / (1.0 + Math.Exp(-Math.Clamp(Dot(_hazard, features, room, hpFraction, floor), -30, 30)));

    public double Delta(RunFeatures features, string room, double hpFraction, int floor) =>
        Dot(_delta, features, room, hpFraction, floor);

    private double Dot(double[] w, RunFeatures features, string room, double hpFraction, int floor)
    {
        var sum = Weight(w, "bias");
        sum += Weight(w, "hpFrac") * hpFraction;
        sum += Weight(w, "deckSize") * features.DeckSize / 20.0;
        sum += Weight(w, "upgrades") * features.Upgrades / 5.0;
        sum += Weight(w, "relics") * features.Relics / 5.0;
        sum += Weight(w, "potions") * features.Potions / 3.0;
        sum += Weight(w, "floor") * floor / 50.0;
        sum += Weight(w, "room:" + room);
        sum += Weight(w, "char:" + features.Character);
        return sum;
    }

    private double Weight(double[] w, string name) => _index.TryGetValue(name, out var j) ? w[j] : 0;
}

public readonly record struct RunFeatures(int DeckSize, int Upgrades, int Relics, int Potions, string Character);
