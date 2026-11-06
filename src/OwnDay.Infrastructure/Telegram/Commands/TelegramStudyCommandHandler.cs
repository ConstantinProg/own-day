using System.Globalization;
using OwnDay.Application.Interactions;
using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Telegram.Commands;

public sealed class TelegramStudyCommandHandler(StudyService service)
{
    private const string Usage = "Use /learn_schedule <time zone> | <every 1-30 days> | <HH:mm-HH:mm>[,HH:mm-HH:mm] (up to 4 windows).\n" +
        "Example: /learn_schedule Europe/Moscow | 1 | 18:00-20:00\n/learn_notify_pause · /learn_notify_resume";
    private const string Expired = "Invitation expired or already handled. /learn_schedule · /learn_next · /learn_reviews";

    public bool Handles(string name) => name is "learn_schedule" or "learn_notify_pause" or "learn_notify_resume" or
        "learn_invite_start" or "learn_invite_later" or "learn_invite_skip";

    public async Task<IReadOnlyList<string>> HandleAsync(ProcessIncomingCommand command, CancellationToken token)
    {
        var args = command.Arguments.Trim();
        if (command.Name == "learn_schedule")
        {
            if (args.Length == 0) return Show(await service.ShowAsync(command.UserId, token));
            var parts = args.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length != 3 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var days)) return [Usage];
            var windows = new List<StudyWindow>();
            foreach (var pair in parts[2].Split(',', StringSplitOptions.TrimEntries))
            {
                var times = pair.Split('-', StringSplitOptions.TrimEntries);
                if (times.Length != 2 || !TimeOnly.TryParseExact(times[0], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
                    !TimeOnly.TryParseExact(times[1], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)) return [Usage];
                windows.Add(new(start, end));
            }
            try
            {
                return Show(await service.ConfigureAsync(command.UserId, parts[0], days, windows, token));
            }
            catch (Exception exception) when (exception is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return [Usage];
            }
        }
        if (command.Name is "learn_notify_pause" or "learn_notify_resume")
        {
            if (args.Length != 0) return [Usage];
            return Show(await service.PauseAsync(command.UserId, command.Name == "learn_notify_pause", token));
        }
        if (!Guid.TryParse(args, out var invitation)) return [Expired];
        if (command.Name == "learn_invite_start")
        {
            var result = await service.StartAsync(command.UserId, invitation, token);
            return result is null ? [Expired] : TelegramCurriculumPresenter.Render(result);
        }
        var later = command.Name == "learn_invite_later";
        return await service.DismissAsync(command.UserId, invitation, later, token)
            ? [later ? "Invitation postponed by at least one hour to an allowed window. No assessment or penalty."
                : "Invitation skipped. Progress and review dates are unchanged. No commitment or penalty."]
            : [Expired];
    }

    public static string Invitation(StudyInvitation invitation) =>
        $"Time to study: {invitation.Topic}" + (invitation.IsReview ? " (review)" : "") +
        $"\nStart: /learn_invite_start {invitation.Id:N}\nLater: /learn_invite_later {invitation.Id:N}\nSkip: /learn_invite_skip {invitation.Id:N}" +
        "\nOptional invitation. No commitment or penalty. /learn_notify_pause";

    private static IReadOnlyList<string> Show(StudySchedule? schedule) => schedule is null ? [Usage] :
        [$"Study schedule: {schedule.TimeZoneId}; every {schedule.EveryDays} day(s).\n" +
         string.Join(", ", schedule.Windows.Select(w => $"{w.Start:HH:mm}-{w.End:HH:mm}")) +
         $"\nNotifications: {(schedule.Paused ? "paused" : "enabled")}. Next eligible time: {schedule.NextAt:O} (UTC).\n" +
         "/learn_notify_pause · /learn_notify_resume\n" + Usage];
}
