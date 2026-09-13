using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Ai4Sts2.Workbench;

public sealed record MapChoice(int Col, int Row, string Type);

public sealed record RewardView(
    int Index,
    string Kind,
    bool Taken,
    int? Gold,
    string? Potion,
    string? Relic,
    IReadOnlyList<string>? Cards,
    IReadOnlyList<string>? Alternatives
);

public sealed record RewardsView(int Player, int Id, bool Completed, IReadOnlyList<RewardView> Rewards);

public sealed record RunView(
    int Act,
    int Floor,
    int ActFloor,
    string? Room,
    string? RoomModel,
    bool InCombat,
    bool CombatFinished,
    MapCoord? Coord,
    IReadOnlyList<MapChoice> Choices,
    IReadOnlyList<RewardsView> Rewards,
    IReadOnlyList<string> RestOptions
);

public sealed class RunFlow(Session session)
{
    private readonly List<(RewardsSet Set, Task Done)> _offered = [];

    public void Begin()
    {
        var run = session.Run ?? throw new InvalidOperationException("run not set up");
        _offered.Clear();
        RunManager.Instance.GenerateRooms();
        session.Pump.Drive(() => RunManager.Instance.EnterAct(0, false), "enter act");
        run.ExtraFields.StartedWithNeow = false;
    }

    public RunView View()
    {
        var run = session.Run ?? throw new InvalidOperationException("run not set up");
        var room = run.CurrentRoom;
        var choices = new List<MapChoice>();
        if (run.CurrentRoomCount == 0 || room is MapRoom || (room is { } && !CombatManager.Instance.IsInProgress))
        {
            IEnumerable<MapPoint> points =
                run.CurrentMapPoint is { } current ? current.Children
                : run.Map.startMapPoints.Count > 0 ? run.Map.startMapPoints
                : [run.Map.StartingMapPoint];
            choices.AddRange(
                points
                    .OrderBy(p => p.coord.col)
                    .Select(p => new MapChoice(p.coord.col, p.coord.row, p.PointType.ToString()))
            );
        }
        var sync = RunManager.Instance.RewardsSetSynchronizer;
        var rewards = _offered
            .Select(entry => new RewardsView(
                run.Players.ToList().IndexOf(entry.Set.Player),
                entry.Set.Id,
                sync.IsRewardsSetCompleted(entry.Set),
                entry.Set.Rewards.Select((r, j) => Describe(r, j)).ToList()
            ))
            .ToList();
        var rest = room is RestSiteRoom site ? site.Options.Select(o => o.OptionId).ToList() : [];
        return new RunView(
            run.CurrentActIndex,
            run.TotalFloor,
            run.ActFloor,
            room?.GetType().Name,
            room?.ModelId?.Entry,
            CombatManager.Instance.IsInProgress,
            room is CombatRoom { IsPreFinished: true },
            run.CurrentMapCoord,
            choices,
            rewards,
            rest
        );
    }

    public void Travel(int col, int row)
    {
        var run = session.Run ?? throw new InvalidOperationException("run not set up");
        var coord = new MapCoord(col, row);
        if (!run.Map.HasPoint(coord))
        {
            throw new ArgumentException($"no map point at {col},{row}");
        }
        _offered.Clear();
        session.DropSnapshots();
        session.Pump.Drive(() => RunManager.Instance.EnterMapCoord(coord), $"travel {col},{row}");
        if (run.CurrentRoom is CombatRoom && CombatManager.Instance.IsInProgress)
        {
            session.RequirePlayable(-1, "travel into combat");
        }
    }

    public IReadOnlyList<RewardsView> OfferRewards()
    {
        var run = session.Run ?? throw new InvalidOperationException("run not set up");
        if (run.CurrentRoom is not CombatRoom room || CombatManager.Instance.IsInProgress)
        {
            throw new InvalidOperationException("rewards are offered after a won combat");
        }
        if (_offered.Count > 0)
        {
            return View().Rewards;
        }
        var sync = RunManager.Instance.RewardsSetSynchronizer;
        foreach (var player in run.Players)
        {
            RewardsSet? set = null;
            session.Pump.Drive(
                async () =>
                {
                    set = await RewardsCmd.GenerateForRoomEnd(player, room);
                    await Hook.BeforeCombatRewardOffered(set, run, room);
                },
                "generate rewards"
            );
            set!.ThrowInTestIfRewardsNotTaken = false;
            var done = sync.BeginRewardsSet(set);
            _offered.Add((set, done));
        }
        return View().Rewards;
    }

    public bool TakeReward(int index, int? card, string? alternative)
    {
        var (set, _) = Current();
        var reward = set.Rewards[index];
        session.Selector.CardReward = (card, alternative);
        var ok = false;
        using (Session.ActAs(set.Player))
        {
            session.Pump.Drive(
                async () => ok = await RunManager.Instance.RewardsSetSynchronizer.SelectLocalReward(reward),
                $"take reward {index}"
            );
        }
        session.Selector.CardReward = null;
        return ok;
    }

    public bool TakeRewardUnsynchronized(int index, int? card, string? alternative)
    {
        var (set, _) = Current();
        var reward = set.Rewards[index];
        session.Selector.CardReward = (card, alternative);
        var ok = false;
        using (Session.ActAs(set.Player))
        {
            session.Pump.Drive(
                async () => ok = await reward.SelectUnsynchronized(),
                $"take reward {index} unsynchronized"
            );
        }
        session.Selector.CardReward = null;
        return ok;
    }

    public void SkipRewards()
    {
        var (set, _) = Current();
        using (Session.ActAs(set.Player))
        {
            session.Pump.Run(RunManager.Instance.RewardsSetSynchronizer.SkipLocalRewardsSet);
        }
    }

    public bool Rest(string optionId)
    {
        var run = session.Run ?? throw new InvalidOperationException("run not set up");
        if (run.CurrentRoom is not RestSiteRoom site)
        {
            throw new InvalidOperationException("not at a rest site");
        }
        var index = site.Options.ToList().FindIndex(o => o.OptionId == optionId);
        if (index < 0)
        {
            throw new ArgumentException($"rest option {optionId} unavailable");
        }
        var ok = false;
        session.Pump.Drive(
            async () => ok = await RunManager.Instance.RestSiteSynchronizer.ChooseLocalOption(index),
            $"rest {optionId}"
        );
        return ok;
    }

    private (RewardsSet Set, Task Done) Current()
    {
        var sync = RunManager.Instance.RewardsSetSynchronizer;
        var local = LocalContext.NetId;
        foreach (var entry in _offered)
        {
            if (!sync.IsRewardsSetCompleted(entry.Set) && entry.Set.Player.NetId == local)
            {
                return entry;
            }
        }
        return _offered.FirstOrDefault(e => !sync.IsRewardsSetCompleted(e.Set)) is { Set: not null } open
            ? open
            : throw new InvalidOperationException("no open rewards set");
    }

    private static RewardView Describe(Reward reward, int index) =>
        reward switch
        {
            GoldReward gold => new RewardView(
                index,
                "gold",
                reward.SuccessfullySelected,
                gold.Amount,
                null,
                null,
                null,
                null
            ),
            PotionReward potion => new RewardView(
                index,
                "potion",
                reward.SuccessfullySelected,
                null,
                potion.Potion?.Id.Entry,
                null,
                null,
                null
            ),
            RelicReward relic => new RewardView(
                index,
                "relic",
                reward.SuccessfullySelected,
                null,
                null,
                relic.Relic?.Id.Entry,
                null,
                null
            ),
            CardReward cards => new RewardView(
                index,
                "card",
                reward.SuccessfullySelected,
                null,
                null,
                null,
                cards.Cards.Select(c => c.Id.Entry).ToList(),
                CardRewardAlternative.Generate(cards).Select(a => a.OptionId).ToList()
            ),
            _ => new RewardView(
                index,
                reward.GetType().Name,
                reward.SuccessfullySelected,
                null,
                null,
                null,
                null,
                null
            ),
        };
}
