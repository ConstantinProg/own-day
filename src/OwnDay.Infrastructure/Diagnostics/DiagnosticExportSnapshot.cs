using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Diagnostics;

internal sealed partial class DiagnosticExportReader
{
    private async Task WriteSnapshotAsync(Utf8JsonWriter writer, IDbContextTransaction transaction,
        IDictionary<string, Section> sections, UserId owner, IQueryable<LearningRequest> requests,
        Guid? lastCorrelation, CancellationToken token)
    {
        Task Rows<T>(string section, IQueryable<T> rows, Func<T, object> project) =>
            RowsAsync(writer, transaction, sections, section, rows, project, token);

        object? Safe(object? value) => DiagnosticStateChanges.Safe(value, text);

        await Rows("dialogue", db.Set<DialogueContext>().AsNoTracking().Where(row => row.UserId == owner),
            row => new
            {
                row.SchemaVersion, row.Revision, row.Phase, row.ExpectedResponse, row.PromptId, row.TargetLessonId,
                row.PromptOpenedAt, row.LastActivityAt, row.InterruptedAt, row.DeferredGoalRequestId,
                pendingClarification = Safe(row.PendingClarification), pendingConfirmation = Safe(row.PendingConfirmation),
                consultation = Safe(row.Consultation), diagnosticTarget = Safe(row.DiagnosticTarget)
            });

        writer.WriteStartObject("learning");
        await Rows("selection", db.Set<LearningContext>().AsNoTracking().Where(row => row.UserId == owner),
            row => new { row.ProgramId, practice = Safe(row.Practice) });
        await ValueAsync(writer, transaction, sections, "selectedProgress", async () =>
        {
            var program = await curriculum.FindAsync(owner, token);
            if (program is null) return new { selected = false };
            var version = program.CurrentRoute.Version;
            var completed = await curriculum.CompletedTopicsAsync(owner, program.Id, version, token);
            return new
            {
                selected = true, program.Id, routeVersion = version, total = program.CurrentRoute.Topics.Count,
                completedTopics = program.CurrentRoute.Topics.Where(topic => completed.Contains(topic.Code)).Select(topic => text.Filter(topic.Code)).ToArray()
            };
        }, token);
        var programs = db.Set<LearningProgram>().AsNoTracking().Where(row => row.UserId == owner);
        await Rows("programs", programs.OrderBy(row => row.Id), row => new
        {
            row.Id, goal = text.Filter(row.Goal), goalCriterion = text.Filter(row.GoalCriterion),
            selfReportedExperience = text.Filter(row.Experience), discipline = text.Filter(row.Discipline),
            row.ProjectId, row.Priority, row.DiagnosticPending,
            diagnosticQuestion = text.Filter(row.DiagnosticQuestion), diagnosticAnswer = text.Filter(row.DiagnosticAnswer),
            englishDiagnostic = Safe(row.EnglishDiagnostic), subjectDiagnostic = Safe(row.SubjectDiagnostic)
        });
        await Rows("routes", db.Set<CurriculumRoute>().AsNoTracking()
            .Where(row => programs.Any(program => program.Id == row.ProgramId)).OrderBy(row => row.ProgramId).ThenBy(row => row.Version),
            row => new
            {
                row.Id, row.ProgramId, row.Version, row.ProgressVersion, row.ProgressRootVersion,
                reason = text.Filter(row.Reason), topics = Safe(row.Topics), path = Safe(row.Path)
            });
        var lessons = db.Set<Lesson>().AsNoTracking().Where(row => row.UserId == owner);
        await Rows("lessons", lessons.OrderBy(row => row.Id), row => new
        {
            row.Id, row.Status, row.SuspendedFrom, row.SuspendedForProgramId, row.ProgramId, row.PracticeProgramId,
            row.RouteVersion, topicCode = text.Filter(row.TopicCode), row.SkillId, row.ProjectActionId, row.IsReview,
            topic = text.Filter(row.Topic), theory = text.Filter(row.Theory), question = text.Filter(row.Question),
            answer = text.Filter(row.Answer), feedback = text.Filter(row.Feedback), expectedAnswer = text.Filter(row.ExpectedAnswer),
            row.ReferenceKind, row.AssessmentStatus, row.IsCorrect, row.AiIsCorrect, row.DeterministicIsCorrect,
            row.Assistance, row.RecognitionOnly, row.TheorySkipped, row.ExercisePending, exercise = Safe(row.Choice),
            rubricEvaluation = Safe(row.RubricEvaluation), content = Safe(row.Content), provenance = Safe(row.Provenance)
        });
        await Rows("interactions", db.Set<LessonInteraction>().AsNoTracking()
            .Where(row => lessons.Any(lesson => lesson.Id == row.LessonId)).OrderBy(row => row.Id),
            row => new { row.Id, row.LessonId, row.Kind, request = text.Filter(row.Request),
                explanation = text.Filter(row.Explanation), provenance = Safe(row.Provenance) });
        await Rows("sessions", db.Set<LearningSession>().AsNoTracking().Where(row => row.UserId == owner).OrderBy(row => row.Id),
            row => new { row.Id, row.ProgramId, row.StartedAt, row.LastActivityAt, row.EndedAt, row.EndReason, row.ActiveSeconds, summary = Safe(row.Summary) });
        await Rows("studySchedules", db.Set<StudySchedule>().AsNoTracking().Where(row => row.UserId == owner),
            row => new { timeZone = text.Filter(row.TimeZoneId), row.EveryDays, row.Windows, row.Paused, row.NextAt,
                row.LastInvitedAt, row.InvitationId, row.ProgramId, row.SkillId, row.EffectiveReminderTypes, row.LessonId });
        writer.WriteEndObject();

        writer.WriteStartObject("knowledge");
        await Rows("skills", db.Set<Skill>().AsNoTracking().Where(row => row.UserId == owner).OrderBy(row => row.Id),
            row => new { row.Id, title = text.Filter(row.Title), description = text.Filter(row.Description) });
        await Rows("evidence", db.Set<LearningEvidence>().AsNoTracking().Where(row => row.UserId == owner)
            .OrderBy(row => row.ObservedAt).ThenBy(row => row.LessonId), row => new
            {
                row.LessonId, row.SkillId, row.ObservedAt, answer = text.Filter(row.Answer),
                assessmentFeedback = text.Filter(row.AssessmentFeedback), row.AssessedCorrect, row.AiAssessedCorrect,
                row.DeterministicIsCorrect, row.AssessmentStatus, row.EffectiveAssessmentStatus, row.Assistance, row.IsReview,
                row.ReassessmentCredit,
                assessmentSource = text.Filter(row.AssessmentSource), method = text.Filter(row.Method),
                rubricEvidence = Safe(row.RubricEvidence), choiceEvidence = Safe(row.ChoiceEvidence), reviews = Safe(row.ReviewHistory)
            });
        await Rows("programSkillLinks", db.Set<ProgramSkillLink>().AsNoTracking().Where(row => row.UserId == owner)
            .OrderBy(row => row.ProgramId).ThenBy(row => row.ProgressVersion).ThenBy(row => row.TopicCode),
            row => new { row.ProgramId, row.ProgressVersion, topicCode = text.Filter(row.TopicCode), row.SkillId,
                source = text.Filter(row.Source), trust = text.Filter(row.Trust), reason = text.Filter(row.Reason) });
        await Rows("preferences", db.Set<LearningPreference>().AsNoTracking().Where(row => row.UserId == owner)
            .OrderBy(row => row.Kind).ThenBy(row => row.Scope),
            row => new { row.Kind, scope = text.Filter(row.Scope), value = text.Filter(row.Value), source = text.Filter(row.Source), row.ResetThrough });
        await Rows("reviewSchedules", db.Set<ReviewSchedule>().AsNoTracking().Where(row => row.UserId == owner)
            .OrderBy(row => row.DueAt).ThenBy(row => row.SkillId),
            row => new { row.SkillId, row.DueAt, row.LastObservedAt, row.LastLessonId, row.SuccessfulReviews });
        writer.WriteEndObject();

        await Rows("requests", requests.OrderBy(row => row.ReceivedAt).ThenBy(row => row.Sequence),
            row => new { row.Id, row.Sequence, row.SourceUpdateId, row.SourceMessageId, row.PayloadKind, row.PayloadVersion,
                row.Status, row.Outcome, row.ReceivedAt, row.AvailableAt, row.CompletedAt, row.AttemptCount, row.LeaseUntil });
        await Rows("providerSteps", db.Set<LearningProviderStep>().AsNoTracking()
            .Where(row => requests.Any(request => request.Id == row.RequestId)).OrderBy(row => row.RequestId).ThenBy(row => row.Ordinal),
            row => new { row.RequestId, row.Ordinal, row.Kind, row.Version, row.Failure, hasResult = row.Result != null });
        await Rows("providerAttempts", db.Set<LearningProviderAttempt>().AsNoTracking()
            .Where(row => requests.Any(request => request.Id == row.RequestId))
            .OrderBy(row => row.RequestId).ThenBy(row => row.Ordinal).ThenBy(row => row.Number),
            row => new { row.Id, row.RequestId, row.Ordinal, row.Number, row.StartedAt, row.FinishedAt, row.Outcome });
        await Rows("requestOperations", db.Set<LearningAppliedOperation>().AsNoTracking()
            .Where(row => requests.Any(request => request.Id == row.RequestId)).OrderBy(row => row.RequestId).ThenBy(row => row.Step),
            row => new { row.RequestId, row.Step, row.AppliedAt });
        await Rows("telegramOutbox", db.TelegramOutboxMessages.AsNoTracking()
            .Where(row => row.DocumentName == null && (row.LearningRequestId.HasValue
                ? requests.Any(request => request.Id == row.LearningRequestId.Value) :
                    row.ChatId == owner.Value && (lastCorrelation == null || row.DiagnosticCorrelationId == lastCorrelation)))
            .OrderBy(row => row.DeliveryOrder), row => new
            {
                row.Id, row.DiagnosticCorrelationId, row.LearningRequestId, row.ReplyOrdinal, row.Status, row.EditMessageId,
                row.AttemptCount, row.CreatedAt, row.SentAt, row.NextAttemptAt, row.LastError
            });
    }
}
