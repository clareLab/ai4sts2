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
        var sw = Stopwatch.StartNew();
        var party = new PartySearch(session, domain, options with { Turns = 1 }, Math.Max(1, options.Beam));
        var (score, line) = party.Solve(Math.Max(1, options.Turns));
        var summary = new SearchResult<SearchAction>(
            score,
            score,
            line,
            "coordinate",
            options.Turns,
            party.Nodes,
            0,
            0,
            0,
            0,
            party.Joints,
            party.Heads,
            0,
            0,
            [],
            sw.Elapsed.TotalMicroseconds,
            0,
            0,
            0,
            true
        );
        return (summary, line);
    }

    private sealed class PartySearch(Session session, CombatDomain domain, SearchOptions single, int width)
    {
        public int Nodes { get; private set; }

        public int Joints { get; private set; }

        public int Heads { get; private set; }

        public (double Score, List<SearchAction> Line) Solve(int turns)
        {
            var (state, _) = Session.Current(0);
            var order = new List<int>();
            for (var p = 0; p < state.Players.Count; p++)
            {
                var player = state.Players[p];
                if (player.Creature.IsAlive && !Session.HasEnded(player))
                {
                    order.Add(p);
                }
            }
            if (order.Count == 0 || domain.Terminal)
            {
                return (domain.Evaluate(), []);
            }
            var root = Loader.Take();
            try
            {
                domain.ActivePlayer = order[0];
                var head = new Search<SearchAction>(domain, single).Run();
                Nodes += head.Nodes;
                var heads = new List<List<SearchAction>> { Strip(head.Line) };
                if (turns > 1)
                {
                    foreach (var entry in head.Beam)
                    {
                        var stripped = Strip(entry.Line);
                        if (heads.Count >= width)
                        {
                            break;
                        }
                        if (heads.All(h => !h.SequenceEqual(stripped)))
                        {
                            heads.Add(stripped);
                        }
                    }
                }
                Heads += heads.Count;
                var best = double.NegativeInfinity;
                var bestLine = new List<SearchAction>();
                foreach (var lead in heads)
                {
                    _ = Loader.Restore(root, session.Pump);
                    var joint = new List<SearchAction>();
                    Follow(lead, joint);
                    foreach (var p in order.Skip(1))
                    {
                        var (current, _) = Session.Current(0);
                        var player = current.Players[p];
                        if (domain.Terminal || !player.Creature.IsAlive || Session.HasEnded(player))
                        {
                            continue;
                        }
                        domain.ActivePlayer = p;
                        var result = new Search<SearchAction>(domain, single).Run();
                        Nodes += result.Nodes;
                        Follow(Strip(result.Line), joint);
                    }
                    domain.ActivePlayer = null;
                    var closing = domain.Closing();
                    foreach (var action in closing)
                    {
                        _ = domain.Apply(action);
                    }
                    var score = turns > 1 && !domain.Terminal ? Solve(turns - 1).Score : domain.Evaluate();
                    Joints++;
                    if (score > best)
                    {
                        best = score;
                        bestLine = joint.Concat(closing).ToList();
                    }
                }
                return (best, bestLine);
            }
            finally
            {
                domain.ActivePlayer = null;
                _ = Loader.Restore(root, session.Pump);
                root.Release();
            }
        }

        private void Follow(List<SearchAction> line, List<SearchAction> joint)
        {
            foreach (var action in line)
            {
                if (domain.Terminal)
                {
                    break;
                }
                _ = domain.Apply(action);
                joint.Add(action);
            }
        }

        private static List<SearchAction> Strip(IReadOnlyList<SearchAction> line) =>
            line.TakeWhile(a => a.Kind != "end").ToList();
    }

    public static (bool Won, int Turns, int Nodes, double Micros) PlayCombat(
        Session session,
        SearchOptions options,
        int maxTurns,
        List<TurnTrace>? trace = null,
        bool coordinate = true
    )
    {
        session.Selector.Clear();
        var sw = Stopwatch.StartNew();
        var turns = 0;
        var nodes = 0;
        var recording = Harness.Recorder.Active;
        var hpStart = session.Run?.Players.Sum(p => p.Creature.CurrentHp) ?? 0;
        var fightId = recording
            ? Harness.Recorder.BeginFight(
                Session.Current(0).State.Encounter?.Id.Entry ?? "?",
                session.Run?.TotalFloor ?? 0,
                null
            )
            : 0;
        using var suspended = Harness.Recorder.Suspend();
        while (CombatManager.Instance.IsInProgress && turns < maxTurns)
        {
            var (result, chosen) = SearchTurn(session, options, coordinate);
            nodes += result.Nodes;
            if (
                options.Escalate > 0
                && options.Turns < 2
                && result.Score < options.Escalate
                && !CoordinateOnly(session)
            )
            {
                var deeper = options with
                {
                    Turns = 2,
                    Beam = Math.Max(options.Beam, 5),
                    MaxNodes = Math.Max(options.MaxNodes, 800),
                };
                var (escalated, chosenDeeper) = SearchTurn(session, deeper, coordinate);
                nodes += escalated.Nodes;
                if (escalated.Score >= result.Score)
                {
                    result = escalated;
                    chosen = chosenDeeper;
                }
            }
            var line = chosen.Count > 0 ? chosen : [new SearchAction("end", 0, -1, null, null)];
            if (recording)
            {
                Harness.Recorder.Turn(fightId, turns + 1, line, result.Score, result.Nodes);
            }
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
            var replay = new CombatDomain(session, false);
            foreach (var action in line)
            {
                if (!CombatManager.Instance.IsInProgress)
                {
                    break;
                }
                if (action.Kind == "play" && action.Card is { } played)
                {
                    session.CardPlays[played] = session.CardPlays.GetValueOrDefault(played) + 1;
                }
                _ = replay.Apply(action);
            }
            turns++;
        }
        var alive = session.Run!.Players.Any(p => p.Creature.IsAlive);
        var won = !CombatManager.Instance.IsInProgress && alive;
        if (recording)
        {
            Harness.Recorder.EndFight(fightId, won, hpStart, session.Run.Players.Sum(p => p.Creature.CurrentHp), turns);
        }
        return (won, turns, nodes, sw.Elapsed.TotalMicroseconds);
    }

    private static bool CoordinateOnly(Session session) => session.Run is { } run && run.Players.Count > 1;

    public static RolloutSummary Fights(Session session, SearchOptions options, RolloutPlan plan)
    {
        using var quiet = Harness.Recorder.Suspend();
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
        var hpAfterFights = run.Players.Sum(p => p.Creature.CurrentHp);
        var score = (wins * 1000) + (hpAfterFights * 10) - ((fights - wins) * 5000);
        FightSummary? boss = null;
        var bossDamage = 0;
        if (plan.Boss && wins == fights)
        {
            var encounter = run.Act.PullNextEncounter(RoomType.Boss);
            var before = run.Players.Sum(p => p.Creature.CurrentHp);
            var state = session.StartEncounter(encounter.Id.Entry, false);
            var bossMax = state.Enemies.Sum(e => e.MaxHp);
            var (won, turns, nodes, micros) = PlayCombat(session, options, plan.BossTurns);
            var after = run.Players.Sum(p => p.Creature.CurrentHp);
            var remaining = state.Enemies.Where(e => e.IsAlive).Sum(e => e.CurrentHp);
            bossDamage = bossMax - remaining;
            boss = new FightSummary(encounter.Id.Entry, won, before, after, turns, nodes, micros);
            var alive = run.Players.Any(p => p.Creature.IsAlive);
            score += (bossDamage * 3) + (after * 6) - (hpAfterFights * 6) + (won ? 3000 : 0) - (alive ? 0 : 4000);
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
        var rollouts = Tournament(session, choices.Select(c => c.Apply).ToList(), options, plan, out var finalists);
        var results = choices.Select((c, i) => new ChoiceResult(c.Label, c.Index, rollouts[i])).ToList();
        var best = finalists.Count > 0 ? results[finalists.MaxBy(i => rollouts[i].Score)].Label : "";
        return new ChoiceEvaluation(kind, results, best, sw.Elapsed.TotalMicroseconds);
    }

    private static List<RolloutSummary> Tournament(
        Session session,
        List<Action> applies,
        SearchOptions options,
        RolloutPlan plan,
        out List<int> finalists
    )
    {
        var root = Loader.Take();
        var results = new RolloutSummary[applies.Count];
        finalists = Enumerable.Range(0, applies.Count).ToList();
        try
        {
            if (applies.Count > Tuning.HalvingAbove && Tuning.HalvingAbove > 0)
            {
                var screen = plan with { Fights = 1, Boss = false };
                foreach (var i in finalists)
                {
                    applies[i]();
                    results[i] = Fights(session, options, screen);
                    _ = Loader.Restore(root, session.Pump);
                }
                var keep = Math.Max(3, (applies.Count + 1) / 2);
                var ordered = finalists.OrderByDescending(i => results[i].Score).ToList();
                var cut = results[ordered[Math.Min(keep, ordered.Count) - 1]].Score;
                finalists = ordered.Where((i, rank) => rank < keep || results[i].Score >= cut).ToList();
            }
            foreach (var i in finalists)
            {
                applies[i]();
                results[i] = Fights(session, options, plan);
                _ = Loader.Restore(root, session.Pump);
            }
        }
        finally
        {
            _ = Loader.Restore(root, session.Pump);
            root.Release();
        }
        return results.ToList();
    }

    public static PathEvaluation EvaluateEvent(Session session, SearchOptions options, int maxTurns)
    {
        using var quiet = Harness.Recorder.Suspend();
        var sw = Stopwatch.StartNew();
        var run = session.Run ?? throw new InvalidOperationException("run not set up");
        var flow = session.Flow;
        var view = flow.View();
        var options0 = view.EventOptions.Where(o => !o.Locked && !o.Proceed && !o.Chosen).ToList();
        var root = Loader.Take();
        var results = new List<PathResult>();
        try
        {
            foreach (var option in options0.Select(o => (EventOptionView?)o).Append(null))
            {
                string? error = null;
                var before = Snapshot(run);
                try
                {
                    if (option is { } pick)
                    {
                        flow.ChooseEvent(pick.Index);
                        ResolveRoom(session, options, maxTurns);
                    }
                }
                catch (Exception e) when (e is LeakedAwaitException or InvalidOperationException or ArgumentException)
                {
                    error = e.Message;
                }
                var after = Snapshot(run);
                var alive = run.Players.All(p => p.Creature.IsAlive);
                var label = option is { } o2 ? o2.Key : "leave";
                var choice = new MapChoice(option?.Index ?? -1, 0, label);
                results.Add(
                    new PathResult(
                        choice,
                        view.Event,
                        after.Hp,
                        after.Gold,
                        after.Deck,
                        after.Relics,
                        after.Potions,
                        alive ? Score(before, after) : -100_000,
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

    public static PathEvaluation EvaluatePaths(Session session, SearchOptions options, int maxTurns)
    {
        using var quiet = Harness.Recorder.Suspend();
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

    private static (int Hp, int MaxHp, int Gold, int Deck, int Relics, int Potions) Snapshot(RunState run)
    {
        var players = run.Players;
        return (
            players.Sum(p => p.Creature.CurrentHp),
            players.Sum(p => p.Creature.MaxHp),
            players.Sum(p => p.Gold),
            players.Sum(p =>
                p.Deck.Cards.Count(c =>
                    c.Rarity != CardRarity.Basic && c.Type is not CardType.Curse and not CardType.Status
                )
                + p.Deck.Cards.Count(c => c.IsUpgraded)
                - (2 * p.Deck.Cards.Count(c => c.Type is CardType.Curse or CardType.Status))
            ),
            players.Sum(p => p.Relics.Count),
            players.Sum(p => p.Potions.Count())
        );
    }

    private static double Score(
        (int Hp, int MaxHp, int Gold, int Deck, int Relics, int Potions) before,
        (int Hp, int MaxHp, int Gold, int Deck, int Relics, int Potions) after
    ) =>
        ((after.Hp - before.Hp) * 10)
        + ((after.MaxHp - before.MaxHp) * 12)
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
                _ = flow.PickRelics(run.Players.Select((_, i) => i < relics.Count ? i : (int?)null).ToList());
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
                        ? flow.TakeRewardUnsynchronized(reward.Index, 0, null, set.Player)
                        : flow.TakeRewardUnsynchronized(reward.Index, null, null, set.Player);
            }
        }
    }

    public static RewardEvaluation EvaluateCardReward(
        Session session,
        int rewardIndex,
        SearchOptions options,
        RolloutPlan plan,
        int player = 0
    )
    {
        var sw = Stopwatch.StartNew();
        var view = session.Flow.View();
        var reward =
            view.Rewards.Where(r => r.Player == player)
                .SelectMany(r => r.Rewards)
                .FirstOrDefault(r => r.Index == rewardIndex && r.Kind == "card")
            ?? throw new InvalidOperationException(
                $"reward {rewardIndex} is not an open card reward for player {player}"
            );
        var choices = new List<(string Label, int? Card, string? Alternative)>();
        for (var i = 0; i < reward.Cards!.Count; i++)
        {
            choices.Add((reward.Cards[i], i, null));
        }
        choices.Add(("Skip", null, null));
        var applies = choices
            .Select(c => new Action(() =>
            {
                if (c.Card is not null)
                {
                    _ = session.Flow.TakeRewardUnsynchronized(rewardIndex, c.Card, c.Alternative, player);
                }
            }))
            .ToList();
        var rollouts = Tournament(session, applies, options, plan, out var finalists);
        var results = choices
            .Select((c, i) => new RewardOptionResult(c.Label, c.Card, c.Alternative, rollouts[i]))
            .ToList();
        var best = results[finalists.MaxBy(i => rollouts[i].Score)].Label;
        return new RewardEvaluation(rewardIndex, results, best, sw.Elapsed.TotalMicroseconds);
    }
}
