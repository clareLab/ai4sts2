using System.Diagnostics;

namespace Ai4Sts2.Workbench;

public sealed record SearchOptions(
    int MaxNodes,
    int MaxDepth,
    bool Estimate,
    int Beam,
    int Turns,
    int MaxTotalNodes = 0,
    bool Diversify = true,
    bool Canonical = true,
    double Escalate = double.NegativeInfinity
)
{
    public int TotalBudget => MaxTotalNodes > 0 ? MaxTotalNodes : MaxNodes * Tuning.TotalFactor;
}

public interface ISearchDomain<TAction>
{
    public Session Session { get; }

    public bool Terminal { get; }

    public string Key();

    public string BeamKey();

    public string Bucket();

    public IReadOnlyList<TAction> Actions();

    public IReadOnlyList<TAction> Variants(TAction action);

    public IReadOnlyList<TAction> Closing();

    public TimeSpan Apply(TAction action);

    public double Evaluate();

    public double Estimate();

    public double Horizon();
}

public sealed record BeamEntry<TAction>(
    IReadOnlyList<TAction> Line,
    double Estimate,
    double Real1,
    double Real,
    string Bucket,
    bool Duplicate
);

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
    int Candidates,
    int Distinct,
    int Duplicates,
    IReadOnlyList<BeamEntry<TAction>> Beam,
    double Micros,
    double SnapMicros,
    double RestoreMicros,
    double ActMicros,
    bool Exhausted
);

public sealed class Search<TAction>(ISearchDomain<TAction> domain, SearchOptions options)
{
    private readonly Dictionary<string, double> _solved = [];
    private readonly Stopwatch _clock = new();
    private Dictionary<string, double> _table = [];
    private int _nodes;
    private int _budget;
    private int _totalBudget;
    private bool _exhausted = true;
    private int _leaves;
    private int _transpositions;
    private int _snapshots;
    private int _restores;
    private int _verified;
    private int _candidates;
    private int _distinct;
    private int _duplicates;
    private double _snapMicros;
    private double _restoreMicros;
    private double _actMicros;

    private sealed record Candidate(double Score, List<TAction> Line, bool Terminal, string BeamKey, string Bucket);

    public SearchResult<TAction> Run()
    {
        _clock.Restart();
        _totalBudget = options.TotalBudget;
        var frozen = Snapshot.Frozen;
        Snapshot.Frozen = Modes.FreezeMap;
        Snapshot root;
        (double score, IReadOnlyList<TAction> line, double estimated, IReadOnlyList<BeamEntry<TAction>> beam) result;
        try
        {
            root = TakeSnapshot();
            result = Solve(1, root);
            RestoreSnapshot(root);
            root.Release();
        }
        finally
        {
            Snapshot.Frozen = frozen;
        }
        var (score, line, estimated, beam) = result;
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
            _candidates,
            _distinct,
            _duplicates,
            beam,
            _clock.Elapsed.TotalMicroseconds,
            _snapMicros,
            _restoreMicros,
            _actMicros,
            _exhausted
        );
    }

    private (double Score, IReadOnlyList<TAction> Line, double Estimated, IReadOnlyList<BeamEntry<TAction>> Beam) Solve(
        int turn,
        Snapshot root
    )
    {
        var saved = _table;
        _table = [];
        try
        {
            var candidates = new List<Candidate>();
            var level = options.MaxNodes;
            for (var t = 1; t < turn; t++)
            {
                level /= Math.Max(1, Tuning.LevelDivisor);
            }
            _budget = _nodes + Math.Max(150, level);
            var estimated = Explore(0, [], candidates, out var best);
            _exhausted &= _nodes < _budget && _nodes < _totalBudget;
            if (turn == 1)
            {
                _candidates = candidates.Count;
            }
            if ((!options.Estimate && turn == options.Turns) || options.Beam <= 0 || candidates.Count == 0)
            {
                return (estimated, best, estimated, []);
            }
            var ordered = candidates
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.Line.Count)
                .DistinctBy(c => c.BeamKey)
                .ToList();
            if (turn == 1)
            {
                _distinct = ordered.Count;
            }
            var beam = ordered.Take(options.Beam).ToList();
            if (options.Diversify)
            {
                var buckets = beam.Select(c => c.Bucket).ToHashSet();
                foreach (var c in ordered.Skip(options.Beam))
                {
                    if (beam.Count >= 2 * options.Beam)
                    {
                        break;
                    }
                    if (buckets.Add(c.Bucket))
                    {
                        beam.Add(c);
                    }
                }
            }
            var entries = new List<BeamEntry<TAction>>();
            var bestScore = double.NegativeInfinity;
            var bestReal1 = double.NegativeInfinity;
            var line = best;
            foreach (var candidate in beam)
            {
                RestoreSnapshot(root);
                var full = candidate.Line.ToList();
                foreach (var action in candidate.Line)
                {
                    Apply(action);
                }
                if (!candidate.Terminal)
                {
                    foreach (var action in domain.Closing())
                    {
                        Apply(action);
                        full.Add(action);
                    }
                }
                var postKey = domain.Terminal ? "terminal:" + string.Join("/", full) : domain.Key();
                var real1 = domain.Evaluate();
                double real;
                var duplicate = _solved.TryGetValue(postKey, out var known);
                if (duplicate)
                {
                    real = known;
                    _duplicates++;
                }
                else
                {
                    if (domain.Terminal)
                    {
                        real = real1;
                    }
                    else if (turn >= options.Turns)
                    {
                        real = Tuning.HorizonEstimate ? domain.Horizon() : real1;
                    }
                    else
                    {
                        var next = TakeSnapshot();
                        real = Solve(turn + 1, next).Score;
                        next.Release();
                    }
                    _solved[postKey] = real;
                    _verified++;
                }
                entries.Add(new BeamEntry<TAction>(full, candidate.Score, real1, real, candidate.Bucket, duplicate));
                if (real > bestScore || (real == bestScore && real1 > bestReal1))
                {
                    bestScore = real;
                    bestReal1 = real1;
                    line = full;
                }
            }
            return (bestScore, line, estimated, entries);
        }
        finally
        {
            _table = saved;
        }
    }

    private double Explore(
        int depth,
        List<TAction> path,
        List<Candidate> candidates,
        out IReadOnlyList<TAction> bestLine
    )
    {
        bestLine = path.ToList();
        if (domain.Terminal)
        {
            _leaves++;
            var terminal = domain.Evaluate();
            candidates.Add(
                new Candidate(terminal, path.ToList(), true, "terminal:" + string.Join("/", path), "terminal")
            );
            return terminal;
        }
        var key = domain.Key();
        if (_table.TryGetValue(key, out var known))
        {
            _transpositions++;
            return known;
        }
        _nodes++;
        var open = _nodes < _budget && _nodes < _totalBudget;
        var actions = depth < options.MaxDepth && open ? domain.Actions() : [];
        var bestScore = double.NegativeInfinity;
        var best = bestLine;
        Snapshot? snap = null;
        if (actions.Count > 0)
        {
            snap = TakeSnapshot();
            foreach (var action in actions)
            {
                if (_nodes >= _budget || _nodes >= _totalBudget)
                {
                    break;
                }
                Apply(action);
                var variants = domain.Variants(action);
                path.Add(action);
                var score = Explore(depth + 1, path, candidates, out var line);
                path.RemoveAt(path.Count - 1);
                RestoreSnapshot(snap);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = line;
                }
                foreach (var variant in variants)
                {
                    if (_nodes >= _budget || _nodes >= _totalBudget)
                    {
                        break;
                    }
                    Apply(variant);
                    path.Add(variant);
                    var alt = Explore(depth + 1, path, candidates, out var altLine);
                    path.RemoveAt(path.Count - 1);
                    RestoreSnapshot(snap);
                    if (alt > bestScore)
                    {
                        bestScore = alt;
                        best = altLine;
                    }
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

    private double Leaf(List<TAction> path, List<Candidate> candidates, ref Snapshot? snap, out List<TAction> closed)
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
        candidates.Add(new Candidate(score, path.ToList(), false, domain.BeamKey(), domain.Bucket()));
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
