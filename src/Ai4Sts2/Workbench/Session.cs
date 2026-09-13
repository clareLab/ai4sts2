using System.Diagnostics;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace Ai4Sts2.Workbench;

public sealed class Session
{
    private const ulong LocalNetId = 1;
    private readonly Dictionary<int, Snapshot> _snaps = [];
    private int _snapSeq;
    private IDisposable? _selectorScope;

    public ScriptSelector Selector { get; } = new();

    public RunState? Run { get; private set; }

    public CombatRoom? Room { get; private set; }

    public Pump Pump { get; } = new();

    public static Session Instance { get; } = new();

    public RunState NewRun(string character, string seed, int ascension)
    {
        if (Run is not null)
        {
            ExitRooms(Run);
            RunManager.Instance.CleanUp(true);
            Run = null;
            _selectorScope?.Dispose();
            _selectorScope = null;
        }
        return EnsureRun(character, seed, ascension);
    }

    public RunState EnsureRun(string character, string seed, int ascension)
    {
        if (Run is not null)
        {
            return Run;
        }
        LocalContext.NetId = LocalNetId;
        if (!Switches.Applied)
        {
            Switches.ApplyEarly(LocalNetId);
        }
        Switches.ApplyLate();
        var model = ModelDb.GetById<CharacterModel>(new ModelId(ModelId.SlugifyCategory<CharacterModel>(), character));
        var unlocks = SaveManager.Instance.GenerateUnlockStateFromProgress();
        var player = Player.CreateForNewRun(model, unlocks, LocalNetId);
        var state = RunState.CreateForNewRun(
            [player],
            ModelDb.Acts.Select(a => a.ToMutable()).ToList(),
            [],
            GameMode.Standard,
            ascension,
            seed
        );
        state.Map = new MockSinglePointActMap();
        RunManager.Instance.SetUpTest(state, new NetSingleplayerGameService());
        Run = state;
        _selectorScope = CardSelectCmd.PushSelector(Selector);
        return state;
    }

    public CombatState StartEncounter(string encounterId, bool fullHeal)
    {
        var run = Run ?? throw new InvalidOperationException("run not set up");
        _snaps.Clear();
        if (fullHeal)
        {
            foreach (var player in run.Players)
            {
                player.Creature.HealInternal(player.Creature.MaxHp - player.Creature.CurrentHp);
            }
        }
        var encounter = ModelDb
            .GetById<EncounterModel>(new ModelId(ModelId.SlugifyCategory<EncounterModel>(), encounterId))
            .ToMutable();
        ExitRooms(run);
        run.AppendToMapPointHistory(MapPointType.Monster, encounter.RoomType, encounter.Id);
        var room = new CombatRoom(encounter, run);
        run.PushRoom(room);
        Pump.Drive(() => Hook.BeforeRoomEntered(run, room), "BeforeRoomEntered");
        Pump.Drive(() => room.Enter(run, false), "enter room");
        RequirePlayable(-1, "combat start");
        Room = room;
        return room.CombatState;
    }

    public TimeSpan Play(int handIndex, int? enemyIndex)
    {
        var (state, player) = Current();
        var card = player.PlayerCombatState!.Hand.Cards[handIndex];
        var target = enemyIndex is { } i ? state.Enemies[i] : null;
        if (target is not null && !card.IsValidTarget(target))
        {
            target = null;
        }
        if (!card.CanPlay(out var reason, out _) || !card.IsValidTarget(target))
        {
            throw new InvalidOperationException($"cannot play {card.Id.Entry}: {reason}");
        }
        var sw = Stopwatch.StartNew();
        Pump.Drive(
            async () =>
            {
                var (energy, stars) = await card.SpendResources();
                var resources = new ResourceInfo
                {
                    EnergySpent = energy,
                    EnergyValue = energy,
                    StarsSpent = stars,
                    StarValue = stars,
                };
                await card.OnPlayWrapper(new BlockingPlayerChoiceContext(), target, false, resources);
            },
            $"play {card.Id.Entry}"
        );
        _ = Pump.Drive(CombatManager.Instance.CheckWinCondition, "win check");
        return sw.Elapsed;
    }

    public (int Id, Snapshot Snapshot) Snap()
    {
        var snap = Loader.Take();
        var id = ++_snapSeq;
        _snaps[id] = snap;
        return (id, snap);
    }

    public RestoreStats Restore(int id)
    {
        return !_snaps.TryGetValue(id, out var snap)
            ? throw new KeyNotFoundException($"snapshot {id} not found")
            : Loader.Restore(snap, Pump);
    }

    public void DropSnapshots() => _snaps.Clear();

    public TimeSpan EndTurn()
    {
        var (_, player) = Current();
        var turn = player.PlayerCombatState!.TurnNumber;
        var sw = Stopwatch.StartNew();
        Pump.Run(() => PlayerCmd.EndTurn(player, false));
        RequirePlayable(turn, "end turn");
        return sw.Elapsed;
    }

    private void ExitRooms(RunState run)
    {
        while (run.CurrentRoomCount > 0)
        {
            var previous = run.PopCurrentRoom();
            Pump.Drive(() => previous.Exit(run), "exit room");
        }
    }

    private void RequirePlayable(int afterTurn, string label)
    {
        if (!CombatManager.Instance.IsInProgress)
        {
            return;
        }
        var state = CombatManager.Instance.DebugOnlyGetState();
        var pcs = state is null ? null : LocalContext.GetMe(state)?.PlayerCombatState;
        if (pcs is { Phase: PlayerTurnPhase.Play } && pcs.TurnNumber > afterTurn)
        {
            return;
        }
        throw new LeakedAwaitException(
            $"{label}: phase={pcs?.Phase} turn={pcs?.TurnNumber} after pump drained (posted={Pump.Posted}, drained={Pump.Drained})"
        );
    }

    private static (CombatState State, Player Player) Current()
    {
        var state = CombatManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("no combat");
        var player = LocalContext.GetMe(state) ?? throw new InvalidOperationException("no local player");
        return (state, player);
    }

    public void Dispose()
    {
        _selectorScope?.Dispose();
        _selectorScope = null;
    }
}
