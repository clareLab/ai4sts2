using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Audio.Debug;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx.Utilities;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using MegaCrit.Sts2.Core.TestSupport;

namespace Ai4Sts2.Workbench;

public enum PatchPurpose
{
    Perf,
    Headless,
    Semantics,
    Workaround,
}

public sealed record PatchEntry(
    Type Type,
    string Method,
    Type[] Parameters,
    string Prefix,
    PatchPurpose Purpose,
    string? Il
);

public sealed record PatchStatus(
    string Target,
    string Purpose,
    bool Applied,
    string? Il,
    string? Expected,
    bool Stale,
    string? Note
);

public static class Patches
{
    private const BindingFlags Any =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static Harmony? _harmony;

    public static bool Applied => _harmony is not null;

    public static int Count { get; private set; }

    public static List<PatchStatus> Statuses { get; } = [];

    public static int Stale => Statuses.Count(s => s.Stale);

    private static Harmony? _headless;

    private static readonly PatchEntry[] _registry =
    [
        new(
            typeof(CombatStateTracker),
            "NotifyCombatStateChanged",
            [typeof(string)],
            nameof(SkipVoid),
            PatchPurpose.Headless,
            "5750C98D9F1F"
        ),
        new(
            typeof(NDebugAudioManager),
            "Play",
            [typeof(string), typeof(float), typeof(PitchVariance)],
            nameof(SkipInt),
            PatchPurpose.Headless,
            "F96C5D4CB4BC"
        ),
        new(
            typeof(SaveManager),
            "SaveRun",
            [typeof(AbstractRoom), typeof(bool)],
            nameof(SkipTask),
            PatchPurpose.Semantics,
            "FDCC8AE07FE4"
        ),
        new(typeof(SaveManager), "SaveProgressFile", [], nameof(SkipVoid), PatchPurpose.Semantics, "EAB7AD109464"),
        new(
            typeof(RunManager),
            "OnEnded",
            [typeof(bool)],
            nameof(SkipRunEnded),
            PatchPurpose.Semantics,
            "7C4BE193B339"
        ),
        new(
            typeof(CombatManager),
            "SetReadyToBeginEnemyTurn",
            [typeof(Player), typeof(Func<Task>)],
            nameof(ReadyEveryoneToBeginEnemyTurn),
            PatchPurpose.Semantics,
            "5DF9CF97AD75"
        ),
        new(
            typeof(NGame),
            "ScreenShake",
            [typeof(ShakeStrength), typeof(ShakeDuration), typeof(float)],
            nameof(SkipVoid),
            PatchPurpose.Headless,
            "7AC9878C5009"
        ),
        new(
            typeof(NGame),
            "ScreenShakeTrauma",
            [typeof(ShakeStrength)],
            nameof(SkipVoid),
            PatchPurpose.Headless,
            "52DE5304CEB7"
        ),
        new(
            typeof(SoulNexus),
            "AfterDeath",
            [typeof(Creature)],
            nameof(SkipWithoutCombatRoom),
            PatchPurpose.Workaround,
            "8226EB050047"
        ),
        new(
            typeof(CardPile),
            "RandomizeOrderInternal",
            [typeof(Player), typeof(Rng), typeof(CombatState)],
            nameof(ArmStableShuffle),
            PatchPurpose.Semantics,
            "DC11BF77C56E"
        ),
        new(typeof(CombatState), "get_Creatures", [], nameof(Creatures), PatchPurpose.Perf, "9FED49BCF1F6"),
        new(typeof(CombatState), "get_PlayerCreatures", [], nameof(PlayerCreatures), PatchPurpose.Perf, "198490C17EB2"),
        new(typeof(CombatState), "get_Players", [], nameof(Players), PatchPurpose.Perf, "9061D2175D68"),
    ];

    public static List<PatchStatus> Preview()
    {
        var list = new List<PatchStatus>();
        foreach (var entry in _registry)
        {
            var name = $"{entry.Type.Name}.{entry.Method}";
            var target = entry.Type.GetMethod(entry.Method, Any, entry.Parameters);
            var il = target is null ? null : Fingerprint(target);
            var stale = target is null || (entry.Il is not null && entry.Il != il);
            list.Add(
                new PatchStatus(
                    name,
                    entry.Purpose.ToString(),
                    false,
                    il,
                    entry.Il,
                    stale,
                    target is null ? "missing" : null
                )
            );
        }
        return list;
    }

    public static string Fingerprint(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(il))[..12];
    }

    public static void ApplyHeadless()
    {
        if (_headless is not null)
        {
            return;
        }
        var harmony = new Harmony(Entry.ModId + ".headless");
        var target = typeof(ProgressSaveManager).GetMethod("SeenFtue", Any, [typeof(string)])!;
        var prefix = typeof(Patches).GetMethod(nameof(SeenFtue), BindingFlags.Static | BindingFlags.NonPublic);
        _ = harmony.Patch(target, prefix: new HarmonyMethod(prefix));
        _headless = harmony;
    }

    private static bool SeenFtue(ref bool __result)
    {
        __result = true;
        return false;
    }

    public static void Apply()
    {
        if (_harmony is not null)
        {
            return;
        }
        var harmony = new Harmony(Entry.ModId);
        foreach (var entry in _registry)
        {
            Prefix(harmony, entry);
        }
        PrefixRitsuLibBaseLibBridge(harmony);
        _harmony = harmony;
        if (Stale > 0)
        {
            Entry.Log.Warn($"{Stale} patch target(s) changed since their fingerprints were recorded");
        }
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
                Statuses.Add(
                    new PatchStatus($"{bridge.Name}.After", "Perf", true, Fingerprint(method), null, false, "ritsulib")
                );
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
            Statuses.Add(new PatchStatus($"{typeName}.{method}", "Perf", false, null, null, false, "missing"));
            return;
        }
        var prefix = typeof(Patches).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic);
        _ = harmony.Patch(target, prefix: new HarmonyMethod(prefix));
        Count++;
        Statuses.Add(
            new PatchStatus($"{typeName}.{method}", "Perf", true, Fingerprint(target), null, false, "ritsulib")
        );
    }

    private static void Prefix(Harmony harmony, PatchEntry entry)
    {
        var name = $"{entry.Type.Name}.{entry.Method}";
        var target = entry.Type.GetMethod(entry.Method, Any, entry.Parameters);
        if (target is null)
        {
            Entry.Log.Warn($"{name} not found; skipping its patch");
            Statuses.Add(new PatchStatus(name, entry.Purpose.ToString(), false, null, entry.Il, true, "missing"));
            return;
        }
        var il = Fingerprint(target);
        var stale = entry.Il is not null && entry.Il != il;
        var prefix = typeof(Patches).GetMethod(entry.Prefix, BindingFlags.Static | BindingFlags.NonPublic);
        _ = harmony.Patch(target, prefix: new HarmonyMethod(prefix));
        Count++;
        Statuses.Add(
            new PatchStatus(name, entry.Purpose.ToString(), true, il, entry.Il, stale, stale ? "changed" : null)
        );
    }

    private static bool SkipVoid() => !Switches.Applied;

    private static bool SkipWithoutCombatRoom() => NCombatRoom.Instance is not null;

    private static void ArmStableShuffle()
    {
        if (Rollout.StableOrder is { } order)
        {
            TestRngInjector.SetInitialShuffleOverride(order);
        }
    }

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

    private static void ReadyEveryoneToBeginEnemyTurn(CombatManager __instance, Player player)
    {
        if (!Switches.Applied || RunManager.Instance.NetService.Type != NetGameType.Host)
        {
            return;
        }
        var ts = __instance._turnState;
        if (ts is null || !ts.IsInProgress || ts.State.CurrentSide != CombatSide.Player)
        {
            return;
        }
        using (ts.ReadyLock.EnterScope())
        {
            foreach (var other in ts.State.Players)
            {
                if (other != player)
                {
                    _ = ts.PlayersReadyToBeginEnemyTurn.Add(other);
                }
            }
        }
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

    private sealed class CreatureLists
    {
        public List<Creature> Creatures = [];
        public List<Creature> PlayerCreatures = [];
        public List<Player> Players = [];
        public readonly List<Creature> Seen = new(8);
    }

    private static readonly ConditionalWeakTable<CombatState, CreatureLists> _lists = [];

    private static CreatureLists Lists(CombatState state)
    {
        var lists = _lists.GetValue(state, _ => new CreatureLists());
        var allies = state._allies;
        var enemies = state._enemies;
        var seen = lists.Seen;
        var same = seen.Count == allies.Count + enemies.Count;
        for (var i = 0; same && i < allies.Count; i++)
        {
            same = ReferenceEquals(seen[i], allies[i]);
        }
        for (var i = 0; same && i < enemies.Count; i++)
        {
            same = ReferenceEquals(seen[allies.Count + i], enemies[i]);
        }
        if (same)
        {
            return lists;
        }
        seen.Clear();
        seen.AddRange(allies);
        seen.AddRange(enemies);
        lists.Creatures = [.. seen];
        lists.PlayerCreatures = new List<Creature>(seen.Count);
        lists.Players = new List<Player>(seen.Count);
        foreach (var creature in seen)
        {
            if (creature.IsPlayer)
            {
                lists.PlayerCreatures.Add(creature);
            }
            if (creature.Player is { } player)
            {
                lists.Players.Add(player);
            }
        }
        return lists;
    }

    private static bool Creatures(CombatState __instance, ref IReadOnlyList<Creature> __result)
    {
        if (!Switches.Applied)
        {
            return true;
        }
        __result = Lists(__instance).Creatures;
        return false;
    }

    private static bool PlayerCreatures(CombatState __instance, ref IReadOnlyList<Creature> __result)
    {
        if (!Switches.Applied)
        {
            return true;
        }
        __result = Lists(__instance).PlayerCreatures;
        return false;
    }

    private static bool Players(CombatState __instance, ref IReadOnlyList<Player> __result)
    {
        if (!Switches.Applied)
        {
            return true;
        }
        __result = Lists(__instance).Players;
        return false;
    }
}
