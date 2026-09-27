using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Inbox;
using OwnDay.Application.StructuredItems;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;
using OwnDay.Infrastructure.Persistence;

namespace OwnDay.Infrastructure.Telegram.Handling;

public sealed class TelegramInboxFlow(
    OwnDayDbContext db,
    InboxCaptureService capture,
    InboxProcessingService processing,
    StructuredItemService structured,
    TimeProvider clock)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public async Task<IReadOnlyList<string>> CaptureOrContinueAsync(UserId user, string text, CancellationToken token)
    {
        var expired = await db.TelegramInboxDrafts.AnyAsync(row => row.UserId == user.Value && row.ExpiresAt <= Now(), token);
        if (expired)
        {
            var stale = await db.TelegramInboxDrafts.SingleAsync(row => row.UserId == user.Value, token);
            db.TelegramInboxDrafts.Remove(stale);
            return ["Черновик истёк. Запись осталась во входящих. Откройте /inbox для продолжения."];
        }

        var draft = await LoadAsync(user, token);
        if (draft is null)
        {
            await capture.CaptureAsync(user, text, token);
            return ["Сохранено во входящие."];
        }

        var item = await capture.FindAsync(user, draft.InboxItemId, token);
        if (item?.Status != InboxItemStatus.Active)
        {
            db.TelegramInboxDrafts.Remove(draft);
            return ["Запись уже недоступна. Черновик закрыт. Откройте /inbox."];
        }

        var value = text.Trim();
        switch (draft.Step)
        {
            case TelegramInboxDraftStep.Target:
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var choice) || choice is < 1 or > 5)
                {
                    return ["Выберите тип числом от 1 до 5. /cancel — отмена, /discard — удалить из входящих."];
                }

                draft.TargetKind = (InboxTargetKind)(choice - 1);
                draft.Text = item.OriginalText;
                draft.Step = TelegramInboxDraftStep.Text;
                break;
            case TelegramInboxDraftStep.Text:
                var edited = value == "." ? draft.Text : value;
                if (string.IsNullOrWhiteSpace(edited) || edited.Trim().Length > 200)
                {
                    return ["Введите текст от 1 до 200 символов или точку, чтобы оставить исходный."];
                }

                draft.Text = edited.Trim();
                draft.Step = draft.TargetKind == InboxTargetKind.WaitingFor
                    ? TelegramInboxDraftStep.Source
                    : draft.TargetKind == InboxTargetKind.Project
                        ? TelegramInboxDraftStep.Confirm
                        : TelegramInboxDraftStep.Project;
                break;
            case TelegramInboxDraftStep.Source:
                if (value != "." && value.Length > 200)
                {
                    return ["Источник должен содержать не более 200 символов. Точка — без источника."];
                }

                draft.Source = value == "." ? null : value;
                draft.Step = TelegramInboxDraftStep.Project;
                break;
            case TelegramInboxDraftStep.Project:
                if (value is "0" or ".")
                {
                    draft.ProjectId = null;
                }
                else if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var projectId) && projectId > 0 &&
                    (await structured.FindProjectAsync(user, projectId, token)) is { Status: OwnDay.Domain.Projects.ProjectStatus.Active })
                {
                    draft.ProjectId = projectId;
                }
                else
                {
                    return ["Проект не найден. Введите его ID или 0 — без проекта."];
                }

                draft.Step = TelegramInboxDraftStep.Confirm;
                break;
            case TelegramInboxDraftStep.Confirm:
                if (!value.Equals("да", StringComparison.OrdinalIgnoreCase))
                {
                    return ["Для создания отправьте «да». /cancel — отмена, /discard — удалить из входящих."];
                }

                var result = await processing.ProcessAsync(user, new ProcessInboxItem(
                    draft.InboxItemId, draft.TargetKind!.Value, draft.Text!, draft.ProjectId, draft.Source), token);
                if (result.Status == ProcessInboxItemStatus.InvalidProject)
                {
                    draft.Step = TelegramInboxDraftStep.Project;
                    return ["Выбранный проект недоступен. Укажите другой ID или 0 — без проекта."];
                }

                if (result.Status == ProcessInboxItemStatus.InvalidTargetData)
                {
                    draft.Step = TelegramInboxDraftStep.Text;
                    return ["Данные некорректны. Введите новый текст от 1 до 200 символов."];
                }

                db.TelegramInboxDrafts.Remove(draft);
                return result.Status == ProcessInboxItemStatus.Processed
                    ? [$"Входящее #{draft.InboxItemId} обработано. Создано: {Label(draft.TargetKind.Value)}, #{result.TargetId}."]
                    : ["Запись уже обработана или недоступна. Черновик закрыт."];
        }

        draft.Version++;
        draft.ExpiresAt = Now().Add(Lifetime);
        return draft.Step switch
        {
            TelegramInboxDraftStep.Text => [$"Тип: {Label(draft.TargetKind!.Value)}. Исходный текст: {Short(item.OriginalText)}\nВведите итоговый текст (до 200 символов) или точку, чтобы оставить исходный."],
            TelegramInboxDraftStep.Source => ["Источник ожидания: введите текст до 200 символов или точку — без источника."],
            TelegramInboxDraftStep.Project => await ProjectPromptAsync(user, token),
            _ => [await SummaryAsync(user, item.OriginalText, draft, token)]
        };
    }

    public async Task<IReadOnlyList<string>> OpenAsync(UserId user, string argument, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            var items = await capture.GetActiveAsync(user, token);
            return TelegramTextLists.Render("Входящие", items,
                item => $"#{item.Id} {Short(item.OriginalText)} — /inbox {item.Id}");
        }

        if (!long.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            return ["Используйте /inbox <id>."];
        }

        var item = await capture.FindAsync(user, id, token);
        if (item?.Status != InboxItemStatus.Active)
        {
            return ["Активное входящее не найдено."];
        }

        var current = await LoadAsync(user, token);
        if (current is not null)
        {
            db.TelegramInboxDrafts.Remove(current);
            await db.SaveChangesAsync(token);
        }

        db.TelegramInboxDrafts.Add(new TelegramInboxDraft
        {
            UserId = user.Value,
            InboxItemId = id,
            Step = TelegramInboxDraftStep.Target,
            ExpiresAt = Now().Add(Lifetime)
        });
        return [$"Входящее #{id}: {Short(item.OriginalText)}\nВыберите тип:\n1 — Задача\n2 — Проект\n3 — Идея\n4 — Заметка\n5 — Ожидаю\n/cancel — отмена, /discard — удалить из входящих."];
    }

    public async Task<IReadOnlyList<string>> CancelAsync(UserId user, CancellationToken token)
    {
        var draft = await LoadAsync(user, token);
        if (draft is null)
        {
            return ["Нет активного выбора. Входящие: /inbox."];
        }

        db.TelegramInboxDrafts.Remove(draft);
        return ["Выбор отменён. Запись осталась во входящих."];
    }

    public async Task<IReadOnlyList<string>> DiscardAsync(UserId user, string argument, CancellationToken token)
    {
        var draft = await LoadAsync(user, token);
        var id = long.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed : string.IsNullOrWhiteSpace(argument) ? draft?.InboxItemId : null;
        if (id is null)
        {
            return ["Используйте /discard <id> или откройте /inbox <id>."];
        }

        var result = await processing.DiscardAsync(user, id.Value, token);
        if (draft?.InboxItemId == id.Value)
        {
            db.TelegramInboxDrafts.Remove(draft);
        }

        return result switch
        {
            DiscardInboxItemResult.Discarded => [$"Входящее #{id} отброшено."],
            DiscardInboxItemResult.AlreadyProcessed => ["Запись уже обработана."],
            DiscardInboxItemResult.AlreadyDiscarded => ["Запись уже отброшена."],
            _ => ["Входящее не найдено."]
        };
    }

    private async Task<TelegramInboxDraft?> LoadAsync(UserId user, CancellationToken token)
    {
        var draft = await db.TelegramInboxDrafts.SingleOrDefaultAsync(row => row.UserId == user.Value, token);
        if (draft is not null && draft.ExpiresAt <= Now())
        {
            db.TelegramInboxDrafts.Remove(draft);
            await db.SaveChangesAsync(token);
            return null;
        }

        return draft;
    }

    private async Task<IReadOnlyList<string>> ProjectPromptAsync(UserId user, CancellationToken token)
    {
        var projects = await structured.GetActiveProjectsAsync(user, token);
        return TelegramTextLists.Render("Выберите проект: 0 — без проекта", projects,
            project => $"#{project.Id} {project.Title}");
    }

    private async Task<string> SummaryAsync(UserId user, string original, TelegramInboxDraft draft, CancellationToken token)
    {
        var project = draft.ProjectId is long id ? await structured.FindProjectAsync(user, id, token) : null;
        return $"Подтверждение: будет создано {Label(draft.TargetKind!.Value)}.\nИсходный текст: {Short(original)}\nИтоговый текст: {draft.Text}\nПроект: {(project is null ? "нет" : $"#{project.Id} {project.Title}")}" +
            (draft.TargetKind == InboxTargetKind.WaitingFor ? $"\nИсточник: {draft.Source ?? "нет"}" : "") +
            "\nОтправьте «да» для обработки, /cancel для отмены или /discard для удаления из входящих.";
    }

    private static string Label(InboxTargetKind kind) => kind switch
    {
        InboxTargetKind.Action => "Задача",
        InboxTargetKind.Project => "Проект",
        InboxTargetKind.SomedayMaybe => "Идея",
        InboxTargetKind.Reference => "Заметка",
        _ => "Ожидаю"
    };

    private static string Short(string text) => text.Length <= 500 ? text : text[..500] + "…";
    private DateTime Now() => clock.GetUtcNow().UtcDateTime;
}
