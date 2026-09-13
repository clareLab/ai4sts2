using System.Text.Json;
using Ai4Sts2.Workbench;
using Godot;

namespace Ai4Sts2.Harness;

public static class Recorder
{
    private static readonly Lock _gate = new();
    private static string? _path;
    private static int _fight;
    private static int _depth;

    public static bool Active => Tuning.Record && _depth == 0;

    public static IDisposable Suspend()
    {
        _depth++;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => _depth--;
    }

    public static int BeginFight(string encounter, int floor, string? tag)
    {
        var id = ++_fight;
        Write(
            new
            {
                kind = "fight",
                fight = id,
                encounter,
                floor,
                tag,
                ts = DateTime.UtcNow,
            }
        );
        return id;
    }

    public static void Turn(int fight, int turn, IReadOnlyList<SearchAction> line, double score, int nodes)
    {
        var state = CombatDump.Capture();
        Write(
            new
            {
                kind = "turn",
                fight,
                turn,
                players = state.Players,
                enemies = state.Enemies,
                line,
                score,
                nodes,
            }
        );
    }

    public static void EndFight(int fight, bool won, int hpBefore, int hpAfter, int turns) =>
        Write(
            new
            {
                kind = "end",
                fight,
                won,
                hpBefore,
                hpAfter,
                turns,
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
            File.AppendAllText(_path, JsonSerializer.Serialize(row, HarnessJson.Options) + "\n");
        }
    }
}
