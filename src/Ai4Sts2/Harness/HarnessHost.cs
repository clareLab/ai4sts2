using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace Ai4Sts2.Harness;

public sealed partial class HarnessHost : Node
{
    private const int PollInterval = 10;
    private static HarnessHost? _instance;
    private readonly string _dir;
    private readonly string _requestPath;
    private readonly string _runningPath;
    private readonly string _readyPath;
    private int _frame;
    private bool _busy;

    public HarnessHost()
    {
        _dir = Path.Combine(OS.GetUserDataDir(), Entry.ModId, "harness");
        _requestPath = Path.Combine(_dir, "request.json");
        _runningPath = Path.Combine(_dir, "running.json");
        _readyPath = Path.Combine(_dir, "ready");
    }

    public const string EnvVar = "AI4STS2_HARNESS";

    public static bool Enabled => System.Environment.GetEnvironmentVariable(EnvVar) == "1";

    public static void Attach(NGame game)
    {
        if (_instance != null && IsInstanceValid(_instance))
        {
            return;
        }
        _instance = new HarnessHost { Name = "ai4sts2_harness" };
        game.AddChild(_instance);
        Entry.Log.Info($"harness attached dir={_instance._dir}");
    }

    public override void _Ready()
    {
        _ = Directory.CreateDirectory(_dir);
        foreach (var stale in new[] { _requestPath, _runningPath })
        {
            File.Delete(stale);
        }
        foreach (var stale in Directory.GetFiles(_dir, "result-*.json*"))
        {
            File.Delete(stale);
        }
        File.WriteAllText(_readyPath, Entry.ModId);
    }

    public override void _Process(double delta)
    {
        if (_busy || ++_frame % PollInterval != 0 || !File.Exists(_requestPath))
        {
            return;
        }
        _busy = true;
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        HarnessRequest? request = null;
        HarnessResult result;
        try
        {
            File.Move(_requestPath, _runningPath, true);
            request =
                JsonSerializer.Deserialize<HarnessRequest>(
                    await File.ReadAllTextAsync(_runningPath),
                    HarnessJson.Options
                ) ?? throw new InvalidDataException("empty request");
            var payload = await HarnessOps.ExecuteAsync(this, request);
            result = new HarnessResult(request.Id, true, null, payload);
        }
        catch (Exception ex)
        {
            Entry.Log.Error($"harness request failed: {ex}");
            result = new HarnessResult(request?.Id ?? "", false, ex.ToString(), null);
        }
        var resultPath = ResultPath(result.Id);
        var tmp = resultPath + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(result, HarnessJson.Options));
        File.Move(tmp, resultPath, true);
        File.Delete(_runningPath);
        _busy = false;
    }

    private string ResultPath(string id)
    {
        var safe = new string(id.Where(char.IsAsciiLetterOrDigit).ToArray());
        return Path.Combine(_dir, $"result-{(safe.Length == 0 ? "unknown" : safe)}.json");
    }
}
