using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OwnDay.Application.Dialogue;
using OwnDay.Application.Learning;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresLearningProviderTests
{
    [Fact]
    public async Task Post_AnotherExerciseAfterProgramSwitch_UsesLatestAssessmentInSelectedProgram()
    {
        Guid firstProgram;
        Guid secondProgram;
        Guid firstSkill;
        Guid secondSkill;
        await using (var db = Open())
        {
            var first = LearningProgram.Create(_user, "First skill", "Beginner", null, "First", [new("root", "First skill", [])], null);
            var second = LearningProgram.Create(_user, "Second skill", "Beginner", null, "Second", [new("root", "Second skill", [])], null);
            var firstAssessment = SeedAssessment(first, _now.AddDays(-1));
            var secondAssessment = SeedAssessment(second, _now.AddDays(-2));
            firstProgram = first.Id;
            secondProgram = second.Id;
            firstSkill = firstAssessment.SkillId;
            secondSkill = secondAssessment.SkillId;
            db.Add(LearningContext.Create(first));
            await db.SaveChangesAsync();

            LearningEvidence SeedAssessment(LearningProgram program, DateTimeOffset at)
            {
                var skill = Skill.Create(_user, program.Goal);
                var lesson = Lesson.Start(_user, program.Goal, "Theory", "Original question");
                lesson.LinkToProgram(program, "root");
                lesson.LinkToSkill(skill);
                lesson.Complete("Original answer", "Accepted", true);
                var evidence = LearningEvidence.FromAnswer(lesson, at, "test");
                db.AddRange(program, skill, lesson, evidence);
                return evidence;
            }
        }

        var probe = new Probe("") { Generate = request => new("Theory", "New exercise for " + request.Topic) };
        using (var host = SwitchHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 1, "ещё задание");
            await SendEnglishAsync(client, 2, "/answer First practice answer");
        }

        using (var host = SwitchHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 3, "/learn_select " + secondProgram);
            await SendEnglishAsync(client, 4, "подтверждаю " + await SwitchCodeAsync());
            await SendEnglishAsync(client, 5, "ещё задание");
        }

        await using var result = Open();
        var practice = await result.Set<Lesson>().SingleAsync(item => item.Answer == "First practice answer");
        Assert.Equal(firstSkill, practice.SkillId);
        Assert.Equal(firstProgram, practice.PracticeProgramId);
        Assert.Null(practice.ProgramId);
        var active = await result.Set<Lesson>().SingleAsync(item => item.Status == LessonStatus.AwaitingAnswer);
        Assert.Equal(secondSkill, active.SkillId);
        Assert.Equal(secondProgram, active.PracticeProgramId);
        Assert.False(result.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Migrate_LegacyGeneratedSampleAndExplicitKey_PreservesTextAndSetsReferenceRole()
    {
        await using var db = Open();
        var example = Lesson.Start(_user, "Examples", "Theory", "Who routes the order?",
            new("OpenAI:historical-model", null, null, null, null, MaterialSourceStatus.None), "broker");
        var key = Lesson.Start(_user, "Keys", "Theory", "Which option?", expectedAnswer: "A");
        key.Finish();
        db.AddRange(example, key);
        await db.SaveChangesAsync();
        await db.GetService<IMigrator>().MigrateAsync("20261118220000_TrackExplanationProvenance");
        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();

        var migratedExample = await db.Set<Lesson>().SingleAsync(item => item.Id == example.Id);
        Assert.Equal("broker", migratedExample.ExpectedAnswer);
        Assert.Equal(AnswerReferenceKind.Example, migratedExample.ReferenceKind);
        migratedExample.Complete("a brokerage firm", "Accepted", true);
        Assert.True(migratedExample.IsCorrect);
        var migratedKey = await db.Set<Lesson>().SingleAsync(item => item.Id == key.Id);
        Assert.Equal("A", migratedKey.ExpectedAnswer);
        Assert.True(migratedKey.UsesReferenceCheck);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_ShortCorrectAnswerWithTopicRubric_CompletesOnlyTheExercise(bool previousAssessment)
    {
        const string question = "Кто обычно передаёт поручение частного инвестора на биржевые торги?";
        const string answer = "брокер";
        const string nextQuestion = "Объясните, чем роль брокера отличается от роли биржи.";
        const string nextAnswer = "Брокер принимает поручение клиента, биржа организует торги.";
        var deliveredAnswer = previousAssessment ? "Биржа организует торги." : answer;
        var content = new LearningContent(LearningContent.GeneratedExerciseId, "rubric-v1", LearningContentKind.StructuredExercise,
            "Понять устройство биржи", question, "Проверить устройство биржи.",
            new(1, ExerciseEvidenceKind.Understanding,
            [
                new("roles", "Различает роли инвестора, брокера и биржи."),
                new("execution", "Объясняет, от чего зависит исполнение заявки."),
                new("settlement", "Отличает торги от расчётов и учёта прав.")
            ]));
        var original = new LessonMaterial("Инвестор передаёт поручение через посредника.", question)
        {
            Content = content, ExpectedAnswer = answer, ReferenceKind = AnswerReferenceKind.Example
        };
        var probe = new Probe("")
        {
            Curriculum = new("Рыночная механика", [new("roles", "Участники биржевых торгов", [])], null),
            Generate = request =>
            {
                if (request.Teaching is null) return original;
                Assert.Equal(ExerciseEvidenceKind.Understanding, request.PracticeEvidenceKind);
                Assert.Equal(question, Assert.Single(request.PreviousQuestions!));
                return QuestionScopedMaterial(ExerciseEvidenceKind.Understanding, new ExerciseCriterion("roles", nextQuestion));
            },
            Assessment = (lesson, reply) =>
            {
                if (previousAssessment && lesson.Question == question)
                {
                    Assert.Equal(question, lesson.RubricEvaluation!.Clarification);
                    Assert.Equal(answer, lesson.RubricEvaluation.InitialAnswer);
                }
                return Grade(lesson, lesson.Content!.StructuredRubric!.Criteria.Length == 1 &&
                    reply == (lesson.Question == question ? deliveredAnswer : nextAnswer));
            }
        };
        using (var host = ConsultationHost(probe))
        using (var client = host.CreateClient())
        {
            await StartRubricExerciseAsync(client);
            if (previousAssessment)
            {
                await using var previous = Open();
                var pending = await previous.Set<Lesson>().SingleAsync();
                var oldRubric = new RubricAssessment([.. content.StructuredRubric!.Criteria.Select(item =>
                    new RubricCriterionAssessment(item.Code, RubricCriterionResult.Unclear, "Нужно раскрыть критерий темы."))]);
                previous.Entry(pending).Property(item => item.RubricEvaluation).CurrentValue =
                    new(oldRubric, answer, Guid.NewGuid(), content.StructuredRubric.Criteria[1].Requirement, Final: false);
                var context = await previous.Set<DialogueContext>().SingleAsync();
                context.Synchronize(pending, context.LastActivityAt, 4);
                await previous.SaveChangesAsync();
            }
        }

        using (var host = ConsultationHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 5, deliveredAnswer);
            using var duplicate = await PostPublicAsync(client, 5, deliveredAnswer);
            duplicate.EnsureSuccessStatusCode();
        }

        await using var db = Open();
        var lesson = await db.Set<Lesson>().SingleAsync();
        Assert.Equal(LessonStatus.Completed, lesson.Status);
        Assert.Equal(deliveredAnswer, lesson.Answer);
        Assert.Equal(3, lesson.Content!.StructuredRubric!.Criteria.Length);
        var evidence = Assert.Single(await db.Set<LearningEvidence>().ToListAsync());
        Assert.True(evidence.AssessedCorrect);
        Assert.Null(evidence.DeterministicIsCorrect);
        Assert.Equal(ExerciseEvidenceKind.Recognition, evidence.Content!.StructuredRubric!.Kind);
        Assert.Equal(SkillMastery.Developing, LearnerSkillState.Evaluate([evidence]).Mastery);
        Assert.Equal(answer, evidence.RubricEvidence!.Evaluation.InitialAnswer);
        if (previousAssessment) Assert.Equal(3, evidence.RubricEvidence.Evaluation.InitialAssessment!.Criteria.Length);
        Assert.Equal(deliveredAnswer, Assert.Single(probe.AssessedAnswers));
        Assert.Contains(await db.TelegramOutboxMessages.Select(item => item.Text).ToListAsync(), text =>
            text.Contains("Верно, упражнение зачтено.", StringComparison.Ordinal) && text.Contains("ещё задание", StringComparison.Ordinal));

        using var continued = ConsultationHost(probe);
        using var continuedClient = continued.CreateClient();
        await SendEnglishAsync(continuedClient, 6, "ещё задание");
        await SendEnglishAsync(continuedClient, 7, nextAnswer);
        var lessons = await db.Set<Lesson>().AsNoTracking().ToListAsync();
        Assert.Equal(2, lessons.Count);
        Assert.All(lessons, item => Assert.Equal(LessonStatus.Completed, item.Status));
        Assert.All(lessons, item => Assert.Equal(lesson.SkillId, item.SkillId));
        Assert.Contains(lessons, item => item.Question == nextQuestion);
        var history = await db.Set<LearningEvidence>().AsNoTracking().ToListAsync();
        Assert.Equal(2, history.Count);
        Assert.Equal(SkillMastery.Developing, LearnerSkillState.Evaluate(history).Mastery);
    }

    [Theory]
    [InlineData("брокре", null)]
    [InlineData("Поручение передаёт брокерская компания.", null)]
    [InlineData("A broker", null)]
    [InlineData("брокер", "дай подсказку")]
    [InlineData("брокер", "покажи решение")]
    public async Task Post_EquivalentAnswerOrAnswerAfterHelp_CreditsTheQuestion(string answer, string? help)
    {
        const string question = "Кто передаёт поручение инвестора?";
        var probe = new Probe("")
        {
            Material = QuestionScopedMaterial(ExerciseEvidenceKind.Recognition, new ExerciseCriterion("role", question)),
            Recognize = input => input.Input == help
                ? help == "покажи решение" ? LearningIntent.RevealSolution : LearningIntent.Clarify
                : LearningIntent.Answer,
            Assessment = (lesson, reply) => Grade(lesson, reply is "брокре" or "Поручение передаёт брокерская компания." or "A broker" or "брокер")
        };
        using var host = ConsultationHost(probe);
        using var client = host.CreateClient();
        await StartRubricExerciseAsync(client);
        var id = 5;
        if (help is not null) await SendEnglishAsync(client, id++, help);
        await SendEnglishAsync(client, id, answer);

        await using var db = Open();
        var evidence = await db.Set<LearningEvidence>().SingleAsync();
        Assert.True(evidence.AssessedCorrect);
        Assert.Equal(answer, evidence.Answer);
        Assert.Equal(help is null ? AnswerAssistance.Independent : AnswerAssistance.Assisted, evidence.Assistance);
        Assert.Equal(help is null ? SkillMastery.Developing : SkillMastery.Unknown, LearnerSkillState.Evaluate([evidence]).Mastery);
        Assert.Equal(1, probe.KindCalls["assess"]);
    }

    [Fact]
    public async Task Post_PartialCompoundAnswer_ClarifiesOnlyMissingTaskAndKeepsBothReplies()
    {
        const string first = "Кто передаёт поручение инвестора?";
        const string second = "Объясните, когда лимитная заявка может исполниться.";
        const string clarification = "Когда есть подходящая встречная заявка по разрешённой цене.";
        var probe = new Probe("")
        {
            Material = QuestionScopedMaterial(ExerciseEvidenceKind.Understanding, new("role", first), new("order", second)),
            Assessment = (lesson, reply) =>
            {
                if (reply == clarification) Assert.Equal("брокер", lesson.RubricEvaluation!.ClarificationAnswer);
                return new("Брокер передаёт поручение. Для исполнения нужна встречная заявка.", reply == clarification)
                {
                    Rubric = new([new("role", RubricCriterionResult.Met, "Брокер назван."),
                        new("order", reply == clarification ? RubricCriterionResult.Met : RubricCriterionResult.Unclear, "Проверяется условие исполнения.")])
                };
            }
        };
        using var host = ConsultationHost(probe);
        using var client = host.CreateClient();
        await StartRubricExerciseAsync(client);
        await SendEnglishAsync(client, 5, "брокер");
        await using var db = Open();
        var pending = await db.Set<Lesson>().AsNoTracking().SingleAsync();
        Assert.Equal(LessonStatus.AwaitingAnswer, pending.Status);
        Assert.Equal(second, pending.ResponseQuestion);
        Assert.Empty(await db.Set<LearningEvidence>().ToListAsync());
        await SendEnglishAsync(client, 6, clarification);

        var evidence = await db.Set<LearningEvidence>().SingleAsync();
        Assert.True(evidence.AssessedCorrect);
        Assert.Equal("брокер", evidence.RubricEvidence!.Evaluation.ClarificationAnswer);
        Assert.Equal(clarification, evidence.Answer);
        Assert.Equal(AnswerAssistance.Independent, evidence.Assistance);
    }

    [Fact]
    public async Task Post_AnotherExerciseAfterStandaloneLesson_ContinuesTheSameSkill()
    {
        const string nextQuestion = "Объясните, как поручение инвестора попадает на биржу.";
        var probe = new Probe("")
        {
            Generate = request => request.Teaching is null
                ? QuestionScopedMaterial(ExerciseEvidenceKind.Recognition, new ExerciseCriterion("role", "Кто передаёт поручение?"))
                : QuestionScopedMaterial(ExerciseEvidenceKind.Understanding, new ExerciseCriterion("process", nextQuestion)),
            Assessment = (lesson, reply) => Grade(lesson, reply == "брокер")
        };
        using var host = ConsultationHost(probe);
        using var client = host.CreateClient();
        await SendEnglishAsync(client, 1, "/learn Участники торгов");
        await SendEnglishAsync(client, 2, "брокер");
        await SendEnglishAsync(client, 3, "ещё упражнение");

        await using var db = Open();
        Assert.Empty(await db.Set<LearningProgram>().ToListAsync());
        var evidence = await db.Set<LearningEvidence>().SingleAsync();
        var active = await db.Set<Lesson>().SingleAsync(item => item.Status == LessonStatus.AwaitingAnswer);
        Assert.True(evidence.AssessedCorrect);
        Assert.Equal(evidence.SkillId, active.SkillId);
        Assert.Equal(nextQuestion, active.Question);
    }

    [Fact]
    public async Task Post_AssessorFails_OriginalAnswerRemainsInDurableRequestAndRetryCreditsOnce()
    {
        const string answer = "брокер";
        var probe = new Probe("")
        {
            Material = QuestionScopedMaterial(ExerciseEvidenceKind.Recognition, new ExerciseCriterion("role", "Кто передаёт поручение?")),
            FailureKind = "assess",
            FailureCount = 1,
            FailureReason = LessonProviderFailure.InvalidResponse,
            Assessment = (lesson, reply) => Grade(lesson, reply == answer)
        };
        using var host = ConsultationHost(probe);
        using var client = host.CreateClient();
        await StartRubricExerciseAsync(client);
        await SendEnglishAsync(client, 5, answer);
        using var duplicate = await PostPublicAsync(client, 5, answer);
        duplicate.EnsureSuccessStatusCode();
        await using var db = Open();
        var request = await db.Set<LearningRequest>().SingleAsync(item => item.SourceUpdateId == 5);
        Assert.Equal(answer, System.Text.Json.JsonSerializer.Deserialize<LearningTextInput>(request.NormalizedInput,
            System.Text.Json.JsonSerializerOptions.Web)!.Text);
        Assert.Equal(LessonStatus.AwaitingAnswer, (await db.Set<Lesson>().AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await db.Set<LearningEvidence>().ToListAsync());
        await SendEnglishAsync(client, 6, answer);

        var evidence = await db.Set<LearningEvidence>().SingleAsync();
        Assert.True(evidence.AssessedCorrect);
        Assert.Equal(answer, evidence.Answer);
        Assert.Equal(2, probe.KindCalls["assess"]);
    }

    private static LessonMaterial QuestionScopedMaterial(ExerciseEvidenceKind kind, params ExerciseCriterion[] criteria) =>
        new("Инвестор передаёт поручение через посредника.", string.Join('\n', criteria.Select(item => item.Requirement)))
        {
            Content = new(LearningContent.GeneratedExerciseId, "rubric-v2", LearningContentKind.StructuredExercise,
                "Участники торгов", criteria[0].Requirement, "Оцените ответ на показанное задание.",
                new(ExerciseRubric.QuestionScopedVersion, kind, [.. criteria]))
        };

    private static LessonAssessment Grade(LessonSnapshot lesson, bool correct) => new("Оценено то, что спрашивалось в задании.", correct)
    {
        Rubric = new([.. lesson.Content!.StructuredRubric!.Criteria.Select(criterion => new RubricCriterionAssessment(criterion.Code,
            correct ? RubricCriterionResult.Met : RubricCriterionResult.NotMet, "Проверен ответ на поставленный вопрос."))])
    };

    private async Task StartRubricExerciseAsync(HttpClient client)
    {
        string[] turns = ["Хочу изучить биржу", "Разобраться в биржевых торгах", "Начинаю с нуля", "Продолжим"];
        for (var index = 0; index < turns.Length; index++) await SendEnglishAsync(client, index + 1, turns[index]);
    }

    [Fact]
    public async Task Post_CorrectionAndExplicitSolutionAcrossRestart_PreservesAttemptsAndMarksAssistedSuccess()
    {
        const string question = "Почему проверка kind сужает union?";
        const string guidance = "Рассмотрите два варианта с разными значениями kind.";
        const string first = "Ветка ничего не исключает.";
        const string success = "Проверка исключает варианты с другими значениями kind.";
        var probe = new Probe("")
        {
            Recognize = input => input.Input switch
            {
                "дай подсказку" => LearningIntent.Clarify,
                "покажи решение" => LearningIntent.RevealSolution,
                _ => LearningIntent.Answer
            },
            Material = new("У общей метки может быть несколько значений.", question)
            {
                Content = new("unions", "rubric-v1", LearningContentKind.StructuredExercise,
                    "Понять сужение", "Описать механизм", "Связать проверку и исключение вариантов.",
                    new(1, ExerciseEvidenceKind.Understanding, [new("mechanism", "Укажите, какие варианты исключаются.")]))
            },
            Assessment = (_, answer) => new LessonAssessment("Обратная связь", answer == success)
            {
                Rubric = new([new("mechanism", answer == success ? RubricCriterionResult.Met : RubricCriterionResult.NotMet,
                    "Проверена связь с исключением вариантов.")]),
                Correction = new(ExerciseCorrectionKind.Example, guidance)
            }
        };

        Guid lessonId;
        using (var host = ConsultationHost(probe))
        using (var client = host.CreateClient())
        {
            string[] turns = ["Хочу понять union types", "Разбирать обработчики TypeScript", "Начинаю с нуля", "Продолжим"];
            for (var index = 0; index < turns.Length; index++) await SendEnglishAsync(client, index + 1, turns[index]);
            await using var db = Open();
            lessonId = (await db.Set<Lesson>().SingleAsync()).Id;
        }
        using (var host = Host(probe, worker: false, publicIngress: true, interceptor: new RejectBoundary("apply"),
            configure: options => { options.Enabled = true; options.DialogueEnabled = true; }))
        using (var client = host.CreateClient())
        {
            using var accepted = await PostPublicAsync(client, 5, "/answer " + first);
            accepted.EnsureSuccessStatusCode();
            using var scope = host.Services.CreateScope();
            var lease = (await scope.ServiceProvider.GetRequiredService<ILearningRequestStore>().ClaimAsync(TimeSpan.FromMinutes(1), default))!;
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                scope.ServiceProvider.GetRequiredService<LearningRequestProcessor>().ProcessAsync(lease, 3, default));
        }
        await using (var db = Open())
        {
            Assert.Null((await db.Set<Lesson>().SingleAsync()).RubricEvaluation);
            await db.Database.ExecuteSqlRawAsync("UPDATE learning_requests SET lease_until = clock_timestamp() - interval '1 second' WHERE status = 2");
        }
        using (var host = ConsultationHost(probe))
        using (var client = host.CreateClient())
        {
            await WaitAllPublicAsync(5);
            await using var db = Open();
            var lesson = await db.Set<Lesson>().SingleAsync();
            Assert.Equal(LessonStatus.AwaitingAnswer, lesson.Status);
            Assert.Equal(first, Assert.Single(lesson.RubricEvaluation!.Corrections).Answer);
            Assert.Empty(await db.Set<LearningEvidence>().ToListAsync());
        }

        using (var host = ConsultationHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 6, "Продолжим");
            await SendEnglishAsync(client, 7, "/answer Варианты сохраняются все.");
            await SendEnglishAsync(client, 8, "дай подсказку");
            await SendEnglishAsync(client, 9, "покажи решение");
            await SendEnglishAsync(client, 10, "покажи решение");
            await SendEnglishAsync(client, 11, "/answer " + success);
            using var duplicate = await PostPublicAsync(client, 11, "/answer " + success);
            duplicate.EnsureSuccessStatusCode();
        }

        await using var result = Open();
        var completed = await result.Set<Lesson>().SingleAsync();
        var evidence = await result.Set<LearningEvidence>().SingleAsync();
        Assert.Equal(lessonId, completed.Id);
        Assert.Equal(question, completed.Question);
        Assert.Equal(2, completed.RubricEvaluation!.Corrections.Length);
        Assert.Equal(first, completed.RubricEvaluation.InitialAnswer);
        Assert.Equal(AnswerAssistance.Assisted, evidence.Assistance);
        Assert.True(evidence.AssessedCorrect);
        Assert.Equal(SkillMastery.Unknown, LearnerSkillState.Evaluate([evidence]).Mastery);
        Assert.Equal(LessonInteractionKind.FullAnswer, Assert.Single(probe.ExplanationKinds));
        Assert.Equal(3, probe.AssessedAnswers.Count);
        Assert.Contains(await result.TelegramOutboxMessages.Select(reply => reply.Text).ToListAsync(),
            text => text.Contains("ещё задание", StringComparison.Ordinal));
        var rollback = await Assert.ThrowsAsync<PostgresException>(() =>
            result.GetService<IMigrator>().MigrateAsync("20261118160000_AddLearningSessions"));
        Assert.Contains("Retain correction and solution history", rollback.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_RubricFollowUpAcrossRestart_CreditsOnlyTheFinalAssessmentOnce()
    {
        const string requirement = "Опишите, почему изменяется тип значения.";
        const string originalAnswer = "Тип меняется.";
        const string clarification = "Проверка kind исключает остальные варианты объединения.";
        var content = new LearningContent("typescript", "rubric-v1", LearningContentKind.StructuredExercise,
            "Объяснить сужение типа", requirement, "Объяснение должно связывать проверку с исключением вариантов.",
            new(1, ExerciseEvidenceKind.Understanding, [new("mechanism", requirement)]));
        var probe = new Probe("")
        {
            Material = new("Проверка общего поля может сузить объединение.", "Что делает проверка kind?") { Content = content },
            Assessment = (lesson, _) => new LessonAssessment("Обратная связь", IsCorrect: lesson.RubricEvaluation is null)
            {
                Rubric = new([new("mechanism", lesson.RubricEvaluation is null
                    ? RubricCriterionResult.Unclear : RubricCriterionResult.Met, "Есть объяснение механизма.")])
            }
        };

        Guid originalPrompt;
        using (var host = ConsultationHost(probe))
        using (var client = host.CreateClient())
        {
            string[] turns = ["Хочу изучить TypeScript", "Проверять типы в React", "Начинаю с нуля", "Продолжим"];
            for (var index = 0; index < turns.Length; index++) await SendEnglishAsync(client, index + 1, turns[index]);
            await using var db = Open();
            originalPrompt = (await db.Set<DialogueContext>().AsNoTracking().SingleAsync()).PromptId;
            Assert.Contains(await db.TelegramOutboxMessages.Select(reply => reply.Text).ToListAsync(),
                text => text.Contains(requirement, StringComparison.Ordinal));
            await SendEnglishAsync(client, 5, "/answer " + originalAnswer);
            var lesson = await db.Set<Lesson>().SingleAsync();
            Assert.Equal(LessonStatus.AwaitingAnswer, lesson.Status);
            Assert.Equal(requirement, lesson.ResponseQuestion);
            Assert.NotEqual(originalPrompt, (await db.Set<DialogueContext>().SingleAsync()).PromptId);
            Assert.Empty(await db.Set<LearningEvidence>().ToListAsync());
        }

        using (var host = ConsultationHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 6, "Продолжим");
            await SendEnglishAsync(client, 7, "/answer " + clarification);
            using var duplicate = await PostPublicAsync(client, 7, "/answer " + clarification);
            duplicate.EnsureSuccessStatusCode();
        }

        await using var result = Open();
        var evidence = await result.Set<LearningEvidence>().SingleAsync();
        var completed = await result.Set<Lesson>().SingleAsync();
        var operation = await result.Set<LearningRequest>().SingleAsync(request => request.SourceUpdateId == 7);
        Assert.True(evidence.AssessedCorrect);
        Assert.Equal(originalAnswer, evidence.RubricEvidence!.Evaluation.InitialAnswer);
        Assert.Equal(operation.Id, evidence.RubricEvidence.Evaluation.OperationId);
        Assert.Equal(clarification, completed.Answer);
        Assert.Equal(content.Version, evidence.Content!.Version);
        Assert.Equal(SkillMastery.Developing, LearnerSkillState.Evaluate([evidence]).Mastery);
        Assert.Equal(2, probe.AssessedAnswers.Count);
        Assert.False(result.Database.HasPendingModelChanges());
    }
}
