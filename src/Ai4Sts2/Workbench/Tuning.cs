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

    public static int DoomWeight { get; set; } = 12;

    public static bool PrimaryBulk { get; set; } = true;

    public static int HalvingAbove { get; set; } = 4;

    public static int SmithCandidates { get; set; } = 5;

    public static int RemovalCandidates { get; set; } = 3;

    public static int ShopCandidates { get; set; } = 3;

    public static int ContrastBeam { get; set; } = 3;

    public static int ContrastSpan { get; set; } = 3;

    public static int RemovalExposure { get; set; } = 2;

    public static int RolloutHpFloor { get; set; } = 65;

    public static bool EliteProbe { get; set; }

    public static int RolloutSamples { get; set; } = 1;

    public static int UsageWeight { get; set; } = 450;

    public static int PriorWeight { get; set; } = 80;

    public static int RolloutWin { get; set; } = 1000;

    public static int RolloutLoss { get; set; } = 5000;

    public static int RolloutHp { get; set; } = 10;

    public static int GoldPercent { get; set; } = 60;

    public static int ProbeDamage { get; set; } = 3;

    public static int EliteHp { get; set; } = 8;

    public static int BossHp { get; set; } = 6;

    public static int EliteWin { get; set; } = 2000;

    public static int BossWin { get; set; } = 3000;

    public static int ProbeDeath { get; set; } = 1500;

    public static int ProbeDeathRemaining { get; set; } = 2500;

    public static int EventMaxHp { get; set; } = 12;

    public static int EventDeck { get; set; } = 40;

    public static int EventRelic { get; set; } = 120;

    public static int EventPotion { get; set; } = 60;

    public static bool RatePricing { get; set; }

    public static bool RateDebuffs { get; set; }

    public static int RateHorizon { get; set; } = 6;

    public static int HpWeight { get; set; } = 15;

    public static int HpWeightNormal { get; set; } = 20;

    public static int TerminalWin { get; set; } = 1_000_000;

    public static bool RateDamage { get; set; }

    public static int FocusFire { get; set; }

    public static int RaceWeight { get; set; } = 25;

    public static int LevelDivisor { get; set; } = 5;

    public static int TotalFactor { get; set; } = 8;

    public static int HorizonBlock { get; set; } = 80;

    public static int ThreatMoves { get; set; } = 5;

    public static int ReservePercent { get; set; } = 50;

    public static int SpikePercent { get; set; } = 50;

    public static int BlockPrior { get; set; } = 10;

    public static int SearchNodes { get; set; } = 400;

    public static int SearchBeam { get; set; } = 3;

    public static int SearchTurns { get; set; } = 2;

    public static int HardNodes { get; set; } = 2500;

    public static int HardBeam { get; set; } = 5;

    public static int HardTurns { get; set; } = 3;

    public static int RolloutNodes { get; set; } = 150;

    public static int RolloutBeam { get; set; } = 2;

    public static int RolloutTurns { get; set; } = 1;

    public static int MaxDepth { get; set; } = 8;

    public static int MaxTurns { get; set; } = 30;

    public static int Fights { get; set; } = 6;

    public static bool BossProbe { get; set; }

    public static int BossTurns { get; set; } = 6;

    public static int EliteTurns { get; set; } = 8;

    public static int RouteMonster { get; set; } = 30;

    public static int RouteElite { get; set; } = 110;

    public static int RouteEliteMinHp { get; set; } = 55;

    public static int RouteElitePenalty { get; set; } = 150;

    public static int RouteUnknown { get; set; } = 25;

    public static int RouteShop { get; set; } = 20;

    public static int RouteShopPoor { get; set; } = 5;

    public static int RouteShopGold { get; set; } = 120;

    public static int RouteTreasure { get; set; } = 90;

    public static int RouteRestFull { get; set; } = 35;

    public static int RouteRestHeal { get; set; } = 60;

    public static int RouteRestBelow { get; set; } = 70;

    public static int RouteRestAmount { get; set; } = 30;

    public static int RouteLossMonster { get; set; } = 8;

    public static int RouteLossElite { get; set; } = 22;

    public static int RouteLossUnknown { get; set; } = 3;

    public static int RouteDanger { get; set; } = 15;

    public static int RouteDangerPenalty { get; set; } = 500;

    public static int RouteHpValue { get; set; } = 250;

    public static int RouteFuture { get; set; } = 3;

    public static int LossHp { get; set; } = 60;

    public static string Value { get; set; } = "hand";

    private static readonly Knobs _knobs = new(typeof(Tuning));

    public static Dictionary<string, object?> Apply(JsonElement? args, bool reset) => _knobs.Apply(args, reset);
}

public static class Modes
{
    public static bool ProbeTurnEnd { get; set; } = true;

    public static bool FreezeMap { get; set; } = true;

    public static bool StableShuffle { get; set; } = true;

    public static bool Record { get; set; } = Environment.GetEnvironmentVariable("AI4STS2_RECORD") == "1";

    public static bool Dump { get; set; }

    private static readonly Knobs _knobs = new(typeof(Modes));

    public static Dictionary<string, object?> Apply(JsonElement? args, bool reset) => _knobs.Apply(args, reset);
}

public sealed class Knobs(Type owner)
{
    private readonly Dictionary<string, PropertyInfo> _properties = owner
        .GetProperties(BindingFlags.Public | BindingFlags.Static)
        .Where(p => p.CanWrite)
        .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, object?> _defaults = owner
        .GetProperties(BindingFlags.Public | BindingFlags.Static)
        .Where(p => p.CanWrite)
        .ToDictionary(p => p.Name, p => p.GetValue(null));

    public Dictionary<string, object?> Apply(JsonElement? args, bool reset)
    {
        if (reset)
        {
            foreach (var (name, value) in _defaults)
            {
                _properties[name].SetValue(null, value);
            }
        }
        if (args is { ValueKind: JsonValueKind.Object } a)
        {
            foreach (var entry in a.EnumerateObject())
            {
                if (!_properties.TryGetValue(entry.Name, out var property))
                {
                    continue;
                }
                object value =
                    property.PropertyType == typeof(string) ? entry.Value.ToString()
                    : property.PropertyType == typeof(bool)
                        ? entry.Value.ValueKind == JsonValueKind.True
                            || (entry.Value.ValueKind == JsonValueKind.String && entry.Value.GetString() == "true")
                    : entry.Value.ValueKind == JsonValueKind.Number ? entry.Value.GetInt32()
                    : int.Parse(entry.Value.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                property.SetValue(null, value);
            }
        }
        return _properties
            .Values.OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToDictionary(p => p.Name, p => p.GetValue(null));
    }
}
