using System.Diagnostics;

namespace Ai4Sts2.Workbench;

public sealed record SearchOptions(int MaxNodes, int MaxDepth, bool Estimate, int Beam, int Turns);

public interface ISearchDomain<TAction>
{
    public Session Session { get; }

    public bool Terminal { get; }

    public string Key();

    public IReadOnlyList<TAction> Actions();

    public IReadOnlyList<TAction> Closing();

    public TimeSpan Apply(TAction action);

    public double Evaluate();

    public double Estimate();
}

public sealed record SearchResult<TAction>(
    double Score,
    double Estimated,
    IReadOnlyList<TAction> Line,
    string Leaf,
    int Turns,
    int Nodes,
    int Leaves,
    int Transpositions,
    int Snapshots,
    int Restores,
    int Verified,
    double Micros,
    double SnapMicros,
    double RestoreMicros,
    double ActMicros,
    bool Exhausted
);

public sealed class Search<TAction>(ISearchDomain<TAction> domain, SearchOptions options)
{
    private readonly Dictionary<string, double> _table = [];
    private readonly Stopwatch _clock = new();
    private int _nodes;
    private int _leaves;
    private int _transpositions;
    private int _snapshots;
    private int _restores;
    private int _verified;
    private double _snapMicros;
    private double _restoreMicros;
    private double _actMicros;

    public SearchResult<TAction> Run()
    {
        _clock.Restart();
        var root = TakeSnapshot();
        var (score, line, estimated) = Solve(1, root);
        RestoreSnapshot(root);
        root.Release();
        return new SearchResult<TAction>(
            score,
            estimated,
            line,
            options.Estimate ? "estimate" : "exact",
            options.Turns,
            _nodes,
            _leaves,
            _transpositions,
            _snapshots,
            _restores,
            _verified,
            _clock.Elapsed.TotalMicroseconds,
            _snapMicros,
            _restoreMicros,
            _actMicros,
            _nodes < options.MaxNodes
        );
    }

    private (double Score, IReadOnlyList<TAction> Line, double Estimated) Solve(int turn, Snapshot root)
    {
        var candidates = new List<(double Score, List<TAction> Line, bool Terminal)>();
        var estimated = Explore(0, [], candidates, out var best);
        if ((!options.Estimate && turn == options.Turns) || options.Beam <= 0 || candidates.Count == 0)
        {
            return (estimated, best, estimated);
        }
        var score = double.NegativeInfinity;
        var line = best;
        foreach (var (_, candidate, terminal) in candidates.OrderByDescending(c => c.Score).Take(options.Beam))
        {
            RestoreSnapshot(root);
            var full = candidate.ToList();
            foreach (var action in candidate)
            {
                Apply(action);
            }
            if (!terminal)
            {
                foreach (var action in domain.Closing())
                {
                    Apply(action);
                    full.Add(action);
                }
            }
            double real;
            if (domain.Terminal || turn >= options.Turns)
            {
                real = domain.Evaluate();
            }
            else
            {
                var next = TakeSnapshot();
                real = Solve(turn + 1, next).Score;
                next.Release();
            }
            _verified++;
            if (real > score)
            {
                score = real;
                line = full;
            }
        }
        return (score, line, estimated);
    }

    private double Explore(
        int depth,
        List<TAction> path,
        List<(double Score, List<TAction> Line, bool Terminal)> candidates,
        out IReadOnlyList<TAction> bestLine
    )
    {
        bestLine = path.ToList();
        if (domain.Terminal)
        {
            _leaves++;
            var terminal = domain.Evaluate();
            candidates.Add((terminal, path.ToList(), true));
            return terminal;
        }
        var key = domain.Key();
        if (_table.TryGetValue(key, out var known))
        {
            _transpositions++;
            return known;
        }
        _nodes++;
        var actions = depth < options.MaxDepth && _nodes < options.MaxNodes ? domain.Actions() : [];
        var bestScore = double.NegativeInfinity;
        var best = bestLine;
        Snapshot? snap = null;
        if (actions.Count > 0)
        {
            snap = TakeSnapshot();
            foreach (var action in actions)
            {
                if (_nodes >= options.MaxNodes)
                {
                    break;
                }
                Apply(action);
                path.Add(action);
                var score = Explore(depth + 1, path, candidates, out var line);
                path.RemoveAt(path.Count - 1);
                RestoreSnapshot(snap);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = line;
                }
            }
        }
        var leaf = Leaf(path, candidates, ref snap, out var closed);
        snap?.Release();
        _leaves++;
        if (leaf > bestScore)
        {
            bestScore = leaf;
            best = closed;
        }
        _table[key] = bestScore;
        bestLine = best;
        return bestScore;
    }

    private double Leaf(
        List<TAction> path,
        List<(double Score, List<TAction> Line, bool Terminal)> candidates,
        ref Snapshot? snap,
        out List<TAction> closed
    )
    {
        var closing = domain.Closing();
        closed = path.Concat(closing).ToList();
        double score;
        if (options.Estimate)
        {
            score = domain.Estimate();
        }
        else if (closing.Count == 0)
        {
            score = domain.Evaluate();
        }
        else
        {
            snap ??= TakeSnapshot();
            foreach (var action in closing)
            {
                Apply(action);
            }
            score = domain.Evaluate();
            RestoreSnapshot(snap);
        }
        candidates.Add((score, path.ToList(), false));
        return score;
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
        var stats = Loader.Restore(snap, domain.Session.Pump);
        _restores++;
        _restoreMicros += stats.RestoreMicros + stats.ResyncMicros;
    }

    private void Apply(TAction action) => _actMicros += domain.Apply(action).TotalMicroseconds;
}
