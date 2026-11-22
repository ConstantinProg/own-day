using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Persistence;

namespace OwnDay.Infrastructure.Diagnostics;

internal sealed record DiagnosticStateChange(string Category, string Entity, Guid? Id, string Operation,
    IReadOnlyDictionary<string, object?> Key, IReadOnlyList<DiagnosticFieldChange> Changes);

internal sealed record DiagnosticFieldChange(string Field, object? Previous, object? CurrentValue);

internal static class DiagnosticStateChanges
{
    private static readonly Dictionary<Type, (string Category, string[] Fields)> Tracked = new()
    {
        [typeof(DialogueContext)] = ("dialogue", ["Phase", "Revision", "ExpectedResponse", "PromptId", "TargetLessonId",
            "DeferredGoalRequestId", "PendingClarification", "PendingConfirmation", "Consultation", "DiagnosticTarget"]),
        [typeof(LearningProgram)] = ("learning", ["Goal", "GoalCriterion", "Experience", "Discipline", "DiagnosticPending",
            "DiagnosticQuestion", "DiagnosticAnswer", "EnglishDiagnostic", "SubjectDiagnostic", "Priority"]),
        [typeof(CurriculumRoute)] = ("learning", ["ProgramId", "Version", "ProgressVersion", "Reason", "Topics", "Path"]),
        [typeof(LearningContext)] = ("learning", ["ProgramId", "Practice"]),
        [typeof(Lesson)] = ("learning", ["Status", "Topic", "Theory", "Question", "Answer", "Feedback", "ProgramId", "SkillId",
            "RouteVersion", "TopicCode", "AssessmentStatus", "IsCorrect", "AiIsCorrect", "DeterministicIsCorrect", "Assistance",
            "Choice", "RubricEvaluation", "ExercisePending", "TheorySkipped"]),
        [typeof(LessonInteraction)] = ("learning", ["LessonId", "Kind", "Request", "Explanation"]),
        [typeof(LearningSession)] = ("learning", ["ProgramId", "StartedAt", "LastActivityAt", "EndedAt", "EndReason", "ActiveSeconds", "Summary"]),
        [typeof(StudySchedule)] = ("learning", ["ProgramId", "SkillId", "LessonId", "Paused", "NextAt", "ReminderTypes", "InvitationId"]),
        [typeof(LearningEvidence)] = ("knowledge", ["SkillId", "ObservedAt", "AssessedCorrect", "AiAssessedCorrect",
            "DeterministicIsCorrect", "AssessmentStatus", "AssessmentSource", "Method", "Answer", "AssessmentFeedback",
            "Assistance", "RubricEvidence", "ChoiceEvidence", "ReviewHistory"]),
        [typeof(Skill)] = ("knowledge", ["Title", "Description"]),
        [typeof(ProgramSkillLink)] = ("knowledge", ["ProgramId", "ProgressVersion", "TopicCode", "SkillId", "Source", "Trust", "Reason"]),
        [typeof(LearningPreference)] = ("knowledge", ["Kind", "Scope", "Value", "Source", "ResetThrough"]),
        [typeof(ReviewSchedule)] = ("knowledge", ["SkillId", "DueAt", "LastObservedAt", "LastLessonId", "SuccessfulReviews"])
    };

    internal static DiagnosticStateChange[] Capture(OwnDayDbContext db, DiagnosticText text)
    {
        var result = new List<DiagnosticStateChange>();
        foreach (var entry in db.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted) ||
                !Tracked.TryGetValue(entry.Metadata.ClrType, out var allowed)) continue;
            var keys = entry.Metadata.FindPrimaryKey()?.Properties.ToDictionary(property => property.Name,
                property => Safe(entry.Property(property.Name).CurrentValue, text), StringComparer.Ordinal) ?? [];
            var id = keys.Values.OfType<Guid>().Select(value => (Guid?)value).FirstOrDefault();
            var changes = new List<DiagnosticFieldChange>();
            foreach (var property in entry.Properties)
            {
                if (!allowed.Fields.Contains(property.Metadata.Name, StringComparer.Ordinal) ||
                    entry.State == EntityState.Modified && !property.IsModified) continue;
                var previous = entry.State == EntityState.Added ? null : Safe(property.OriginalValue, text);
                var current = entry.State == EntityState.Deleted ? null : Safe(property.CurrentValue, text);
                if (previous is null && current is null) continue;
                changes.Add(new(property.Metadata.Name, previous, current));
            }
            if (changes.Count > 0)
                result.Add(new(allowed.Category, entry.Metadata.ClrType.Name, id, entry.State.ToString(), keys, changes));
        }
        return [.. result];
    }

    internal static object? Safe(object? value, DiagnosticText text) => value switch
    {
        string content => text.Filter(content),
        Guid or int or long or bool or DateTime or DateTimeOffset or Enum => value,
        OwnDay.Domain.UserId owner => owner.Value,
        DialogueClarification or DialogueConfirmation or ConsultationDraft or DiagnosticPromptTarget or
        LearningPractice or EnglishDiagnostic or SubjectDiagnostic or CurriculumPath or IReadOnlyList<CurriculumTopic> or
        ChoiceExerciseState or LessonRubricEvaluation or RubricEvidence or ChoiceEvidence or AssessmentReviewHistory or LearningSessionSummary or
        LearningContent or LessonProvenance =>
            text.Json(JsonSerializer.Serialize(value, JsonSerializerOptions.Web)),
        _ => null
    };
}
