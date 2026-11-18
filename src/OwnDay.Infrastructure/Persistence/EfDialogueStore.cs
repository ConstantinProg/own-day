using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Dialogue;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfDialogueStore(OwnDayDbContext db) : IDialogueStore
{
    public Task<DialogueContext?> FindAsync(UserId user, CancellationToken token)
    {
        var local = db.Set<DialogueContext>().Local.SingleOrDefault(context => context.UserId == user);
        return local is null ? db.Set<DialogueContext>().SingleOrDefaultAsync(context => context.UserId == user, token)
            : Task.FromResult<DialogueContext?>(local);
    }
    public void Add(DialogueContext context) => db.Add(context);
    public async Task<DialogueProgramSnapshot?> ReadProgramAsync(UserId user, CancellationToken token)
    {
        var selected = await (from context in db.Set<LearningContext>() join program in db.Set<LearningProgram>() on context.ProgramId equals program.Id
         where context.UserId == user && program.UserId == user
         select new { Snapshot = new DialogueProgramSnapshot(program.Id, program.Goal, program.Discipline, program.Routes.Max(route => route.Version), EF.Property<uint>(program, "TargetVersion"), program.EnglishDiagnostic != null,
             null, null, null, program.DiagnosticPending ? program.DiagnosticQuestion : null), context.Practice })
        .SingleOrDefaultAsync(token);
        return selected is null ? null : selected.Snapshot with { PracticeStatus = selected.Practice?.Status };
    }
    public async Task<DialogueProgramSnapshot?> ReadProgramByIdAsync(UserId user, Guid programId, CancellationToken token)
    {
        var program = await db.Set<LearningProgram>().Where(program => program.UserId == user && program.Id == programId)
            .Select(program => new DialogueProgramSnapshot(program.Id, program.Goal, program.Discipline, program.Routes.Max(route => route.Version), EF.Property<uint>(program, "TargetVersion"), program.EnglishDiagnostic != null))
            .SingleOrDefaultAsync(token);
        if (program is null) return null;
        var suspended = await db.Set<Lesson>().Where(lesson => lesson.UserId == user && lesson.SuspendedForProgramId == programId && lesson.Status == LessonStatus.Suspended)
            .Select(lesson => new { lesson.Id, Version = EF.Property<uint>(lesson, "TargetVersion") }).SingleOrDefaultAsync(token);
        return program with { SuspendedLessonId = suspended?.Id, SuspendedLessonVersion = suspended?.Version };
    }
    public async Task<DialogueOriginalInput?> ReadOriginalAsync(UserId user, Guid requestId, CancellationToken token)
    {
        var json = await db.Set<LearningRequest>().Where(request => request.Id == requestId && request.UserId == user &&
            request.PayloadKind == "learning_text" && request.PayloadVersion == 1).Select(request => request.NormalizedInput).SingleOrDefaultAsync(token);
        if (json is null) return null;
        try
        {
            var input = JsonSerializer.Deserialize<LearningTextInput>(json, JsonSerializerOptions.Web);
            return input is null ? null : new(input.Text, input.LessonId, input.PromptId);
        }
        catch (JsonException) { return null; }
    }
}
