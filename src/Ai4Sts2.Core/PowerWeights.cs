namespace Ai4Sts2.Core;

public static class PowerWeights
{
    public static (int Weight, int Cap) For(string id, bool enemy)
    {
        var name = id.EndsWith("_POWER", StringComparison.Ordinal) ? id[..^6] : id;
        return name switch
        {
            "STRENGTH" or "DEXTERITY" => (30, 20),
            "TEMPORARY_STRENGTH" or "TEMPORARY_DEXTERITY" => (20, 20),
            "VULNERABLE" or "WEAK" => (20, 6),
            "FRAIL" => (15, 6),
            "POISON" => (8, 40),
            "RITUAL" => (25, 10),
            "ARTIFACT" => (20, 5),
            "METALLICIZE" or "PLATED_ARMOR" => (12, 20),
            "REGEN" => (10, 20),
            "THORNS" => (10, 10),
            "RINGING" => (25, 1),
            "PLOW" => (0, 0),
            "PLATING" => (12, 20),
            "DISINTEGRATION" => (75, 30),
            "MIND_ROT" => (60, 3),
            "WASTE_AWAY" => (150, 3),
            "SLOTH" => (40, 3),
            "STEAM_ERUPTION" => (10, 90),
            "SANDPIT" => (0, 0),
            _ => enemy ? (0, 0) : (5, 10),
        };
    }
}
