using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

internal static class TelegramPersonalizationFixture
{
    internal static async Task<(Guid Source, Guid Target, Guid Skill)> SeedTransferAsync(IServiceProvider services, string scenario = "valid")
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var user = new UserId(101);
        var sourceUser = scenario == "foreign" ? new(202) : user;
        CurriculumTopic[] topics = [new("basics", "Shared concept", []), new("advanced", "Advanced", ["basics"])];
        var source = LearningProgram.Create(sourceUser, "Algebra", "Beginner", null, "Mathematics", topics,
            scenario == "sourceDiagnosis" ? "Diagnostic question" : null);
        var target = LearningProgram.Create(user, "Mechanics", "Beginner", null,
            scenario == "sameDiscipline" ? "Mathematics" : "Physics", topics, scenario == "diagnosis" ? "Question" : null);
        var sourceSkill = Skill.Create(sourceUser, "Algebra", "Algebraic reasoning");
        var shared = sourceUser == user && scenario != "differentSkill" ? sourceSkill : Skill.Create(user, "Other", "Other concept");
        var lesson = Lesson.Start(sourceUser, "Algebra", "Theory", "Question");
        if (scenario != "sourceDiagnosis") lesson.LinkToProgram(source, "basics");
        lesson.LinkToSkill(sourceSkill);
        lesson.Complete("Answer", "AI interpretation", scenario is not ("sourcePrerequisite" or "sourceUncompleted"));
        db.AddRange(source, target, sourceSkill, lesson, LearningEvidence.FromAnswer(lesson, DateTimeOffset.UtcNow, "Fake AI"), LearningContext.Create(target));
        if (sourceSkill != shared) db.Add(shared);
        if (scenario == "activeLesson") db.Add(Lesson.Start(user, "Existing", "Theory", "Question"));
        await db.SaveChangesAsync();
        return (source.Id, target.Id, shared.Id);
    }

}
