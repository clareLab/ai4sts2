using System.Diagnostics;
using System.Text.Json;
using Ai4Sts2.Core;
using Ai4Sts2.Workbench;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using MegaCrit.Sts2.Core.TestSupport;

namespace Ai4Sts2.Harness;

public sealed record CardInfo(string Id, string Pool, string Type, string Rarity, int Cost, bool CostsX, string Target);

public sealed record EncounterInfo(string Id, string Type);

public static class HarnessOps
{
    private static readonly TimeSpan _maxWait = TimeSpan.FromSeconds(60);
    private static IDisposable? _oracleSelector;

    public static async Task<JsonElement?> ExecuteAsync(HarnessHost host, HarnessRequest request)
    {
        var sw = Stopwatch.StartNew();
        var payload = await DispatchAsync(host, request);
        return payload is null ? null : WithTiming(payload.Value, sw.Elapsed);
    }

    private static Task<JsonElement?> DispatchAsync(HarnessHost host, HarnessRequest request) =>
        request.Op switch
        {
            "ping" => Result(Ping()),
            "quit" => Quit(host),
            "mode.set" => Result(SetMode(request.Args)),
            "models.cards" => Result(ListCards()),
            "models.encounters" => Result(ListEncounters()),
            "run.new" => NewRunAsync(request.Args),
            "run.state" => Result(RunSetup.Capture(RunManager.Instance.State!)),
            "run.rng.set" => Result(SetRunRng(request.Args)),
            "deck.set" => SetDeckAsync(request.Args),
            "combat.enter" => EnterCombatAsync(host, request.Args),
            "combat.state" => Result(CombatDump.Capture()),
            "combat.play" => PlayAsync(host, request.Args),
            "combat.endturn" => EndTurnAsync(host, request.Args),
            "wb.run" => Result(WorkbenchRun(request.Args)),
            "wb.start" => Result(WorkbenchStart(request.Args)),
            "wb.play" => Result(WorkbenchPlay(request.Args)),
            "wb.endturn" => Result(WorkbenchEndTurn(request.Args)),
            "wb.state" => Result(CombatDump.Capture()),
            "wb.bench" => Result(WorkbenchBench(request.Args)),
            "wb.warmup" => WorkbenchWarmupAsync(host, request.Args),
            "wb.census" => Result(Census.Run()),
            "wb.snap" => Result(WorkbenchSnap()),
            "wb.snapbench" => Result(WorkbenchSnapBench(request.Args)),
            "wb.restore" => Result(WorkbenchRestore(request.Args)),
            "wb.search" => Result(WorkbenchSearch(request.Args)),
            _ => throw new NotSupportedException($"unknown op '{request.Op}'"),
        };

    private static object Ping() =>
        new
        {
            Game = ReleaseInfoManager.Instance.SemVer?.ToString(),
            Mod = typeof(Entry).Assembly.GetName().Version?.ToString(),
            MainThread = NGame.IsMainThread(),
            Headless = DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase),
            InRun = RunManager.Instance.IsInProgress,
            InCombat = CombatManager.Instance.IsInProgress,
            TestMode = TestMode.IsOn,
            NonInteractive = NonInteractiveMode.IsActive,
            FastMode = SaveManager.Instance.PrefsSave.FastMode.ToString(),
            Workbench = Switches.Applied,
            Patches = Patches.Count,
        };

    private static List<CardInfo> ListCards() =>
        ModelDb
            .AllCards.Select(c => new CardInfo(
                c.Id.Entry,
                c.Pool.Id.Entry,
                c.Type.ToString(),
                c.Rarity.ToString(),
                c.EnergyCost.GetResolved(),
                c.EnergyCost.CostsX,
                c.TargetType.ToString()
            ))
            .OrderBy(c => c.Pool)
            .ThenBy(c => c.Id)
            .ToList();

    private static List<EncounterInfo> ListEncounters() =>
        ModelDb
            .AllEncounters.Select(e => new EncounterInfo(e.Id.Entry, e.RoomType.ToString()))
            .OrderBy(e => e.Id)
            .ToList();

    private static Task<JsonElement?> Quit(HarnessHost host)
    {
        host.GetTree().Quit();
        return Task.FromResult<JsonElement?>(null);
    }

    private static object SetMode(JsonElement? args)
    {
        if (args is { } a)
        {
            if (a.TryGetProperty("testMode", out var t))
            {
                TestMode.IsOn = t.GetBoolean();
            }
            if (a.TryGetProperty("nonInteractive", out var n))
            {
                var on = n.GetBoolean();
                NonInteractiveMode.AutoSlayerCheck = () => on;
            }
            if (a.TryGetProperty("fastMode", out var f))
            {
                SaveManager.Instance.PrefsSave.FastMode = Enum.Parse<FastModeType>(f.GetString()!, true);
            }
        }
        return Ping();
    }

    private static async Task<JsonElement?> NewRunAsync(JsonElement? args)
    {
        var a = args ?? throw new ArgumentException("args required");
        var characters = Characters(a);
        var seed = a.TryGetProperty("seed", out var s) ? s.GetString()! : "AI4STS2";
        var ascension = a.TryGetProperty("ascension", out var asc) ? asc.GetInt32() : 0;
        var models = characters
            .Select(c => ModelDb.GetById<CharacterModel>(new ModelId(ModelId.SlugifyCategory<CharacterModel>(), c)))
            .ToList();
        var game = NGame.Instance ?? throw new InvalidOperationException("NGame missing");
        if (RunManager.Instance.IsInProgress)
        {
            await game.ReturnToMainMenu();
        }
        RunState run;
        if (models.Count == 1)
        {
            run = await game.StartNewSingleplayerRun(
                models[0],
                false,
                ModelDb.Acts.ToList(),
                [],
                seed,
                GameMode.Standard,
                ascension
            );
        }
        else
        {
            TestFlags.ShouldSendResumeForRemotePlayers = true;
            var unlocks = SaveManager.Instance.GenerateUnlockStateFromProgress();
            run = RunState.CreateForNewRun(
                models.Select((m, i) => Player.CreateForNewRun(m, unlocks, 1UL + (ulong)i)).ToList(),
                ModelDb.Acts.Select(act => act.ToMutable()).ToList(),
                [],
                GameMode.Standard,
                ascension,
                seed
            );
            RunManager.Instance.SetUpNewSingleplayer(run, false);
            await game.StartRun(run);
        }
        _oracleSelector?.Dispose();
        _oracleSelector = CardSelectCmd.PushSelector(new ScriptSelector());
        return Json(
            new
            {
                Seed = run.Rng.Seed,
                Players = run.Players.Count,
                Floor = run.TotalFloor,
                NetId = LocalContext.NetId,
            }
        );
    }

    private static async Task<JsonElement?> EnterCombatAsync(HarnessHost host, JsonElement? args)
    {
        var a = args ?? throw new ArgumentException("args required");
        var id = a.GetProperty("encounter").GetString()!.ToUpperInvariant();
        var encounter = ModelDb
            .GetById<EncounterModel>(new ModelId(ModelId.SlugifyCategory<EncounterModel>(), id))
            .ToMutable();
        _ = await RunManager.Instance.EnterRoomDebug(RoomType.Monster, MapPointType.Unassigned, encounter, false);
        await WaitForPlayerTurnAsync(host);
        return Json(new { State = CombatDump.Capture() });
    }

    private static async Task<JsonElement?> PlayAsync(HarnessHost host, JsonElement? args)
    {
        var a = args ?? throw new ArgumentException("args required");
        var state = CombatManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("no combat");
        var player = OraclePlayer(state, a);
        var hand = player.PlayerCombatState!.Hand.Cards;
        var index = a.GetProperty("hand").GetInt32();
        var card = hand[index];
        Creature? target = null;
        if (a.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.Number)
        {
            target = state.Enemies[t.GetInt32()];
        }
        if (target is not null && !card.IsValidTarget(target))
        {
            target = null;
        }
        var sw = Stopwatch.StartNew();
        if (!card.TryManualPlay(target))
        {
            throw new InvalidOperationException($"cannot play {card.Id.Entry} targeting {target?.Name ?? "none"}");
        }
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        await WaitForPlayerTurnAsync(host);
        var elapsed = sw.Elapsed;
        return Json(
            new
            {
                Card = card.Id.Entry,
                PlayMicros = elapsed.TotalMicroseconds,
                State = CombatDump.Capture(),
            }
        );
    }

    private static Player OraclePlayer(CombatState state, JsonElement a)
    {
        return a.TryGetProperty("player", out var p) && p.ValueKind == JsonValueKind.Number
            ? state.Players[p.GetInt32()]
            : LocalContext.GetMe(state) ?? throw new InvalidOperationException("no local player");
    }

    private static async Task<JsonElement?> EndTurnAsync(HarnessHost host, JsonElement? args)
    {
        var state = CombatManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("no combat");
        var player = args is { } a ? OraclePlayer(state, a) : LocalContext.GetMe(state)!;
        var turn = player.PlayerCombatState!.TurnNumber;
        var sw = Stopwatch.StartNew();
        PlayerCmd.EndTurn(player, false);
        await WaitForPlayerTurnAsync(host, turn);
        var elapsed = sw.Elapsed;
        return Json(new { EndTurnMicros = elapsed.TotalMicroseconds, State = CombatDump.Capture() });
    }

    private static RunDump SetRunRng(JsonElement? args)
    {
        var a = args ?? throw new ArgumentException("args required");
        var run = RunManager.Instance.State ?? throw new InvalidOperationException("no run");
        RunSetup.RestoreRng(run, a.GetProperty("rng").Deserialize<Dictionary<string, RngState>>(HarnessJson.Options)!);
        if (a.TryGetProperty("playerRng", out var p))
        {
            RunSetup.RestorePlayerRng(
                LocalContext.GetMe(run)!,
                p.Deserialize<Dictionary<string, RngState>>(HarnessJson.Options)!
            );
        }
        return RunSetup.Capture(run);
    }

    private static async Task<JsonElement?> SetDeckAsync(JsonElement? args)
    {
        var a = args ?? throw new ArgumentException("args required");
        var run = RunManager.Instance.State ?? throw new InvalidOperationException("no run");
        var cards = a.GetProperty("cards").EnumerateArray().Select(c => c.GetString()!).ToList();
        await RunSetup.SetDeckAsync(LocalContext.GetMe(run)!, cards);
        return Json(RunSetup.Capture(run));
    }

    private static RunDump WorkbenchRun(JsonElement? args)
    {
        var a = args ?? throw new ArgumentException("args required");
        var seed = a.TryGetProperty("seed", out var s) ? s.GetString()! : "AI4STS2";
        var ascension = a.TryGetProperty("ascension", out var asc) ? asc.GetInt32() : 0;
        return RunSetup.Capture(Session.Instance.NewRun(Characters(a), seed, ascension));
    }

    private static List<string> Characters(JsonElement a)
    {
        if (a.TryGetProperty("characters", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            return list.EnumerateArray().Select(c => c.GetString()!.ToUpperInvariant()).ToList();
        }
        var character = a.GetProperty("character").GetString()!.ToUpperInvariant();
        var players = a.TryGetProperty("players", out var p) ? p.GetInt32() : 1;
        return Enumerable.Repeat(character, players).ToList();
    }

    private static object WorkbenchStart(JsonElement? args)
    {
        var a = args ?? throw new ArgumentException("args required");
        var seed = a.TryGetProperty("seed", out var s) ? s.GetString()! : "AI4STS2";
        var ascension = a.TryGetProperty("ascension", out var asc) ? asc.GetInt32() : 0;
        var encounter = a.GetProperty("encounter").GetString()!.ToUpperInvariant();
        var heal = !a.TryGetProperty("heal", out var h) || h.GetBoolean();
        var session = Session.Instance;
        var run = session.EnsureRun(Characters(a), seed, ascension);
        if (a.TryGetProperty("rng", out var rng))
        {
            RunSetup.RestoreRng(run, rng.Deserialize<Dictionary<string, RngState>>(HarnessJson.Options)!);
        }
        var sw = Stopwatch.StartNew();
        _ = session.StartEncounter(encounter, heal);
        return new
        {
            StartMicros = sw.Elapsed.TotalMicroseconds,
            Pump = new { session.Pump.Posted, session.Pump.Drained },
            State = CombatDump.Capture(),
        };
    }

    private static object WorkbenchPlay(JsonElement? args)
    {
        var a = args ?? throw new ArgumentException("args required");
        int? target =
            a.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : null;
        var player = a.TryGetProperty("player", out var p) ? p.GetInt32() : 0;
        var elapsed = Session.Instance.Play(player, a.GetProperty("hand").GetInt32(), target);
        return new { PlayMicros = elapsed.TotalMicroseconds, State = CombatDump.Capture() };
    }

    private static object WorkbenchEndTurn(JsonElement? args)
    {
        var player = args is { } a && a.TryGetProperty("player", out var p) ? p.GetInt32() : 0;
        var elapsed = Session.Instance.EndTurn(player);
        return new { EndTurnMicros = elapsed.TotalMicroseconds, State = CombatDump.Capture() };
    }

    private static object WorkbenchSnap()
    {
        var (id, snap) = Session.Instance.Snap();
        return new
        {
            Id = id,
            Micros = snap.Elapsed.TotalMicroseconds,
            snap.Objects,
            snap.Arrays,
            snap.Fields,
        };
    }

    private static object WorkbenchSnapBench(JsonElement? args)
    {
        var n = args is { } a && a.TryGetProperty("n", out var v) ? v.GetInt32() : 200;
        var snaps = new List<double>(n);
        var restores = new List<double>(n);
        Snapshot? last = null;
        var gen0 = GC.CollectionCount(0);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < n; i++)
        {
            var snap = Loader.Take();
            snaps.Add(snap.Elapsed.TotalMicroseconds);
            restores.Add(snap.Restore().TotalMicroseconds);
            last?.Release();
            last = snap;
        }
        return new
        {
            N = n,
            Snap = Stats(snaps),
            Restore = Stats(restores),
            Gen0 = GC.CollectionCount(0) - gen0,
            AllocatedPerIteration = (GC.GetAllocatedBytesForCurrentThread() - allocated) / n,
            last?.Objects,
            last?.Arrays,
            last?.Fields,
            last?.Bytes,
        };
    }

    private static object WorkbenchRestore(JsonElement? args)
    {
        var a = args ?? throw new ArgumentException("args required");
        var stats = Session.Instance.Restore(a.GetProperty("id").GetInt32());
        return new { Stats = stats, State = CombatDump.Capture() };
    }

    private static object WorkbenchSearch(JsonElement? args)
    {
        var a = args ?? throw new ArgumentException("args required");
        var maxNodes = a.TryGetProperty("maxNodes", out var n) ? n.GetInt32() : 2000;
        var maxDepth = a.TryGetProperty("maxDepth", out var d) ? d.GetInt32() : 8;
        var leaf = a.TryGetProperty("leaf", out var l) ? l.GetString() ?? "exact" : "exact";
        var beam = a.TryGetProperty("beam", out var b) ? b.GetInt32() : 3;
        var turns = a.TryGetProperty("turns", out var t) ? t.GetInt32() : 1;
        var options = new SearchOptions(maxNodes, maxDepth, leaf == "estimate", beam, turns);
        var result = new Search<SearchAction>(new CombatDomain(Session.Instance), options).Run();
        return new { Result = result, State = CombatDump.Capture() };
    }

    private static object WorkbenchBench(JsonElement? args)
    {
        var a = args ?? throw new ArgumentException("args required");
        var rounds = a.TryGetProperty("rounds", out var r) ? r.GetInt32() : 20;
        var encounter = a.GetProperty("encounter").GetString()!.ToUpperInvariant();
        var session = Session.Instance;
        var plays = new List<double>();
        var ends = new List<double>();
        var starts = new List<double>();
        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var gcBefore = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        var sw = Stopwatch.StartNew();
        var bucket = a.TryGetProperty("bucket", out var b) ? b.GetInt32() : 0;
        var buckets = new List<double>();
        var bucketStart = 0.0;
        for (var i = 0; i < rounds; i++)
        {
            if (bucket > 0 && i % bucket == 0 && i > 0)
            {
                buckets.Add(Math.Round((sw.Elapsed.TotalMilliseconds - bucketStart) / bucket, 3));
                bucketStart = sw.Elapsed.TotalMilliseconds;
            }
            var t0 = Stopwatch.StartNew();
            var state = session.StartEncounter(encounter, true);
            starts.Add(t0.Elapsed.TotalMicroseconds);
            var guard = 0;
            while (CombatManager.Instance.IsInProgress && guard++ < 40)
            {
                var player = LocalContext.GetMe(state)!;
                var pcs = player.PlayerCombatState!;
                var played = false;
                for (var h = 0; h < pcs.Hand.Cards.Count; h++)
                {
                    var card = pcs.Hand.Cards[h];
                    var needsTarget = card.IsValidTarget(state.Enemies.FirstOrDefault(e => e.IsAlive));
                    var target = needsTarget
                        ? state.Enemies.Select((e, idx) => (e, idx)).First(x => x.e.IsAlive).idx
                        : (int?)null;
                    if (card.CanPlay(out _, out _) && (target is not null || card.IsValidTarget(null)))
                    {
                        plays.Add(session.Play(h, target).TotalMicroseconds);
                        played = true;
                        break;
                    }
                }
                if (!CombatManager.Instance.IsInProgress)
                {
                    break;
                }
                if (!played)
                {
                    ends.Add(session.EndTurn().TotalMicroseconds);
                }
            }
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        return new
        {
            Rounds = rounds,
            TotalMillis = sw.Elapsed.TotalMilliseconds,
            AllocatedBytes = allocated,
            Starts = Stats(starts),
            Plays = Stats(plays),
            EndTurns = Stats(ends),
            Pump = new { session.Pump.Posted, session.Pump.Drained },
            Selector = session.Selector.Log.Count,
            Gc = new
            {
                Gen0 = GC.CollectionCount(0) - gcBefore.Item1,
                Gen1 = GC.CollectionCount(1) - gcBefore.Item2,
                Gen2 = GC.CollectionCount(2) - gcBefore.Item3,
            },
            BucketMillisPerRound = buckets,
        };
    }

    private static async Task<JsonElement?> WorkbenchWarmupAsync(HarnessHost host, JsonElement? args)
    {
        var a = args ?? throw new ArgumentException("args required");
        var encounter = a.GetProperty("encounter").GetString()!.ToUpperInvariant();
        var batches = a.TryGetProperty("batches", out var b) ? b.GetInt32() : 8;
        var rounds = a.TryGetProperty("rounds", out var r) ? r.GetInt32() : 150;
        var idleSeconds = a.TryGetProperty("idle", out var i) ? i.GetDouble() : 0.25;
        var perRound = new List<double>();
        for (var k = 0; k < batches; k++)
        {
            var sw = Stopwatch.StartNew();
            for (var n = 0; n < rounds; n++)
            {
                AutoPlayCombat(Session.Instance, encounter);
            }
            perRound.Add(Math.Round(sw.Elapsed.TotalMilliseconds / rounds, 3));
            _ = await host.ToSignal(host.GetTree().CreateTimer(idleSeconds), SceneTreeTimer.SignalName.Timeout);
        }
        return Json(
            new
            {
                Batches = batches,
                Rounds = rounds,
                MillisPerRound = perRound,
            }
        );
    }

    private static void AutoPlayCombat(Session session, string encounter)
    {
        var state = session.StartEncounter(encounter, true);
        var guard = 0;
        while (CombatManager.Instance.IsInProgress && guard++ < 40)
        {
            var pcs = LocalContext.GetMe(state)!.PlayerCombatState!;
            var played = false;
            for (var h = 0; h < pcs.Hand.Cards.Count; h++)
            {
                var card = pcs.Hand.Cards[h];
                var alive = state.Enemies.Select((e, idx) => (e, idx)).FirstOrDefault(x => x.e.IsAlive);
                var target = alive.e is not null && card.IsValidTarget(alive.e) ? alive.idx : (int?)null;
                if (card.CanPlay(out _, out _) && (target is not null || card.IsValidTarget(null)))
                {
                    _ = session.Play(h, target);
                    played = true;
                    break;
                }
            }
            if (!CombatManager.Instance.IsInProgress)
            {
                break;
            }
            if (!played)
            {
                _ = session.EndTurn();
            }
        }
    }

    private static object Stats(List<double> xs)
    {
        if (xs.Count == 0)
        {
            return new { Count = 0 };
        }
        var sorted = xs.OrderBy(x => x).ToList();
        return new
        {
            sorted.Count,
            Median = sorted[sorted.Count / 2],
            P95 = sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(sorted.Count * 0.95))],
            Min = sorted[0],
            Max = sorted[^1],
        };
    }

    private static async Task WaitForPlayerTurnAsync(HarnessHost host, int afterTurn = -1)
    {
        var tree = host.GetTree();
        var sw = Stopwatch.StartNew();
        for (var i = 0; sw.Elapsed < _maxWait; i++)
        {
            var state = CombatManager.Instance.DebugOnlyGetState();
            if (!CombatManager.Instance.IsInProgress && i > 5)
            {
                return;
            }
            var me = state is null ? null : LocalContext.GetMe(state);
            var pcs = me?.PlayerCombatState;
            if (
                pcs is { Phase: PlayerTurnPhase.Play }
                && pcs.TurnNumber > afterTurn
                && RunManager.Instance.ActionExecutor.CurrentlyRunningAction is null
            )
            {
                return;
            }
            _ = await host.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
        throw new TimeoutException("player turn not reached");
    }

    private static JsonElement WithTiming(JsonElement payload, TimeSpan elapsed)
    {
        using var doc = JsonDocument.Parse(payload.GetRawText());
        var dict = new Dictionary<string, object?>();
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                dict[p.Name] = p.Value.Clone();
            }
        }
        else
        {
            dict["value"] = doc.RootElement.Clone();
        }
        dict["opMillis"] = elapsed.TotalMilliseconds;
        return JsonSerializer.SerializeToElement(dict, HarnessJson.Options);
    }

    private static Task<JsonElement?> Result(object value) => Task.FromResult<JsonElement?>(Json(value));

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value, HarnessJson.Options);
}
