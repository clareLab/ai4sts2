using System.Diagnostics;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Ai4Sts2.Workbench;

public sealed record TurnTrace(
    int Turn,
    IReadOnlyList<int> PlayerHp,
    IReadOnlyList<int> EnemyHp,
    IReadOnlyList<SearchAction> Line,
    double Score,
    int Nodes,
    double Micros
);

public sealed record FightSummary(
    string Encounter,
    bool Won,
    int HpBefore,
    int HpAfter,
    int Turns,
    int Nodes,
    double Micros
);

public sealed record RolloutSummary(
    int Fights,
    int Wins,
    int HpLost,
    double Score,
    IReadOnlyList<FightSummary> Details,
    FightSummary? Boss,
    int BossDamage
);

public sealed record RolloutPlan(int Fights, int MaxTurns, bool Boss, int BossTurns);

public sealed record RewardOptionResult(string Label, int? Card, string? Alternative, RolloutSummary Rollout);

public sealed record RewardEvaluation(int Index, IReadOnlyList<RewardOptionResult> Options, string Best, double Micros);

public sealed record ChoiceResult(string Label, int? Index, RolloutSummary Rollout);

public sealed record ChoiceEvaluation(string Kind, IReadOnlyList<ChoiceResult> Options, string Best, double Micros);

public sealed record PathResult(
    MapChoice Choice,
    string? Room,
    int Hp,
    int Gold,
    int Deck,
    int Relics,
    int Potions,
    double Score,
    string? Error
);

public sealed record PathEvaluation(IReadOnlyList<PathResult> Options, MapChoice? Best, double Micros);

public static class Rollout
{
    public static (SearchResult<SearchAction> Result, IReadOnlyList<SearchAction> Line) SearchTurn(
        Session session,
        SearchOptions options,
        bool coordinate
    )
    {
        var domain = new CombatDomain(session) { CanonicalKeys = options.Canonical };
        var (state, _) = Session.Current(0);
        if (!coordinate || state.Players.Count < 2)
        {
            var single = new Search<SearchAction>(domain, options).Run();
            return (single, single.Line);
        }
        var root = Loader.Take();
        var line = new List<SearchAction>();
        SearchResult<SearchAction>? last = null;
        var nodes = 0;
        var micros = 0.0;
        for (var p = 0; p < state.Players.Count; p++)
        {
            var player = state.Players[p];
            if (Session.HasEnded(player) || !player.Creature.IsAlive || domain.Terminal)
            {
                continue;
            }
            domain.ActivePlayer = p;
            var result = new Search<SearchAction>(domain, options with { Turns = 1 }).Run();
            nodes += result.Nodes;
            micros += result.Micros;
            last = result;
            foreach (var action in result.Line)
            {
                if (action.Kind == "end" || domain.Terminal)
                {
                    break;
                }
                _ = domain.Apply(action);
                line.Add(action);
            }
        }
        domain.ActivePlayer = null;
        _ = Loader.Restore(root, session.Pump);
        root.Release();
        line.AddRange(domain.Closing());
        var summary = last is null
            ? new SearchResult<SearchAction>(
                0,
                0,
                line,
                "coordinate",
                1,
                nodes,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                [],
                micros,
                0,
                0,
                0,
                true
            )
            : last with
            {
                Line = line,
                Leaf = "coordinate",
                Nodes = nodes,
                Micros = micros,
            };
        return (summary, line);
    }

    public static (bool Won, int Turns, int Nodes, double Micros) PlayCombat(
        Session session,
        SearchOptions options,
        int maxTurns,
        List<TurnTrace>? trace = null,
        bool coordinate = true
    )
    {
        var sw = Stopwatch.StartNew();
        var turns = 0;
        var nodes = 0;
        while (CombatManager.Instance.IsInProgress && turns < maxTurns)
        {
            var (result, chosen) = SearchTurn(session, options, coordinate);
            nodes += result.Nodes;
            var line = chosen.Count > 0 ? chosen : [new SearchAction("end", 0, -1, null, null)];
            if (trace is not null)
            {
                var (state, player) = Session.Current(0);
                trace.Add(
                    new TurnTrace(
                        player.PlayerCombatState?.TurnNumber ?? turns + 1,
                        state.Players.Select(p => p.Creature.CurrentHp).ToList(),
                        state.Enemies.Select(e => e.CurrentHp).ToList(),
                        line,
                        result.Score,
                        result.Nodes,
                        result.Micros
                    )
                );
            }
            foreach (var action in line)
            {
                if (!CombatManager.Instance.IsInProgress)
                {
                    break;
                }
                _ = action.Kind switch
                {
                    "end" => session.EndTurn(action.Player),
                    "potion" => session.UsePotion(action.Player, action.Hand, action.Target),
                    _ => session.Play(action.Player, action.Hand, action.Target),
                };
            }
            turns++;
        }
        var alive = session.Run!.Players.Any(p => p.Creature.IsAlive);
        return (!CombatManager.Instance.IsInProgress && alive, turns, nodes, sw.Elapsed.TotalMicroseconds);
    }

    public static RolloutSummary Fights(Session session, SearchOptions options, RolloutPlan plan)
    {
        var run = session.Run ?? throw new InvalidOperationException("run not set up");
        var details = new List<FightSummary>();
        var wins = 0;
        var lost = 0;
        var fights = plan.Fights;
        var maxTurns = plan.MaxTurns;
        for (var i = 0; i < fights; i++)
        {
            var encounter = run.Act.PullNextEncounter(RoomType.Monster);
            run.Act.MarkRoomVisited(RoomType.Monster);
            var before = run.Players.Sum(p => p.Creature.CurrentHp);
            _ = session.StartEncounter(encounter.Id.Entry, false);
            var (won, turns, nodes, micros) = PlayCombat(session, options, maxTurns);
            var after = run.Players.Sum(p => p.Creature.CurrentHp);
            details.Add(new FightSummary(encounter.Id.Entry, won, before, after, turns, nodes, micros));
            lost += Math.Max(0, before - after);
            if (!won)
            {
                break;
            }
            wins++;
        }
        var score = (wins * 1000) - (lost * 10) - ((fights - wins) * 5000);
        FightSummary? boss = null;
        var bossDamage = 0;
        if (plan.Boss && wins == fights)
        {
            var encounter = run.Act.PullNextEncounter(RoomType.Boss);
            var before = run.Players.Sum(p => p.Creature.CurrentHp);
            var state = session.StartEncounter(encounter.Id.Entry, true);
            var bossMax = state.Enemies.Sum(e => e.MaxHp);
            var (won, turns, nodes, micros) = PlayCombat(session, options, plan.BossTurns);
            var after = run.Players.Sum(p => p.Creature.CurrentHp);
            var remaining = state.Enemies.Where(e => e.IsAlive).Sum(e => e.CurrentHp);
            bossDamage = bossMax - remaining;
            boss = new FightSummary(encounter.Id.Entry, won, before, after, turns, nodes, micros);
            var alive = run.Players.Any(p => p.Creature.IsAlive);
            score += (bossDamage * 3) - (Math.Max(0, before - after) * 6) + (won ? 3000 : 0) - (alive ? 0 : 4000);
        }
        return new RolloutSummary(fights, wins, lost, score, details, boss, bossDamage);
    }

    public static ChoiceEvaluation EvaluateChoices(
        Session session,
        string kind,
        IReadOnlyList<(string Label, int? Index, Action Apply)> choices,
        SearchOptions options,
        RolloutPlan plan
    )
    {
        var sw = Stopwatch.StartNew();
        var root = Loader.Take();
        var results = new List<ChoiceResult>();
        try
        {
            foreach (var (label, index, apply) in choices)
            {
                apply();
                results.Add(new ChoiceResult(label, index, Fights(session, options, plan)));
                _ = Loader.Restore(root, session.Pump);
            }
        }
        finally
        {
            _ = Loader.Restore(root, session.Pump);
            root.Release();
        }
        var best = results.OrderByDescending(r => r.Rollout.Score).First().Label;
        return new ChoiceEvaluation(kind, results, best, sw.Elapsed.TotalMicroseconds);
    }

    public static PathEvaluation EvaluatePaths(Session session, SearchOptions options, int maxTurns)
    {
        var sw = Stopwatch.StartNew();
        var run = session.Run ?? throw new InvalidOperationException("run not set up");
        var flow = session.Flow;
        var choices = flow.View().Choices;
        var root = Loader.Take();
        var results = new List<PathResult>();
        try
        {
            foreach (var choice in choices)
            {
                string? error = null;
                var before = Snapshot(run);
                try
                {
                    flow.Travel(choice.Col, choice.Row);
                    ResolveRoom(session, options, maxTurns);
                }
                catch (Exception e) when (e is LeakedAwaitException or InvalidOperationException or ArgumentException)
                {
                    error = e.Message;
                }
                var after = Snapshot(run);
                var alive = run.Players.All(p => p.Creature.IsAlive);
                var score = alive ? Score(before, after) : -100_000;
                results.Add(
                    new PathResult(
                        choice,
                        flow.View().RoomModel,
                        after.Hp,
                        after.Gold,
                        after.Deck,
                        after.Relics,
                        after.Potions,
                        score,
                        error
                    )
                );
                _ = Loader.Restore(root, session.Pump);
            }
        }
        finally
        {
            _ = Loader.Restore(root, session.Pump);
            root.Release();
        }
        var best = results.Count > 0 ? results.OrderByDescending(r => r.Score).First().Choice : null;
        return new PathEvaluation(results, best, sw.Elapsed.TotalMicroseconds);
    }

    private static (int Hp, int Gold, int Deck, int Relics, int Potions) Snapshot(RunState run)
    {
        var players = run.Players;
        return (
            players.Sum(p => p.Creature.CurrentHp),
            players.Sum(p => p.Gold),
            players.Sum(p =>
                p.Deck.Cards.Count(c => c.Rarity != CardRarity.Basic) + p.Deck.Cards.Count(c => c.IsUpgraded)
            ),
            players.Sum(p => p.Relics.Count),
            players.Sum(p => p.Potions.Count())
        );
    }

    private static double Score(
        (int Hp, int Gold, int Deck, int Relics, int Potions) before,
        (int Hp, int Gold, int Deck, int Relics, int Potions) after
    ) =>
        ((after.Hp - before.Hp) * 10)
        + ((after.Gold - before.Gold) * 0.6)
        + ((after.Deck - before.Deck) * 40)
        + ((after.Relics - before.Relics) * 120)
        + ((after.Potions - before.Potions) * 60);

    private static void ResolveRoom(Session session, SearchOptions options, int maxTurns)
    {
        var flow = session.Flow;
        var run = session.Run!;
        for (var step = 0; step < 4; step++)
        {
            var view = flow.View();
            if (view.InCombat)
            {
                var (won, _, _, _) = PlayCombat(session, options, maxTurns);
                if (!won)
                {
                    return;
                }
                TakeRewardsGreedy(flow);
                if (run.CurrentRoomCount > 1)
                {
                    flow.Proceed();
                    continue;
                }
                return;
            }
            if (view.Room == "EventRoom" && !view.EventFinished)
            {
                var option = view.EventOptions.FirstOrDefault(o => !o.Locked && !o.Proceed && !o.Chosen);
                if (option is null)
                {
                    return;
                }
                flow.ChooseEvent(option.Index);
                continue;
            }
            if (view.RestOptions.Count > 0)
            {
                var me = run.Players[0].Creature;
                var wanted = me.CurrentHp < me.MaxHp * 0.6 ? "HEAL" : "SMITH";
                var pick =
                    view.RestOptions.FirstOrDefault(o => o.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    ?? view.RestOptions[0];
                _ = flow.Rest(pick);
                return;
            }
            if (view.Room == "TreasureRoom")
            {
                _ = flow.OpenChest();
                var relics = flow.View().TreasureRelics;
                _ = flow.PickRelic(relics.Count > 0 ? 0 : null);
                return;
            }
            return;
        }
    }

    private static void TakeRewardsGreedy(RunFlow flow)
    {
        var offered = flow.OfferRewards();
        foreach (var set in offered)
        {
            foreach (var reward in set.Rewards)
            {
                if (reward.Taken)
                {
                    continue;
                }
                _ =
                    reward.Kind == "card"
                        ? flow.TakeRewardUnsynchronized(reward.Index, 0, null)
                        : flow.TakeRewardUnsynchronized(reward.Index, null, null);
            }
            break;
        }
    }

    public static RewardEvaluation EvaluateCardReward(
        Session session,
        int rewardIndex,
        SearchOptions options,
        RolloutPlan plan
    )
    {
        var sw = Stopwatch.StartNew();
        var view = session.Flow.View();
        var reward =
            view.Rewards.SelectMany(r => r.Rewards).FirstOrDefault(r => r.Index == rewardIndex && r.Kind == "card")
            ?? throw new InvalidOperationException($"reward {rewardIndex} is not an open card reward");
        var choices = new List<(string Label, int? Card, string? Alternative)>();
        for (var i = 0; i < reward.Cards!.Count; i++)
        {
            choices.Add((reward.Cards[i], i, null));
        }
        choices.Add(("Skip", null, null));
        var root = Loader.Take();
        var results = new List<RewardOptionResult>();
        try
        {
            foreach (var (label, card, alternative) in choices)
            {
                if (card is not null)
                {
                    _ = session.Flow.TakeRewardUnsynchronized(rewardIndex, card, alternative);
                }
                var summary = Fights(session, options, plan);
                results.Add(new RewardOptionResult(label, card, alternative, summary));
                _ = Loader.Restore(root, session.Pump);
            }
        }
        finally
        {
            _ = Loader.Restore(root, session.Pump);
            root.Release();
        }
        var best = results.OrderByDescending(r => r.Rollout.Score).First().Label;
        return new RewardEvaluation(rewardIndex, results, best, sw.Elapsed.TotalMicroseconds);
    }
}
