using OwnDay.Application.Dialogue;
using OwnDay.Application.Learning;
using OwnDay.Infrastructure.Telegram.Localization;

namespace OwnDay.Infrastructure.Telegram.Commands;

public static class TelegramDialoguePresenter
{
    public const string Introduction = "Пишите обычным текстом: расскажите, чему хотите научиться, ответьте на задание или задайте вопрос.";
    public const string EnglishIntroduction = "Tell me what you'd like to learn, answer an exercise, or ask a question in your own words.";

    public static IReadOnlyList<string> Help(bool introduce, bool consultation = false, OwnDay.Domain.Dialogue.DialogueContext? context = null,
        Guid? selectedProgramId = null, string locale = "ru", bool listCommands = false)
    {
        var replies = RenderHelp(introduce, consultation, context, selectedProgramId, locale == "en");
        return listCommands && replies.Count > 0
            ? [.. replies.SkipLast(1), replies[^1] + "\n\n" + TelegramTexts.Get(locale, "help.settingsCommands") +
                "\n" + TelegramTexts.Get(locale, "help.releaseCommands")]
            : replies;
    }

    private static IReadOnlyList<string> RenderHelp(bool introduce, bool consultation, OwnDay.Domain.Dialogue.DialogueContext? context,
        Guid? selectedProgramId, bool english)
    {
        if (context?.DiagnosticTarget is not null)
            return [english ? "Your diagnostic and answers are saved. Write “continue” to see the current task. You can also write “help” or “skip”."
                : "Диагностика и ответы сохранены. Напишите «Продолжим», чтобы увидеть текущее задание; можно попросить «помощь» или написать «пропустим»."];
        var introduction = english ? EnglishIntroduction : Introduction;
        if (consultation && context?.TargetLessonId is null &&
            (selectedProgramId is null || selectedProgramId == context?.Consultation?.CreatedProgramId))
        {
            var prompt = context?.Consultation is { CreatedProgramId: not null } completed
                ? RenderText(new(DialogueReplyKind.FirstProgramReady, [completed.OriginalGoal, completed.PracticalUse!],
                    english))
                : RenderText(new(context?.Consultation is null ? DialogueReplyKind.AskGoal :
                    context.Consultation.PracticalUse is null ? DialogueReplyKind.AskPracticalUse :
                    context.Consultation.Experience is null ? DialogueReplyKind.AskExperience : DialogueReplyKind.ConsultationRetry, InEnglish: english));
            return introduce ? [introduction, .. prompt] : prompt;
        }
        var help = english
            ? "Write “continue” to open the current exercise, then answer or ask a question in your own words. " +
                "Finishing an exercise or switching programs requires a separate confirmation code. " +
                "To request a new program, use /learn_request <goal> | <experience>."
            : "Для текущего упражнения напишите «Продолжим», затем ответьте или задайте вопрос обычным текстом. " +
            "Завершение упражнения и смена программы требуют отдельного кода подтверждения. " +
            "Новая программа: /learn_request <цель> | <опыт>. Для возврата к диалогу напишите «Продолжим».";
        return introduce ? [introduction, help] : [help];
    }

    public static IReadOnlyList<TelegramLearningReply> Render(DialogueReply reply, bool? inRussian = null)
    {
        if (inRussian.HasValue) reply = reply with { InEnglish = !inRussian.Value };
        if (reply.Recap is { } recap) return TelegramLearningPresenter.RenderRecap(recap, reply.InEnglish);
        if (reply.ProgramChoices is { } choices)
            return [reply.InEnglish ? "Several programs match. Pick one below or clarify your request. Use /learn_programs to see all programs."
                : "Подходят несколько программ. Выберите программу ниже или уточните запрос. Все программы доступны через /learn_programs.",
                .. choices.Select(item => TelegramLearningReply.WithActions($"{item.Goal} ({item.Discipline})\n/learn_select {item.Id}",
                    [new(LearningActionKind.SelectProgram, item.Id)], inRussian: !reply.InEnglish))];
        var text = RenderText(reply);
        var actions = reply.Kind == DialogueReplyKind.FirstProgramReady
            ? reply.Values is { Count: > 3 } values && values[3] == "start"
                ? [new LearningAction(LearningActionKind.Continue)]
                : LearningActionProvider.ForDialogue(DialogueReplyKind.DiagnosticOffer)
            : LearningActionProvider.ForDialogue(reply.Kind);
        return text.Count == 0 ? [] : [.. text.SkipLast(1), TelegramLearningReply.WithActions(text[^1], actions, !reply.InEnglish)];
    }

    private static IReadOnlyList<string> RenderText(DialogueReply reply)
    {
        var values = reply.Values ?? [];
        if (reply.InEnglish)
        {
            var english = reply.Kind switch
            {
                DialogueReplyKind.AskGoal => "What would you like to learn? Tell me the subject or skill.",
                DialogueReplyKind.AskPracticalUse => "Where would you use this skill? We will measure progress toward your goal by whether you can solve a new task in that situation independently and explain your solution.",
                DialogueReplyKind.ClarifyPracticalUse => "Could you give one example of where you'd use this skill?",
                DialogueReplyKind.AskExperience => "What can you already do in this area? Name a familiar task, give your current level, or say that you are starting from scratch.",
                DialogueReplyKind.AskRelevantExperience => $"What can you already do toward your goal “{values[0]}”? Give one example or say “no experience”.",
                DialogueReplyKind.ConsultationPaused => "Your goal and answers are saved. Write “continue” when you're ready.",
                DialogueReplyKind.ConsultationRetry => "Your goal and answers are saved. Write “retry” to try creating the program again.",
                DialogueReplyKind.DiagnosticQuestion => "Diagnostic question\n" + values[0] + "\nAnswer the question or write “skip” to start the lesson.",
                DialogueReplyKind.DiagnosticOffer => "Write “yes” to see the optional diagnostic question or “skip” to open the exercise.",
                DialogueReplyKind.FirstProgramReady => $"Goal saved — {TelegramCurriculumPresenter.DisplayGoal(values[0])}\nApplication — {TelegramCurriculumPresenter.DisplayGoal(values[1])}\n" +
                    (values.Count > 2 ? $"First topics — {values[2]}\n" : "") +
                    (values.Count > 4 && values[4].Length > 0 ? $"Success criterion — {TelegramCurriculumPresenter.DisplayGoal(values[4])}\n" : "") +
                    (values.Count > 3 && values[3] == "start" ? "Write “continue” to open the first exercise." :
                        "You can start with a short diagnostic. Write “yes” to see the question or “skip” to open the first exercise."),
                DialogueReplyKind.SessionEnded => SessionSummary(values, true),
                DialogueReplyKind.ExerciseCriteria => "What to work on\n" + string.Join("\n\n", values),
                DialogueReplyKind.NoActiveLesson => "There is no open exercise. Write “continue” to get started.",
                DialogueReplyKind.AskNewGoal => "What would you like to learn instead? Write “change goal to” followed by your goal. I’ll ask you to confirm the change.",
                DialogueReplyKind.Paused => "The exercise is on pause. Write “continue” to return to the same question.",
                DialogueReplyKind.NeedBinding => "Write “continue” to open the current exercise, then send your message again.",
                DialogueReplyKind.ChooseAnswerOrQuestion => "Is that your answer to the exercise or a question about it? Write “answer” or “question”.",
                DialogueReplyKind.ChooseQuestionOrGoal => "Is this a question about the current lesson or a request to change your goal? Write “question” or “goal”.",
                DialogueReplyKind.ChoosePauseOrResume => "Write “pause” or “resume” to choose what to do.",
                DialogueReplyKind.Interrupted => "This message arrived before the input mode changed. Finish the current input, then write “continue”.",
                DialogueReplyKind.Cancelled => "Proposal cancelled. Your current lesson is saved.",
                DialogueReplyKind.ConfirmationRejected => "The confirmation is missing, expired, or for another proposal. Repeat the original request.",
                DialogueReplyKind.ChoiceUnbound => "This choice is for another prompt. Read the latest question and choose again.",
                DialogueReplyKind.ChoiceExpired => "The prompt expired or your choice arrived before the question. Check the current lesson and choose again.",
                DialogueReplyKind.OriginalStale => "The original message is for an earlier exercise. No answer was assessed.",
                DialogueReplyKind.NeedActualInput => "There is no choice pending. Send your answer or question about the exercise.",
                DialogueReplyKind.MustFinishLesson => "Finish the current exercise and confirm before switching programs. Pausing saves the exercise for later.",
                DialogueReplyKind.MustEndPractice => "End the current practice first. Write “end practice” to save your answers and open another exercise.",
                DialogueReplyKind.ProgramUnavailable => "Program unavailable. Use /learn_programs.",
                DialogueReplyKind.GoalDeferred => "Your plan to change goals is saved. Switching is currently unavailable. Your exercise and program are saved.",
                DialogueReplyKind.GoalChanged => $"New goal — {values[0]}. Your previous program and question are saved. Write “continue” to start working toward the new goal.",
                DialogueReplyKind.ConsultationUnavailable => "Your first program is ready and its goal is saved. Write “continue” to return to it.",
                DialogueReplyKind.Unavailable => "Learning dialogue is temporarily unavailable. Try again later.",
                DialogueReplyKind.RouteEditHelp => "Use a topic code from /learn_route tree.\n" +
                    "/learn_route_edit skip <code> · unskip <code> · brief <code> · detailed <code> · standard <code> · before <code> <code> · restore <version>",
                _ => null
            };
            if (english is not null) return [english];
        }
        if (reply.Kind is DialogueReplyKind.ConfirmFinish or DialogueReplyKind.ConfirmProgramSelection or
            DialogueReplyKind.ConfirmProgramCreation or DialogueReplyKind.ConfirmGoalIntent or DialogueReplyKind.ConfirmProgramSwitch or
            DialogueReplyKind.ConfirmGoalSwitch or DialogueReplyKind.ConfirmRouteEdit or DialogueReplyKind.ConfirmGoalUpdate)
        {
            if (reply.InEnglish)
            {
                var summaryInEnglish = reply.Kind switch
                {
                    DialogueReplyKind.ConfirmFinish => $"Finish “{values[0]}” without an assessment? Your history will be saved.",
                    DialogueReplyKind.ConfirmProgramSelection => $"Select “{values[0]}” in place of the current program?",
                    DialogueReplyKind.ConfirmProgramSwitch => $"Switch to “{values[0]}”? Your current question, help and answers will be saved. Any active practice will end.",
                    DialogueReplyKind.ConfirmGoalSwitch => $"Create and select a program for “{values[0]}”? Your current question and help will be saved. Any active practice will end.",
                    DialogueReplyKind.ConfirmGoalIntent => $"Save your plan to change goals to “{values[0]}”? Your current exercise and program will be saved.",
                    DialogueReplyKind.ConfirmRouteEdit => values[0],
                    DialogueReplyKind.ConfirmGoalUpdate => $"Apply the proposed route to the current program and set the goal to “{values[0]}”?",
                    _ => $"Create and select a program for “{values[0]}”? Your previous program history will be saved."
                };
                return [$"{summaryInEnglish}\n\nBy text, use /learn_confirm {values[1]} or /cancel. The code expires in 10 minutes."];
            }
            var summary = reply.Kind switch
            {
                DialogueReplyKind.ConfirmFinish => "Завершить упражнение «" + values[0] + "» без оценки, сохранив историю?",
                DialogueReplyKind.ConfirmProgramSelection => "Выбрать программу «" + values[0] + "» вместо текущей?",
                DialogueReplyKind.ConfirmProgramSwitch => "Выбрать программу «" + values[0] + "»? Текущий вопрос и помощь сохраним для возврата. Ограниченную практику остановим; ответы и история сохранятся.",
                DialogueReplyKind.ConfirmGoalSwitch => "Создать и выбрать программу по новой цели «" + values[0] + "»? Текущий вопрос и помощь сохраним для возврата, ограниченную практику остановим. Чтобы обновить текущую программу, напишите «обновить цель на <цель> | <проверяемый критерий>».",
                DialogueReplyKind.ConfirmGoalIntent => "Сохранить намерение сменить цель: «" + values[0] + "»? Текущее упражнение и программа сохранятся.",
                DialogueReplyKind.ConfirmRouteEdit => values[0],
                DialogueReplyKind.ConfirmGoalUpdate => "Применить показанный маршрут к текущей программе и установить цель «" + values[0] + "»?",
                _ => "Создать и выбрать новую программу по запросу «" + values[0] + "»? История прежней программы сохранится."
            };
            return [summary + "\n\nТекстом — /learn_confirm " + values[1] + " или /cancel. Код действует 10 минут."];
        }
        if (reply.Kind == DialogueReplyKind.RouteEditHelp)
            return ["Укажите код темы из /learn_route tree. Например, «пропустить тему basics» или «перенеси тему examples перед practice».\n" +
                "/learn_route_edit skip <код> · unskip <код> · brief <код> · detailed <код> · standard <код> · before <код> <код> · restore <версия>"];
        if (reply.Kind == DialogueReplyKind.ChooseMatchingProgram)
            return [reply.InEnglish ? "Several programs match. Select one using the command under its name, or clarify your request. Use /learn_programs to see all programs."
                : "Подходят несколько программ. Выберите нужную командой под названием или уточните запрос. Все программы доступны через /learn_programs.", .. values];
        return [reply.Kind switch
        {
            DialogueReplyKind.Interrupted => "Учебная реплика поступила до смены режима ввода и устарела. Завершите текущий ввод, затем напишите «Продолжим».",
            DialogueReplyKind.Cancelled => "Предложение отменено. Текущий урок сохранён.",
            DialogueReplyKind.ConfirmationRejected => "Подтверждение отсутствует, устарело или не относится к текущему предложению. Повторите исходный запрос.",
            DialogueReplyKind.ChoiceUnbound => "Выбор не относится к текущему уточнению. Прочитайте последний вопрос и выберите ответ после него.",
            DialogueReplyKind.ChoiceExpired => "Уточнение устарело или выбор поступил до вопроса. Проверьте текущий урок и повторите выбор после этого сообщения.",
            DialogueReplyKind.OriginalStale => "Исходная реплика больше не относится к текущему упражнению. Ничего не оценено.",
            DialogueReplyKind.NoActiveLesson => "Сейчас нет открытого упражнения. Напишите «Продолжим».",
            DialogueReplyKind.Paused => "Упражнение на паузе. Напишите «Продолжим», чтобы вернуться к тому же вопросу.",
            DialogueReplyKind.NeedBinding => "Вопрос неактуален или занятие на паузе. Напишите «Продолжим» и повторите реплику. Ничего не оценено.",
            DialogueReplyKind.NeedActualInput => "Сейчас нет ожидающего выбора. Отправьте сам ответ или вопрос по упражнению; ничего не оценено.",
            DialogueReplyKind.MustFinishLesson => "Сначала завершите текущее упражнение с подтверждением. Пауза сохраняет его и не разрешает смену программы.",
            DialogueReplyKind.MustEndPractice => "Сначала завершите текущую ограниченную практику или напишите «хватит практики». Её ответы сохранятся; затем можно начать другое упражнение.",
            DialogueReplyKind.ProgramUnavailable => "Программа недоступна. /learn_programs",
            DialogueReplyKind.GoalDeferred => "Намерение сменить цель сохранено. Текущее упражнение и программа сохранены; переключение сейчас недоступно.",
            DialogueReplyKind.AskNewGoal => "Какую цель выбрать? Напишите «сменить цель на <ваша цель>». Перед сменой покажу, что изменится, и попрошу отдельное подтверждение.",
            DialogueReplyKind.GoalChanged => "Новая цель: «" + values[0] + "». Прежняя программа и вопрос сохранены; вернуться можно через список программ. Напишите «Продолжим», чтобы начать новую цель.",
            DialogueReplyKind.ChooseAnswerOrQuestion => "Это ваш ответ на упражнение или вопрос по нему? Напишите «ответ» или «вопрос». До выбора ничего не оценивается; при выборе ответа будет оценён исходный текст.",
            DialogueReplyKind.ChooseQuestionOrGoal => "Это вопрос в рамках текущего урока или желание сменить учебную цель? Напишите «вопрос» или «цель». Учебное состояние пока не изменено.",
            DialogueReplyKind.ChoosePauseOrResume => "Уточните одно действие: «пауза» или «продолжить». Учебное состояние пока не изменено.",
            DialogueReplyKind.AskGoal => "Чему вы хотите научиться? Напишите «Хочу…» и свою цель.",
            DialogueReplyKind.AskPracticalUse => "Для чего эти знания нужны прежде всего? Опишите ситуацию, в которой хотите их применить. Будем считать цель достигнутой, когда вы сможете самостоятельно решить новую задачу в этой ситуации и объяснить решение.",
            DialogueReplyKind.ClarifyPracticalUse => "Можете привести один пример ситуации, в которой пригодятся эти знания?",
            DialogueReplyKind.AskExperience => "Что вы уже умеете в этой области? Можно назвать знакомую задачу, указать свой уровень или сказать, что начинаете с нуля.",
            DialogueReplyKind.AskRelevantExperience => "Какой опыт у вас именно по цели «" + TelegramCurriculumPresenter.DisplayGoal(values[0]) +
                "»? Назовите один знакомый пример или напишите «с нуля».",
            DialogueReplyKind.ConsultationRetry => "Цель, применение и опыт сохранены. Программу пока не удалось создать. Напишите «повторить», чтобы попробовать ещё раз, или «Продолжим», чтобы вернуться позже.",
            DialogueReplyKind.ConsultationUnavailable => "Первая программа уже создана. Для возврата к ней напишите «Продолжим». Текущая цель сохранена.",
            DialogueReplyKind.ConsultationPaused => "Консультация приостановлена, цель и ответы сохранены. Напишите «Продолжим», когда будете готовы.",
            DialogueReplyKind.FirstProgramReady => "Цель сохранена: «" + TelegramCurriculumPresenter.DisplayGoal(values[0]) +
                "». Применение: «" + TelegramCurriculumPresenter.DisplayGoal(values[1]) + "».\n" +
                (values.Count > 2 ? "Первые темы: " + values[2] + ".\n" : "") +
                (values.Count > 4 && values[4].Length > 0 ? "Критерий цели — " + TelegramCurriculumPresenter.DisplayGoal(values[4]) + "\n" : "") +
                (values.Count > 3 && values[3] == "start"
                    ? "Напишите «Продолжим», чтобы открыть первое упражнение."
                    : "Можно начать с короткой диагностики. Напишите «давай» или «пропустить»."),
            DialogueReplyKind.DiagnosticQuestion => "Диагностический вопрос\n" + values[0] + "\nОтветьте на вопрос или напишите «пропустить», чтобы начать занятие.",
            DialogueReplyKind.DiagnosticOffer => "Можно начать с короткой диагностики. Напишите «давай», чтобы увидеть вопрос, или «пропустить», чтобы открыть упражнение.",
            DialogueReplyKind.SessionEnded => SessionSummary(values, false),
            DialogueReplyKind.ExerciseCriteria => "На что обратить внимание\n" + string.Join("\n\n", values),
            _ => "Учебный диалог временно недоступен. Попробуйте позже."
        }];
    }

    private static string SessionSummary(IReadOnlyList<string> values, bool english)
    {
        var studied = values.ElementAtOrDefault(0);
        var next = values.ElementAtOrDefault(1);
        var hasNext = !string.IsNullOrWhiteSpace(next);
        var newTopic = hasNext && next != studied;
        var topic = string.IsNullOrWhiteSpace(studied) ? "" : english ? $" We covered {studied}." : $" Тема занятия — {studied}.";
        var transition = english
            ? newTopic ? $" Next time we'll work on {next}." : hasNext ? " Next time we'll return to the open exercise." : " Next time we'll pick up where we left off."
            : newTopic ? $" В следующий раз — {next}." : hasNext ? " В следующий раз вернёмся к открытому заданию." : " В следующий раз продолжим с того же места.";
        return (english ? "Session ended. Your progress is saved." : "Сессия завершена. Ваш прогресс сохранён.") + topic + transition;
    }
}
