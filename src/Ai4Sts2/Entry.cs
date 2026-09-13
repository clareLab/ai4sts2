using Ai4Sts2.Harness;
using Ai4Sts2.Workbench;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Saves;
using STS2RitsuLib;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace Ai4Sts2;

[ModInitializer(nameof(Initialize))]
public static class Entry
{
    public const string ModId = "ai4sts2";

    public static Logger Log { get; } = new(ModId, LogType.Generic);

    public static void Initialize()
    {
        if (HarnessHost.Enabled)
        {
            Patches.ApplyHeadless();
        }
        if (Switches.Requested)
        {
            Switches.ApplyEarly(1);
            Log.Info("workbench switches applied");
        }
        _ = RitsuLibFramework.SubscribeLifecycle<GameReadyEvent>(evt =>
        {
            if (Switches.Requested)
            {
                Switches.ApplyLate();
            }
            if (HarnessHost.Enabled)
            {
                SaveManager.Instance.SetFtuesEnabled(false);
                HarnessHost.Attach(evt.Game);
            }
        });
        _ = RitsuLibFramework.SubscribeLifecycle<CombatStartingEvent>(OnCombatStarting);
        _ = RitsuLibFramework.SubscribeLifecycle<CombatEndedEvent>(OnCombatEnded);
        Log.Info($"{ModId} initialized version={typeof(Entry).Assembly.GetName().Version}");
    }

    private static void OnCombatStarting(CombatStartingEvent evt) =>
        Log.Info($"combat starting: {evt.CombatState?.Encounter?.Id}");

    private static void OnCombatEnded(CombatEndedEvent evt) => Log.Info("combat ended");
}
