using System.Diagnostics;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;

namespace Ai4Sts2.Workbench;

public sealed class Session
{
    private const ulong LocalNetId = 1;
    private readonly Dictionary<int, Snapshot> _snaps = [];
    private int _snapSeq;
    private bool _appendedHistory;
    private IDisposable? _selectorScope;

    public ScriptSelector Selector { get; } = new();

    public RunState? Run { get; private set; }

    public CombatRoom? Room { get; private set; }

    public Pump Pump { get; } = new();

    public RunFlow Flow { get; }

    public bool RealMap { get; private set; }

    public static Session Instance { get; } = new();

    public Session()
    {
        Flow = new RunFlow(this);
    }

    public RunState NewRun(string character, string seed, int ascension) => NewRun([character], seed, ascension);

    public RunState NewRun(IReadOnlyList<string> characters, string seed, int ascension, bool realMap = false)
    {
        if (Run is not null)
        {
            ExitRooms(Run);
            RunManager.Instance.CleanUp(true);
            Run = null;
            _appendedHistory = false;
            _selectorScope?.Dispose();
            _selectorScope = null;
        }
        return EnsureRun(characters, seed, ascension, realMap);
    }

    public RunState EnsureRun(string character, string seed, int ascension) => EnsureRun([character], seed, ascension);

    public RunState EnsureRun(IReadOnlyList<string> characters, string seed, int ascension, bool realMap = false)
    {
        if (Run is not null)
        {
            return Run;
        }
        RealMap = realMap;
        LocalContext.NetId = LocalNetId;
        if (!Switches.Applied)
        {
            Switches.ApplyEarly(LocalNetId);
        }
        Switches.ApplyLate();
        TestFlags.ShouldSendResumeForRemotePlayers = characters.Count > 1;
        var unlocks = SaveManager.Instance.GenerateUnlockStateFromProgress();
        var players = characters
            .Select(
                (character, i) =>
                    Player.CreateForNewRun(
                        ModelDb.GetById<CharacterModel>(
                            new ModelId(ModelId.SlugifyCategory<CharacterModel>(), character)
                        ),
                        unlocks,
                        LocalNetId + (ulong)i
                    )
            )
            .ToList();
        var state = RunState.CreateForNewRun(
            players,
            ModelDb.Acts.Select(a => a.ToMutable()).ToList(),
            [],
            GameMode.Standard,
            ascension,
            seed
        );
        if (!realMap)
        {
            state.Map = new MockSinglePointActMap();
        }
        RunManager.Instance.SetUpTest(state, new NetSingleplayerGameService());
        Run = state;
        _selectorScope = CardSelectCmd.PushSelector(Selector);
        if (realMap)
        {
            Flow.Begin();
        }
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
        if (!RealMap && _appendedHistory && run._mapPointHistory.Count > 0 && run._mapPointHistory[^1].Count > 0)
        {
            run._mapPointHistory[^1].RemoveAt(run._mapPointHistory[^1].Count - 1);
        }
        run.AppendToMapPointHistory(MapPointType.Monster, encounter.RoomType, encounter.Id);
        _appendedHistory = true;
        var room = new CombatRoom(encounter, run);
        CombatManager.Instance._turnLoopTask = null;
        run.PushRoom(room);
        Pump.Drive(() => Hook.BeforeRoomEntered(run, room), "BeforeRoomEntered");
        Pump.Drive(() => room.Enter(run, false), "enter room");
        RequirePlayable(-1, "combat start");
        Room = room;
        return room.CombatState;
    }

    public TimeSpan Play(int handIndex, int? enemyIndex) => Play(0, handIndex, enemyIndex);

    public TimeSpan Play(int playerIndex, int handIndex, int? enemyIndex)
    {
        var (state, player) = Current(playerIndex);
        using var scope = ActAs(player);
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

    public static PotionModel? UsablePotion(Player player, int slot)
    {
        var potion = player.GetPotionAtSlotIndex(slot);
        if (potion is null || potion.IsQueued || potion.HasBeenRemovedFromState)
        {
            return null;
        }
        var usable =
            potion.Usage is PotionUsage.AnyTime or PotionUsage.CombatOnly
            && player.CanUseOrRemovePotions
            && potion.PassesCustomUsabilityCheck;
        return usable ? potion : null;
    }

    public static Creature? PotionTarget(PotionModel potion, CombatState state, int? enemyIndex)
    {
        return !potion.TargetType.IsSingleTarget() ? null
            : potion.TargetType == TargetType.AnyEnemy
                ? enemyIndex is { } i && i < state.Enemies.Count ? state.Enemies[i]
                    : null
            : potion.Owner.Creature;
    }

    public TimeSpan UsePotion(int playerIndex, int slot, int? enemyIndex)
    {
        var (state, player) = Current(playerIndex);
        using var scope = ActAs(player);
        var potion =
            UsablePotion(player, slot) ?? throw new InvalidOperationException($"potion slot {slot} not usable");
        var target = PotionTarget(potion, state, enemyIndex);
        if (!potion.IsValidTarget(target))
        {
            throw new InvalidOperationException($"invalid target for {potion.Id.Entry}");
        }
        var sw = Stopwatch.StartNew();
        Pump.Drive(() => potion.OnUseWrapper(new BlockingPlayerChoiceContext(), target), $"potion {potion.Id.Entry}");
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

    public TimeSpan EndTurn() => EndTurn(0);

    public TimeSpan EndTurn(int playerIndex)
    {
        var (state, player) = Current(playerIndex);
        var turn = player.PlayerCombatState!.TurnNumber;
        var last = state.Players.All(p => p == player || CombatManager.Instance.IsPlayerReadyToEndTurn(p));
        var sw = Stopwatch.StartNew();
        using (ActAs(player))
        {
            Pump.Run(() => PlayerCmd.EndTurn(player, false));
        }
        if (last)
        {
            RequirePlayable(turn, "end turn");
        }
        return sw.Elapsed;
    }

    public static bool HasEnded(Player player) => CombatManager.Instance.IsPlayerReadyToEndTurn(player);

    public static IDisposable ActAs(Player player) => new ContextScope(player.NetId);

    private sealed class ContextScope : IDisposable
    {
        private readonly ulong? _previous = LocalContext.NetId;

        public ContextScope(ulong netId)
        {
            LocalContext.NetId = netId;
        }

        public void Dispose() => LocalContext.NetId = _previous;
    }

    private void ExitRooms(RunState run)
    {
        while (run.CurrentRoomCount > 0)
        {
            var previous = run.PopCurrentRoom();
            Pump.Drive(() => previous.Exit(run), "exit room");
        }
    }

    public void RequirePlayable(int afterTurn, string label)
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

    public static (CombatState State, Player Player) Current(int playerIndex)
    {
        var state = CombatManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("no combat");
        var players = state.Players;
        return playerIndex < 0 || playerIndex >= players.Count
            ? throw new ArgumentOutOfRangeException(nameof(playerIndex), $"player {playerIndex} of {players.Count}")
            : (state, players[playerIndex]);
    }

    public void Dispose()
    {
        _selectorScope?.Dispose();
        _selectorScope = null;
    }
}
