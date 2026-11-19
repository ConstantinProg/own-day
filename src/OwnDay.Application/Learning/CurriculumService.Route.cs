using System.Globalization;
using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Application.Learning;

public sealed partial class CurriculumService
{
    public async Task<CurriculumResult> ViewRouteAsync(UserId user, string arguments, CancellationToken token)
    {
        ValidateUser(user, token);
        var program = await store.FindAsync(user, token);
        if (program is null) return new CurriculumResult.NoProgram();
        var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts is ["history"])
            return new CurriculumResult.RouteHistory(program.Routes.OrderByDescending(route => route.Version).Take(PageSize).ToArray());
        var version = program.CurrentRoute.Version;
        var full = parts.Length > 0;
        if (full && (parts[0] != "tree" || parts.Length > 2 || parts.Length == 2 && !TryVersion(parts[1], out version)))
            return new CurriculumResult.RouteEditRejected();
        var route = program.Routes.SingleOrDefault(item => item.Version == version);
        return route is null ? new CurriculumResult.RouteEditRejected() : new CurriculumResult.RouteView(route, full);
    }

    public async Task<CurriculumResult> EditRouteAsync(UserId user, Guid expectedProgram, int expectedVersion, string arguments,
        CancellationToken token)
    {
        ValidateUser(user, token);
        var program = await store.FindAsync(user, token);
        if (program is null || program.Id != expectedProgram || program.CurrentRoute.Version != expectedVersion || program.DiagnosticPending)
            return new CurriculumResult.RouteEditRejected();

        var route = program.CurrentRoute;
        var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var code = parts.Length >= 2 ? ResolveCode(route, parts[1]) : "";
        try
        {
            token.ThrowIfCancellationRequested();
            var changed = parts switch
            {
                ["skip", _] => program.SetTopicSkipped(expectedVersion, code, true, $"Пользователь пропустил тему {code}."),
                ["unskip", _] => program.SetTopicSkipped(expectedVersion, code, false, $"Пользователь вернул тему {code}."),
                ["brief" or "detailed" or "standard", _] when Enum.TryParse<CurriculumDepth>(parts[0], true, out var depth) =>
                    program.SetTopicDepth(expectedVersion, code, depth, $"Пользователь изменил глубину темы {code} ({parts[0]})."),
                ["before", _, var target] => MoveTopic(program, route, code, ResolveCode(route, target)),
                ["restore", var version] when TryVersion(version, out var restoredVersion) =>
                    program.RestoreRoute(expectedVersion, restoredVersion, $"Пользователь восстановил маршрут v{restoredVersion}."),
                _ => false
            };
            if (!changed) return new CurriculumResult.RouteEditRejected();
            var optionalCheck = parts[0] == "skip" && route.Topics.Any(topic => topic.Prerequisites.Contains(code)) ? code : null;
            return new CurriculumResult.RouteView(program.CurrentRoute, true, optionalCheck);
        }
        catch (ArgumentException)
        {
            return new CurriculumResult.RouteEditRejected();
        }
    }

    private static string ResolveCode(CurriculumRoute route, string code)
    {
        if (route.Topics.Any(topic => topic.Code == code)) return code;
        var matches = route.Topics.Where(topic => topic.Code.Equals(code, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0].Code : "";
    }

    private static bool MoveTopic(LearningProgram program, CurriculumRoute route, string code, string target)
    {
        var topics = route.Topics.ToList();
        var from = topics.FindIndex(topic => topic.Code == code);
        var before = topics.FindIndex(topic => topic.Code == target);
        if (from < 0 || before < 0 || from == before || from + 1 == before) return false;
        var moving = topics[from];
        topics.RemoveAt(from);
        topics.Insert(from < before ? before - 1 : before, moving);
        return program.ReviseRoute(route.Version, topics, $"Пользователь переместил тему {code} перед {target}.");
    }

    private static bool TryVersion(string text, out int version) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out version) && version > 0;
}
