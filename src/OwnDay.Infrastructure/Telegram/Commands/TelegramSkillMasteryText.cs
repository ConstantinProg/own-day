using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Telegram.Commands;

internal static class TelegramSkillMasteryText
{
    public static string Explain(SkillMastery mastery) => mastery switch
    {
        SkillMastery.Unknown => "Unknown: no assessed answers.",
        SkillMastery.NeedsPractice => "Needs practice: only negative AI assessments so far.",
        SkillMastery.Developing => "Developing: one or two positive AI assessments; evidence is limited.",
        SkillMastery.Conflicting => "Conflicting: mixed AI results or disagreement with a deterministic check; further verification is needed.",
        SkillMastery.Demonstrated => "Demonstrated: sufficient independent evidence without unresolved conflicts. Structured assessments cover understanding, application and delayed retrieval.",
        _ => throw new ArgumentOutOfRangeException(nameof(mastery))
    };
}
