using System.Globalization;
using Ai4Sts2.Core;
using Ai4Sts2.Game;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace Ai4Sts2.Harness;

public sealed record RunPlayerDump(
    string Character,
    ulong NetId,
    int Hp,
    int MaxHp,
    int Gold,
    int MaxEnergy,
    IReadOnlyList<CardDump> Deck,
    IReadOnlyList<string> Relics,
    IReadOnlyList<string?> Potions,
    IReadOnlyDictionary<string, RngState> Rng
);

public sealed record RunDump(
    string Seed,
    int Ascension,
    int TotalFloor,
    int ActIndex,
    string? Room,
    IReadOnlyList<RunPlayerDump> Players,
    IReadOnlyDictionary<string, RngState> Rng
);

public static class RunSetup
{
    public static RunDump Capture(RunState run) =>
        new(
            run.Rng.StringSeed,
            run.AscensionLevel,
            run.TotalFloor,
            run.CurrentActIndex,
            run.CurrentRoom?.GetType().Name,
            run.Players.Select(CapturePlayer).ToList(),
            CaptureRng(run)
        );

    public static Dictionary<string, RngState> CaptureRng(RunState run)
    {
        var rng = new Dictionary<string, RngState>();
        foreach (var type in Enum.GetValues<RunRngType>())
        {
            rng[type.ToString()] = RngAccess.Capture(run.Rng.GetRng(type));
        }
        return rng;
    }

    public static void RestoreRng(RunState run, IReadOnlyDictionary<string, RngState> states)
    {
        foreach (var (name, state) in states)
        {
            RngAccess.Restore(run.Rng.GetRng(Enum.Parse<RunRngType>(name)), state);
        }
    }

    public static void RestorePlayerRng(Player player, IReadOnlyDictionary<string, RngState> states)
    {
        foreach (var (name, state) in states)
        {
            RngAccess.Restore(player.PlayerRng.GetRng(Enum.Parse<PlayerRngType>(name)), state);
        }
    }

    public static async Task SetDeckAsync(Player player, IReadOnlyList<string> specs)
    {
        var run = (RunState)player.RunState;
        foreach (var card in player.Deck.Cards.ToList())
        {
            player.Deck.RemoveInternal(card, true);
            run.RemoveCard(card);
        }
        foreach (var spec in specs)
        {
            var plus = spec.IndexOf('+', StringComparison.Ordinal);
            var id = plus < 0 ? spec : spec[..plus];
            var upgrades =
                plus < 0
                    ? 0
                    : (spec.Length == plus + 1 ? 1 : int.Parse(spec[(plus + 1)..], CultureInfo.InvariantCulture));
            var canonical = ModelDb.GetById<CardModel>(
                new ModelId(ModelId.SlugifyCategory<CardModel>(), id.ToUpperInvariant())
            );
            var card = run.CreateCard(canonical, player);
            for (var u = 0; u < upgrades; u++)
            {
                card.UpgradeInternal();
                card.FinalizeUpgradeInternal();
            }
            _ = await CardPileCmd.Add(card, PileType.Deck);
        }
    }

    private static RunPlayerDump CapturePlayer(Player player)
    {
        var rng = new Dictionary<string, RngState>();
        foreach (var type in Enum.GetValues<PlayerRngType>())
        {
            rng[type.ToString()] = RngAccess.Capture(player.PlayerRng.GetRng(type));
        }
        return new RunPlayerDump(
            player.Character.Id.Entry,
            player.NetId,
            player.Creature.CurrentHp,
            player.Creature.MaxHp,
            player.Gold,
            player.MaxEnergy,
            player
                .Deck.Cards.Select(c => new CardDump(
                    c.Id.Entry,
                    c.CurrentUpgradeLevel,
                    c.EnergyCost.GetResolved(),
                    c.EnergyCost.CostsX
                ))
                .ToList(),
            player.Relics.Select(r => r.Id.Entry).ToList(),
            player.PotionSlots.Select(p => p?.Id.Entry).ToList(),
            rng
        );
    }
}
