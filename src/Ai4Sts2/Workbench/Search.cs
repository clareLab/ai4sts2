using System.Diagnostics;
using System.Text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Models;

namespace Ai4Sts2.Workbench;

public sealed record SearchAction(string Kind, int Hand, int? Target, string? Card);

public sealed record SearchResult(
    double Score,
    IReadOnlyList<SearchAction> Line,
    int Nodes,
    int Leaves,
    int Transpositions,
    int Snapshots,
    int Restores,
    double Micros,
    double SnapMicros,
    double RestoreMicros,
    double ActMicros,
    bool Exhausted
);

public sealed class Search(Session session, int maxNodes, int maxDepth)
{
    private readonly Dictionary<string, double> _table = [];
    private readonly Stopwatch _clock = new();
    private readonly Dictionary<uint, int> _rootEnemyMaxHp = [];
    private int _nodes;
    private int _leaves;
    private int _transpositions;
    private int _snapshots;
    private int _restores;
    private double _snapMicros;
    private double _restoreMicros;
    private double _actMicros;

    public SearchResult Run()
    {
        _clock.Restart();
        _rootEnemyMaxHp.Clear();
        var (root, _) = Current();
        foreach (var enemy in root.Enemies)
        {
            if (enemy.CombatId is { } id)
            {
                _rootEnemyMaxHp[id] = enemy.MaxHp;
            }
        }
        var line = new List<SearchAction>();
        var score = Explore(0, line, out var best);
        return new SearchResult(
            score,
            best,
            _nodes,
            _leaves,
            _transpositions,
            _snapshots,
            _restores,
            _clock.Elapsed.TotalMicroseconds,
            _snapMicros,
            _restoreMicros,
            _actMicros,
            _nodes < maxNodes
        );
    }

    private double Explore(int depth, List<SearchAction> path, out IReadOnlyList<SearchAction> bestLine)
    {
        bestLine = path.ToList();
        if (!CombatManager.Instance.IsInProgress)
        {
            _leaves++;
            return Evaluate();
        }
        var (state, player) = Current();
        var key = Key(state, player);
        if (_table.TryGetValue(key, out var known))
        {
            _transpositions++;
            return known;
        }
        _nodes++;
        var actions = depth < maxDepth && _nodes < maxNodes ? Enumerate(state, player) : [];
        var snap = TakeSnapshot();
        var bestScore = double.NegativeInfinity;
        var best = bestLine;
        foreach (var action in actions)
        {
            if (_nodes >= maxNodes)
            {
                break;
            }
            Apply(action);
            path.Add(action);
            var score = Explore(depth + 1, path, out var line);
            path.RemoveAt(path.Count - 1);
            RestoreSnapshot(snap);
            if (score > bestScore)
            {
                bestScore = score;
                best = line;
            }
        }
        var end = new SearchAction("end", -1, null, null);
        Apply(end);
        path.Add(end);
        var leaf = Evaluate();
        path.RemoveAt(path.Count - 1);
        RestoreSnapshot(snap);
        _leaves++;
        if (leaf > bestScore)
        {
            bestScore = leaf;
            best = path.Concat([end]).ToList();
        }
        _table[key] = bestScore;
        bestLine = best;
        return bestScore;
    }

    private Snapshot TakeSnapshot()
    {
        var snap = Loader.Take();
        _snapshots++;
        _snapMicros += snap.Elapsed.TotalMicroseconds;
        return snap;
    }

    private void RestoreSnapshot(Snapshot snap)
    {
        var stats = Loader.Restore(snap, session.Pump);
        _restores++;
        _restoreMicros += stats.RestoreMicros + stats.ResyncMicros;
    }

    private void Apply(SearchAction action)
    {
        var elapsed = action.Kind == "end" ? session.EndTurn() : session.Play(action.Hand, action.Target);
        _actMicros += elapsed.TotalMicroseconds;
    }

    private static string CardSignature(CardModel card)
    {
        var sb = new StringBuilder();
        sb.Append(card.Id.Entry)
            .Append('/')
            .Append(card.CurrentUpgradeLevel)
            .Append('/')
            .Append(card.EnergyCost.GetResolved());
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

    private static List<SearchAction> Enumerate(CombatState state, Player player)
    {
        var pcs = player.PlayerCombatState!;
        var seen = new HashSet<string>();
        var list = new List<SearchAction>();
        var enemies = state.Enemies.Select((e, i) => (e, i)).Where(x => x.e.IsAlive).ToList();
        for (var h = 0; h < pcs.Hand.Cards.Count; h++)
        {
            var card = pcs.Hand.Cards[h];
            if (!card.CanPlay(out _, out _))
            {
                continue;
            }
            if (!seen.Add(CardSignature(card)))
            {
                continue;
            }
            if (card.TargetType is TargetType.AnyEnemy)
            {
                foreach (var (e, i) in enemies)
                {
                    if (card.IsValidTarget(e))
                    {
                        list.Add(new SearchAction("play", h, i, card.Id.Entry));
                    }
                }
            }
            else if (card.IsValidTarget(null))
            {
                list.Add(new SearchAction("play", h, null, card.Id.Entry));
            }
        }
        return list;
    }

    private double Evaluate()
    {
        var manager = CombatManager.Instance;
        var state = manager.DebugOnlyGetState() ?? throw new InvalidOperationException("no combat");
        var me = LocalContext.GetMe(state) ?? throw new InvalidOperationException("no local player");
        var hp = me.Creature.CurrentHp;
        if (!manager.IsInProgress)
        {
            return me.Creature.IsAlive ? 1_000_000 + (hp * 100) : -1_000_000;
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
        score -= (me.Creature.MaxHp - hp) * 15;
        score += PowerScore(me.Creature, 1);
        foreach (var pet in me.PlayerCombatState!.Pets)
        {
            score += pet.CurrentHp * 3;
        }
        score += me.PlayerCombatState.OrbQueue.Orbs.Count * 8;
        return score;
    }

    private static double PowerScore(Creature creature, int sign)
    {
        double score = 0;
        foreach (var power in creature.Powers)
        {
            var weight = power.Id.Entry switch
            {
                "STRENGTH" or "DEXTERITY" => 30,
                "VULNERABLE" or "WEAK" => 20,
                "FRAIL" => 15,
                "POISON" => 8,
                _ => 5,
            };
            var polarity = power.TypeForCurrentAmount == PowerType.Debuff ? -1 : 1;
            score += sign * polarity * power.Amount * weight;
        }
        return score;
    }

    private static string Key(CombatState state, Player player)
    {
        var pcs = player.PlayerCombatState!;
        var sb = new StringBuilder(512);
        sb.Append(pcs.TurnNumber).Append('|').Append(pcs.Energy).Append('|').Append(pcs.Stars).Append('|');
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
        sb.Append('|');
        Pile(sb, pcs.Hand, true);
        Pile(sb, pcs.DrawPile, false);
        Pile(sb, pcs.DiscardPile, false);
        Pile(sb, pcs.ExhaustPile, false);
        Pile(sb, pcs.PlayPile, false);
        foreach (var orb in pcs.OrbQueue.Orbs)
        {
            sb.Append(orb.Id.Entry).Append(',');
        }
        sb.Append('|');
        foreach (var type in Enum.GetValues<RunRngType>())
        {
            sb.Append(state.RunState.Rng.GetRng(type)._counter).Append(',');
        }
        return sb.ToString();
    }

    private static void Pile(StringBuilder sb, CardPile pile, bool sorted)
    {
        var cards = pile.Cards.Select(CardSignature);
        sb.Append(string.Join(",", sorted ? cards.Order() : cards)).Append('|');
    }

    private static void Powers(StringBuilder sb, Creature c)
    {
        foreach (var power in c.Powers.OrderBy(p => p.Id.Entry, StringComparer.Ordinal))
        {
            sb.Append(power.Id.Entry).Append('=').Append(power.Amount).Append(',');
        }
    }

    private static (CombatState State, Player Player) Current()
    {
        var state = CombatManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("no combat");
        var player = LocalContext.GetMe(state) ?? throw new InvalidOperationException("no local player");
        return (state, player);
    }
}
