using OwnDay.Application.Inbox.Analysis;

namespace OwnDay.InboxEvaluation;

public sealed record AutomaticChecks(bool Structural, bool Classification, bool Splitting, bool Properties,
    bool Links, bool Ambiguities, bool ContentAnchors, bool Joint)
{
    public static AutomaticChecks Invalid { get; } = new(false, false, false, false, false, false, false, false);
}

public static class GoldenScorer
{
    public const string Version = "inbox-scorer-v1";

    public static AutomaticChecks Score(GoldenScenario scenario, InboxAnalysisOutput? output)
    {
        if (output is null)
        {
            return AutomaticChecks.Invalid;
        }

        var items = output.Items;
        bool Any(Func<ExpectedItem, InboxProposal, bool> predicate) =>
            scenario.Variants.Any(v => Matches(v.Items, items, predicate));

        return new(true, Any(Kind), scenario.Variants.Any(v => v.Items.Length == items.Length),
            Any((e, a) => Kind(e, a) && Properties(e, a)), Any((e, a) => Kind(e, a) && Links(e, a)),
            Any((e, a) => Kind(e, a) && Ambiguities(e, a)), Any((e, a) => Kind(e, a) && Anchors(e, a)),
            Any((e, a) => Kind(e, a) && Properties(e, a) && Links(e, a) && Ambiguities(e, a) && Anchors(e, a)));
    }

    private static bool Kind(ExpectedItem expected, InboxProposal actual) => expected.Kinds.Contains(actual.Kind);
    private static bool Properties(ExpectedItem expected, InboxProposal actual) => expected.PropertySets.Contains(actual.Properties);
    private static bool Links(ExpectedItem expected, InboxProposal actual) =>
        expected.ProjectIdSets.Any(ids => ids.Order().SequenceEqual(actual.ProjectIds.Order()));
    private static bool Ambiguities(ExpectedItem expected, InboxProposal actual) =>
        !expected.RequireAmbiguity || actual.Ambiguities.Length > 0;
    private static bool Anchors(ExpectedItem expected, InboxProposal actual) =>
        expected.ContentAnchorGroups.All(group => group.Any(anchor =>
            (actual.Title + "\n" + actual.Description).Contains(anchor, StringComparison.OrdinalIgnoreCase)));

    // Bipartite matching avoids order-sensitive grading, factorial searches and reusing an item twice.
    private static bool Matches(ExpectedItem[] expected, InboxProposal[] actual,
        Func<ExpectedItem, InboxProposal, bool> predicate)
    {
        if (expected.Length != actual.Length)
        {
            return false;
        }

        var owners = Enumerable.Repeat(-1, actual.Length).ToArray();
        bool Augment(int index, bool[] visited)
        {
            for (var candidate = 0; candidate < actual.Length; candidate++)
            {
                if (visited[candidate] || !predicate(expected[index], actual[candidate]))
                {
                    continue;
                }

                visited[candidate] = true;
                if (owners[candidate] < 0 || Augment(owners[candidate], visited))
                {
                    owners[candidate] = index;
                    return true;
                }
            }

            return false;
        }

        return Enumerable.Range(0, expected.Length).All(index => Augment(index, new bool[actual.Length]));
    }
}
