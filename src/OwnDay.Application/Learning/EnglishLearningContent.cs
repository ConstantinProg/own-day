using OwnDay.Domain.Learning;

namespace OwnDay.Application.Learning;

// Authored, bounded content. Keys are used only by the reading assessor.
public static class EnglishLearningContent
{
    public const string Version = "work-email-v1";
    public const string FirstTopicCode = "email_request_reply";
    public static bool IsEnglishGoal(string goal) => goal.Trim().ToLowerInvariant().TrimEnd('.', '!') is
        "хочу читать и писать на английском" or "хочу научиться читать и писать на английском" or
        "хочу читать английские письма" or "хочу читать рабочие письма на английском" or
        "i want to read and write in english";

    public static CurriculumTopic[] Topics(bool foundation = false) =>
        [new(FirstTopicCode, foundation ? "Короткая просьба и ответ с опорой" : "Просьба в письме и краткий ответ", [])];

    public static LearningContent Reading { get; } = new("email-reading", Version, LearningContentKind.ReadingChoice,
        "Найти основную просьбу в коротком рабочем письме.", "Выбрать один вариант A, B или C.",
        "Один верный вариант из трёх; проверка по авторскому ключу, без LLM.");
    public static LearningContent Writing { get; } = new("email-writing", Version, LearningContentKind.OpenWriting,
        "Написать короткий уместный ответ на рабочую просьбу.", "Ответить на английском одной-двумя фразами: согласие или отказ и когда сможете отправить список.",
        "Meaning: explicitly accepts or declines sending the list and gives timing. Clarity: understandable short English reply; minor grammar errors are acceptable. Appropriateness: polite and relevant. All three are needed for IsCorrect. Explain each criterion in Russian; this is provisional AI feedback, not CEFR.");
    public const string ReadingText = "Hi Alex, could you send me the meeting notes today? Thanks, Sam.";
    public const string ReadingQuestion = "Что просит Sam? Выберите A, B или C.\nA. Назначить встречу.\nB. Отправить записи встречи сегодня.\nC. Позвонить завтра.";
    public const string WritingText = "Hi Alex, could you send me the guest list today? Thanks, Jo.";
    public const string WritingQuestion = "Напишите Jo краткий ответ на английском. Скажите, можете ли отправить список и когда. " +
        "Критерий: понятный, вежливый ответ по смыслу, одна-две фразы; небольшие ошибки допустимы.";
    public static bool ReadingIsCorrect(string answer) => answer.Trim().Equals("B", StringComparison.OrdinalIgnoreCase);
    public static bool IsReadingChoice(string answer) => answer.Trim().ToUpperInvariant() is "A" or "B" or "C";
    public static LearningContent Item(int index) => index == 0 ? Reading : index == 1 ? Writing
        : throw new ArgumentOutOfRangeException(nameof(index));
    public static string Question(int index) => index == 0 ? ReadingQuestion : WritingQuestion;
    public static string Text(int index) => index == 0 ? ReadingText : WritingText;
    public static string Help(int index) => index == 0
        ? "Найдите часть после could you и посмотрите, что нужно сделать и когда. Затем выберите один вариант."
        : "Ответьте на просьбу: можете ли вы помочь и когда. Можно согласиться или вежливо отказаться. Оценка будет отмечена как ответ с помощью.";

    public static LearningContent FirstLesson { get; } = new("email-first-lesson", Version, LearningContentKind.OpenWriting,
        "Найти просьбу в коротком рабочем письме и написать уместный ответ.",
        "Назвать просьбу и ответить на английском одной-двумя фразами, указав срок.",
        "Identify the requested action (sending the updated agenda) and write a polite understandable English response accepting or declining it with timing. Minor grammar errors are acceptable. Give Russian feedback for meaning, clarity and appropriateness. AI-only provisional assessment, not CEFR.");
    public const string LessonTheory = "Начнём: найдём просьбу в коротком письме и ответим одной-двумя фразами. " +
        "Could you…? — вежливая просьба. Например, Could you call Lee? — просьба позвонить Lee.\n\n" +
        "Hi Alex, could you send me the updated agenda by Friday? Thanks, Kim.";
    public const string LessonQuestion = "Что просит Kim? Назовите просьбу и напишите Kim ответ на английском одной-двумя фразами. " +
        "Критерий: верно передать просьбу, вежливо согласиться или отказаться и указать срок; небольшие ошибки допустимы.";
    public const string LessonScaffold = "Опора для ответа: после Could you найдите действие и предмет просьбы. " +
        "Ответ можно начать с Yes, I can… или Sorry, I can’t… и добавить срок. Это помощь: ответ не будет засчитан как самостоятельный успех.";
}
