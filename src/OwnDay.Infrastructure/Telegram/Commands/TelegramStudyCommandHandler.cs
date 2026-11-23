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
        "/learn_notify_pause · /learn_notify_resume · /learn_notify_off";
    private const string RussianUsage = "Формат: /learn_schedule <часовой пояс> | <интервал от 1 до 30 дней> | <ЧЧ:ММ-ЧЧ:ММ>[,ЧЧ:ММ-ЧЧ:ММ] | [study,review,open].\n" +
        "Пример: /learn_schedule Europe/Moscow | 1 | 18:00-20:00 | review,open\n" +
        "Напоминания приходят в заданные часы. При создании расписания без списка типов включаются занятия и повторения.\n" +
        "/learn_notify_pause · /learn_notify_resume · /learn_notify_off\n" +
        "В учебном диалоге: «напоминай мне Europe/Moscow | 1 | 18:00-20:00», «расписание занятий», " +
        "«не присылай приглашения», «возобнови приглашения», «покажи повторения». " +
        "Для приглашения: «начать приглашение <код>», «отложить приглашение <код>», «пропустить приглашение <код>».";
    private const string Expired = "Invitation expired or already handled. /learn_schedule · /learn_next · /learn_reviews";

    public bool Handles(string name) => name is "learn_schedule" or "learn_notify_pause" or "learn_notify_resume" or "learn_notify_off" or
        "learn_invite_start" or "learn_invite_later" or "learn_invite_skip";

    public async Task<IReadOnlyList<TelegramLearningReply>> HandleAsync(ProcessIncomingCommand command, CancellationToken token, bool? inRussian = null)
    {
        var args = command.Arguments.Trim();
        var russian = inRussian == true;
        var usage = russian ? RussianUsage : Usage;
        var expired = russian ? "Срок приглашения истёк или оно уже обработано. /learn_schedule · /learn_next · /learn_reviews" : Expired;
        if (command.Name == "learn_schedule")
        {
            if (args.Length == 0) return Show(await service.ShowAsync(command.UserId, token), russian, usage);
            var parts = args.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length is < 3 or > 4 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var days)) return [usage];
            StudyReminderType? allowed = parts.Length == 4 ? ParseTypes(parts[3]) : null;
            if (parts.Length == 4 && allowed is null) return [usage];
            var windows = new List<StudyWindow>();
            foreach (var pair in parts[2].Split(',', StringSplitOptions.TrimEntries))
            {
                var times = pair.Split('-', StringSplitOptions.TrimEntries);
                if (times.Length != 2 || !TimeOnly.TryParseExact(times[0], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
                    !TimeOnly.TryParseExact(times[1], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)) return [usage];
                windows.Add(new(start, end));
            }
            try
            {
                return Show(await service.ConfigureAsync(command.UserId, parts[0], days, windows, token, allowed), russian, usage);
            }
            catch (Exception exception) when (exception is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return [usage];
            }
        }
        if (command.Name == "learn_notify_off")
        {
            if (args.Length != 0) return [usage];
            await service.OptOutAsync(command.UserId, token);
            return [inRussian == false ? "Study reminders are off. Reviews are available via /learn_reviews."
                : "Учебные напоминания отключены. Повторения доступны через /learn_reviews."];
        }
        if (command.Name is "learn_notify_pause" or "learn_notify_resume")
        {
            if (args.Length != 0) return [usage];
            return Show(await service.PauseAsync(command.UserId, command.Name == "learn_notify_pause", token), russian, usage);
        }
        if (!Guid.TryParse(args, out var invitation)) return [expired];
        if (command.Name == "learn_invite_start")
        {
            var result = await service.StartAsync(command.UserId, invitation, token);
            return result is null ? [expired] : TelegramCurriculumPresenter.Render(result, inRussian);
        }
        var later = command.Name == "learn_invite_later";
        if (!await service.DismissAsync(command.UserId, invitation, later, token)) return [expired];
        return [(russian, later) switch
        {
            (true, true) => "Приглашение отложено минимум на час и придёт в часы напоминаний.",
            (true, false) => "Приглашение пропущено. Прогресс и даты повторений сохранены.",
            (false, true) => "Invitation postponed by at least one hour to an allowed window. No assessment or penalty.",
            _ => "Invitation skipped. Progress and review dates are unchanged. No commitment or penalty."
        }];
    }

    public static TelegramLearningReply Invitation(StudyInvitation invitation, bool? inRussian = null)
    {
        var russian = inRussian ?? MarketLearningContent.InRussian(invitation.Topic);
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

    private static IReadOnlyList<TelegramLearningReply> Show(StudySchedule? schedule, bool russian, string usage)
    {
        if (schedule is null || schedule.EffectiveReminderTypes == StudyReminderType.None)
            return [russian ? "Напоминания отключены." : "Notifications are off.", usage];
        var title = russian ? $"Расписание занятий: {schedule.TimeZoneId}, интервал {schedule.EveryDays} дн."
            : $"Study schedule: {schedule.TimeZoneId}; every {schedule.EveryDays} day(s).";
        var status = russian
            ? $"Напоминания {(schedule.Paused ? "приостановлены" : "включены")}. Типы: {schedule.EffectiveReminderTypes}. Возможны с {schedule.NextAt:O} (UTC)."
            : $"Notifications: {(schedule.Paused ? "paused" : "enabled")}. Types: {schedule.EffectiveReminderTypes}. Next eligible time: {schedule.NextAt:O} (UTC).";
        return [TelegramLearningReply.WithActions(title + "\n" +
            string.Join(", ", schedule.Windows.Select(w => $"{w.Start:HH:mm}-{w.End:HH:mm}")) + "\n" + status +
            "\n/learn_notify_pause · /learn_notify_resume · /learn_notify_off", StudyActionProvider.ForSchedule(schedule), russian)];
    }

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
