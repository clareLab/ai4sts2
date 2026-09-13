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

    public void Clear() => _script.Clear();

    public int? Forced { get; set; }

    public int Prompts { get; private set; }

    public int Options { get; private set; }

    public void BeginAction(int? forced)
    {
        Forced = forced;
        Prompts = 0;
        Options = 0;
    }

    public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
    {
        var cards = options as IReadOnlyList<CardModel> ?? options.ToList();
        var count = Math.Min(minSelect > 0 ? minSelect : Math.Min(1, maxSelect), cards.Count);
        int[] chosen;
        if (_script.Count > 0 && _script.Peek().All(i => i >= 0 && i < cards.Count))
        {
            chosen = _script.Dequeue();
        }
        else
        {
            if (_script.Count > 0)
            {
                _script.Dequeue();
            }
            Prompts++;
            if (Prompts == 1 && count == 1 && maxSelect <= 1)
            {
                Options = cards.Count;
            }
            chosen =
                Prompts == 1 && count == 1 && Forced is { } forced && forced < cards.Count
                    ? [forced]
                    : Default(cards, count);
        }
        if (Log.Count >= 200)
        {
            Log.RemoveAt(0);
        }
        Log.Add((cards.Select(c => c.Id.Entry).ToList(), minSelect, maxSelect, chosen));
        IEnumerable<CardModel> result = chosen.Select(i => cards[i]).ToList();
        return Task.FromResult(result);
    }

    private static int[] Default(IReadOnlyList<CardModel> cards, int count)
    {
        if (count == 1 && cards.Count > 1)
        {
            var disintegration = cards.ToList().FindIndex(c => c.Id.Entry == "DISINTEGRATION");
            if (disintegration >= 0)
            {
                return [disintegration == 0 ? 1 : 0];
            }
        }
        return Enumerable.Range(0, count).ToArray();
    }

    public (int? Card, string? Alternative)? CardReward { get; set; }

    public CardRewardSelection GetSelectedCardReward(
        IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives
    )
    {
        if (CardReward is null)
        {
            Entry.Log.Warn($"unscripted card reward skipped: {string.Join(",", options.Select(o => o.Card.Id.Entry))}");
            return default;
        }
        var (card, alternative) = CardReward.Value;
        return card is { } index ? new CardRewardSelection { card = options[index].Card }
            : alternative is { } id
                ? new CardRewardSelection { alternative = alternatives.First(a => a.OptionId == id) }
            : default;
    }
}
