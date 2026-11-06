using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Application.Learning;

public sealed partial class CurriculumService
{
    public async Task<CurriculumResult> TransferPracticeAsync(UserId user, string code, Guid sourceId, string sourceCode,
        CancellationToken token)
    {
        ValidateUser(user, token);
        var program = await store.FindAsync(user, token);
        if (program is null) return new CurriculumResult.NoProgram();
        var source = await store.FindAsync(user, sourceId, token);
        if (source is null) return new CurriculumResult.ProgramNotFound();
        if (source.Id == program.Id || program.DiagnosticPending || source.DiagnosticPending ||
            string.Equals(source.Discipline, program.Discipline, StringComparison.OrdinalIgnoreCase))
            return new CurriculumResult.TransferUnavailable();
        var topic = program.CurrentRoute.Topics.SingleOrDefault(t => t.Code == code);
        var sourceTopic = source.CurrentRoute.Topics.SingleOrDefault(t => t.Code == sourceCode);
        var completed = await store.CompletedTopicsAsync(user, program.Id, program.CurrentRoute.Version, token);
        var sourceCompleted = await store.CompletedTopicsAsync(user, source.Id, source.CurrentRoute.Version, token);
        if (topic is null || sourceTopic is null || !topic.Prerequisites.All(completed.Contains) ||
            !sourceTopic.Prerequisites.All(sourceCompleted.Contains)) return new CurriculumResult.TransferUnavailable();
        var link = (await knowledge.LinksAsync(program, token)).SingleOrDefault(l => l.TopicCode == code);
        var sourceStatus = await knowledge.TopicStatusAsync(user, source.Id, source.CurrentRoute.Version, sourceCode, token);
        if (link is null || sourceStatus is null || sourceStatus.Skill.Id != link.SkillId)
            return new CurriculumResult.TransferUnavailable();
        if (await lessons.FindActiveAsync(user, token) is not null) return new CurriculumResult.LessonInProgress();
        try
        {
            var personalization = await knowledge.PersonalizationAsync(user, token);
            // Validate and construct the provenance before provider I/O or adding tracked state.
            var relation = InterdisciplinaryPractice.Create(program, source, sourceCode, link);
            var material = await teacher.GenerateAsync(new(topic.Title,
                new(program.Discipline, program.Goal, program.Experience, program.DiagnosticQuestion, program.DiagnosticAnswer),
                SharedSkill: new(sourceStatus.Skill.Description, sourceStatus.State), Personalization: personalization.ForGeneration(),
                Transfer: new(sourceTopic.Title, relation)), token);
            token.ThrowIfCancellationRequested();
            var generated = Lesson.Start(user, topic.Title, material.Theory, material.Question, material.Provenance, material.ExpectedAnswer);
            generated.LinkToProgram(program, code);
            generated.LinkToSkill(sourceStatus.Skill);
            generated.LinkTransfer(program, source, sourceCode, link);
            generated.SetTeaching(new(PedagogicalStrategy.InterdisciplinaryPractice, personalization.ExplicitPreferences));
            lessons.Add(generated);
            return new CurriculumResult.Lesson(LearningService.Material(generated));
        }
        catch (Exception exception) when (exception is LessonProviderException or ArgumentException)
        {
            return new CurriculumResult.ProviderUnavailable();
        }
    }
}
