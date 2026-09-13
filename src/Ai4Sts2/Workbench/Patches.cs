using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Audio.Debug;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Vfx.Utilities;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
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
        Prefix(harmony, typeof(RunManager), "OnEnded", [typeof(bool)], nameof(SkipRunEnded));
        Prefix(
            harmony,
            typeof(NGame),
            "ScreenShake",
            [typeof(ShakeStrength), typeof(ShakeDuration), typeof(float)],
            nameof(SkipVoid)
        );
        Prefix(harmony, typeof(NGame), "ScreenShakeTrauma", [typeof(ShakeStrength)], nameof(SkipVoid));
        Prefix(harmony, typeof(CombatState), "get_Creatures", [], nameof(Creatures));
        Prefix(harmony, typeof(CombatState), "get_PlayerCreatures", [], nameof(PlayerCreatures));
        Prefix(harmony, typeof(CombatState), "get_Players", [], nameof(Players));
        PrefixRitsuLibBaseLibBridge(harmony);
        _harmony = harmony;
    }

    private static void PrefixRitsuLibBaseLibBridge(Harmony harmony)
    {
        var registry = AccessTools.TypeByName("STS2RitsuLib.Compat.ExternalFrameworkRegistry");
        var isPresent = registry is null ? null : AccessTools.Method(registry, "IsFrameworkPresent");
        if (isPresent is null || (bool)isPresent.Invoke(null, ["baselib"])!)
        {
            return;
        }
        PrefixByName(
            harmony,
            "STS2RitsuLib.Combat.HandSize.BaseLibMaxHandSizeBridge",
            "TryGetMaxHandSizeFromBaseLib",
            nameof(SkipTryGet)
        );
        PrefixByName(
            harmony,
            "STS2RitsuLib.Combat.CardTargeting.BaseLibTargetTypeBridge",
            "EnsureResolved",
            nameof(SkipVoid)
        );
        PrefixLifecycleBridge(harmony);
    }

    private static void PrefixLifecycleBridge(Harmony harmony)
    {
        var bridge = AccessTools.TypeByName("STS2RitsuLib.Lifecycle.Patches.LifecyclePatchTaskBridge");
        if (bridge is null)
        {
            return;
        }
        var prefix = typeof(Patches).GetMethod(nameof(PassThrough), BindingFlags.Static | BindingFlags.NonPublic);
        foreach (var method in bridge.GetMethods(BindingFlags.Static | BindingFlags.Public))
        {
            if (method.Name == "After" && !method.IsGenericMethodDefinition)
            {
                _ = harmony.Patch(method, prefix: new HarmonyMethod(prefix));
                Count++;
            }
        }
    }

    private static bool PassThrough(Task originalTask, ref Task __result)
    {
        __result = originalTask;
        return !Switches.Applied;
    }

    private static void PrefixByName(Harmony harmony, string typeName, string method, string prefixName)
    {
        var type = AccessTools.TypeByName(typeName);
        var target = type is null ? null : AccessTools.Method(type, method);
        if (target is null)
        {
            Entry.Log.Warn($"{typeName}.{method} not found; skipping its patch");
            return;
        }
        var prefix = typeof(Patches).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic);
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

    private static bool SkipRunEnded(ref SerializableRun __result)
    {
        __result = null!;
        return !Switches.Applied;
    }

    private static bool SkipTask(ref Task __result)
    {
        __result = Task.CompletedTask;
        return !Switches.Applied;
    }

    private static bool Creatures(CombatState __instance, ref IReadOnlyList<Creature> __result)
    {
        if (!Switches.Applied)
        {
            return true;
        }
        var allies = __instance._allies;
        var enemies = __instance._enemies;
        var list = new List<Creature>(allies.Count + enemies.Count);
        list.AddRange(allies);
        list.AddRange(enemies);
        __result = list;
        return false;
    }

    private static bool PlayerCreatures(CombatState __instance, ref IReadOnlyList<Creature> __result)
    {
        if (!Switches.Applied)
        {
            return true;
        }
        var list = new List<Creature>(__instance._allies.Count);
        Collect(__instance._allies, list);
        Collect(__instance._enemies, list);
        __result = list;
        return false;
    }

    private static bool Players(CombatState __instance, ref IReadOnlyList<Player> __result)
    {
        if (!Switches.Applied)
        {
            return true;
        }
        var list = new List<Player>(__instance._allies.Count);
        foreach (var creature in __instance._allies)
        {
            if (creature.Player is { } player)
            {
                list.Add(player);
            }
        }
        foreach (var creature in __instance._enemies)
        {
            if (creature.Player is { } player)
            {
                list.Add(player);
            }
        }
        __result = list;
        return false;
    }

    private static void Collect(List<Creature> source, List<Creature> into)
    {
        foreach (var creature in source)
        {
            if (creature.IsPlayer)
            {
                into.Add(creature);
            }
        }
    }
}
