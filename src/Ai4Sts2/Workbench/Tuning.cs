using System.Reflection;
using System.Text.Json;

namespace Ai4Sts2.Workbench;

public static class Tuning
{
    public static bool HorizonEstimate { get; set; } = true;

    public static int ConvexHp { get; set; } = 25;

    public static int PotionHoldHard { get; set; } = 120;

    public static bool GradedLethal { get; set; } = true;

    public static int MaxVariants { get; set; } = 6;

    public static bool TemporaryPowers { get; set; } = true;

    public static bool Doom { get; set; } = true;

    public static int HalvingAbove { get; set; } = 4;

    public static bool ProbeTurnEnd { get; set; } = true;

    public static int SmithCandidates { get; set; } = 5;

    public static int RemovalCandidates { get; set; } = 3;

    public static int RemovalExposure { get; set; } = 2;

    public static int RolloutHpFloor { get; set; } = 65;

    public static bool EliteProbe { get; set; }

    public static bool RatePricing { get; set; }

    public static bool RateDebuffs { get; set; }

    public static int RateHorizon { get; set; } = 6;

    public static bool FreezeMap { get; set; } = true;

    public static bool Spotlight { get; set; } = true;

    public static bool Record { get; set; } = Environment.GetEnvironmentVariable("AI4STS2_RECORD") == "1";

    public static int FocusFire { get; set; }

    public static int RaceWeight { get; set; } = 25;

    public static int LevelDivisor { get; set; } = 5;

    public static int TotalFactor { get; set; } = 8;

    public static int HorizonBlock { get; set; } = 80;

    public static int ThreatMoves { get; set; } = 5;

    public static int ReservePercent { get; set; } = 50;

    public static int SpikePercent { get; set; } = 50;

    public static int BlockPrior { get; set; } = 10;

    private static readonly Dictionary<string, object?> _defaults = Properties()
        .ToDictionary(p => p.Name, p => p.GetValue(null));

    private static IEnumerable<PropertyInfo> Properties() =>
        typeof(Tuning).GetProperties(BindingFlags.Public | BindingFlags.Static).Where(p => p.CanWrite);

    public static Dictionary<string, object?> Apply(JsonElement? args)
    {
        var properties = Properties().ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        if (args is { ValueKind: JsonValueKind.Object } a)
        {
            if (a.TryGetProperty("reset", out var reset) && reset.ValueKind == JsonValueKind.True)
            {
                foreach (var (name, value) in _defaults)
                {
                    properties[name].SetValue(null, value);
                }
            }
            foreach (var entry in a.EnumerateObject())
            {
                if (!properties.TryGetValue(entry.Name, out var property))
                {
                    continue;
                }
                object value =
                    property.PropertyType == typeof(bool)
                        ? entry.Value.ValueKind == JsonValueKind.True
                            || (entry.Value.ValueKind == JsonValueKind.String && entry.Value.GetString() == "true")
                    : entry.Value.ValueKind == JsonValueKind.Number ? entry.Value.GetInt32()
                    : int.Parse(entry.Value.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                property.SetValue(null, value);
            }
        }
        return Properties().ToDictionary(p => p.Name, p => p.GetValue(null));
    }
}
