using System.Diagnostics;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Rooms;

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

public static class Rollout
{
    public static (bool Won, int Turns, int Nodes, double Micros) PlayCombat(
        Session session,
        SearchOptions options,
        int maxTurns,
        List<TurnTrace>? trace = null
    )
    {
        var sw = Stopwatch.StartNew();
        var turns = 0;
        var nodes = 0;
        while (CombatManager.Instance.IsInProgress && turns < maxTurns)
        {
            var result = new Search<SearchAction>(new CombatDomain(session), options).Run();
            nodes += result.Nodes;
            var line = result.Line.Count > 0 ? result.Line : [new SearchAction("end", 0, -1, null, null)];
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
