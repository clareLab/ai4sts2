using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Runs;

namespace Ai4Sts2.Workbench;

public sealed record RestoreStats(double RestoreMicros, double ResyncMicros, int Objects, int Arrays, int Fields);

public static class Loader
{
    public static IEnumerable<object> Roots()
    {
        yield return CombatManager.Instance;
        yield return RunManager.Instance;
        if (NetCombatCardDb.Instance is { } db)
        {
            yield return db;
        }
    }

    public static Snapshot Take() => Snapshot.Take(Roots());

    public static RestoreStats Restore(Snapshot snap, Pump pump)
    {
        var restore = snap.Restore();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Resync(pump);
        return new RestoreStats(
            restore.TotalMicroseconds,
            sw.Elapsed.TotalMicroseconds,
            snap.Objects,
            snap.Arrays,
            snap.Fields
        );
    }

    private static void Resync(Pump pump)
    {
        var manager = CombatManager.Instance;
        var turnState = manager._turnState;
        if (turnState is null || !turnState.IsInProgress)
        {
            manager._turnLoopTask = null;
            return;
        }
        using (turnState.ReadyLock.EnterScope())
        {
            turnState.EndTurnSignalSource = new TaskCompletionSource<EndTurnSignal>();
            turnState.BeginEnemyTurnSignalSource = new TaskCompletionSource<Func<Task>?>();
        }
        pump.Run(() => manager._turnLoopTask = ResumeLoop(manager, turnState));
    }

    private static async Task ResumeLoop(CombatManager manager, CombatTurnState turnState)
    {
        var actionDuringEnemyTurn = await manager.AwaitTurnEndAndSwitchSides(turnState);
        while (turnState.IsLive)
        {
            if (turnState.State.CurrentSide == CombatSide.Player)
            {
                await manager.StartTurn(turnState, null);
                actionDuringEnemyTurn = await manager.AwaitTurnEndAndSwitchSides(turnState);
            }
            else
            {
                await manager.StartTurn(turnState, actionDuringEnemyTurn);
                actionDuringEnemyTurn = null;
            }
        }
    }
}
