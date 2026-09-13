using System.Diagnostics;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

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
            var sig = $"{card.Id.Entry}/{card.CurrentUpgradeLevel}/{card.EnergyCost.GetResolved()}";
            if (!seen.Add(sig))
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

    private static double Evaluate()
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
        foreach (var enemy in state.Enemies)
        {
            score += (enemy.MaxHp - enemy.CurrentHp) * 10;
            if (!enemy.IsAlive)
            {
                score += 500;
            }
            score -= enemy.Block;
            score += PowerScore(enemy, -1);
        }
        score -= (me.Creature.MaxHp - hp) * 15;
        score += PowerScore(me.Creature, 1);
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
                "VULNERABLE" or "WEAK" => -20,
                "FRAIL" => -15,
                "POISON" => -8,
                _ => 5,
            };
            score += sign * power.Amount * weight;
        }
        return score;
    }

    private static string Key(CombatState state, Player player)
    {
        var pcs = player.PlayerCombatState!;
        var hand = string.Join(
            ",",
            pcs.Hand.Cards.Select(c => c.Id.Entry + c.CurrentUpgradeLevel + ":" + c.EnergyCost.GetResolved()).Order()
        );
        var draw = pcs.DrawPile.Cards.Count;
        var discard = string.Join(",", pcs.DiscardPile.Cards.Select(c => c.Id.Entry).Order());
        var exhaust = pcs.ExhaustPile.Cards.Count;
        var enemies = string.Join(
            ";",
            state.Enemies.Select(e => $"{e.CurrentHp}/{e.Block}/{Powers(e)}/{e.Monster?.NextMove?.StateId}")
        );
        return $"{pcs.TurnNumber}|{pcs.Energy}|{pcs.Stars}|{player.Creature.CurrentHp}/{player.Creature.Block}/{Powers(player.Creature)}|{hand}|{draw}|{discard}|{exhaust}|{enemies}|{string.Join(",", state.RunState.Rng.GetRng(MegaCrit.Sts2.Core.Entities.Rngs.RunRngType.Shuffle)._counter)}";
    }

    private static string Powers(Creature c) =>
        string.Join(",", c.Powers.Select(p => p.Id.Entry + "=" + p.Amount).Order());

    private static (CombatState State, Player Player) Current()
    {
        var state = CombatManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("no combat");
        var player = LocalContext.GetMe(state) ?? throw new InvalidOperationException("no local player");
        return (state, player);
    }
}
