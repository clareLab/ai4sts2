using System.Text;
using Ai4Sts2.Core;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.ValueProps;

namespace Ai4Sts2.Workbench;

public sealed record SearchAction(string Kind, int Player, int Hand, int? Target, string? Card, int? Choice = null);

public sealed class CombatDomain : ISearchDomain<SearchAction>
{
    private static int HpWeight => Tuning.HpWeight;
    private readonly Dictionary<uint, int> _rootEnemyMaxHp = [];
    private readonly Dictionary<string, Threat> _threats = [];
    private readonly int _potionValue;
    private readonly int _blockPotionValue;
    private readonly Dictionary<string, (int[] Self, int[] Plating)> _probes = [];
    private readonly bool _probe;
    private readonly double _damagePerTurn;
    private readonly double _blockPerTurn;
    private readonly double _attacksPerTurn;
    private readonly double _skillsPerTurn;
    private readonly double _horizon;
    private double _incomingPerTurn;
    private int _enemyBulk;
    private bool _atTurnStart;

    private sealed record Threat(int[] Damage, double PerTurn, double HitsPerTurn, int MaxHit, int Moves);

    public CombatDomain(Session session, bool probe = true)
    {
        Session = session;
        var (state, _) = Session.Current(0);
        var hard = state.Encounter?.RoomType is RoomType.Elite or RoomType.Boss;
        _potionValue = hard ? Tuning.PotionHoldHard : 260;
        _blockPotionValue = _potionValue;
        foreach (var enemy in state.Enemies)
        {
            if (enemy.CombatId is { } id)
            {
                _rootEnemyMaxHp[id] = enemy.MaxHp;
            }
        }
        if (hard && state.Players.Count > 0)
        {
            var spike = 0;
            foreach (var enemy in state.Enemies)
            {
                if (enemy.IsAlive && enemy.Monster is not null && enemy.MaxHp < 1_000_000)
                {
                    spike = Math.Max(spike, ThreatOf(enemy, state, state.Players[0]).MaxHit);
                }
            }
            _blockPotionValue = Math.Max(_potionValue, HpWeight * Math.Min(12, spike - Tuning.BlockPrior));
        }
        _probe = probe && Tuning.ProbeTurnEnd;
        var prior = 8.0 * (state.Players.Count > 0 ? state.Players[0].PlayerCombatState?.MaxEnergy ?? 3 : 3);
        var fight = Session.Fight;
        _damagePerTurn = fight.Turns > 0 ? Math.Max(prior * 0.5, (double)fight.Dealt / fight.Turns) : prior;
        _blockPerTurn = fight.Turns > 0 ? (double)fight.Block / fight.Turns : Tuning.BlockPrior;
        _attacksPerTurn = fight.Turns > 0 ? Math.Max(1, (double)fight.Attacks / fight.Turns) : 2.5;
        _skillsPerTurn = fight.Turns > 0 ? Math.Max(0.5, (double)fight.Skills / fight.Turns) : 1.5;
        var bulk = state.Enemies.Where(e => e.IsAlive && e.MaxHp < 1_000_000).Sum(e => e.CurrentHp + e.Block);
        _horizon = Math.Clamp(bulk / _damagePerTurn, 1, Tuning.RateHorizon);
    }

    private double Race(CombatState state)
    {
        var streams = new List<(double Rate, int Hp, int[] Chain)>();
        var seat = state.Players.Count > 0 ? state.Players[Math.Min(ActivePlayer ?? 0, state.Players.Count - 1)] : null;
        if (seat is null)
        {
            return 0;
        }
        foreach (var enemy in state.Enemies)
        {
            if (!enemy.IsAlive || enemy.Monster is null || enemy.MaxHp >= 1_000_000)
            {
                continue;
            }
            var threat = ThreatOf(enemy, state, seat);
            streams.Add((threat.PerTurn, enemy.CurrentHp + enemy.Block, threat.Damage));
        }
        if (streams.Count == 0)
        {
            return 0;
        }
        double time = 0;
        double incoming = 0;
        foreach (var (Rate, Hp, Chain) in streams.OrderByDescending(s => s.Rate / Math.Max(1, s.Hp)))
        {
            time += Hp / _damagePerTurn;
            for (var i = 0; i < time; i++)
            {
                var hit = i < Chain.Length ? Chain[i] : Rate;
                incoming += hit * Math.Min(1, time - i);
            }
        }
        double penalty = 0;
        foreach (var player in state.Players)
        {
            var creature = player.Creature;
            if (!creature.IsAlive)
            {
                continue;
            }
            var deficit = incoming - (creature.CurrentHp - 1) - (_blockPerTurn * time);
            penalty -= Tuning.RaceWeight * Math.Max(0, deficit);
        }
        return penalty;
    }

    private static string DebuffSignature(CombatState state)
    {
        var sb = new StringBuilder();
        foreach (var player in state.Players)
        {
            foreach (var power in player.Creature.Powers)
            {
                if (power.Amount > 0 && power.GetTypeForAmount(power.Amount) == PowerType.Debuff)
                {
                    sb.Append(power.Id.Entry).Append('=').Append(power.Amount).Append(',');
                }
                else if (power.Amount > 0 && power.Id.Entry.Contains("PLATING", StringComparison.Ordinal))
                {
                    sb.Append(power.Id.Entry).Append('=').Append(power.Amount).Append(',');
                }
            }
            sb.Append('|');
        }
        return sb.ToString();
    }

    private (int[] Self, int[] Plating) ProbeFor(CombatState state)
    {
        var zero = (new int[state.Players.Count], new int[state.Players.Count]);
        if (!_probe || Terminal || !CombatManager.Instance.IsInProgress)
        {
            return zero;
        }
        var signature = DebuffSignature(state);
        if (_probes.TryGetValue(signature, out var known))
        {
            return known;
        }
        var measured = _probes.Count < 64 ? ProbeTurnEnd(state) : zero;
        _probes[signature] = measured;
        return measured;
    }

    private (int[] Self, int[] Plating) ProbeTurnEnd(CombatState state)
    {
        var players = state.Players.ToList();
        var self = new int[players.Count];
        var plating = new int[players.Count];
        var attack = new int[players.Count];
        var targets = state.PlayerCreatures;
        foreach (var enemy in state.Enemies)
        {
            if (!enemy.IsAlive || enemy.Monster?.NextMove is not { } move)
            {
                continue;
            }
            foreach (var intent in move.Intents)
            {
                if (intent is not AttackIntent a)
                {
                    continue;
                }
                for (var p = 0; p < players.Count; p++)
                {
                    if (players[p].Creature.IsAlive)
                    {
                        using var scope = Session.ActAs(players[p]);
                        attack[p] += a.GetTotalDamage(targets, enemy);
                    }
                }
            }
        }
        var before = players.Select(pl => pl.Creature.CurrentHp).ToArray();
        var block = players.Select(pl => pl.Creature.Block).ToArray();
        for (var p = 0; p < players.Count; p++)
        {
            if (players[p].Creature.IsAlive && attack[p] + 5 >= before[p] + block[p])
            {
                return (self, plating);
            }
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var snap = Loader.Take();
        try
        {
            var plain = Measure(before);
            _ = Loader.Restore(snap, Session.Pump);
            foreach (var player in players)
            {
                if (player.Creature.IsAlive)
                {
                    player.Creature.Block += 10_000;
                }
            }
            var shielded = Measure(before);
            for (var p = 0; p < players.Count; p++)
            {
                if (!players[p].Creature.IsAlive || plain[p] >= before[p] || shielded[p] >= before[p])
                {
                    continue;
                }
                var blockable = plain[p] - shielded[p];
                var extra = blockable - Math.Max(0, attack[p] - block[p]);
                self[p] = Math.Max(0, extra) + shielded[p];
                plating[p] = Math.Max(0, -extra);
            }
        }
        catch (Exception e) when (e is LeakedAwaitException or InvalidOperationException)
        {
            Entry.Log.Warn($"turn-end probe skipped: {e.Message}");
        }
        finally
        {
            _ = Loader.Restore(snap, Session.Pump);
            snap.Release();
            ProbeMicros += sw.Elapsed.TotalMicroseconds;
            Probes++;
        }
        return (self, plating);
    }

    public static double ProbeMicros { get; private set; }

    public static int Probes { get; private set; }

    private int[] Measure(int[] before)
    {
        foreach (var action in Closing())
        {
            _ = Apply(action);
        }
        var (after, _) = Session.Current(0);
        var loss = new int[before.Length];
        for (var p = 0; p < before.Length && p < after.Players.Count; p++)
        {
            loss[p] = before[p] - after.Players[p].Creature.CurrentHp;
        }
        return loss;
    }

    private Threat ThreatOf(Creature enemy, CombatState state, Player player)
    {
        var monster = enemy.Monster!;
        var sb = new StringBuilder(96).Append(enemy.CombatId).Append('|').Append(monster.NextMove.Id).Append('|');
        Powers(sb, enemy);
        sb.Append('|');
        Powers(sb, player.Creature);
        var key = sb.ToString();
        if (_threats.TryGetValue(key, out var cached))
        {
            return cached;
        }
        var targets = state.PlayerCreatures;
        var damage = new int[Math.Max(2, Tuning.ThreatMoves)];
        double hits = 0;
        var moves = 0;
        MonsterState? node = monster.NextMove;
        using (Session.ActAs(player))
        {
            for (var guard = 0; node is not null && moves < damage.Length && guard < 16; guard++)
            {
                if (node is MoveState move)
                {
                    foreach (var intent in move.Intents)
                    {
                        if (intent is AttackIntent attack)
                        {
                            damage[moves] += attack.GetTotalDamage(targets, enemy);
                            hits += Math.Max(1, attack.Repeats);
                        }
                    }
                    moves++;
                }
                else if (node is RandomBranchState random)
                {
                    var expected = 0.0;
                    var total = 0.0;
                    var best = 0.0;
                    MonsterState? likely = null;
                    foreach (var branch in random.States)
                    {
                        if (
                            monster.MoveStateMachine is not { } fsm
                            || !fsm.States.TryGetValue(branch.stateId, out var candidate)
                            || candidate is not MoveState option
                        )
                        {
                            continue;
                        }
                        float weight;
                        try
                        {
                            weight = RandomBranchState.GetStateWeight(branch, enemy) * branch.GetWeight();
                        }
                        catch (Exception e) when (e is InvalidOperationException or NullReferenceException)
                        {
                            weight = 1;
                        }
                        if (weight <= 0)
                        {
                            continue;
                        }
                        var sum = 0;
                        foreach (var intent in option.Intents)
                        {
                            if (intent is AttackIntent attack)
                            {
                                sum += attack.GetTotalDamage(targets, enemy);
                            }
                        }
                        expected += weight * sum;
                        total += weight;
                        if (weight > best)
                        {
                            best = weight;
                            likely = option;
                        }
                    }
                    if (total <= 0 || likely is null)
                    {
                        break;
                    }
                    damage[moves] = (int)Math.Round(expected / total);
                    moves++;
                    node = likely;
                    continue;
                }
                string next;
                try
                {
                    next = node.GetNextState(enemy, null!);
                }
                catch (Exception e) when (e is InvalidOperationException or NullReferenceException or ArgumentException)
                {
                    break;
                }
                node =
                    monster.MoveStateMachine is { } machine && machine.States.TryGetValue(next, out var s) ? s : null;
            }
        }
        var mean = moves == 0 ? 0 : damage.Take(moves).Average();
        for (var i = moves; i < damage.Length; i++)
        {
            damage[i] = (int)mean;
        }
        var threat = new Threat(damage, mean, moves == 0 ? 0 : hits / moves, damage.Max(), moves);
        _threats[key] = threat;
        return threat;
    }

    public double Horizon()
    {
        _atTurnStart = true;
        try
        {
            return Estimate();
        }
        finally
        {
            _atTurnStart = false;
        }
    }

    private static int ExpectedBlock(Player player)
    {
        if (player.PlayerCombatState is not { } pcs || Tuning.HorizonBlock <= 0)
        {
            return 0;
        }
        var creature = player.Creature;
        var options = new List<(int Cost, int Block)>();
        foreach (var card in pcs.Hand.Cards)
        {
            if (!card.GainsBlock || !card.DynamicVars.TryGetValue("Block", out var v) || !card.CanPlay(out _, out _))
            {
                continue;
            }
            var cost = card.EnergyCost.CostsX ? pcs.Energy : card.EnergyCost.GetWithModifiers(CostModifiers.Local);
            var props = v is BlockVar bv ? bv.Props : ValueProp.Move;
            var block = (int)Hook.ModifyBlock(card.CombatState!, creature, v.BaseValue, props, card, null, out _);
            if (block > 0)
            {
                options.Add((cost, block));
            }
        }
        var energy = pcs.Energy;
        var total = 0;
        foreach (
            var (cost, block) in options.OrderByDescending(o =>
                o.Cost == 0 ? double.MaxValue : o.Block / (double)o.Cost
            )
        )
        {
            if (cost > energy)
            {
                continue;
            }
            energy -= cost;
            total += block;
        }
        if (player.Potions.Any(q => q.Id.Entry == "BLOCK_POTION"))
        {
            total += 12;
        }
        return total * Tuning.HorizonBlock / 100;
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
                else if (card.TargetType is TargetType.AnyPlayer or TargetType.AnyAlly)
                {
                    for (var k = 0; k < state.Players.Count; k++)
                    {
                        var ally = state.Players[k].Creature;
                        if (ally.IsAlive && card.IsValidTarget(ally))
                        {
                            list.Add(new SearchAction("play", p, h, -(k + 1), card.Id.Entry));
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
                else if (potion.TargetType is TargetType.AnyPlayer or TargetType.AnyAlly)
                {
                    for (var k = 0; k < state.Players.Count; k++)
                    {
                        var ally = state.Players[k].Creature;
                        if (ally.IsAlive && potion.IsValidTarget(ally))
                        {
                            list.Add(new SearchAction("potion", p, slot, -(k + 1), potion.Id.Entry));
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

    public TimeSpan Apply(SearchAction action)
    {
        Session.Selector.BeginAction(action.Choice);
        return action.Kind switch
        {
            "end" => Session.EndTurn(action.Player),
            "potion" => Session.UsePotion(action.Player, action.Hand, action.Target),
            _ => Session.Play(action.Player, action.Hand, action.Target),
        };
    }

    public IReadOnlyList<SearchAction> Variants(SearchAction action)
    {
        var options = Session.Selector.Options;
        if (action.Choice is not null || action.Kind == "end" || options < 2)
        {
            return [];
        }
        var list = new List<SearchAction>();
        for (var choice = 1; choice < Math.Min(options, Tuning.MaxVariants); choice++)
        {
            list.Add(action with { Choice = choice });
        }
        return list;
    }

    public double Evaluate()
    {
        var state = State();
        var players = state.Players;
        double score = 0;
        if (Terminal)
        {
            var alive = players.Where(p => p.Creature.IsAlive).ToList();
            if (alive.Count == 0)
            {
                var turn = players.Max(p => p.PlayerCombatState?.TurnNumber ?? 0);
                return -1_000_000 + (turn * 2_000) + (Dealt(state) * 10);
            }
            if (Tuning.TerminalWin >= 1_000_000)
            {
                return 1_000_000 + alive.Sum(p => p.Creature.CurrentHp * 100);
            }
            score = Tuning.TerminalWin;
        }
        if (Tuning.RatePricing)
        {
            Tempo(state);
        }
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
            if (present.TryGetValue(id, out var enemy) && enemy.MaxHp <= maxHp)
            {
                score += (maxHp - enemy.CurrentHp) * 10;
                if (!enemy.IsAlive)
                {
                    score += 500;
                }
                score -= enemy.Block;
                score += PowerScore(enemy, -1, state);
                if (Tuning.Doom)
                {
                    score += Doom(enemy);
                }
            }
            else
            {
                score += (maxHp * 10) + 500 + (enemy is null ? 0 : 1_000);
            }
        }
        var weakest = int.MaxValue;
        foreach (var enemy in state.Enemies)
        {
            if (enemy.CombatId is { } id && !_rootEnemyMaxHp.ContainsKey(id))
            {
                score -= enemy.CurrentHp * 10;
            }
            if (enemy.IsAlive && enemy.MaxHp < 1_000_000)
            {
                weakest = Math.Min(weakest, enemy.CurrentHp);
            }
        }
        if (weakest != int.MaxValue)
        {
            score -= Tuning.FocusFire * weakest;
        }
        if (Tuning.RaceWeight > 0)
        {
            score += Race(state);
        }
        foreach (var player in players)
        {
            var creature = player.Creature;
            score -= (creature.MaxHp - creature.CurrentHp) * HpWeight;
            score -= Tuning.ConvexHp * Math.Max(0, (0.4 * creature.MaxHp) - creature.CurrentHp);
            if (!creature.IsAlive)
            {
                score -= 100_000;
                continue;
            }
            score += PowerScore(creature, 1, state);
            score += player.Potions.Sum(q => q.Id.Entry == "BLOCK_POTION" ? _blockPotionValue : _potionValue);
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
        var incoming = new int[state.Players.Count];
        foreach (var enemy in state.Enemies)
        {
            if (enemy.Monster?.NextMove is not { } move || (!enemy.IsAlive && enemy.MaxHp < 1_000_000))
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
                for (var p = 0; p < state.Players.Count; p++)
                {
                    var player = state.Players[p];
                    if (!player.Creature.IsAlive)
                    {
                        continue;
                    }
                    using var scope = Session.ActAs(player);
                    incoming[p] += attack.GetTotalDamage(targets, enemy);
                }
            }
        }
        var (Self, Plating) = ProbeFor(state);
        for (var p = 0; p < state.Players.Count; p++)
        {
            var creature = state.Players[p].Creature;
            if (!creature.IsAlive)
            {
                continue;
            }
            var anticipated = _atTurnStart ? ExpectedBlock(state.Players[p]) : 0;
            var through = Math.Max(0, incoming[p] + Self[p] - creature.Block - Plating[p] - anticipated);
            score -= through * HpWeight;
            if (through >= creature.CurrentHp)
            {
                score -= 100_000 + (Tuning.GradedLethal ? 200 * (through - creature.CurrentHp) : 0);
            }
            var hpAfter = creature.CurrentHp - through;
            var following = 0;
            var spike = 0;
            foreach (var enemy in state.Enemies)
            {
                if (!enemy.IsAlive || enemy.Monster is null || enemy.MaxHp >= 1_000_000)
                {
                    continue;
                }
                var threat = ThreatOf(enemy, state, state.Players[p]);
                following += threat.Damage[1];
                for (var i = 1; i < threat.Damage.Length; i++)
                {
                    spike = Math.Max(spike, threat.Damage[i]);
                }
            }
            var cover = Tuning.BlockPrior;
            score -= HpWeight * Tuning.ReservePercent / 100.0 * Math.Max(0, following - cover - hpAfter);
            score -= HpWeight * Tuning.SpikePercent / 100.0 * Math.Max(0, spike - cover - hpAfter);
        }
        return score;
    }

    private static double Doom(Creature enemy)
    {
        double score = 0;
        foreach (var power in enemy.Powers)
        {
            if (power is SandpitPower sandpit && sandpit.Target is { IsAlive: true })
            {
                score += sandpit.Amount * 150;
                if (sandpit.Amount <= 1)
                {
                    score -= 3_000;
                }
            }
        }
        return score;
    }

    public static int Dealt(CombatState state)
    {
        var total = 0;
        foreach (var enemy in state.Enemies)
        {
            total += Math.Max(0, enemy.MaxHp - enemy.CurrentHp);
        }
        return total;
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

    private void Tempo(CombatState state)
    {
        _enemyBulk = 0;
        _incomingPerTurn = 0;
        var seat = state.Players.Count > 0 ? state.Players[Math.Min(ActivePlayer ?? 0, state.Players.Count - 1)] : null;
        foreach (var enemy in state.Enemies)
        {
            if (!enemy.IsAlive || enemy.Monster is null || enemy.MaxHp >= 1_000_000)
            {
                continue;
            }
            _enemyBulk += enemy.CurrentHp + enemy.Block;
            if (seat is not null && enemy.Monster.NextMove is not null)
            {
                var threat = ThreatOf(enemy, state, seat);
                _incomingPerTurn += threat.PerTurn;
            }
        }
    }

    private double? RatePrice(Creature creature, PowerModel power, int amount, int sign, CombatState state)
    {
        var turns = Math.Min(Math.Abs(amount), _horizon);
        if (sign < 0 && creature.Monster is not null && state.Players.Count > 0 && _enemyBulk > 0)
        {
            var seat = state.Players[Math.Min(ActivePlayer ?? 0, state.Players.Count - 1)];
            var threat = ThreatOf(creature, state, seat);
            var hits = threat.HitsPerTurn;
            var share = (double)(creature.CurrentHp + creature.Block) / _enemyBulk;
            return power switch
            {
                StrengthPower => amount * hits * _horizon * HpWeight,
                VulnerablePower when Tuning.RateDebuffs => -0.5 * _damagePerTurn * turns * 10 * share,
                WeakPower when Tuning.RateDebuffs => -0.25 * threat.PerTurn * turns * HpWeight,
                _ => null,
            };
        }
        return power switch
        {
            StrengthPower => amount * _attacksPerTurn * _horizon * 10,
            DexterityPower => amount * _skillsPerTurn * _horizon * HpWeight,
            VulnerablePower when Tuning.RateDebuffs => -0.5 * _incomingPerTurn * turns * HpWeight,
            WeakPower when Tuning.RateDebuffs => -0.25 * _damagePerTurn * turns * 10,
            FrailPower when Tuning.RateDebuffs => -0.25 * _blockPerTurn * turns * HpWeight,
            _ => null,
        };
    }

    private double PowerScore(Creature creature, int sign, CombatState state)
    {
        double score = 0;
        var temporaryStrength = 0;
        var temporaryDexterity = 0;
        foreach (var power in creature.Powers)
        {
            switch (power)
            {
                case TemporaryStrengthPower strength:
                    temporaryStrength += strength.Sign * strength.Amount;
                    break;
                case TemporaryDexterityPower dexterity:
                    temporaryDexterity += dexterity.Sign * dexterity.Amount;
                    break;
                default:
                    break;
            }
        }
        foreach (var power in creature.Powers)
        {
            if (Tuning.TemporaryPowers && power is ITemporaryPower)
            {
                continue;
            }
            var (weight, cap) = PowerWeights.For(power.Id.Entry, sign < 0);
            if (weight == 0)
            {
                continue;
            }
            var amount = power switch
            {
                StrengthPower when Tuning.TemporaryPowers => power.Amount - temporaryStrength,
                DexterityPower when Tuning.TemporaryPowers => power.Amount - temporaryDexterity,
                _ => power.Amount,
            };
            if (amount == 0)
            {
                continue;
            }
            if (Tuning.RatePricing && RatePrice(creature, power, amount, sign, state) is { } priced)
            {
                score += sign * priced;
                continue;
            }
            var polarity = weight < 0 || power.GetTypeForAmount(amount) != PowerType.Debuff ? 1 : -1;
            score += sign * polarity * Math.Min(Math.Abs(amount), cap) * weight;
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
            sb.Append(power.Id.Entry).Append('=').Append(power.Amount);
            if (power is SurroundedPower surrounded)
            {
                sb.Append('/').Append((int)surrounded.Facing);
            }
            sb.Append(',');
        }
    }
}
