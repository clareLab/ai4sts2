namespace Ai4Sts2.Workbench;

public sealed record RouteScore(
    int Col,
    int Row,
    string Type,
    double Score,
    double Future,
    double? Path,
    double? Combined
);

public sealed record RoutePlan(
    MapChoice? Choice,
    IReadOnlyList<RouteScore> Options,
    PathEvaluation? Paths,
    string Reason
);

public sealed class RoutePlanner(MapView map, int gold, RunFeatures features = default, int floor = 0)
{
    private readonly Dictionary<(int Col, int Row), MapPointView> _points = map.Points.ToDictionary(p =>
        (p.Col, p.Row)
    );
    private readonly Dictionary<((int Col, int Row) Coord, int Hp), (double Value, (int Col, int Row)? Next)> _memo =
    [];
    private readonly int _row = map.Current is { Length: 2 } current ? current[1] : 0;

    public static RoutePlan Plan(Session session, SearchOptions options, int maxTurns, bool paths)
    {
        var run = session.Run ?? throw new InvalidOperationException("run not set up");
        var choices = session.Flow.View().Choices;
        if (choices.Count == 0)
        {
            return new RoutePlan(null, [], null, "no choices");
        }
        var alive = run.Players.Where(p => p.Creature.IsAlive).ToList();
        var seat = (alive.Count > 0 ? alive : run.Players.ToList()).MinBy(p =>
            (double)p.Creature.CurrentHp / Math.Max(1, p.Creature.MaxHp)
        )!;
        var ratio = (double)seat.Creature.CurrentHp / Math.Max(1, seat.Creature.MaxHp);
        var deck = seat.Deck.Cards.ToList();
        var planner = new RoutePlanner(
            session.Flow.MapSnapshot(),
            run.Players.Sum(p => p.Gold),
            new RunFeatures(
                deck.Count,
                deck.Count(c => c.CurrentUpgradeLevel > 0),
                seat.Relics.Count,
                seat.Potions.Count(),
                seat.Character.Id.Entry
            ),
            run.TotalFloor
        );
        var scored = choices
            .Select(c => new RouteScore(c.Col, c.Row, c.Type, planner.Best((c.Col, c.Row), ratio).Value, 0, null, null))
            .ToList();
        var best = scored.MaxBy(s => s.Score)!;
        var choice = choices.First(c => c.Col == best.Col && c.Row == best.Row);
        var kinds = choices.Select(c => c.Type).ToHashSet();
        var worthwhile =
            choices.Count >= 2 && (kinds.Count >= 2 || kinds.Contains("Elite") || kinds.Contains("Unknown"));
        if (!paths || !worthwhile)
        {
            return new RoutePlan(choice, scored, null, paths ? "same kinds" : "dp");
        }
        var evaluation = Rollout.EvaluatePaths(session, options, maxTurns);
        var combined = new List<RouteScore>();
        foreach (var s in scored)
        {
            var option = evaluation.Options.FirstOrDefault(o =>
                o.Choice.Col == s.Col && o.Choice.Row == s.Row && o.Error is null
            );
            if (option is null)
            {
                combined.Add(s);
                continue;
            }
            var future = planner.Future((s.Col, s.Row), (double)option.Hp / Math.Max(1, seat.Creature.MaxHp));
            combined.Add(
                s with
                {
                    Future = future,
                    Path = option.Score,
                    Combined = option.Score + (Tuning.RouteFuture * future),
                }
            );
        }
        var blended = combined.Where(s => s.Combined is not null).MaxBy(s => s.Combined!.Value);
        if (blended is not null)
        {
            choice = choices.First(c => c.Col == blended.Col && c.Row == blended.Row);
        }
        return new RoutePlan(choice, combined, evaluation, blended is null ? "dp (paths failed)" : "paths");
    }

    public double Future((int Col, int Row) coord, double hp)
    {
        if (!_points.TryGetValue(coord, out var point))
        {
            var entry = _points.Values.Where(p => p.Row == _points.Values.Min(q => q.Row)).ToList();
            return entry.Count == 0
                ? SurvivalModel.Current is null
                    ? hp * Tuning.RouteHpValue
                    : 1
                : entry.Max(p => Best((p.Col, p.Row), hp).Value);
        }
        return point.Children.Count == 0
            ? SurvivalModel.Current is null
                ? hp * Tuning.RouteHpValue
                : 1
            : point.Children.Max(c => Best((c[0], c[1]), hp).Value);
    }

    public (double Value, (int Col, int Row)? Next) Best((int Col, int Row) coord, double hp)
    {
        var key = (coord, (int)Math.Round(hp * 100));
        if (_memo.TryGetValue(key, out var known))
        {
            return known;
        }
        var point = _points[coord];
        if (SurvivalModel.Current is { } model)
        {
            var at = floor + point.Row - _row;
            var survive = 1 - model.Hazard(features, point.Type, hp, at);
            var next = Math.Clamp(hp + model.Delta(features, point.Type, hp, at), 0, 1);
            (double Value, (int Col, int Row)? Next) learned;
            if (point.Children.Count == 0 || point.Type == "Boss")
            {
                learned = (survive, null);
            }
            else
            {
                var child = point
                    .Children.Select(c => ((c[0], c[1]), Best((c[0], c[1]), next).Value))
                    .MaxBy(x => x.Value);
                learned = (survive * child.Value, child.Item1);
            }
            _memo[key] = learned;
            return learned;
        }
        var value = Value(point.Type, ref hp);
        hp -= Loss(point.Type);
        if (hp <= Tuning.RouteDanger / 100.0)
        {
            value -= Tuning.RouteDangerPenalty;
        }
        (double Value, (int Col, int Row)? Next) result;
        if (point.Children.Count == 0 || point.Type == "Boss")
        {
            result = (value + (hp * Tuning.RouteHpValue), null);
        }
        else
        {
            var bestChild = point
                .Children.Select(c => ((c[0], c[1]), Best((c[0], c[1]), hp).Value))
                .MaxBy(x => x.Value);
            result = (value + bestChild.Value, bestChild.Item1);
        }
        _memo[key] = result;
        return result;
    }

    private double Value(string type, ref double hp)
    {
        switch (type)
        {
            case "Elite":
                return hp < Tuning.RouteEliteMinHp / 100.0 ? -Tuning.RouteElitePenalty : Tuning.RouteElite;
            case "RestSite":
                if (hp < Tuning.RouteRestBelow / 100.0)
                {
                    var value = Tuning.RouteRestHeal * (1 - hp);
                    hp = Math.Min(1.0, hp + (Tuning.RouteRestAmount / 100.0));
                    return value;
                }
                return Tuning.RouteRestFull;
            case "Shop":
                return gold >= Tuning.RouteShopGold ? Tuning.RouteShop : Tuning.RouteShopPoor;
            case "Monster":
                return Tuning.RouteMonster;
            case "Unknown":
            case "Ancient":
                return Tuning.RouteUnknown;
            case "Treasure":
                return Tuning.RouteTreasure;
            default:
                return 0;
        }
    }

    private static double Loss(string type) =>
        type switch
        {
            "Monster" => Tuning.RouteLossMonster / 100.0,
            "Elite" => Tuning.RouteLossElite / 100.0,
            "Unknown" or "Ancient" => Tuning.RouteLossUnknown / 100.0,
            _ => 0,
        };
}
