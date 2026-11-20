using System.Globalization;
using OwnDay.Application.Interactions;
using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Telegram.Commands;

public sealed class TelegramStudyCommandHandler(StudyService service)
{
    public static ProcessIncomingCommand? ParseText(string text, OwnDay.Domain.UserId user)
    {
        var value = text.Trim();
        var name = value.ToLowerInvariant() switch
        {
            "расписание занятий" => "learn_schedule",
            "не присылай приглашения" => "learn_notify_pause",
            "возобнови приглашения" => "learn_notify_resume",
            "отключи учебные напоминания" or "turn off study reminders" => "learn_notify_off",
            "покажи повторения" => "learn_reviews",
            _ => null
        };
        if (name is not null) return new(name, "", user);
        (string Prefix, string Command)[] prefixes =
        [
            ("напоминай мне ", "learn_schedule"),
            ("начать приглашение ", "learn_invite_start"),
            ("отложить приглашение ", "learn_invite_later"),
            ("пропустить приглашение ", "learn_invite_skip")
        ];
        foreach (var (prefix, command) in prefixes)
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return new(command, value[prefix.Length..].Trim(), user);
        return null;
    }

    private const string Usage = "Use /learn_schedule <time zone> | <every 1-30 days> | <HH:mm-HH:mm>[,HH:mm-HH:mm] | [study,review,open].\n" +
        "Example: /learn_schedule Europe/Moscow | 1 | 18:00-20:00 | review,open\nTimes outside your windows are quiet. The three-part form enables study and review for a new schedule.\n" +
        "/learn_notify_pause · /learn_notify_resume · /learn_notify_off\n" +
        "В учебном диалоге: «напоминай мне Europe/Moscow | 1 | 18:00-20:00», «расписание занятий», " +
        "«не присылай приглашения», «возобнови приглашения», «покажи повторения». " +
        "Для приглашения: «начать приглашение <код>», «отложить приглашение <код>», «пропустить приглашение <код>».";
    private const string Expired = "Invitation expired or already handled. /learn_schedule · /learn_next · /learn_reviews";

    public bool Handles(string name) => name is "learn_schedule" or "learn_notify_pause" or "learn_notify_resume" or "learn_notify_off" or
        "learn_invite_start" or "learn_invite_later" or "learn_invite_skip";

    public async Task<IReadOnlyList<TelegramLearningReply>> HandleAsync(ProcessIncomingCommand command, CancellationToken token)
    {
        var args = command.Arguments.Trim();
        if (command.Name == "learn_schedule")
        {
            if (args.Length == 0) return Show(await service.ShowAsync(command.UserId, token));
            var parts = args.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length is < 3 or > 4 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var days)) return [Usage];
            StudyReminderType? allowed = parts.Length == 4 ? ParseTypes(parts[3]) : null;
            if (parts.Length == 4 && allowed is null) return [Usage];
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
                return Show(await service.ConfigureAsync(command.UserId, parts[0], days, windows, token, allowed));
            }
            catch (Exception exception) when (exception is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return [Usage];
            }
        }
        if (command.Name == "learn_notify_off")
        {
            if (args.Length != 0) return [Usage];
            await service.OptOutAsync(command.UserId, token);
            return ["Учебные напоминания отключены. Повторения доступны через /learn_reviews."];
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

    public static TelegramLearningReply Invitation(StudyInvitation invitation)
    {
        var russian = MarketLearningContent.InRussian(invitation.Topic);
        var heading = (russian, invitation.IsOpenExercise, invitation.IsReview) switch
        {
            (true, true, _) => $"Есть открытое упражнение по теме «{invitation.Topic}».",
            (true, false, true) => $"Пора повторить «{invitation.Topic}».",
            (true, false, false) => $"Пора вернуться к теме «{invitation.Topic}».",
            (false, true, _) => $"Your exercise on {invitation.Topic} is still open.",
            _ => $"Time to study: {invitation.Topic}" + (invitation.IsReview ? " (review)" : "")
        };
        var text = russian
            ? heading + $"\nТекстом — «начать приглашение», «отложить приглашение» или «пропустить приглашение» и код {invitation.Id:N}."
            : heading + $"\nBy text — /learn_invite_start {invitation.Id:N} · /learn_invite_later {invitation.Id:N} · /learn_invite_skip {invitation.Id:N}";
        return TelegramLearningReply.WithActions(text, StudyActionProvider.ForInvitation(invitation), russian);
    }

    private static IReadOnlyList<TelegramLearningReply> Show(StudySchedule? schedule) => schedule is null || schedule.EffectiveReminderTypes == StudyReminderType.None
        ? ["Notifications are off.", Usage] :
        [TelegramLearningReply.WithActions($"Study schedule: {schedule.TimeZoneId}; every {schedule.EveryDays} day(s).\n" +
         string.Join(", ", schedule.Windows.Select(w => $"{w.Start:HH:mm}-{w.End:HH:mm}")) +
         $"\nNotifications: {(schedule.Paused ? "paused" : "enabled")}. Types: {schedule.EffectiveReminderTypes}. Next eligible time: {schedule.NextAt:O} (UTC).\n" +
         "/learn_notify_pause · /learn_notify_resume · /learn_notify_off",
         StudyActionProvider.ForSchedule(schedule), inRussian: false)];

    private static StudyReminderType? ParseTypes(string text)
    {
        var result = StudyReminderType.None;
        foreach (var word in text.Split(',', StringSplitOptions.TrimEntries))
        {
            var type = word.ToLowerInvariant() switch
            {
                "study" => StudyReminderType.Study,
                "review" => StudyReminderType.Review,
                "open" => StudyReminderType.OpenExercise,
                _ => StudyReminderType.None
            };
            if (type == StudyReminderType.None || (result & type) != StudyReminderType.None) return null;
            result |= type;
        }
        return result == StudyReminderType.None ? null : result;
    }
}
