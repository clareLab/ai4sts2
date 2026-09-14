using System.Text.Json;
using Ai4Sts2.Workbench;
using Godot;
using MegaCrit.Sts2.Core.Debug;

namespace Ai4Sts2.Harness;

public sealed record FightHeader(
    string Encounter,
    string RoomType,
    int Floor,
    int Act,
    int Ascension,
    int Players,
    IReadOnlyList<string> Characters,
    int MaxTurns,
    int HpStart,
    string Budget
);

public sealed record FightEnd(bool Won, bool Truncated, int HpEnd, double EnemyHpFraction, int Turns, int Dealt);

public static class Recorder
{
    private static readonly Lock _gate = new();
    private static readonly JsonSerializerOptions _line = new(HarnessJson.Options) { WriteIndented = false };
    private static readonly Dictionary<string, int> _names = [];
    private static string? _path;
    private static int _fight;
    private static int _depth;

    public static bool Active => Modes.Record && _depth == 0;

    public static string? Run { get; private set; }

    public static IDisposable Suspend()
    {
        _depth++;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => _depth--;
    }

    public static void Begin(string dir, string run)
    {
        lock (_gate)
        {
            _ = Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, $"{run}.jsonl");
            Run = run;
            _fight = 0;
            _names.Clear();
            Write(
                new
                {
                    k = "run",
                    run,
                    game = ReleaseInfoManager.Instance.SemVer?.ToString(),
                    mod = typeof(Entry).Assembly.GetName().Version?.ToString(),
                    built = File.GetLastWriteTimeUtc(typeof(Entry).Assembly.Location),
                    ts = DateTime.UtcNow,
                }
            );
        }
    }

    public static int BeginFight(FightHeader header)
    {
        var id = ++_fight;
        Write(
            new
            {
                k = "fight",
                fight = id,
                header.Encounter,
                header.RoomType,
                header.Floor,
                header.Act,
                header.Ascension,
                header.Players,
                header.Characters,
                header.MaxTurns,
                header.HpStart,
                header.Budget,
            }
        );
        return id;
    }

    public static void Turn(int fight, int turn, IReadOnlyList<SearchAction> line, double score, int nodes)
    {
        var dump = Modes.Dump ? CombatDump.Capture() : null;
        Write(
            new
            {
                k = "turn",
                fight,
                turn,
                line,
                score,
                nodes,
                players = dump?.Players,
                enemies = dump?.Enemies,
            }
        );
    }

    public static void Features(
        int fight,
        int turn,
        string phase,
        IReadOnlyList<KeyValuePair<string, double>> features,
        object? extra = null
    )
    {
        lock (_gate)
        {
            var indices = new int[features.Count];
            var values = new double[features.Count];
            for (var j = 0; j < features.Count; j++)
            {
                var (name, value) = features[j];
                if (!_names.TryGetValue(name, out var index))
                {
                    index = _names.Count;
                    _names[name] = index;
                    Write(
                        new
                        {
                            k = "name",
                            i = index,
                            n = name,
                        }
                    );
                }
                indices[j] = index;
                values[j] = value;
            }
            Write(
                new
                {
                    k = "x",
                    fight,
                    turn,
                    phase,
                    i = indices,
                    v = values,
                    extra,
                }
            );
        }
    }

    public static void EndFight(int fight, FightEnd end) =>
        Write(
            new
            {
                k = "end",
                fight,
                end.Won,
                end.Truncated,
                end.HpEnd,
                end.EnemyHpFraction,
                end.Turns,
                end.Dealt,
            }
        );

    private static void Write(object row)
    {
        lock (_gate)
        {
            if (_path is null)
            {
                var dir = Path.Combine(OS.GetUserDataDir(), Entry.ModId, "records");
                _ = Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{System.Environment.ProcessId}.jsonl");
            }
            File.AppendAllText(_path, JsonSerializer.Serialize(row, _line) + "\n");
        }
    }
}
