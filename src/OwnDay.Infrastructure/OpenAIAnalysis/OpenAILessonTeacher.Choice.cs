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
              "Review":{"type":"object","properties":{
                "SelfContained":{"type":"boolean"},"Unambiguous":{"type":"boolean"},
                "DerivedOptions":{"type":"array","items":{"type":"integer"}}
              },"required":["SelfContained","Unambiguous","DerivedOptions"],"additionalProperties":false}
            },"required":["Format","Question","Options","CorrectOptions","Explanation","Review"],"additionalProperties":false}
            """);
        schema["required"]!.AsArray().Add("Choice");
        return schema.ToJsonString();
    }

    private static string ChoiceInstructions(ChoiceFormat format) =>
        $" Also propose Choice in the exact requested format {format} when a short recognition check helps the current goal and level, or null. Keep the top-level Theory/Criteria as a separate usable open fallback. " +
        "Publishable choice questions use a self-contained hypothetical or established principle and never depend on recent, disputed, or sensitive facts. " +
        "Choice.Question is up to 1000 characters and contains all data needed to answer. Avoid giving away the key in the question or option wording. " +
        "Write Question, option descriptions and Explanation in the learner's language. Give 2–6 distinct options (each one line up to 160 characters), with numeric IDs from 1 to 6. " +
        "SingleChoice has exactly one correct option. MultipleChoice has at least two correct options and one distractor. TrueFalse has two options with literal labels True and False in that order and exactly one correct option. " +
        "After drafting the key, solve the question again from its wording and give the resulting IDs in Review.DerivedOptions. Recheck whether every distractor is clearly wrong under all reasonable interpretations. " +
        "Set Review.Unambiguous or Review.SelfContained to false if the question cannot meet that rule, if anything relies on a sensitive, current or disputed assertion, or if it needs an external source. " +
        "Explanation up to 1500 characters justifies the entire answer set, including why plausible distractors fail. Avoid repeating a previous question. ";

    private static (ChoiceExercise Exercise, string Context)? TryChoice(GeneratedLesson lesson, ChoiceFormat format, LessonRequest request)
    {
        if (lesson.NeedsCurrentVerification || lesson.Choice is not { Review: { SelfContained: true, Unambiguous: true } review } candidate ||
            candidate.Format != format.ToString())
            return null;

        var russian = MarketLearningContent.InRussian(candidate.Question + " " + (request.Program?.Goal ?? request.Topic));
        string[] hints = [russian
            ? "Сопоставьте каждый вариант с условиями вопроса. Проверьте, какие выводы из них следуют."
            : "Compare each option against the conditions in the question. Check what follows from those conditions."];
        var exercise = new ChoiceExercise(ChoiceExercise.CurrentVersion, format, candidate.Question,
            candidate.Options, candidate.CorrectOptions, candidate.Explanation, [.. hints]);
        try { exercise.Validate(); }
        catch (ArgumentException) { return null; }
        if (!exercise.IsValidSelection(review.DerivedOptions) || exercise.Grade(review.DerivedOptions) != ChoiceOutcome.Correct)
            return null;
        return (exercise, russian ? "Короткая проверка по теме." : "A short check on this topic.");
    }

    private sealed record GeneratedChoice(string Format, string Question, ImmutableArray<ChoiceOption> Options,
        ImmutableArray<int> CorrectOptions, string Explanation, GeneratedChoiceReview Review);

    private sealed record GeneratedChoiceReview(bool SelfContained, bool Unambiguous, ImmutableArray<int> DerivedOptions);
}
