using System.Text;
using Ai4Sts2.Core;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Rooms;

namespace Ai4Sts2.Workbench;

public sealed record SearchAction(string Kind, int Player, int Hand, int? Target, string? Card);

public sealed class CombatDomain : ISearchDomain<SearchAction>
{
    private const int HpWeight = 15;
    private readonly Dictionary<uint, int> _rootEnemyMaxHp = [];
    private readonly int _potionValue;

    public CombatDomain(Session session)
    {
        Session = session;
        var (state, _) = Session.Current(0);
        _potionValue = state.Encounter?.RoomType is RoomType.Elite or RoomType.Boss ? 40 : 260;
        foreach (var enemy in state.Enemies)
        {
            if (enemy.CombatId is { } id)
            {
                _rootEnemyMaxHp[id] = enemy.MaxHp;
            }
        }
    }

    public Session Session { get; }

    public int? ActivePlayer { get; set; }

    public bool Terminal => !CombatManager.Instance.IsInProgress;

    public IReadOnlyList<SearchAction> Actions()
    {
        var state = State();
        var list = new List<SearchAction>();
        var enemies = state.Enemies.Select((e, i) => (e, i)).Where(x => x.e.IsAlive).ToList();
        var separateEnds = Session.PerPlayerEnds;
        for (var p = 0; p < state.Players.Count; p++)
        {
            var player = state.Players[p];
            if (ActivePlayer is { } active && active != p)
            {
                continue;
            }
            if (Session.HasEnded(player) || player.PlayerCombatState is not { } pcs || !player.Creature.IsAlive)
            {
                continue;
            }
            using var scope = Session.ActAs(player);
            var seen = new HashSet<string>();
            for (var h = 0; h < pcs.Hand.Cards.Count; h++)
            {
                var card = pcs.Hand.Cards[h];
                if (!card.CanPlay(out _, out _) || !seen.Add(CardSignature(card)))
                {
                    continue;
                }
                if (card.TargetType is TargetType.AnyEnemy)
                {
                    foreach (var (e, i) in enemies)
                    {
                        if (card.IsValidTarget(e))
                        {
                            list.Add(new SearchAction("play", p, h, i, card.Id.Entry));
                        }
                    }
                }
                else if (card.IsValidTarget(null))
                {
                    list.Add(new SearchAction("play", p, h, null, card.Id.Entry));
                }
            }
            var potions = new HashSet<string>();
            for (var slot = 0; slot < player.PotionSlots.Count; slot++)
            {
                var potion = Session.UsablePotion(player, slot);
                if (potion is null || !potions.Add(potion.Id.Entry))
                {
                    continue;
                }
                if (potion.TargetType is TargetType.AnyEnemy)
                {
                    foreach (var (e, i) in enemies)
                    {
                        if (potion.IsValidTarget(e))
                        {
                            list.Add(new SearchAction("potion", p, slot, i, potion.Id.Entry));
                        }
                    }
                }
                else if (potion.IsValidTarget(Session.PotionTarget(potion, state, null)))
                {
                    list.Add(new SearchAction("potion", p, slot, null, potion.Id.Entry));
                }
            }
            if (separateEnds)
            {
                list.Add(new SearchAction("end", p, -1, null, null));
            }
        }
        return list;
    }

    public IReadOnlyList<SearchAction> Closing()
    {
        if (Terminal)
        {
            return [];
        }
        var state = State();
        var list = new List<SearchAction>();
        for (var p = 0; p < state.Players.Count; p++)
        {
            if (!Session.HasEnded(state.Players[p]))
            {
                list.Add(new SearchAction("end", p, -1, null, null));
                if (!Session.PerPlayerEnds)
                {
                    break;
                }
            }
        }
        return list;
    }

    public TimeSpan Apply(SearchAction action) =>
        action.Kind switch
        {
            "end" => Session.EndTurn(action.Player),
            "potion" => Session.UsePotion(action.Player, action.Hand, action.Target),
            _ => Session.Play(action.Player, action.Hand, action.Target),
        };

    public double Evaluate()
    {
        var state = State();
        var players = state.Players;
        if (Terminal)
        {
            var alive = players.Where(p => p.Creature.IsAlive).ToList();
            return alive.Count == 0 ? -1_000_000 : 1_000_000 + alive.Sum(p => p.Creature.CurrentHp * 100);
        }
        double score = 0;
        var present = new Dictionary<uint, Creature>();
        foreach (var enemy in state.Enemies)
        {
            if (enemy.CombatId is { } id)
            {
                present[id] = enemy;
            }
        }
        foreach (var (id, maxHp) in _rootEnemyMaxHp)
        {
            if (present.TryGetValue(id, out var enemy))
            {
                score += (maxHp - enemy.CurrentHp) * 10;
                if (!enemy.IsAlive)
                {
                    score += 500;
                }
                score -= enemy.Block;
                score += PowerScore(enemy, -1);
            }
            else
            {
                score += (maxHp * 10) + 500;
            }
        }
        foreach (var enemy in state.Enemies)
        {
            if (enemy.CombatId is { } id && !_rootEnemyMaxHp.ContainsKey(id))
            {
                score -= enemy.CurrentHp * 10;
            }
        }
        foreach (var player in players)
        {
            var creature = player.Creature;
            score -= (creature.MaxHp - creature.CurrentHp) * HpWeight;
            if (!creature.IsAlive)
            {
                score -= 100_000;
                continue;
            }
            score += PowerScore(creature, 1);
            score += player.Potions.Count() * _potionValue;
            if (player.PlayerCombatState is { } pcs)
            {
                score += pcs.Pets.Sum(pet => pet.CurrentHp * 3);
                score += pcs.OrbQueue.Orbs.Count * 8;
            }
        }
        return score;
    }

    public double Estimate()
    {
        var state = State();
        var score = Evaluate();
        if (Terminal)
        {
            return score;
        }
        var targets = state.PlayerCreatures;
        foreach (var enemy in state.Enemies)
        {
            if (!enemy.IsAlive || enemy.Monster?.NextMove is not { } move)
            {
                continue;
            }
            foreach (var intent in move.Intents)
            {
                score += IntentPenalty(intent);
                if (intent is not AttackIntent attack)
                {
                    continue;
                }
                foreach (var player in state.Players)
                {
                    var creature = player.Creature;
                    if (!creature.IsAlive)
                    {
                        continue;
                    }
                    using var scope = Session.ActAs(player);
                    var damage = attack.GetTotalDamage(targets, enemy);
                    var through = Math.Max(0, damage - creature.Block);
                    score -= through * HpWeight;
                    if (through >= creature.CurrentHp)
                    {
                        score -= 100_000;
                    }
                }
            }
        }
        return score;
    }

    public bool CanonicalKeys { get; set; }

    public string Key() => Key(CanonicalKeys ? PileOrder.Ends : PileOrder.Full);

    public string BeamKey() => Key(PileOrder.Set);

    private enum PileOrder
    {
        Full,
        Ends,
        Set,
    }

    public string Bucket()
    {
        var state = State();
        var sb = new StringBuilder(128);
        foreach (var enemy in state.Enemies)
        {
            sb.Append(enemy.CombatId)
                .Append(enemy.IsAlive ? '+' : '-')
                .Append(enemy.Monster?.NextMove?.StateId)
                .Append('[');
            foreach (var power in enemy.Powers.Select(p => p.Id.Entry).Order())
            {
                sb.Append(power).Append(',');
            }
            sb.Append(']');
        }
        return sb.ToString();
    }

    private static string Key(PileOrder order)
    {
        var state = State();
        var sb = new StringBuilder(512);
        foreach (var creature in state.Creatures)
        {
            sb.Append(creature.CombatId)
                .Append(':')
                .Append(creature.CurrentHp)
                .Append('/')
                .Append(creature.Block)
                .Append('/');
            Powers(sb, creature);
            sb.Append('/').Append(creature.Monster?.NextMove?.StateId).Append(';');
        }
        foreach (var player in state.Players)
        {
            var pcs = player.PlayerCombatState!;
            sb.Append('|')
                .Append(pcs.TurnNumber)
                .Append('|')
                .Append(pcs.Energy)
                .Append('|')
                .Append(pcs.Stars)
                .Append('|')
                .Append(Session.HasEnded(player) ? 'e' : 'p')
                .Append('|');
            Pile(sb, pcs.Hand, PileOrder.Set);
            Pile(sb, pcs.DrawPile, PileOrder.Full);
            Pile(sb, pcs.DiscardPile, order);
            Pile(sb, pcs.ExhaustPile, order);
            Pile(sb, pcs.PlayPile, order);
            foreach (var orb in pcs.OrbQueue.Orbs)
            {
                sb.Append(orb.Id.Entry).Append(',');
            }
            sb.Append('|');
            foreach (var potion in player.PotionSlots)
            {
                sb.Append(potion?.Id.Entry).Append(',');
            }
        }
        sb.Append('|');
        foreach (var type in Enum.GetValues<RunRngType>())
        {
            sb.Append(state.RunState.Rng.GetRng(type)._counter).Append(',');
        }
        return sb.ToString();
    }

    private static double IntentPenalty(AbstractIntent intent) =>
        intent.IntentType switch
        {
            IntentType.Debuff => -40,
            IntentType.DebuffStrong => -80,
            IntentType.CardDebuff => -40,
            IntentType.StatusCard => -20 * ((intent as StatusIntent)?.CardCount ?? 1),
            IntentType.Buff => -30,
            IntentType.Defend => -10,
            IntentType.Heal => -30,
            IntentType.Summon => -60,
            IntentType.Attack
            or IntentType.DeathBlow
            or IntentType.Escape
            or IntentType.Hidden
            or IntentType.Sleep
            or IntentType.Stun
            or IntentType.Unknown => 0,
            _ => 0,
        };

    private static CombatState State() =>
        CombatManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("no combat");

    private static string CardSignature(CardModel card)
    {
        var sb = new StringBuilder();
        sb.Append(card.Id.Entry)
            .Append('/')
            .Append(card.CurrentUpgradeLevel)
            .Append('/')
            .Append(card.EnergyCost.GetWithModifiers(CostModifiers.Local));
        sb.Append('/').Append(card.EnergyCost.CostsX ? 'x' : '-');
        foreach (var (name, v) in card.DynamicVars)
        {
            sb.Append('/').Append(name).Append('=').Append(v.BaseValue);
        }
        if (card.Enchantment is { } enchantment)
        {
            sb.Append("/e:").Append(enchantment.Id.Entry);
        }
        if (card.Affliction is { } affliction)
        {
            sb.Append("/a:").Append(affliction.Id.Entry);
        }
        return sb.ToString();
    }

    private static double PowerScore(Creature creature, int sign)
    {
        double score = 0;
        foreach (var power in creature.Powers)
        {
            var (weight, cap) = PowerWeights.For(power.Id.Entry, sign < 0);
            if (weight == 0)
            {
                continue;
            }
            var polarity = power.TypeForCurrentAmount == PowerType.Debuff ? -1 : 1;
            score += sign * polarity * Math.Min(Math.Abs(power.Amount), cap) * Math.Sign(power.Amount) * weight;
        }
        return score;
    }

    private static void Pile(StringBuilder sb, CardPile pile, PileOrder order)
    {
        var cards = pile.Cards.Select(CardSignature).ToList();
        if (order == PileOrder.Ends && cards.Count > 2)
        {
            sb.Append(cards[0]).Append('>').Append(cards[^1]).Append('>');
        }
        sb.Append(string.Join(",", order == PileOrder.Full ? cards : cards.Order())).Append('|');
    }

    private static void Powers(StringBuilder sb, Creature c)
    {
        foreach (var power in c.Powers.OrderBy(p => p.Id.Entry, StringComparer.Ordinal))
        {
            sb.Append(power.Id.Entry).Append('=').Append(power.Amount).Append(',');
        }
    }
}
