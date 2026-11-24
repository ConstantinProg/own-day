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
        [typeof(TelegramUserLanguage)] = ("settings", [nameof(TelegramUserLanguage.Locale)]),
        [typeof(DialogueContext)] = ("dialogue", [nameof(DialogueContext.Phase), nameof(DialogueContext.Revision),
            nameof(DialogueContext.ExpectedResponse), nameof(DialogueContext.PromptId),
            nameof(DialogueContext.TargetLessonId),
            nameof(DialogueContext.DeferredGoalRequestId), nameof(DialogueContext.PendingClarification),
            nameof(DialogueContext.PendingConfirmation), nameof(DialogueContext.Consultation),
            nameof(DialogueContext.DiagnosticTarget)]),
        [typeof(LearningProgram)] = ("learning", [nameof(LearningProgram.Goal), nameof(LearningProgram.GoalCriterion),
            nameof(LearningProgram.Experience), nameof(LearningProgram.Discipline),
            nameof(LearningProgram.DiagnosticPending),
            nameof(LearningProgram.DiagnosticQuestion), nameof(LearningProgram.DiagnosticAnswer),
            nameof(LearningProgram.EnglishDiagnostic), nameof(LearningProgram.SubjectDiagnostic),
            nameof(LearningProgram.Priority)]),
        [typeof(CurriculumRoute)] = ("learning", [nameof(CurriculumRoute.ProgramId), nameof(CurriculumRoute.Version),
            nameof(CurriculumRoute.ProgressVersion), nameof(CurriculumRoute.Reason), nameof(CurriculumRoute.Topics),
            nameof(CurriculumRoute.Path)]),
        [typeof(LearningContext)] = ("learning", [nameof(LearningContext.ProgramId), nameof(LearningContext.Practice)]),
        [typeof(Lesson)] = ("learning", [nameof(Lesson.Status), nameof(Lesson.Topic), nameof(Lesson.Theory),
            nameof(Lesson.Question), nameof(Lesson.Answer), nameof(Lesson.Feedback), nameof(Lesson.ProgramId),
            nameof(Lesson.SkillId),
            nameof(Lesson.RouteVersion), nameof(Lesson.TopicCode), nameof(Lesson.AssessmentStatus),
            nameof(Lesson.IsCorrect), nameof(Lesson.AiIsCorrect), nameof(Lesson.DeterministicIsCorrect),
            nameof(Lesson.Assistance),
            nameof(Lesson.Choice), nameof(Lesson.RubricEvaluation), nameof(Lesson.ExercisePending),
            nameof(Lesson.TheorySkipped)]),
        [typeof(LessonInteraction)] = ("learning", [nameof(LessonInteraction.LessonId), nameof(LessonInteraction.Kind),
            nameof(LessonInteraction.Request), nameof(LessonInteraction.Explanation)]),
        [typeof(LearningSession)] = ("learning", [nameof(LearningSession.ProgramId), nameof(LearningSession.StartedAt),
            nameof(LearningSession.LastActivityAt), nameof(LearningSession.EndedAt), nameof(LearningSession.EndReason),
            nameof(LearningSession.ActiveSeconds), nameof(LearningSession.Summary)]),
        [typeof(StudySchedule)] = ("learning", [nameof(StudySchedule.ProgramId), nameof(StudySchedule.SkillId),
            nameof(StudySchedule.LessonId), nameof(StudySchedule.Paused), nameof(StudySchedule.NextAt),
            nameof(StudySchedule.ReminderTypes), nameof(StudySchedule.InvitationId)]),
        [typeof(LearningEvidence)] = ("knowledge", [nameof(LearningEvidence.SkillId),
            nameof(LearningEvidence.ObservedAt), nameof(LearningEvidence.AssessedCorrect),
            nameof(LearningEvidence.AiAssessedCorrect),
            nameof(LearningEvidence.DeterministicIsCorrect), nameof(LearningEvidence.AssessmentStatus),
            nameof(LearningEvidence.AssessmentSource), nameof(LearningEvidence.Method), nameof(LearningEvidence.Answer),
            nameof(LearningEvidence.AssessmentFeedback),
            nameof(LearningEvidence.Assistance), nameof(LearningEvidence.RubricEvidence),
            nameof(LearningEvidence.ChoiceEvidence), nameof(LearningEvidence.ReviewHistory)]),
        [typeof(Skill)] = ("knowledge", [nameof(Skill.Title), nameof(Skill.Description)]),
        [typeof(ProgramSkillLink)] = ("knowledge", [nameof(ProgramSkillLink.ProgramId),
            nameof(ProgramSkillLink.ProgressVersion), nameof(ProgramSkillLink.TopicCode),
            nameof(ProgramSkillLink.SkillId), nameof(ProgramSkillLink.Source), nameof(ProgramSkillLink.Trust),
            nameof(ProgramSkillLink.Reason)]),
        [typeof(LearningPreference)] = ("knowledge", [nameof(LearningPreference.Kind), nameof(LearningPreference.Scope),
            nameof(LearningPreference.Value), nameof(LearningPreference.Source),
            nameof(LearningPreference.ResetThrough)]),
        [typeof(ReviewSchedule)] = ("knowledge", [nameof(ReviewSchedule.SkillId), nameof(ReviewSchedule.DueAt),
            nameof(ReviewSchedule.LastObservedAt), nameof(ReviewSchedule.LastLessonId),
            nameof(ReviewSchedule.SuccessfulReviews)])
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
