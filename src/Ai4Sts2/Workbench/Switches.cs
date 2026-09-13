using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using MegaCrit.Sts2.Core.TestSupport;

namespace Ai4Sts2.Workbench;

public static class Switches
{
    public const string EnvVar = "AI4STS2_WORKBENCH";

    public static bool Requested => Environment.GetEnvironmentVariable(EnvVar) == "1";

    public static bool Applied { get; private set; }

    public static void ApplyEarly(ulong localNetId)
    {
        TestMode.IsOn = true;
        NonInteractiveMode.AutoSlayerCheck = static () => true;
        LocalContext.NetId = localNetId;
        RewardsSet.testSelector = SelectRewardsLeniently;
        Applied = true;
        Patches.Apply();
    }

    private static async Task SelectRewardsLeniently(RewardsSet set)
    {
        var sync = RunManager.Instance.RewardsSetSynchronizer;
        var local = set.Player.NetId == sync._localPlayerId;
        var state = local
            ? null
            : sync.GetRewardStateForPlayer(set.Player).rewardsStack.LastOrDefault(s => s.set == set);
        if (!local && state is null)
        {
            return;
        }
        foreach (var reward in set.Rewards.ToList())
        {
            try
            {
                _ = local ? await sync.SelectLocalReward(reward) : await sync.SelectRewardForPlayer(state!, reward);
            }
            catch (InvalidOperationException e)
            {
                Entry.Log.Warn($"reward {reward} for {set.Player.NetId} not taken: {e.Message}");
            }
        }
        if (sync.IsRewardsSetCompleted(set))
        {
            return;
        }
        if (local)
        {
            sync.SkipLocalRewardsSet();
        }
        else
        {
            sync.SkipRewardsSet(state!);
        }
    }

    public static void ApplyLate()
    {
        var prefs = SaveManager.Instance.PrefsSave;
        if (prefs is not null)
        {
            prefs.FastMode = FastModeType.Instant;
        }
        Logger.GlobalLogLevel = LogLevel.Warn;
        foreach (var type in Enum.GetValues<LogType>())
        {
            Logger.SetLogLevelForType(type, LogLevel.Warn);
        }
    }
}
