using Ai4Sts2.Core;
using Ai4Sts2.Game;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rngs;

namespace Ai4Sts2.Harness;

public sealed record CardDump(string Id, int Upgrade, int Cost, bool CostsX);

public sealed record PowerDump(string Id, int Amount);

public sealed record CreatureDump(
    string Name,
    string? Monster,
    uint? CombatId,
    int Hp,
    int MaxHp,
    int Block,
    bool Alive,
    IReadOnlyList<PowerDump> Powers,
    string? NextMove,
    IReadOnlyList<string>? MoveLog
);

public sealed record PlayerDump(
    string Character,
    CreatureDump Creature,
    int Turn,
    string Phase,
    int Energy,
    int Stars,
    int Gold,
    IReadOnlyList<CardDump> Hand,
    IReadOnlyList<bool> Playable,
    IReadOnlyList<CardDump> Draw,
    IReadOnlyList<CardDump> Discard,
    IReadOnlyList<CardDump> Exhaust,
    IReadOnlyList<CardDump> Play,
    IReadOnlyList<string?> Potions,
    IReadOnlyList<string> Relics,
    IReadOnlyList<string> Orbs
);

public sealed record CombatStateDump(
    bool InProgress,
    string? Encounter,
    int Round,
    string Side,
    IReadOnlyList<PlayerDump> Players,
    IReadOnlyList<CreatureDump> Enemies,
    IReadOnlyDictionary<string, RngState> Rng
);

public static class CombatDump
{
    public static CombatStateDump Capture()
    {
        var manager = CombatManager.Instance;
        var state = manager.DebugOnlyGetState();
        if (state is null)
        {
            return new CombatStateDump(false, null, 0, "None", [], [], new Dictionary<string, RngState>());
        }
        var rng = new Dictionary<string, RngState>();
        foreach (var type in Enum.GetValues<RunRngType>())
        {
            rng[type.ToString()] = RngAccess.Capture(state.RunState.Rng.GetRng(type));
        }
        return new CombatStateDump(
            manager.IsInProgress,
            state.Encounter?.Id.Entry,
            state.RoundNumber,
            state.CurrentSide.ToString(),
            state.Players.Select(DumpPlayer).ToList(),
            state.Enemies.Select(DumpCreature).ToList(),
            rng
        );
    }

    private static PlayerDump DumpPlayer(Player player)
    {
        var pcs = player.PlayerCombatState ?? throw new InvalidOperationException("player has no combat state");
        return new PlayerDump(
            player.Character.Id.Entry,
            DumpCreature(player.Creature),
            pcs.TurnNumber,
            pcs.Phase.ToString(),
            pcs.Energy,
            pcs.Stars,
            player.Gold,
            DumpPile(pcs.Hand),
            pcs.Hand.Cards.Select(c => c.CanPlay(out _, out _)).ToList(),
            DumpPile(pcs.DrawPile),
            DumpPile(pcs.DiscardPile),
            DumpPile(pcs.ExhaustPile),
            DumpPile(pcs.PlayPile),
            player.PotionSlots.Select(p => p?.Id.Entry).ToList(),
            player.Relics.Select(r => r.Id.Entry).ToList(),
            pcs.OrbQueue.Orbs.Select(o => o.Id.Entry).ToList()
        );
    }

    private static List<CardDump> DumpPile(CardPile pile) =>
        pile
            .Cards.Select(c => new CardDump(
                c.Id.Entry,
                c.CurrentUpgradeLevel,
                c.EnergyCost.GetResolved(),
                c.EnergyCost.CostsX
            ))
            .ToList();

    private static CreatureDump DumpCreature(Creature creature)
    {
        var monster = creature.Monster;
        return new CreatureDump(
            creature.Name,
            monster?.Id.Entry,
            creature.CombatId,
            creature.CurrentHp,
            creature.MaxHp,
            creature.Block,
            creature.IsAlive,
            creature.Powers.Select(p => new PowerDump(p.Id.Entry, p.Amount)).ToList(),
            monster?.NextMove.StateId,
            monster?.MoveStateMachine?.StateLog.Select(s => s.GetType().Name).ToList()
        );
    }
}
