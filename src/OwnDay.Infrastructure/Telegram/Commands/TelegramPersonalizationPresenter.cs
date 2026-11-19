using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Telegram.Handling;

namespace OwnDay.Infrastructure.Telegram.Commands;

public static class TelegramPersonalizationPresenter
{
    public static IReadOnlyList<string> Usage =>
        [$"/learn_preferences\n/learn_preference [subject] <format|depth|pace|examples> | <value, 1–{LearningPreference.MaxValueLength} characters>\n" +
         "/learn_preference_reset [subject] <format|depth|pace|examples|all>\nUse subject for the selected discipline."];

    public static IReadOnlyList<string> Render(LearningPersonalization profile)
    {
        var values = profile.EffectivePreferences();
        var entries = new List<string>
        {
            $"Format: {values.Format ?? "unspecified"}\nDepth: {values.Depth ?? "unspecified"}\n" +
            $"Pace: {values.Pace ?? "unspecified"}\nExamples: {values.Examples ?? "unspecified"}\n" +
            "A request in the current turn takes priority. Saved preferences apply to lessons and explanations.",
            "Strategy observations: up to 100 recent assessed lessons with a saved teaching request. " +
            "Results are provisional AI assessments, not proof of effectiveness or a stable learner trait. " +
            "Fewer than 3 answers never support a strategy suggestion; conflicts require further checks."
        };
        entries.AddRange(profile.Sources.Select(item => $"{item.Kind} · {item.Value}\nScope — {(item.Scope.Length == 0 ? "all disciplines" : item.Scope)}\nSource — {item.Source}"));
        entries.AddRange(profile.StrategyEvidence.Select(s => $"{s.Strategy}: {s.Answers} answers; {s.Positive} positive, {s.Negative} negative, {s.Conflicts} conflicts. " +
            (s.HasRepeatedEvidence ? "Repeated observations; association only, no causal conclusion." : "Insufficient evidence for a suggestion.")));
        entries.AddRange(Usage);
        return TelegramTextLists.Split(entries, "Learning preferences and provisional strategy observations");
    }
}
