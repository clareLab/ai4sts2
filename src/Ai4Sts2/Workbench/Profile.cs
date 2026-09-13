using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Timeline;
using MegaCrit.Sts2.Core.Unlocks;

namespace Ai4Sts2.Workbench;

public static class Profile
{
    public static bool Veteran { get; set; } = Environment.GetEnvironmentVariable("AI4STS2_PROFILE") != "fresh";

    public static UnlockState Unlocks()
    {
        var progress = SaveManager.Instance.Progress;
        if (Veteran)
        {
            foreach (var id in EpochModel.AllEpochIds)
            {
                progress.ObtainEpochOverride(id, EpochState.Revealed);
            }
            foreach (var character in ModelDb.AllCharacters)
            {
                var stats = progress.GetOrCreateCharacterStats(character.Id);
                stats.TotalLosses = Math.Max(stats.TotalLosses, 1);
            }
        }
        return SaveManager.Instance.GenerateUnlockStateFromProgress();
    }
}
