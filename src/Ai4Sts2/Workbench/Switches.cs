using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
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
        Applied = true;
        Patches.Apply();
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
