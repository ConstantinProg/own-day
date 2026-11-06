using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Telegram.Handling;

namespace OwnDay.Infrastructure.Telegram.Commands;

public static class TelegramProgressPresenter
{
    public static IReadOnlyList<string> Render(LearningProgress report)
    {
        var program = report.Program;
        var route = program.CurrentRoute;
        var entries = new List<string>
        {
            $"Goal: {program.Goal}\nProgram: {program.Id}\nDiscipline: {program.Discipline}\n" +
            $"Selected: {report.Selected}; priority: {program.Priority}\nRoute v{route.Version}: {route.Reason}\n" +
            $"Diagnosis: {(program.DiagnosticPending ? "pending" : "finished or skipped")}\n" +
            $"Route questions credited: {report.Topics.Count(t => t.Completed)}/{report.Topics.Count}. " +
            "A credited question does not establish long-term mastery.\n" +
            $"/learn_select {program.Id}",
            "Skill estimates use distinct assessed answers across all routes and confirmed shared identities. " +
            string.Join(" ", Enum.GetValues<SkillMastery>().Select(TelegramSkillMasteryText.Explain)) + " " +
            "All estimates are provisional. Gaps below include unassessed topics and uncertain skills."
        };
        foreach (var item in report.Topics)
        {
            var status = item.Status;
            var state = status?.State;
            var text = $"{item.Topic.Code}: {item.Topic.Title}\nRoute credit: {item.Completed}; " +
                $"practice available: {item.Available}\n";
            if (!item.Available)
                text += program.DiagnosticPending ? "Finish diagnosis: /learn_diagnose <answer|skip>\n"
                    : $"Prerequisites: {string.Join(", ", item.Topic.Prerequisites)}\n";
            text += state is null ? "Gap: no assessment evidence yet.\n"
                : $"Skill estimate: {state.Mastery}; answers: {state.Answers}; credited positive: {state.PositiveAssessments}; " +
                  $"negative without conflict: {state.NegativeAssessments}.\n" +
                  (state.Mastery != SkillMastery.Demonstrated ? "Gap: further practice or verification needed.\n" : "") +
                  $"/learn_status {status!.Skill.Id}\n";
            if (status?.Evidence.LastOrDefault() is { } evidence)
                text += $"Latest assessment ({evidence.ObservedAt:O}): {evidence.AssessmentStatus}; " +
                    $"AI interpretation: {((evidence.AiAssessedCorrect ?? evidence.AssessedCorrect) ? "positive" : "negative")}\n" +
                    TelegramLearningPresenter.RenderAssessment(evidence.AssessmentStatus, evidence.DeterministicIsCorrect) + "\n" +
                    $"source: {evidence.AssessmentSource}\nBasis (excerpt): {Excerpt(evidence.AssessmentFeedback)}\n";
            text += status?.Review is { } review
                ? $"Review (UTC): {review.DueAt:O}; {(item.ReviewDue ? "due" : "upcoming")}\n/learn_review {status.Skill.Id}\n"
                : "No review scheduled yet.\n";
            if (item.Link is { } link)
                text += $"Shared identity: {link.SkillId}; {link.Trust}; source: {link.Source}\nBasis: {link.Reason}\n" +
                    "Identity confirmation does not credit this program's questions.\n";
            entries.Add(text.TrimEnd());
        }
        entries.Add(report.Project is { } project
            ? $"Project link: #{project.Id} {project.Title}; status: {project.Status}. " +
              "Linked by your program creation request. Applicability remains a learning proposal; " +
              "answers do not prove delivery or completion of project work.\n" +
              $"Select this program with /learn_select {program.Id}, then /learn_project_report."
            : program.ProjectId is null ? "No project link."
                : "Linked project is unavailable for this user; no project details can be shown.");
        entries.Add("All skills and assessment details: /learn_skills; due reviews: /learn_reviews.\n" +
            "For the selected program: /learn_route; /learn_next; /learn_resume; /learn_pause.");
        if (report.Page > 1) entries.Add($"Previous: /learn_progress {report.Page - 1}");
        if (report.HasNext) entries.Add($"Next: /learn_progress {report.Page + 1}");
        return TelegramTextLists.Split(entries, $"Learning progress: page {report.Page}");
    }

    private static string Excerpt(string value) => value.Length <= 300 ? value : value[..299] + "…";
}
