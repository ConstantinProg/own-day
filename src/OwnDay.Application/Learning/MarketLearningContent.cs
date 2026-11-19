using OwnDay.Domain.Learning;

namespace OwnDay.Application.Learning;

public enum MarketFactRequest { None, CurrentUnverified, MoscowEquitySettlementCode, PersonalDecision }

public static class MarketLearningContent
{
    public static MarketFactRequest Identify(string request, string? subject)
    {
        var text = request.ToLowerInvariant();
        var context = text + " " + subject?.ToLowerInvariant();
        if (!Contains(context, "бирж", "moex", "мосбирж", "фондов", "market", "stock", "equity", "equities", "инвестиц", "investment", "акци"))
            return MarketFactRequest.None;
        if (Contains(text, "мне купить", "мне продать", "мне инвестировать", "should i buy", "should i sell", "what should i invest"))
            return MarketFactRequest.PersonalDecision;
        if (Contains(text, "код расчёт", "код расчет", "settlement code", "стакан т+", "order book t+"))
        {
            return Contains(text, "moex", "мосбирж", "московской бирж", "московская бирж", "moscow exchange") &&
                Contains(context, "акци", "equity", "equities", "shares", "stock") &&
                !Contains(text, "облигац", "bond", "фьючерс", "future", "crypto", "репо", "repo", "cny", "usd", "eur", "юан", "доллар", "евро")
                ? MarketFactRequest.MoscowEquitySettlementCode : MarketFactRequest.CurrentUnverified;
        }
        return Contains(text, "актуальн", "действующ", "сейчас", "сегодня", "текущ", "current", "today", "latest", "trading hours", "расписание торгов")
            ? MarketFactRequest.CurrentUnverified : MarketFactRequest.None;
    }

    public static LessonMaterial Material(MarketFactRequest request, bool russian, LessonProvenance? provenance)
    {
        var personal = request == MarketFactRequest.PersonalDecision;
        var requirement = personal
            ? russian ? "Назвать два параметра для сравнения инвестиционных рисков." : "Name two criteria for comparing investment risks."
            : russian ? "Назвать рынок, режим торгов и дату, для которых действует правило." : "Identify the market, trading mode, and date to which a rule applies.";
        var question = personal
            ? russian ? "По каким двум параметрам вы сравнили бы риск двух условных портфелей?" : "Which two criteria would you use to compare the risk of two hypothetical portfolios?"
            : russian ? "Что нужно уточнить перед применением биржевого правила к сделке?" : "What should you check before applying an exchange rule to a trade?";
        return new(Explanation(request, russian), question)
        {
            Provenance = provenance,
            Content = new("market-facts", "rubric-v1", LearningContentKind.StructuredExercise, question, requirement, requirement,
                new(1, ExerciseEvidenceKind.Understanding, [new("scope", requirement)]))
        };
    }

    public static string Explanation(MarketFactRequest request, bool russian) => request == MarketFactRequest.PersonalDecision
        ? russian
            ? "Для учебного сравнения рассмотрим риск двух условных портфелей. Сопоставьте горизонт инвестирования, возможные потери, диверсификацию и издержки. Персональное решение зависит от ваших целей и способности нести убытки."
            : "Compare two hypothetical portfolios by investment horizon, possible losses, diversification, and costs. A personal decision depends on your goals and ability to bear losses."
        : russian
            ? "Биржевое правило применяется в своей области. Сначала проверьте биржу, рынок, режим торгов и дату. Код расчётов и время торгов описывают разные условия сделки."
            : "An exchange rule applies within a defined scope. Check the exchange, market, trading mode, and date. Settlement codes and trading hours describe different conditions of a trade.";

    public static bool InRussian(string text) => text.Any(character => character is >= '\u0400' and <= '\u04FF');

    private static bool Contains(string text, params string[] parts) => parts.Any(part => text.Contains(part, StringComparison.Ordinal));
}
