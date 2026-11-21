using System.Collections.Immutable;
using System.Text.Json.Nodes;
using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.OpenAIAnalysis;

public sealed partial class OpenAILessonTeacher
{
    private const string OpenExerciseSchema =
        """{"type":"object","properties":{"Theory":{"type":"string"},"EvidenceKind":{"type":"string","enum":["Recognition","Understanding","Application","Retrieval"]},"SourceQuote":{"type":["string","null"]},"NeedsCurrentVerification":{"type":"boolean"},"Criteria":{"type":"array","items":{"type":"object","properties":{"Code":{"type":"string"},"Requirement":{"type":"string"}},"required":["Code","Requirement"],"additionalProperties":false}}},"required":["Theory","EvidenceKind","SourceQuote","Criteria","NeedsCurrentVerification"],"additionalProperties":false}""";

    private static readonly string ChoiceExerciseSchema = BuildChoiceExerciseSchema();

    private static string BuildChoiceExerciseSchema()
    {
        var schema = JsonNode.Parse(OpenExerciseSchema)!;
        schema["properties"]!["Choice"] = JsonNode.Parse("""
            {"type":["object","null"],"properties":{
              "Format":{"type":"string","enum":["SingleChoice","MultipleChoice","TrueFalse"]},
              "Question":{"type":"string"},
              "Options":{"type":"array","items":{"type":"object","properties":{"Id":{"type":"integer"},"Text":{"type":"string"}},"required":["Id","Text"],"additionalProperties":false}},
              "CorrectOptions":{"type":"array","items":{"type":"integer"}},"Explanation":{"type":"string"},
              "Guidance":{"type":"array","items":{"type":"object","properties":{
                "OptionId":{"type":"integer"},"Hint":{"type":"string"},"Example":{"type":"string"}
              },"required":["OptionId","Hint","Example"],"additionalProperties":false}},
              "Review":{"type":"object","properties":{
                "SelfContained":{"type":"boolean"},"Unambiguous":{"type":"boolean"},
                "DerivedOptions":{"type":"array","items":{"type":"integer"}}
              },"required":["SelfContained","Unambiguous","DerivedOptions"],"additionalProperties":false}
            },"required":["Format","Question","Options","CorrectOptions","Explanation","Guidance","Review"],"additionalProperties":false}
            """);
        schema["required"]!.AsArray().Add("Choice");
        return schema.ToJsonString();
    }

    private static string ChoiceInstructions(ChoiceFormat format) =>
        $" Also propose Choice in the exact requested format {format} when a short recognition check helps the current goal and level, or null. Choice and the open fallback in Criteria must both check the same idea taught in Theory. " +
        "Publishable choice questions use a self-contained hypothetical or established principle and never depend on recent, disputed, or sensitive facts. " +
        "Prefer a small scenario that asks for the best action or to spot an error when that tests the idea better than recalling a definition. Choice.Question is up to 1000 characters and contains all data needed to answer. Ask only for the selection: if a justification is essential use the open fallback. Avoid giving away the key in the question or option wording. " +
        "Write Question, option descriptions and Explanation in the learner's language. Give 2–6 distinct options (each one line up to 160 characters), with numeric IDs from 1 to 6. " +
        "SingleChoice has exactly one correct option. MultipleChoice has at least two correct options and one distractor. TrueFalse has two options with literal labels True and False in that order and exactly one correct option. " +
        "After drafting the key, solve the question again from its wording and give the resulting IDs in Review.DerivedOptions. Recheck whether every distractor is clearly wrong under all reasonable interpretations. " +
        "Set Review.Unambiguous or Review.SelfContained to false if the question cannot meet that rule, if anything relies on a sensitive, current or disputed assertion, or if it needs an external source. " +
        "Explanation up to 1500 characters justifies the entire answer set, including why plausible distractors fail. " +
        "For each distractor provide a Guidance entry with its OptionId, a short Hint (up to 400 characters) and a different, simpler Example (up to 600 characters) for a second attempt. " +
        "A hint should point to the distinction the option tests and ask the learner to apply it. If two distractors test different distinctions, their hints and examples must reflect the difference. The example should let the learner work out the distinction in a parallel situation. " +
        "Use the learner's language. Treat the selection as the only observation: never attribute an unstated belief or reasoning to the learner. Do not name, quote or paraphrase the correct option, give the answer set, or eliminate individual options in a multiple-choice question. " +
        "For TrueFalse return an empty Guidance array. Avoid repeating a previous question. ";

    private static ChoiceExercise? TryChoice(GeneratedLesson lesson, ChoiceFormat format)
    {
        if (lesson.NeedsCurrentVerification || lesson.Choice is not { Review: { SelfContained: true, Unambiguous: true } review } candidate ||
            candidate.Format != format.ToString())
            return null;

        var exercise = new ChoiceExercise(ChoiceExercise.CurrentVersion, format, candidate.Question,
            candidate.Options, candidate.CorrectOptions, candidate.Explanation, []) { Guidance = candidate.Guidance };
        try { exercise.Validate(); }
        catch (ArgumentException) { return null; }
        if (format != ChoiceFormat.TrueFalse && (exercise.Guidance.IsDefault ||
            exercise.Guidance.Length != exercise.Options.Length - exercise.CorrectOptions.Length))
            return null;
        if (!exercise.Guidance.IsDefaultOrEmpty && exercise.Options.Any(option => option.Text.Length >= 12 &&
            exercise.CorrectOptions.Contains(option.Id) && exercise.Guidance.Any(guidance =>
                guidance.Hint.Contains(option.Text, StringComparison.OrdinalIgnoreCase) ||
                guidance.Example.Contains(option.Text, StringComparison.OrdinalIgnoreCase))))
            return null;
        if (!exercise.IsValidSelection(review.DerivedOptions) || exercise.Grade(review.DerivedOptions) != ChoiceOutcome.Correct)
            return null;
        return exercise;
    }

    private sealed record GeneratedChoice(string Format, string Question, ImmutableArray<ChoiceOption> Options,
        ImmutableArray<int> CorrectOptions, string Explanation, GeneratedChoiceReview Review,
        ImmutableArray<ChoiceGuidance> Guidance);

    private sealed record GeneratedChoiceReview(bool SelfContained, bool Unambiguous, ImmutableArray<int> DerivedOptions);
}
