using System.Text.Json;
using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Application.Learning;

public sealed record CurriculumGoalProposal(CurriculumGoal Goal, IReadOnlyList<CurriculumTopic> Topics);

public sealed partial class CurriculumService
{
    private const string GoalRevisionReason = "Пользователь подтвердил новую цель и маршрут.";

    public async Task<CurriculumResult> ProposeGoalUpdateAsync(UserId user, Guid programId, int version, string text,
        CancellationToken token)
    {
        ValidateUser(user, token);
        var parts = text.Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts[0].Length is < 1 or > LearningProgram.MaxGoalLength ||
            parts[1].Length is < 1 or > LearningProgram.MaxGoalCriterionLength) return new CurriculumResult.GoalEditRejected();
        var program = await store.FindAsync(user, token);
        if (!CanChangeGoal(program, programId, version)) return new CurriculumResult.GoalEditRejected();
        try
        {
            var goal = new CurriculumGoal(parts[0], parts[1]);
            var proposal = await planner.PlanAsync(new(goal.Description, program.Experience, null, null, program.Discipline), token);
            token.ThrowIfCancellationRequested();
            if (proposal.Discipline != program.Discipline || proposal.Topics is null) return new CurriculumResult.GoalEditRejected();
            var route = program.ProposeGoalRevision(version, goal, proposal.Topics, GoalRevisionReason);
            var snapshot = JsonSerializer.Serialize(new CurriculumGoalProposal(goal, route.Topics), JsonSerializerOptions.Web);
            return snapshot.Length > 4000 ? new CurriculumResult.GoalEditRejected() :
                new CurriculumResult.GoalUpdateOffered(goal, route, snapshot, route.ProgressRootVersion == program.CurrentRoute.ProgressRootVersion);
        }
        catch (Exception exception) when (exception is ArgumentException or LessonProviderException)
        {
            return new CurriculumResult.GoalEditRejected();
        }
    }

    public async Task<CurriculumResult> ApplyGoalUpdateAsync(UserId user, Guid programId, int version, string snapshot, CancellationToken token)
    {
        ValidateUser(user, token);
        var program = await store.FindAsync(user, token);
        if (!CanChangeGoal(program, programId, version)) return new CurriculumResult.GoalEditRejected();
        try
        {
            var proposal = JsonSerializer.Deserialize<CurriculumGoalProposal>(snapshot, JsonSerializerOptions.Web);
            token.ThrowIfCancellationRequested();
            return proposal is { Goal: not null, Topics: not null } &&
                program.ReviseGoal(version, proposal.Goal, proposal.Topics, GoalRevisionReason)
                ? new CurriculumResult.RouteView(program.CurrentRoute, true) : new CurriculumResult.GoalEditRejected();
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return new CurriculumResult.GoalEditRejected();
        }
    }

    private static bool CanChangeGoal([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] LearningProgram? program, Guid id, int version) =>
        program is { DiagnosticPending: false, ProjectId: null } && program.Id == id && program.CurrentRoute.Version == version;
}
