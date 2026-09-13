using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.TestSupport;

namespace Ai4Sts2.Workbench;

public sealed class ScriptSelector : ICardSelector
{
    private readonly Queue<int[]> _script = new();

    public List<(IReadOnlyList<string> Options, int Min, int Max, int[] Chosen)> Log { get; } = [];

    public void Enqueue(params int[] choice) => _script.Enqueue(choice);

    public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
    {
        var cards = options as IReadOnlyList<CardModel> ?? options.ToList();
        var count = Math.Min(minSelect > 0 ? minSelect : Math.Min(1, maxSelect), cards.Count);
        var chosen = _script.Count > 0 ? _script.Dequeue() : Enumerable.Range(0, count).ToArray();
        if (Log.Count >= 200)
        {
            Log.RemoveAt(0);
        }
        Log.Add((cards.Select(c => c.Id.Entry).ToList(), minSelect, maxSelect, chosen));
        IEnumerable<CardModel> result = chosen.Select(i => cards[i]).ToList();
        return Task.FromResult(result);
    }

    public (int? Card, string? Alternative)? CardReward { get; set; }

    public CardRewardSelection GetSelectedCardReward(
        IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives
    )
    {
        var (card, alternative) = CardReward ?? throw new InvalidOperationException("no card reward choice scripted");
        return card is { } index ? new CardRewardSelection { card = options[index].Card }
            : alternative is { } id
                ? new CardRewardSelection { alternative = alternatives.First(a => a.OptionId == id) }
            : default;
    }
}
