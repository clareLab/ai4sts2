using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Audio.Debug;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Saves;

namespace Ai4Sts2.Workbench;

public static class Patches
{
    private const BindingFlags Any =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static Harmony? _harmony;

    public static bool Applied => _harmony is not null;

    public static int Count { get; private set; }

    public static void Apply()
    {
        if (_harmony is not null)
        {
            return;
        }
        var harmony = new Harmony(Entry.ModId);
        Prefix(harmony, typeof(CombatStateTracker), "NotifyCombatStateChanged", [typeof(string)], nameof(SkipVoid));
        Prefix(
            harmony,
            typeof(NDebugAudioManager),
            "Play",
            [typeof(string), typeof(float), typeof(PitchVariance)],
            nameof(SkipInt)
        );
        Prefix(harmony, typeof(SaveManager), "SaveRun", [typeof(AbstractRoom), typeof(bool)], nameof(SkipTask));
        Prefix(harmony, typeof(SaveManager), "SaveProgressFile", [], nameof(SkipVoid));
        PrefixRitsuLibBaseLibBridge(harmony);
        _harmony = harmony;
    }

    private static void PrefixRitsuLibBaseLibBridge(Harmony harmony)
    {
        var registry = AccessTools.TypeByName("STS2RitsuLib.Compat.ExternalFrameworkRegistry");
        var bridge = AccessTools.TypeByName("STS2RitsuLib.Combat.HandSize.BaseLibMaxHandSizeBridge");
        if (registry is null || bridge is null)
        {
            return;
        }
        var isPresent = AccessTools.Method(registry, "IsFrameworkPresent");
        var target = AccessTools.Method(bridge, "TryGetMaxHandSizeFromBaseLib");
        if (isPresent is null || target is null)
        {
            Entry.Log.Warn("RitsuLib BaseLib bridge not found; skipping its patch");
            return;
        }
        if ((bool)isPresent.Invoke(null, ["baselib"])!)
        {
            return;
        }
        var prefix = typeof(Patches).GetMethod(nameof(SkipTryGet), BindingFlags.Static | BindingFlags.NonPublic);
        _ = harmony.Patch(target, prefix: new HarmonyMethod(prefix));
        Count++;
    }

    private static void Prefix(Harmony harmony, Type type, string method, Type[] parameters, string prefixName)
    {
        var target = type.GetMethod(method, Any, parameters) ?? throw new MissingMethodException(type.FullName, method);
        var prefix = typeof(Patches).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic);
        _ = harmony.Patch(target, prefix: new HarmonyMethod(prefix));
        Count++;
    }

    private static bool SkipVoid() => !Switches.Applied;

    private static bool SkipInt(ref int __result)
    {
        __result = 0;
        return !Switches.Applied;
    }

    private static bool SkipTryGet(out int amount, ref bool __result)
    {
        amount = 0;
        __result = false;
        return !Switches.Applied;
    }

    private static bool SkipTask(ref Task __result)
    {
        __result = Task.CompletedTask;
        return !Switches.Applied;
    }
}
