using OwnDay.Application.Dialogue;

namespace OwnDay.Infrastructure.Telegram.Commands;

public static class TelegramDialoguePresenter
{
    public const string Introduction = "Для учебного диалога пишите обычным текстом, для заметок в Inbox — /capture <текст>. Учебные команды тоже доступны.";

    public static IReadOnlyList<string> Help(bool introduce, bool consultation = false, OwnDay.Domain.Dialogue.DialogueContext? context = null,
        Guid? selectedProgramId = null)
    {
        if (context?.DiagnosticTarget is not null)
            return ["Диагностика и ответы сохранены. Напишите «Продолжим», чтобы увидеть текущее задание; можно попросить «помощь» или написать «пропустим»."];
        if (consultation && context?.TargetLessonId is null &&
            (selectedProgramId is null || selectedProgramId == context?.Consultation?.CreatedProgramId))
        {
            var prompt = context?.Consultation is { CreatedProgramId: not null } completed
                ? Render(new(DialogueReplyKind.FirstProgramReady, [completed.OriginalGoal, completed.PracticalUse!]))
                : Render(new(context?.Consultation is null ? DialogueReplyKind.AskGoal :
                    context.Consultation.PracticalUse is null ? DialogueReplyKind.AskPracticalUse :
                    context.Consultation.Experience is null ? DialogueReplyKind.AskExperience : DialogueReplyKind.ConsultationRetry));
            return introduce ? [Introduction, .. prompt] : prompt;
        }
        const string help = "Для текущего упражнения напишите «Продолжим», затем ответьте или задайте вопрос обычным текстом. " +
            "Завершение упражнения и смена программы требуют отдельного кода подтверждения. " +
            "Новая программа: /learn_request <цель> | <опыт>. Все команды: /help commands.";
        return introduce ? [Introduction, help] : [help];
    }

    public static IReadOnlyList<string> Render(DialogueReply reply)
    {
        var values = reply.Values ?? [];
        if (reply.Kind == DialogueReplyKind.Recap) return ["Текущее упражнение: " + values[0], values[1],
            .. values.Count > 3 && !string.IsNullOrWhiteSpace(values[3]) ? new[] { "Ранее разбирали (помощь сохранена):\n" + values[3] } : Array.Empty<string>(),
            values[2] + "\n\n" + (values.Count > 4 && values[4] == "Paused"
                ? "Упражнение на паузе. Напишите «Продолжим», чтобы вернуться к этому вопросу."
                : "Напишите ответ или вопрос по упражнению. «Пауза» сохраняет вопрос, «помощь» показывает текущую точку возврата.")];
        if (reply.Kind is DialogueReplyKind.ConfirmFinish or DialogueReplyKind.ConfirmProgramSelection or
            DialogueReplyKind.ConfirmProgramCreation or DialogueReplyKind.ConfirmGoalIntent)
        {
            var summary = reply.Kind switch
            {
                DialogueReplyKind.ConfirmFinish => "Завершить упражнение «" + values[0] + "» без оценки, сохранив историю?",
                DialogueReplyKind.ConfirmProgramSelection => "Выбрать программу «" + values[0] + "» вместо текущей?",
                DialogueReplyKind.ConfirmGoalIntent => "Сохранить намерение сменить цель: «" + values[0] + "»? Текущее упражнение и программа сохранятся.",
                _ => "Создать и выбрать новую программу по запросу «" + values[0] + "»? История прежней программы сохранится."
            };
            return [summary + " Напишите «подтверждаю " + values[1] + "» или /learn_confirm " + values[1] +
                ". Для отмены: /cancel. Код действует 10 минут."];
        }
        return [reply.Kind switch
        {
            DialogueReplyKind.Interrupted => "Учебная реплика поступила до смены режима ввода и устарела. Завершите текущий ввод, затем напишите «Продолжим».",
            DialogueReplyKind.Cancelled => "Предложение отменено. Текущий урок сохранён.",
            DialogueReplyKind.ConfirmationRejected => "Подтверждение отсутствует, устарело или не относится к текущему предложению. Повторите исходный запрос.",
            DialogueReplyKind.ChoiceUnbound => "Выбор не относится к текущему уточнению. Прочитайте последний вопрос и выберите ответ после него.",
            DialogueReplyKind.ChoiceExpired => "Уточнение устарело или выбор поступил до вопроса. Проверьте текущий урок и повторите выбор после этого сообщения.",
            DialogueReplyKind.OriginalStale => "Исходная реплика больше не относится к текущему упражнению. Ничего не оценено.",
            DialogueReplyKind.NoActiveLesson => "Сейчас нет активного упражнения. Для существующей программы: /learn_next. Новая программа: /learn_request <цель> | <опыт>. Для заметки в Inbox: /capture <текст>.",
            DialogueReplyKind.Paused => "Упражнение на паузе. Напишите «Продолжим», чтобы вернуться к тому же вопросу.",
            DialogueReplyKind.NeedBinding => "Вопрос неактуален или занятие на паузе. Напишите «Продолжим» и повторите реплику. Ничего не оценено.",
            DialogueReplyKind.NeedActualInput => "Сейчас нет ожидающего выбора. Отправьте сам ответ или вопрос по упражнению; ничего не оценено.",
            DialogueReplyKind.MustFinishLesson => "Сначала завершите текущее упражнение с подтверждением. Пауза сохраняет его и не разрешает смену программы.",
            DialogueReplyKind.MustEndPractice => "Сначала завершите текущую ограниченную практику или напишите «хватит практики». Её ответы сохранятся; затем можно начать другое упражнение.",
            DialogueReplyKind.ProgramUnavailable => "Программа недоступна. /learn_programs",
            DialogueReplyKind.GoalDeferred => "Намерение сменить цель сохранено. Текущее упражнение и программа сохранены; переключение сейчас недоступно.",
            DialogueReplyKind.ChooseAnswerOrQuestion => "Это ваш ответ на упражнение или вопрос по нему? Напишите «ответ» или «вопрос». До выбора ничего не оценивается; при выборе ответа будет оценён исходный текст.",
            DialogueReplyKind.ChooseQuestionOrGoal => "Это вопрос в рамках текущего урока или желание сменить учебную цель? Напишите «вопрос» или «цель». Учебное состояние пока не изменено.",
            DialogueReplyKind.ChoosePauseOrResume => "Уточните одно действие: «пауза» или «продолжить». Учебное состояние пока не изменено.",
            DialogueReplyKind.AskGoal => "Я помогу вам учиться и применять знания. Например, читать рабочие письма или писать короткие сообщения. Чему вы хотите научиться? Напишите «Хочу…» и свою цель.",
            DialogueReplyKind.AskPracticalUse => "Для чего эти знания нужны прежде всего? Например, рабочие письма, статьи или повседневная переписка. Исходную цель сохраним.",
            DialogueReplyKind.AskExperience => "Какой у вас опыт? Например, понимаете отдельные слова, уже читаете короткие тексты или начинаете с нуля.",
            DialogueReplyKind.ConsultationRetry => "Цель, применение и опыт сохранены. Программу пока не удалось создать. Напишите «повторить», чтобы попробовать ещё раз, или «Продолжим», чтобы вернуться позже.",
            DialogueReplyKind.ConsultationUnavailable => "Первая программа уже создана. Для возврата к ней напишите «Продолжим». Текущая цель сохранена.",
            DialogueReplyKind.ConsultationPaused => "Консультация приостановлена, цель и ответы сохранены. Напишите «Продолжим», когда будете готовы.",
            DialogueReplyKind.FirstProgramReady => "Цель сохранена: «" + values[0] + "». Применение: «" + values[1] + "». " +
                "Предлагаю два-три коротких задания, чтобы выбрать сложность. Можно пропустить.",
            _ => "Учебный диалог временно недоступен. Попробуйте позже."
        }];
    }
}
