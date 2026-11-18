using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
public partial class OwnDayDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder
            .HasAnnotation("ProductVersion", "10.0.12")
            .HasAnnotation("Relational:MaxIdentifierLength", 63);

        NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

        modelBuilder.Entity("OwnDay.Domain.Dialogue.DialogueContext", b =>
        {
            b.Property<long>("UserId").ValueGeneratedNever().HasColumnType("bigint").HasColumnName("user_id");
            b.Property<long>("Revision").IsConcurrencyToken().HasColumnType("bigint").HasColumnName("revision");
            b.Property<int>("SchemaVersion").ValueGeneratedOnAdd().HasDefaultValue(5).HasColumnType("integer").HasColumnName("schema_version");
            b.Property<int>("Phase").HasColumnType("integer").HasColumnName("phase");
            b.Property<int>("ExpectedResponse").HasColumnType("integer").HasColumnName("expected_response");
            b.Property<Guid>("PromptId").HasColumnType("uuid").HasColumnName("prompt_id");
            b.Property<Guid?>("TargetLessonId").HasColumnType("uuid").HasColumnName("target_lesson_id");
            b.Property<string>("QuestionFingerprint").HasMaxLength(64).HasColumnType("character varying(64)").HasColumnName("question_fingerprint");
            b.Property<DateTimeOffset?>("PromptOpenedAt").HasColumnType("timestamp with time zone").HasColumnName("prompt_opened_at");
            b.Property<DateTimeOffset>("LastActivityAt").HasColumnType("timestamp with time zone").HasColumnName("last_activity_at");
            b.Property<DateTimeOffset?>("InterruptedAt").HasColumnType("timestamp with time zone").HasColumnName("interrupted_at");
            b.Property<long>("InterruptedSourceOrder").HasColumnType("bigint").HasColumnName("interrupted_source_order");
            b.Property<long>("ResponseAfterSourceOrder").HasColumnType("bigint").HasColumnName("response_after_source_order");
            b.Property<Guid?>("DeferredGoalRequestId").HasColumnType("uuid").HasColumnName("deferred_goal_request_id");
            b.Property<string>("PendingClarification").HasColumnType("jsonb").HasColumnName("pending_clarification");
            b.Property<string>("PendingConfirmation").HasColumnType("jsonb").HasColumnName("pending_confirmation");
            b.Property<string>("Consultation").HasColumnType("jsonb").HasColumnName("consultation");
            b.Property<uint>("TargetVersion").IsConcurrencyToken().ValueGeneratedOnAddOrUpdate().HasColumnType("xid").HasColumnName("xmin");
            b.HasKey("UserId");
            b.ToTable("dialogue_contexts", null, table => table.HasCheckConstraint("CK_dialogue_contexts_identity", "user_id > 0 AND revision > 0 AND schema_version > 0"));
        });

        modelBuilder.Entity("OwnDay.Application.Inbox.Observability.InboxOperationEvent", b =>
            {
                b.Property<Guid>("Id")
                    .ValueGeneratedOnAdd()
                    .HasColumnType("uuid");

                b.Property<DateTime>("At")
                    .HasColumnType("timestamp with time zone");

                b.Property<string>("Code")
                    .IsRequired()
                    .HasMaxLength(50)
                    .HasColumnType("character varying(50)");

                b.Property<long>("InboxItemId")
                    .HasColumnType("bigint");

                b.Property<int?>("Kind")
                    .HasColumnType("integer");

                b.Property<long?>("TargetId")
                    .HasColumnType("bigint");

                b.Property<long>("UserId")
                    .HasColumnType("bigint");

                b.HasKey("Id");

                b.HasIndex("Kind", "TargetId")
                    .IsUnique()
                    .HasFilter("\"Code\" = 'created'");

                b.HasIndex("UserId", "At");

                b.HasIndex("UserId", "Code", "InboxItemId");

                b.ToTable("inbox_operation_events", (string)null);
            });

        modelBuilder.Entity("OwnDay.Application.Inbox.Observability.OpenAIRequestAttempt", b =>
            {
                b.Property<Guid>("Id")
                    .ValueGeneratedOnAdd()
                    .HasColumnType("uuid");

                b.Property<Guid?>("AnalysisId")
                    .HasColumnType("uuid");

                b.Property<int?>("CachedInputTokens")
                    .HasColumnType("integer");

                b.Property<decimal?>("CachedInputUsdPerMillion")
                    .HasPrecision(20, 8)
                    .HasColumnType("numeric(20,8)");

                b.Property<long>("ChargedTokens")
                    .HasColumnType("bigint");

                b.Property<Guid?>("LearningProviderAttemptId").HasColumnType("uuid");
                b.Property<decimal?>("ReservedUsd").HasPrecision(20, 12).HasColumnType("numeric(20,12)");
                b.Property<decimal?>("ChargedUsd").HasPrecision(20, 12).HasColumnType("numeric(20,12)");
                b.Property<string>("PromptVersion").HasMaxLength(100).HasColumnType("character varying(100)");
                b.Property<string>("SchemaVersion").HasMaxLength(100).HasColumnType("character varying(100)");
                b.Property<int?>("MaxOutputTokens").HasColumnType("integer");

                b.Property<string>("Code")
                    .IsRequired()
                    .HasMaxLength(50)
                    .HasColumnType("character varying(50)");

                b.Property<decimal?>("EstimatedUsd")
                    .HasPrecision(20, 12)
                    .HasColumnType("numeric(20,12)");

                b.Property<DateTime?>("FinishedAt")
                    .HasColumnType("timestamp with time zone");

                b.Property<int?>("InputTokens")
                    .HasColumnType("integer");

                b.Property<decimal?>("InputUsdPerMillion")
                    .HasPrecision(20, 8)
                    .HasColumnType("numeric(20,8)");

                b.Property<string>("Model")
                    .IsRequired()
                    .HasMaxLength(200)
                    .HasColumnType("character varying(200)");

                b.Property<int?>("OutputTokens")
                    .HasColumnType("integer");

                b.Property<decimal?>("OutputUsdPerMillion")
                    .HasPrecision(20, 8)
                    .HasColumnType("numeric(20,8)");

                b.Property<string>("ProviderRequestId")
                    .HasMaxLength(200)
                    .HasColumnType("character varying(200)");

                b.Property<long>("ReservedTokens")
                    .HasColumnType("bigint");

                b.Property<int>("Sequence")
                    .HasColumnType("integer");

                b.Property<DateTime>("StartedAt")
                    .HasColumnType("timestamp with time zone");

                b.Property<long>("UserId")
                    .HasColumnType("bigint");

                b.HasKey("Id");

                b.HasIndex("AnalysisId", "StartedAt");

                b.HasIndex("UserId", "StartedAt");

                b.HasIndex("LearningProviderAttemptId").IsUnique();
                b.HasIndex("StartedAt");
                b.ToTable("openai_request_attempts", null, table => table.HasCheckConstraint("CK_openai_request_attempts_source",
                    "(\"AnalysisId\" IS NOT NULL AND \"LearningProviderAttemptId\" IS NULL) OR (\"AnalysisId\" IS NULL AND \"LearningProviderAttemptId\" IS NOT NULL)"));
            });

        modelBuilder.Entity("OwnDay.Application.Inbox.Observability.OpenAIRequestAttempt", b =>
            {
                b.HasOne("OwnDay.Application.Inbox.Analysis.InboxAnalysis", null)
                    .WithMany()
                    .HasForeignKey("AnalysisId")
                    .OnDelete(DeleteBehavior.Restrict);
                b.HasOne("OwnDay.Application.Learning.Requests.LearningProviderAttempt", null)
                    .WithMany().HasForeignKey("LearningProviderAttemptId")
                    .HasConstraintName("FK_openai_attempts_learning_attempt").OnDelete(DeleteBehavior.Restrict);
            });

        modelBuilder.Entity("OwnDay.Application.Inbox.InboxDraft", b =>
        {
            b.Property<long>("UserId").ValueGeneratedNever().HasColumnType("bigint").HasColumnName("user_id");
            b.Property<long>("InboxItemId").HasColumnType("bigint").HasColumnName("inbox_item_id");
            b.Property<int>("Step").HasColumnType("integer").HasColumnName("step");
            b.Property<int?>("TargetKind").HasColumnType("integer").HasColumnName("target_kind");
            b.Property<string>("Text").HasColumnType("text").HasColumnName("text");
            b.Property<string>("Source").HasColumnType("text").HasColumnName("source");
            b.Property<long?>("ProjectId").HasColumnType("bigint").HasColumnName("project_id");
            b.Property<long>("Version").IsConcurrencyToken().HasColumnType("bigint").HasColumnName("version");
            b.Property<DateTime>("ExpiresAt").HasColumnType("timestamp with time zone").HasColumnName("expires_at");
            b.HasKey("UserId");
            b.HasIndex("ExpiresAt");
            b.ToTable("telegram_inbox_drafts");
        });

        modelBuilder.Entity("OwnDay.Infrastructure.Persistence.TelegramUserLanguage", b =>
        {
            b.Property<long>("UserId").ValueGeneratedNever().HasColumnType("bigint").HasColumnName("user_id");
            b.Property<string>("Locale").HasMaxLength(2).HasColumnType("character varying(2)").HasColumnName("locale");
            b.Property<DateTime?>("SelectionExpiresAt").HasColumnType("timestamp with time zone").HasColumnName("selection_expires_at");
            b.HasKey("UserId");
            b.ToTable("telegram_user_languages", t =>
            {
                t.HasCheckConstraint("CK_telegram_user_languages_user", "user_id > 0");
                t.HasCheckConstraint("CK_telegram_user_languages_locale", "locale IS NULL OR locale IN ('en', 'ru')");
            });
        });

        modelBuilder.Entity("OwnDay.Domain.Inbox.InboxItem", b =>
        {
                b.Property<bool>("ManualProcessingOnly").HasColumnType("boolean").HasColumnName("manual_processing_only");

            b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("bigint").HasColumnName("id");
            NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<long>("Id"));
            b.Property<DateTime>("CapturedAt").HasColumnType("timestamp with time zone").HasColumnName("captured_at");
            b.Property<DateTime?>("DiscardedAt").HasColumnType("timestamp with time zone").HasColumnName("discarded_at");
            b.Property<string>("OriginalText").IsRequired().HasColumnType("text").HasColumnName("original_text");
            b.Property<DateTime?>("ProcessedAt").HasColumnType("timestamp with time zone").HasColumnName("processed_at");
            b.Property<int>("Status").IsConcurrencyToken().HasColumnType("integer").HasColumnName("status");
            b.Property<long?>("TargetId").HasColumnType("bigint").HasColumnName("target_id");
            b.Property<int?>("TargetKind").HasColumnType("integer").HasColumnName("target_kind");
            b.Property<long>("UserId").HasColumnType("bigint").HasColumnName("user_id");
            b.HasKey("Id");
            b.HasAlternateKey("Id", "UserId");
            b.HasIndex("UserId", "Status", "CapturedAt", "Id");
            b.ToTable("inbox_items", t => t.HasCheckConstraint("CK_inbox_items_lifecycle",
                "(status = 0 AND processed_at IS NULL AND discarded_at IS NULL AND target_kind IS NULL AND target_id IS NULL) OR " +
                "(status = 1 AND processed_at IS NOT NULL AND discarded_at IS NULL AND target_kind IS NOT NULL AND target_kind BETWEEN 0 AND 4 AND target_id IS NOT NULL AND target_id > 0 AND processed_at >= captured_at) OR " +
                "(status = 2 AND processed_at IS NULL AND discarded_at IS NOT NULL AND target_kind IS NULL AND target_id IS NULL AND discarded_at >= captured_at)"));
        });

        modelBuilder.Entity("OwnDay.Domain.Actions.Action", b =>
        {
            b.Property<long>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("bigint")
                .HasColumnName("id");

            NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<long>("Id"));

            b.Property<DateTime?>("CompletedAt")
                .HasColumnType("timestamp with time zone")
                .HasColumnName("completed_at");

            b.Property<DateTime>("CreatedAt")
                .HasColumnType("timestamp with time zone")
                .HasColumnName("created_at");

            b.Property<long?>("ProjectId")
                .HasColumnType("bigint")
                .HasColumnName("project_id");

            b.Property<int>("Status")
                .IsConcurrencyToken()
                .HasColumnType("integer")
                .HasColumnName("status");

            b.Property<string>("Title")
                .IsRequired()
                .HasMaxLength(200)
                .HasColumnType("character varying(200)")
                .HasColumnName("title");

            b.Property<long>("UserId")
                .HasColumnType("bigint")
                .HasColumnName("user_id");

            b.HasKey("Id");

            b.HasIndex("ProjectId", "UserId");

            b.HasIndex("UserId", "Status", "CreatedAt", "Id");

            b.ToTable("actions", t => t.HasCheckConstraint(
                "CK_actions_status_completed_at",
                "(status = 0 AND completed_at IS NULL) OR (status = 1 AND completed_at IS NOT NULL)"));
        });

        modelBuilder.Entity("OwnDay.Domain.Projects.Project", b =>
        {
            b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("bigint").HasColumnName("id");
            NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<long>("Id"));
            b.Property<DateTime?>("CompletedAt").HasColumnType("timestamp with time zone").HasColumnName("completed_at");
            b.Property<DateTime>("CreatedAt").HasColumnType("timestamp with time zone").HasColumnName("created_at");
            b.Property<int>("Status").IsConcurrencyToken().HasColumnType("integer").HasColumnName("status");
            b.Property<string>("Title").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)").HasColumnName("title");
            b.Property<long>("UserId").HasColumnType("bigint").HasColumnName("user_id");
            b.HasKey("Id");
            b.HasAlternateKey("Id", "UserId");
            b.HasIndex("UserId", "Status", "CreatedAt", "Id");
            b.ToTable("projects", t => t.HasCheckConstraint("CK_projects_status_completed_at",
                "(status = 0 AND completed_at IS NULL) OR (status = 1 AND completed_at IS NOT NULL)"));
        });

        modelBuilder.Entity("OwnDay.Domain.SomedayMaybes.SomedayMaybe", b =>
        {
            b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("bigint").HasColumnName("id");
            NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<long>("Id"));
            b.Property<DateTime?>("ArchivedAt").HasColumnType("timestamp with time zone").HasColumnName("archived_at");
            b.Property<DateTime>("CreatedAt").HasColumnType("timestamp with time zone").HasColumnName("created_at");
            b.Property<long?>("ProjectId").HasColumnType("bigint").HasColumnName("project_id");
            b.Property<int>("Status").IsConcurrencyToken().HasColumnType("integer").HasColumnName("status");
            b.Property<string>("Text").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)").HasColumnName("text");
            b.Property<long>("UserId").HasColumnType("bigint").HasColumnName("user_id");
            b.HasKey("Id");
            b.HasIndex("ProjectId", "UserId");
            b.HasIndex("UserId", "Status", "CreatedAt", "Id");
            b.ToTable("someday_maybes", t => t.HasCheckConstraint("CK_someday_maybes_status_archived_at",
                "(status = 0 AND archived_at IS NULL) OR (status = 1 AND archived_at IS NOT NULL)"));
        });

        modelBuilder.Entity("OwnDay.Domain.References.Reference", b =>
        {
            b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("bigint").HasColumnName("id");
            NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<long>("Id"));
            b.Property<DateTime?>("ArchivedAt").HasColumnType("timestamp with time zone").HasColumnName("archived_at");
            b.Property<DateTime>("CreatedAt").HasColumnType("timestamp with time zone").HasColumnName("created_at");
            b.Property<long?>("ProjectId").HasColumnType("bigint").HasColumnName("project_id");
            b.Property<int>("Status").IsConcurrencyToken().HasColumnType("integer").HasColumnName("status");
            b.Property<string>("Text").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)").HasColumnName("text");
            b.Property<long>("UserId").HasColumnType("bigint").HasColumnName("user_id");
            b.HasKey("Id");
            b.HasIndex("ProjectId", "UserId");
            b.HasIndex("UserId", "Status", "CreatedAt", "Id");
            b.ToTable("references", t => t.HasCheckConstraint("CK_references_status_archived_at",
                "(status = 0 AND archived_at IS NULL) OR (status = 1 AND archived_at IS NOT NULL)"));
        });

        modelBuilder.Entity("OwnDay.Domain.WaitingFors.WaitingFor", b =>
        {
            b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("bigint").HasColumnName("id");
            NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<long>("Id"));
            b.Property<DateTime>("CreatedAt").HasColumnType("timestamp with time zone").HasColumnName("created_at");
            b.Property<string>("Description").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)").HasColumnName("description");
            b.Property<long?>("ProjectId").HasColumnType("bigint").HasColumnName("project_id");
            b.Property<DateTime?>("ResolvedAt").HasColumnType("timestamp with time zone").HasColumnName("resolved_at");
            b.Property<string>("Source").HasMaxLength(200).HasColumnType("character varying(200)").HasColumnName("source");
            b.Property<int>("Status").IsConcurrencyToken().HasColumnType("integer").HasColumnName("status");
            b.Property<long>("UserId").HasColumnType("bigint").HasColumnName("user_id");
            b.Property<DateTime>("WaitingSince").HasColumnType("timestamp with time zone").HasColumnName("waiting_since");
            b.HasKey("Id");
            b.HasIndex("ProjectId", "UserId");
            b.HasIndex("UserId", "Status", "CreatedAt", "Id");
            b.ToTable("waiting_fors", t => t.HasCheckConstraint("CK_waiting_fors_status_resolved_at",
                "(status = 0 AND resolved_at IS NULL) OR (status = 1 AND resolved_at IS NOT NULL)"));
        });

        modelBuilder.Entity("OwnDay.Domain.Actions.Action", b =>
        {
            b.HasOne("OwnDay.Domain.Projects.Project", null)
                .WithMany()
                .HasForeignKey("ProjectId", "UserId")
                .HasPrincipalKey("Id", "UserId");
        });

        modelBuilder.Entity("OwnDay.Domain.SomedayMaybes.SomedayMaybe", b =>
        {
            b.HasOne("OwnDay.Domain.Projects.Project", null)
                .WithMany()
                .HasForeignKey("ProjectId", "UserId")
                .HasPrincipalKey("Id", "UserId");
        });

        modelBuilder.Entity("OwnDay.Domain.References.Reference", b =>
        {
            b.HasOne("OwnDay.Domain.Projects.Project", null)
                .WithMany()
                .HasForeignKey("ProjectId", "UserId")
                .HasPrincipalKey("Id", "UserId");
        });

        modelBuilder.Entity("OwnDay.Domain.WaitingFors.WaitingFor", b =>
        {
            b.HasOne("OwnDay.Domain.Projects.Project", null)
                .WithMany()
                .HasForeignKey("ProjectId", "UserId")
                .HasPrincipalKey("Id", "UserId");
        });

        modelBuilder.Entity("OwnDay.Infrastructure.Persistence.ProcessedTelegramUpdate", b =>
        {
            b.Property<long>("UpdateId")
                .HasColumnType("bigint")
                .HasColumnName("update_id");

            b.Property<DateTime>("ReceivedAt")
                .HasColumnType("timestamp with time zone")
                .HasColumnName("received_at");

            b.Property<DateTime?>("ProcessedAt")
                .HasColumnType("timestamp with time zone")
                .HasColumnName("processed_at");

            b.HasKey("UpdateId");

            b.ToTable("processed_telegram_updates");
        });

        modelBuilder.Entity("OwnDay.Infrastructure.Persistence.TelegramOutboxMessage", b =>
        {
            b.Property<string>("ReplyMarkupJson")
                .HasColumnType("text")
                .HasColumnName("reply_markup_json");

            b.Property<Guid>("Id")
                .HasColumnType("uuid")
                .HasColumnName("id");

            b.Property<int>("AttemptCount")
                .HasColumnType("integer")
                .HasColumnName("attempt_count");

            b.Property<long>("ChatId")
                .HasColumnType("bigint")
                .HasColumnName("chat_id");

            b.Property<DateTime>("CreatedAt")
                .HasColumnType("timestamp with time zone")
                .HasColumnName("created_at");

            b.Property<string>("LastError")
                .HasColumnType("text")
                .HasColumnName("last_error");

            b.Property<DateTime>("NextAttemptAt")
                .HasColumnType("timestamp with time zone")
                .HasColumnName("next_attempt_at");

            b.Property<DateTime?>("SentAt")
                .HasColumnType("timestamp with time zone")
                .HasColumnName("sent_at");

            b.Property<int>("Status")
                .HasColumnType("integer")
                .HasColumnName("status");

            b.Property<string>("Text")
                .IsRequired()
                .HasColumnType("text")
                .HasColumnName("text");

            b.Property<Guid?>("StudyInvitationId")
                .HasColumnType("uuid")
                .HasColumnName("study_invitation_id");

            b.HasKey("Id");

            b.HasIndex("Status", "NextAttemptAt");

            b.HasIndex("Status", "SentAt");

            b.ToTable("telegram_outbox_messages");
        });
        modelBuilder.Entity("OwnDay.Application.Inbox.Analysis.InboxAnalysis", b =>
            {
                b.Property<Guid>("Id")
                    .HasColumnType("uuid")
                    .HasColumnName("id");

                b.Property<DateTime?>("CompletedAt")
                    .HasColumnType("timestamp with time zone")
                    .HasColumnName("completed_at");

                b.Property<string>("ContextFingerprint")
                    .IsRequired()
                    .HasMaxLength(64)
                    .HasColumnType("character varying(64)")
                    .HasColumnName("context_fingerprint");

                b.Property<string>("ContextJson")
                    .IsRequired()
                    .HasColumnType("jsonb")
                    .HasColumnName("context");

                b.Property<DateTime>("CreatedAt")
                    .HasColumnType("timestamp with time zone")
                    .HasColumnName("created_at");

                b.Property<string>("DiagnosticsJson")
                    .IsRequired()
                    .HasColumnType("jsonb")
                    .HasColumnName("diagnostics");

                b.Property<long>("InboxItemId")
                    .HasColumnType("bigint")
                    .HasColumnName("inbox_item_id");

                b.Property<string>("InputFingerprint")
                    .IsRequired()
                    .HasMaxLength(64)
                    .HasColumnType("character varying(64)")
                    .HasColumnName("input_fingerprint");

                b.Property<string>("Model")
                    .IsRequired()
                    .HasMaxLength(200)
                    .HasColumnType("character varying(200)")
                    .HasColumnName("model");

                b.Property<string>("PromptVersion")
                    .IsRequired()
                    .HasMaxLength(50)
                    .HasColumnType("character varying(50)")
                    .HasColumnName("prompt_version");

                b.Property<string>("ReasoningEffort")
                    .HasMaxLength(20)
                    .HasColumnType("character varying(20)")
                    .HasColumnName("reasoning_effort");

                b.Property<int?>("MaxOutputTokens")
                    .HasColumnType("integer")
                    .HasColumnName("max_output_tokens");

                b.Property<int?>("TimeoutSeconds")
                    .HasColumnType("integer")
                    .HasColumnName("timeout_seconds");

                b.Property<string>("SchemaVersion")
                    .IsRequired()
                    .HasMaxLength(50)
                    .HasColumnType("character varying(50)")
                    .HasColumnName("schema_version");

                b.Property<int>("Status")
                    .IsConcurrencyToken()
                    .HasColumnType("integer")
                    .HasColumnName("status");

                b.Property<long>("UserId")
                    .HasColumnType("bigint")
                    .HasColumnName("user_id");

                b.Property<int>("Version")
                    .HasColumnType("integer")
                    .HasColumnName("version");

                b.HasKey("Id");

                b.HasIndex("InboxItemId", "UserId");

                b.HasIndex("InboxItemId", "Version")
                    .IsUnique();

                b.HasIndex("UserId", "InboxItemId");

                b.ToTable("inbox_analyses", null, t =>
                    {
                        t.HasCheckConstraint("CK_inbox_analyses_lifecycle", "(status = 0 AND completed_at IS NULL) OR (status BETWEEN 1 AND 3 AND completed_at IS NOT NULL AND completed_at >= created_at)");

                        t.HasCheckConstraint("CK_inbox_analyses_version", "version > 0");
                    });
            });

        modelBuilder.Entity("OwnDay.Application.Inbox.Analysis.InboxAnalysisProposal", b =>
            {
                b.Property<Guid>("Id")
                    .HasColumnType("uuid")
                    .HasColumnName("id");

                b.Property<Guid>("AnalysisId")
                    .HasColumnType("uuid")
                    .HasColumnName("analysis_id");

                b.Property<int>("Kind")
                    .HasColumnType("integer")
                    .HasColumnName("kind");

                b.Property<string>("PayloadJson")
                    .IsRequired()
                    .HasColumnType("jsonb")
                    .HasColumnName("payload");

                b.Property<int>("Position")
                    .HasColumnType("integer")
                    .HasColumnName("position");

                b.Property<string>("Title")
                    .IsRequired()
                    .HasMaxLength(200)
                    .HasColumnType("character varying(200)")
                    .HasColumnName("title");

                b.HasKey("Id");

                b.HasIndex("AnalysisId", "Position")
                    .IsUnique();

                b.ToTable("inbox_analysis_proposals", null, t =>
                    {
                        t.HasCheckConstraint("CK_inbox_analysis_proposals_kind", "kind BETWEEN 0 AND 4");

                        t.HasCheckConstraint("CK_inbox_analysis_proposals_position", "position >= 0 AND position < 20");
                    });
            });

        modelBuilder.Entity("OwnDay.Application.Inbox.Analysis.InboxAnalysis", b =>
            {
                b.HasOne("OwnDay.Domain.Inbox.InboxItem", null)
                    .WithMany()
                    .HasForeignKey("InboxItemId", "UserId")
                    .HasPrincipalKey("Id", "UserId")
                    .OnDelete(DeleteBehavior.Restrict)
                    .IsRequired();
            });

        modelBuilder.Entity("OwnDay.Application.Inbox.Analysis.InboxAnalysisProposal", b =>
            {
                b.HasOne("OwnDay.Application.Inbox.Analysis.InboxAnalysis", null)
                    .WithMany("Proposals")
                    .HasForeignKey("AnalysisId")
                    .OnDelete(DeleteBehavior.Cascade)
                    .IsRequired();
            });

        modelBuilder.Entity("OwnDay.Application.Inbox.Analysis.InboxAnalysis", b =>
            {
                b.Navigation("Proposals");
            });

        modelBuilder.Entity("OwnDay.Application.Inbox.Analysis.InboxAnalysisJob", b =>
            {
                b.Property<Guid>("Id")
                    .HasColumnType("uuid")
                    .HasColumnName("id");

                b.Property<Guid?>("AnalysisId")
                    .HasColumnType("uuid")
                    .HasColumnName("analysis_id");

                b.Property<int>("AttemptCount")
                    .HasColumnType("integer")
                    .HasColumnName("attempt_count");

                b.Property<DateTime>("AvailableAt")
                    .HasColumnType("timestamp with time zone")
                    .HasColumnName("available_at");

                b.Property<DateTime>("CreatedAt")
                    .HasColumnType("timestamp with time zone")
                    .HasColumnName("created_at");

                b.Property<long>("InboxItemId")
                    .HasColumnType("bigint")
                    .HasColumnName("inbox_item_id");

                b.Property<string>("LastCode")
                    .HasMaxLength(50)
                    .HasColumnType("character varying(50)")
                    .HasColumnName("last_code");

                b.Property<Guid?>("LeaseToken")
                    .HasColumnType("uuid")
                    .HasColumnName("lease_token");

                b.Property<DateTime?>("LeaseUntil")
                    .HasColumnType("timestamp with time zone")
                    .HasColumnName("lease_until");

                b.Property<int>("Status")
                    .HasColumnType("integer")
                    .HasColumnName("status");

                b.Property<long>("UserId")
                    .HasColumnType("bigint")
                    .HasColumnName("user_id");

                b.HasKey("Id");

                b.HasIndex("AnalysisId")
                    .IsUnique();

                b.HasIndex("InboxItemId")
                    .IsUnique();

                b.HasIndex("AvailableAt", "Id")
                    .HasFilter("status IN (0, 1)");

                b.HasIndex("InboxItemId", "UserId");

                b.ToTable("inbox_analysis_jobs", null, t =>
                    {
                        t.HasCheckConstraint("CK_inbox_analysis_jobs_attempts", "attempt_count >= 0");

                        t.HasCheckConstraint("CK_inbox_analysis_jobs_lifecycle", "(status = 1 AND lease_token IS NOT NULL AND lease_until IS NOT NULL) OR (status IN (0, 2, 3, 4) AND lease_token IS NULL AND lease_until IS NULL)");
                    });
            });

        modelBuilder.Entity("OwnDay.Application.Inbox.Analysis.InboxAnalysisJob", b =>
            {
                b.HasOne("OwnDay.Application.Inbox.Analysis.InboxAnalysis", null)
                    .WithMany()
                    .HasForeignKey("AnalysisId")
                    .OnDelete(DeleteBehavior.Restrict);

                b.HasOne("OwnDay.Domain.Inbox.InboxItem", "Item")
                    .WithMany()
                    .HasForeignKey("InboxItemId", "UserId")
                    .HasPrincipalKey("Id", "UserId")
                    .OnDelete(DeleteBehavior.Restrict)
                    .IsRequired();

                b.Navigation("Item");
            });
        modelBuilder.Entity("OwnDay.Application.Inbox.InboxAutomaticApplication", b =>
            {
                b.Property<DateTime?>("UndoneAt").HasColumnType("timestamp with time zone").HasColumnName("undone_at");
                b.Property<string>("UndoStateJson").IsRequired().HasColumnType("jsonb").HasColumnName("undo_state");

                b.Property<int?>("ConfirmationOutcome")
                    .HasColumnType("integer")
                    .HasColumnName("confirmation_outcome");

                b.Property<string>("ConfirmedTargetsJson")
                    .IsRequired()
                    .HasColumnType("jsonb")
                    .HasColumnName("confirmed_targets");

                b.Property<DateTime?>("ResolvedAt")
                    .HasColumnType("timestamp with time zone")
                    .HasColumnName("resolved_at");

                b.Property<Guid>("AnalysisId")
                    .HasColumnType("uuid")
                    .HasColumnName("analysis_id");

                b.Property<string>("DecisionJson")
                    .IsRequired()
                    .HasColumnType("jsonb")
                    .HasColumnName("decision");

                b.Property<DateTime>("EvaluatedAt")
                    .HasColumnType("timestamp with time zone")
                    .HasColumnName("evaluated_at");

                b.Property<long>("InboxItemId")
                    .HasColumnType("bigint")
                    .HasColumnName("inbox_item_id");

                b.Property<string>("PolicyVersion")
                    .IsRequired()
                    .HasMaxLength(50)
                    .HasColumnType("character varying(50)")
                    .HasColumnName("policy_version");

                b.Property<string>("ProposalsJson")
                    .IsRequired()
                    .HasColumnType("jsonb")
                    .HasColumnName("proposals");

                b.Property<int>("Route")
                    .HasColumnType("integer")
                    .HasColumnName("route");

                b.Property<string>("TargetsJson")
                    .IsRequired()
                    .HasColumnType("jsonb")
                    .HasColumnName("targets");

                b.Property<long>("UserId")
                    .HasColumnType("bigint")
                    .HasColumnName("user_id");

                b.HasKey("AnalysisId");

                b.HasIndex("InboxItemId", "UserId");

                b.ToTable("inbox_automatic_applications", (string)null);
            });

        modelBuilder.Entity("OwnDay.Application.Inbox.InboxAutomaticApplication", b =>
            {
                b.HasOne("OwnDay.Application.Inbox.Analysis.InboxAnalysis", null)
                    .WithMany()
                    .HasForeignKey("AnalysisId")
                    .OnDelete(DeleteBehavior.Restrict)
                    .IsRequired();

                b.HasOne("OwnDay.Domain.Inbox.InboxItem", null)
                    .WithMany()
                    .HasForeignKey("InboxItemId", "UserId")
                    .HasPrincipalKey("Id", "UserId")
                    .OnDelete(DeleteBehavior.Restrict)
                    .IsRequired();
            });

        modelBuilder.Entity("OwnDay.Domain.Inbox.InboxItem", b =>
            {
                b.OwnsMany("OwnDay.Domain.Inbox.InboxTarget", "Targets", b1 =>
                    {
                        b1.Property<long>("InboxItemId")
                            .HasColumnType("bigint")
                            .HasColumnName("inbox_item_id");

                        b1.Property<int>("Kind")
                            .HasColumnType("integer")
                            .HasColumnName("kind");

                        b1.Property<long>("TargetId")
                            .HasColumnType("bigint")
                            .HasColumnName("target_id");

                        b1.HasKey("InboxItemId", "Kind", "TargetId");

                        b1.ToTable("inbox_targets", null, t =>
                            {
                                t.HasCheckConstraint("CK_inbox_targets_identity", "kind BETWEEN 0 AND 4 AND target_id > 0");
                            });

                        b1.WithOwner()
                            .HasForeignKey("InboxItemId");
                    });

                b.Navigation("Targets");
            });
        modelBuilder.Entity("OwnDay.Infrastructure.Persistence.TelegramInboxDestination", b =>
            {
                b.Property<long>("InboxItemId")
                    .HasColumnType("bigint")
                    .HasColumnName("inbox_item_id");

                b.Property<long>("ChatId")
                    .HasColumnType("bigint")
                    .HasColumnName("chat_id");

                b.Property<string>("Locale")
                    .IsRequired()
                    .HasMaxLength(2)
                    .HasColumnType("character varying(2)")
                    .HasColumnName("locale");

                b.HasKey("InboxItemId");

                b.ToTable("telegram_inbox_destinations", (string)null);
            });

        modelBuilder.Entity("OwnDay.Infrastructure.Persistence.TelegramInboxDestination", b =>
            {
                b.HasOne("OwnDay.Domain.Inbox.InboxItem", null)
                    .WithMany()
                    .HasForeignKey("InboxItemId")
                    .OnDelete(DeleteBehavior.Cascade)
                    .IsRequired();
            });
        modelBuilder.Entity("OwnDay.Domain.Learning.ReviewSchedule", b =>
            {
                b.Property<Guid>("SkillId")
                    .HasColumnType("uuid");

                b.Property<DateTime>("DueAt")
                    .HasColumnType("timestamp with time zone");

                b.Property<Guid>("LastLessonId")
                    .HasColumnType("uuid");

                b.Property<DateTimeOffset>("LastObservedAt")
                    .HasColumnType("timestamp with time zone");

                b.Property<int>("SuccessfulReviews")
                    .HasColumnType("integer");

                b.Property<long>("UserId")
                    .HasColumnType("bigint");

                b.HasKey("SkillId");

                b.HasIndex("SkillId", "UserId");

                b.HasIndex("UserId", "DueAt", "SkillId");

                b.ToTable("learning_reviews", (string)null);
            });

        modelBuilder.Entity("OwnDay.Domain.Learning.Skill", b =>
            {
                b.Property<Guid>("Id")
                    .HasColumnType("uuid");
                b.Property<string>("Description")
                    .IsRequired()
                    .HasMaxLength(1200)
                    .HasColumnType("character varying(1200)");
                b.Property<string>("Title")
                    .IsRequired()
                    .HasMaxLength(500)
                    .HasColumnType("character varying(500)");
                b.Property<long>("UserId")
                    .HasColumnType("bigint");
                b.HasKey("Id");
                b.HasIndex("UserId");
                b.ToTable("learning_skills", (string)null);
            });

        modelBuilder.Entity("OwnDay.Domain.Learning.LearningEvidence", b =>
            {
                b.Property<bool?>("AiAssessedCorrect")
                    .HasColumnType("boolean");

                b.Property<int>("AssessmentStatus")
                    .HasColumnType("integer");

                b.Property<bool?>("DeterministicIsCorrect")
                    .HasColumnType("boolean");

                b.Property<bool>("IsReview")
                    .HasColumnType("boolean");
                b.Property<Guid>("LessonId")
                    .HasColumnType("uuid");
                b.Property<string>("Answer")
                    .IsRequired()
                    .HasMaxLength(3000)
                    .HasColumnType("character varying(3000)");
                b.Property<bool>("AssessedCorrect")
                    .HasColumnType("boolean");
                b.Property<string>("AssessmentFeedback")
                    .IsRequired()
                    .HasMaxLength(3000)
                    .HasColumnType("character varying(3000)");
                b.Property<string>("AssessmentSource")
                    .IsRequired()
                    .HasMaxLength(200)
                    .HasColumnType("character varying(200)");
                b.Property<string>("Method")
                    .IsRequired()
                    .HasMaxLength(50)
                    .HasColumnType("character varying(50)");
                b.Property<DateTime>("ObservedAt")
                    .HasColumnType("timestamp with time zone");
                b.Property<Guid>("SkillId")
                    .HasColumnType("uuid");
                b.Property<long>("UserId")
                    .HasColumnType("bigint");
                b.HasKey("LessonId");
                b.HasIndex("LessonId", "UserId");
                b.HasIndex("SkillId", "UserId");
                b.ToTable("learning_evidence", (string)null);
            });

        modelBuilder.Entity("OwnDay.Domain.Learning.CurriculumRoute", b =>
            {
                b.Property<Guid>("Id")
                    .HasColumnType("uuid");
                b.Property<int?>("ProgressVersion")
                    .HasColumnType("integer");
                b.Property<Guid>("ProgramId")
                    .HasColumnType("uuid");
                b.Property<string>("Reason")
                    .IsRequired()
                    .HasMaxLength(500)
                    .HasColumnType("character varying(500)");
                b.Property<string>("Topics")
                    .IsRequired()
                    .HasColumnType("jsonb");
                b.Property<int>("Version")
                    .HasColumnType("integer");
                b.HasKey("Id");
                b.ToTable("learning_routes", (string)null);
            });

        modelBuilder.Entity("OwnDay.Domain.Learning.LearningPreference", b =>
            {
                b.Property<long>("UserId")
                    .HasColumnType("bigint");

                b.Property<int>("Kind")
                    .HasColumnType("integer");

                b.Property<string>("Source")
                    .IsRequired()
                    .HasMaxLength(50)
                    .HasColumnType("character varying(50)");

                b.Property<DateTimeOffset>("UpdatedAt")
                    .HasColumnType("timestamp with time zone");

                b.Property<string>("Value")
                    .IsRequired()
                    .HasMaxLength(200)
                    .HasColumnType("character varying(200)");

                b.HasKey("UserId", "Kind");

                b.ToTable("learning_preferences", null, t =>
                    {
                        t.HasCheckConstraint("CK_learning_preference_kind", "\"Kind\" BETWEEN 0 AND 3");

                        t.HasCheckConstraint("CK_learning_preference_owner", "\"UserId\" > 0");

                        t.HasCheckConstraint("CK_learning_preference_value", "length(trim(\"Value\")) BETWEEN 1 AND 200");
                    });
            });

        modelBuilder.Entity("OwnDay.Domain.Learning.LearningProgram", b =>
            {
                b.Property<Guid>("Id")
                    .HasColumnType("uuid");
                b.Property<string>("DiagnosticAnswer")
                    .HasMaxLength(3000)
                    .HasColumnType("character varying(3000)");
                b.Property<bool>("DiagnosticPending")
                    .HasColumnType("boolean");
                b.Property<string>("DiagnosticQuestion")
                    .HasMaxLength(1000)
                    .HasColumnType("character varying(1000)");
                b.Property<string>("Discipline")
                    .IsRequired()
                    .HasMaxLength(100)
                    .HasColumnType("character varying(100)");
                b.Property<string>("Experience")
                    .IsRequired()
                    .HasMaxLength(1000)
                    .HasColumnType("character varying(1000)");
                b.Property<string>("Goal")
                    .IsRequired()
                    .HasMaxLength(500)
                    .HasColumnType("character varying(500)");

                b.Property<string>("GoalCriterion")
                    .HasMaxLength(500)
                    .HasColumnType("character varying(500)");
                b.Property<int>("Priority")
                    .HasColumnType("integer");
                b.Property<long?>("ProjectId")
                    .HasColumnType("bigint");
                b.Property<long>("UserId")
                    .HasColumnType("bigint");
                b.HasKey("Id");
                b.HasIndex("UserId");
                b.HasIndex("ProjectId", "UserId");
                b.ToTable("learning_programs", (string)null);
            });

        modelBuilder.Entity("OwnDay.Domain.Learning.Lesson", b =>
            {
                b.Property<bool?>("AiIsCorrect")
                    .HasColumnType("boolean");

                b.Property<int>("AssessmentStatus")
                    .HasColumnType("integer");

                b.Property<bool?>("DeterministicIsCorrect")
                    .HasColumnType("boolean");

                b.Property<string>("ExpectedAnswer")
                    .HasMaxLength(500)
                    .HasColumnType("character varying(500)");

                b.Property<string>("Provenance")
                    .HasColumnType("jsonb");

                b.Property<bool>("IsReview")
                    .HasColumnType("boolean");
                b.Property<Guid>("Id")
                    .HasColumnType("uuid");
                b.Property<string>("Answer")
                    .HasMaxLength(3000)
                    .HasColumnType("character varying(3000)");
                b.Property<string>("Feedback")
                    .HasMaxLength(3000)
                    .HasColumnType("character varying(3000)");
                b.Property<bool?>("IsCorrect")
                    .HasColumnType("boolean");
                b.Property<Guid?>("ProgramId")
                    .HasColumnType("uuid");
                b.Property<string>("Question")
                    .IsRequired()
                    .HasMaxLength(1000)
                    .HasColumnType("character varying(1000)");
                b.Property<int?>("RouteVersion")
                    .HasColumnType("integer");
                b.Property<Guid?>("SkillId")
                    .HasColumnType("uuid");
                b.Property<int>("Status")
                    .HasColumnType("integer");
                b.Property<string>("Teaching")
                    .HasColumnType("jsonb");

                b.Property<string>("Transfer")
                    .HasColumnType("jsonb");
                b.Property<string>("Theory")
                    .IsRequired()
                    .HasMaxLength(2500)
                    .HasColumnType("character varying(2500)");
                b.Property<string>("Topic")
                    .IsRequired()
                    .HasMaxLength(500)
                    .HasColumnType("character varying(500)");
                b.Property<string>("TopicCode")
                    .HasMaxLength(40)
                    .HasColumnType("character varying(40)");
                b.Property<long>("UserId")
                    .HasColumnType("bigint");
                b.HasKey("Id");
                b.HasIndex("UserId")
                    .IsUnique()
                    .HasFilter("\"Status\" IN (0, 1)");
                b.HasIndex("ProgramId", "RouteVersion");
                b.HasIndex("ProgramId", "UserId");
                b.HasIndex("SkillId", "UserId");
                b.ToTable("learning_lessons", (string)null);
            });

        modelBuilder.Entity("OwnDay.Domain.Learning.LessonInteraction", b =>
            {
                b.Property<long>("Id")
                    .ValueGeneratedOnAdd()
                    .HasColumnType("bigint");
                NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<long>("Id"));
                b.Property<string>("Explanation")
                    .IsRequired()
                    .HasMaxLength(2500)
                    .HasColumnType("character varying(2500)");
                b.Property<int>("Kind")
                    .HasColumnType("integer");
                b.Property<Guid>("LessonId")
                    .HasColumnType("uuid");
                b.Property<string>("Request")
                    .IsRequired()
                    .HasMaxLength(1000)
                    .HasColumnType("character varying(1000)");
                b.HasKey("Id");
                b.HasIndex("LessonId");
                b.ToTable("learning_interactions", (string)null);
            });

        modelBuilder.Entity("OwnDay.Domain.Learning.CurriculumRoute", b =>
            {
                b.HasOne("OwnDay.Domain.Learning.LearningProgram", null)
                    .WithMany("Routes")
                    .HasForeignKey("ProgramId")
                    .OnDelete(DeleteBehavior.Cascade)
                    .IsRequired();
            });

        modelBuilder.Entity("OwnDay.Domain.Learning.LearningProgram", b =>
            {
                b.HasOne("OwnDay.Domain.Projects.Project", null)
                    .WithMany()
                    .HasForeignKey("ProjectId", "UserId")
                    .HasPrincipalKey("Id", "UserId")
                    .OnDelete(DeleteBehavior.Restrict);
            });

        modelBuilder.Entity("OwnDay.Domain.Learning.Lesson", b =>
            {
                b.HasOne("OwnDay.Domain.Learning.CurriculumRoute", null)
                    .WithMany()
                    .HasForeignKey("ProgramId", "RouteVersion")
                    .HasPrincipalKey("ProgramId", "Version")
                    .OnDelete(DeleteBehavior.Restrict);
                b.HasOne("OwnDay.Domain.Learning.LearningProgram", null)
                    .WithMany()
                    .HasForeignKey("ProgramId", "UserId")
                    .HasPrincipalKey("Id", "UserId")
                    .OnDelete(DeleteBehavior.Restrict);
                b.HasOne("OwnDay.Domain.Learning.Skill", null)
                    .WithMany()
                    .HasForeignKey("SkillId", "UserId")
                    .HasPrincipalKey("Id", "UserId")
                    .OnDelete(DeleteBehavior.Restrict);
            });

        modelBuilder.Entity("OwnDay.Domain.Learning.LessonInteraction", b =>
            {
                b.HasOne("OwnDay.Domain.Learning.Lesson", null)
                    .WithMany("Interactions")
                    .HasForeignKey("LessonId")
                    .OnDelete(DeleteBehavior.Cascade)
                    .IsRequired();
            });

        modelBuilder.Entity("OwnDay.Domain.Learning.LearningProgram", b =>
            {
                b.Navigation("Routes");
            });

        modelBuilder.Entity("OwnDay.Domain.Learning.Lesson", b =>
            {
                b.Navigation("Interactions");
            });

        modelBuilder.Entity("OwnDay.Domain.Learning.LearningEvidence", b =>
            {
                b.HasOne("OwnDay.Domain.Learning.Lesson", null)
                    .WithMany()
                    .HasForeignKey("LessonId", "UserId")
                    .HasPrincipalKey("Id", "UserId")
                    .OnDelete(DeleteBehavior.Restrict)
                    .IsRequired();
                b.HasOne("OwnDay.Domain.Learning.Skill", null)
                    .WithMany()
                    .HasForeignKey("SkillId", "UserId")
                    .HasPrincipalKey("Id", "UserId")
                    .OnDelete(DeleteBehavior.Restrict)
                    .IsRequired();
            });

        modelBuilder.Entity("OwnDay.Domain.Learning.ReviewSchedule", b =>
            {
                b.HasOne("OwnDay.Domain.Learning.Skill", null)
                    .WithMany()
                    .HasForeignKey("SkillId", "UserId")
                    .HasPrincipalKey("Id", "UserId")
                    .OnDelete(DeleteBehavior.Restrict)
                    .IsRequired();
            });

        modelBuilder.Entity("OwnDay.Domain.Learning.LearningContext", b =>
            {
                b.Property<long>("UserId")
                    .HasColumnType("bigint");

                b.Property<Guid>("ProgramId")
                    .HasColumnType("uuid");

                b.HasKey("UserId");

                b.HasIndex("ProgramId", "UserId");

                b.ToTable("learning_contexts", (string)null);
            });

        modelBuilder.Entity("OwnDay.Domain.Learning.ProgramSkillLink", b =>
            {
                b.Property<Guid>("ProgramId")
                    .HasColumnType("uuid");

                b.Property<int>("ProgressVersion")
                    .HasColumnType("integer");

                b.Property<string>("TopicCode")
                    .HasMaxLength(40)
                    .HasColumnType("character varying(40)");

                b.Property<DateTimeOffset>("ConfirmedAt")
                    .HasColumnType("timestamp with time zone");

                b.Property<string>("Reason")
                    .IsRequired()
                    .HasMaxLength(500)
                    .HasColumnType("character varying(500)");

                b.Property<Guid>("SkillId")
                    .HasColumnType("uuid");

                b.Property<string>("Source")
                    .IsRequired()
                    .HasMaxLength(50)
                    .HasColumnType("character varying(50)");

                b.Property<string>("Trust")
                    .IsRequired()
                    .HasMaxLength(50)
                    .HasColumnType("character varying(50)");

                b.Property<long>("UserId")
                    .HasColumnType("bigint");

                b.HasKey("ProgramId", "ProgressVersion", "TopicCode");

                b.HasIndex("ProgramId", "UserId");

                b.HasIndex("SkillId", "UserId");

                b.ToTable("learning_program_skill_links", (string)null);
            });

        modelBuilder.Entity("OwnDay.Domain.Learning.LearningContext", b =>
            {
                b.HasOne("OwnDay.Domain.Learning.LearningProgram", null)
                    .WithMany()
                    .HasForeignKey("ProgramId", "UserId")
                    .HasPrincipalKey("Id", "UserId")
                    .OnDelete(DeleteBehavior.Restrict)
                    .IsRequired();
            });

        modelBuilder.Entity("OwnDay.Domain.Learning.ProgramSkillLink", b =>
            {
                b.HasOne("OwnDay.Domain.Learning.CurriculumRoute", null)
                    .WithMany()
                    .HasForeignKey("ProgramId", "ProgressVersion")
                    .HasPrincipalKey("ProgramId", "Version")
                    .OnDelete(DeleteBehavior.Restrict)
                    .IsRequired();

                b.HasOne("OwnDay.Domain.Learning.LearningProgram", null)
                    .WithMany()
                    .HasForeignKey("ProgramId", "UserId")
                    .HasPrincipalKey("Id", "UserId")
                    .OnDelete(DeleteBehavior.Restrict)
                    .IsRequired();

                b.HasOne("OwnDay.Domain.Learning.Skill", null)
                    .WithMany()
                    .HasForeignKey("SkillId", "UserId")
                    .HasPrincipalKey("Id", "UserId")
                    .OnDelete(DeleteBehavior.Restrict)
                    .IsRequired();
            });

        modelBuilder.Entity("OwnDay.Domain.Learning.StudySchedule", b =>
            {
                b.Property<long>("UserId")
                    .HasColumnType("bigint");

                b.Property<int>("EveryDays")
                    .HasColumnType("integer");

                b.Property<Guid?>("InvitationId")
                    .HasColumnType("uuid");

                b.Property<DateTimeOffset?>("LastInvitedAt")
                    .HasColumnType("timestamp with time zone");

                b.Property<DateTime>("NextAt")
                    .HasColumnType("timestamp with time zone");

                b.Property<bool>("Paused")
                    .HasColumnType("boolean");

                b.Property<Guid?>("ProgramId")
                    .HasColumnType("uuid");

                b.Property<Guid?>("SkillId")
                    .HasColumnType("uuid");

                b.Property<string>("TimeZoneId")
                    .IsRequired()
                    .HasMaxLength(100)
                    .HasColumnType("character varying(100)");

                b.Property<string>("Windows")
                    .IsRequired()
                    .HasColumnType("jsonb");

                b.HasKey("UserId");

                b.HasIndex("ProgramId", "UserId");

                b.HasIndex("SkillId", "UserId");

                b.HasIndex("Paused", "NextAt", "UserId");

                b.ToTable("learning_study_schedules", null, t =>
                    {
                        t.HasCheckConstraint("CK_learning_study_frequency", "\"EveryDays\" BETWEEN 1 AND 30");

                        t.HasCheckConstraint("CK_learning_study_invitation", "(\"InvitationId\" IS NULL AND \"ProgramId\" IS NULL AND \"SkillId\" IS NULL) OR (\"InvitationId\" IS NOT NULL AND ((\"ProgramId\" IS NOT NULL AND \"SkillId\" IS NULL) OR (\"ProgramId\" IS NULL AND \"SkillId\" IS NOT NULL)))");

                        t.HasCheckConstraint("CK_learning_study_owner", "\"UserId\" > 0");
                    });
            });

        modelBuilder.Entity("OwnDay.Domain.Learning.StudySchedule", b =>
            {
                b.HasOne("OwnDay.Domain.Learning.LearningProgram", null)
                    .WithMany()
                    .HasForeignKey("ProgramId", "UserId")
                    .HasPrincipalKey("Id", "UserId")
                    .OnDelete(DeleteBehavior.Restrict);

                b.HasOne("OwnDay.Domain.Learning.Skill", null)
                    .WithMany()
                    .HasForeignKey("SkillId", "UserId")
                    .HasPrincipalKey("Id", "UserId")
                    .OnDelete(DeleteBehavior.Restrict);
            });

        modelBuilder.Entity("OwnDay.Domain.Actions.Action", b =>
            {
                b.HasAlternateKey("Id", "UserId");
            });

        modelBuilder.Entity("OwnDay.Domain.Learning.Lesson", b =>
            {
                b.Property<long?>("ProjectActionId").HasColumnType("bigint");
                b.HasIndex("ProjectActionId", "UserId");
                b.ToTable("learning_lessons", null, t => t.HasCheckConstraint(
                    "CK_learning_lessons_project_action", "\"ProjectActionId\" IS NULL OR \"ProgramId\" IS NOT NULL"));
                b.HasOne("OwnDay.Domain.Actions.Action", null)
                    .WithMany()
                    .HasForeignKey("ProjectActionId", "UserId")
                    .HasPrincipalKey("Id", "UserId")
                    .OnDelete(DeleteBehavior.Restrict);
            });

        modelBuilder.Entity("OwnDay.Application.Learning.Requests.LearningRequest", b =>
            {
                b.Property<Guid>("Id")
                    .HasColumnType("uuid")
                    .HasColumnName("id");

                b.Property<int>("AttemptCount")
                    .HasColumnType("integer")
                    .HasColumnName("attempt_count");

                b.Property<DateTime>("AvailableAt")
                    .HasColumnType("timestamp with time zone")
                    .HasColumnName("available_at");

                b.Property<long>("ChatId")
                    .HasColumnType("bigint")
                    .HasColumnName("chat_id");

                b.Property<DateTime?>("CompletedAt")
                    .HasColumnType("timestamp with time zone")
                    .HasColumnName("completed_at");

                b.Property<Guid?>("LeaseToken")
                    .HasColumnType("uuid")
                    .HasColumnName("lease_token");

                b.Property<DateTime?>("LeaseUntil")
                    .HasColumnType("timestamp with time zone")
                    .HasColumnName("lease_until");

                b.Property<string>("NormalizedInput")
                    .IsRequired()
                    .HasColumnType("jsonb")
                    .HasColumnName("normalized_input");

                b.Property<string>("Outcome")
                    .HasMaxLength(100)
                    .HasColumnType("character varying(100)")
                    .HasColumnName("outcome");

                b.Property<string>("PayloadKind")
                    .IsRequired()
                    .HasMaxLength(50)
                    .HasColumnType("character varying(50)")
                    .HasColumnName("payload_kind");

                b.Property<int>("PayloadVersion")
                    .HasColumnType("integer")
                    .HasColumnName("payload_version");

                b.Property<DateTime>("ReceivedAt")
                    .HasColumnType("timestamp with time zone")
                    .HasColumnName("received_at");

                b.Property<long>("Sequence")
                    .HasColumnType("bigint")
                    .HasColumnName("sequence");

                b.Property<string>("SourceKey")
                    .IsRequired()
                    .HasMaxLength(100)
                    .HasColumnType("character varying(100)")
                    .HasColumnName("source_key");

                b.Property<int?>("SourceMessageId")
                    .HasColumnType("integer")
                    .HasColumnName("source_message_id");

                b.Property<int>("SourceUpdateId")
                    .HasColumnType("integer")
                    .HasColumnName("source_update_id");

                b.Property<int>("Status")
                    .HasColumnType("integer")
                    .HasColumnName("status");

                b.Property<long>("UserId")
                    .HasColumnType("bigint")
                    .HasColumnName("user_id");

                b.HasKey("Id");

                b.HasIndex("SourceKey")
                    .IsUnique();

                b.Property<string>("DialogueOutcomeJson").HasColumnType("jsonb").HasColumnName("dialogue_outcome");

                b.HasIndex("UserId").HasDatabaseName("IX_learning_requests_dialogue_owner")
                    .HasFilter("payload_kind = 'learning_text' OR (payload_kind = 'learning_command' AND payload_version = 3)");

                b.HasIndex("UserId", "Sequence")
                    .IsUnique();

                b.ToTable("learning_requests", null, t =>
                    {
                        t.HasCheckConstraint("CK_learning_requests_attempts", "attempt_count >= 0");

                        t.HasCheckConstraint("CK_learning_requests_completion", "(status = 0 AND completed_at IS NOT NULL AND completed_at >= received_at AND outcome IS NOT NULL AND lease_token IS NULL AND lease_until IS NULL) OR (status = 1 AND completed_at IS NULL AND outcome IS NULL AND lease_token IS NULL AND lease_until IS NULL) OR (status = 2 AND completed_at IS NULL AND outcome IS NULL AND lease_token IS NOT NULL AND lease_until IS NOT NULL)");

                        t.HasCheckConstraint("CK_learning_requests_identity", "user_id > 0 AND sequence > 0 AND payload_version > 0");
                    });
            });

        modelBuilder.Entity("OwnDay.Infrastructure.Persistence.TelegramOutboxMessage", b =>
            {
                b.Property<Guid?>("LearningRequestId").HasColumnType("uuid").HasColumnName("learning_request_id");
                b.Property<int?>("ReplyOrdinal").HasColumnType("integer").HasColumnName("reply_ordinal");
                b.HasIndex("LearningRequestId", "ReplyOrdinal").IsUnique();
                b.HasOne("OwnDay.Application.Learning.Requests.LearningRequest", null)
                    .WithMany().HasForeignKey("LearningRequestId").OnDelete(DeleteBehavior.Restrict);
                b.ToTable("telegram_outbox_messages", null, t => t.HasCheckConstraint(
                    "CK_telegram_outbox_messages_learning_reply",
                    "(learning_request_id IS NULL AND reply_ordinal IS NULL) OR (learning_request_id IS NOT NULL AND reply_ordinal IS NOT NULL AND reply_ordinal >= 0)"));
            });

        modelBuilder.Entity("OwnDay.Application.Learning.Requests.LearningAppliedOperation", b =>
            {
                b.Property<Guid>("RequestId")
                    .HasColumnType("uuid")
                    .HasColumnName("request_id");

                b.Property<string>("Step")
                    .HasMaxLength(50)
                    .HasColumnType("character varying(50)")
                    .HasColumnName("step");

                b.Property<DateTime>("AppliedAt")
                    .HasColumnType("timestamp with time zone")
                    .HasColumnName("applied_at");

                b.HasKey("RequestId", "Step");

                b.ToTable("learning_applied_operations", (string)null);
            });

        modelBuilder.Entity("OwnDay.Application.Learning.Requests.LearningAppliedOperation", b =>
            {
                b.HasOne("OwnDay.Application.Learning.Requests.LearningRequest", null)
                    .WithMany()
                    .HasForeignKey("RequestId")
                    .OnDelete(DeleteBehavior.Restrict)
                    .IsRequired();
            });

        modelBuilder.Entity("OwnDay.Application.Learning.Requests.LearningProviderStep", b =>
            {
                b.Property<Guid>("RequestId").HasColumnType("uuid").HasColumnName("request_id");
                b.Property<int>("Ordinal").HasColumnType("integer").HasColumnName("ordinal");
                b.Property<string>("Kind").IsRequired().HasMaxLength(20).HasColumnType("character varying(20)").HasColumnName("kind");
                b.Property<int>("Version").HasColumnType("integer").HasColumnName("version");
                b.Property<string>("Input").IsRequired().HasColumnType("text").HasColumnName("input");
                b.Property<string>("Fingerprint").IsRequired().HasMaxLength(64).HasColumnType("character varying(64)").HasColumnName("fingerprint");
                b.Property<string>("TargetStamp").IsRequired().HasMaxLength(64).HasColumnType("character varying(64)").HasColumnName("target_stamp");
                b.Property<string>("Targets").IsRequired().HasColumnType("text").HasColumnName("targets");
                b.Property<string>("Result").HasColumnType("text").HasColumnName("result");
                b.Property<int?>("Failure").HasColumnType("integer").HasColumnName("failure");
                b.HasKey("RequestId", "Ordinal");
                b.ToTable("learning_provider_steps", (string)null);
                b.HasOne("OwnDay.Application.Learning.Requests.LearningRequest", null).WithMany()
                    .HasForeignKey("RequestId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            });

        foreach (var target in new[]
        {
            "OwnDay.Domain.Learning.CurriculumRoute", "OwnDay.Domain.Learning.LearningContext",
            "OwnDay.Domain.Learning.LearningEvidence", "OwnDay.Domain.Learning.LearningPreference",
            "OwnDay.Domain.Learning.LearningProgram", "OwnDay.Domain.Learning.Lesson",
            "OwnDay.Domain.Learning.LessonInteraction", "OwnDay.Domain.Learning.ProgramSkillLink",
            "OwnDay.Domain.Learning.ReviewSchedule", "OwnDay.Domain.Learning.Skill",
            "OwnDay.Domain.Learning.StudySchedule", "OwnDay.Domain.Projects.Project", "OwnDay.Domain.Actions.Action"
        })
        {
            modelBuilder.Entity(target, b => b.Property<uint>("TargetVersion").IsConcurrencyToken()
                .ValueGeneratedOnAddOrUpdate().HasColumnType("xid").HasColumnName("xmin"));
        }

        modelBuilder.Entity("OwnDay.Application.Learning.Requests.LearningProviderAttempt", b =>
            {
                b.Property<Guid>("Id").HasColumnType("uuid").HasColumnName("id");
                b.Property<Guid>("RequestId").HasColumnType("uuid").HasColumnName("request_id");
                b.Property<int>("Ordinal").HasColumnType("integer").HasColumnName("ordinal");
                b.Property<int>("Number").HasColumnType("integer").HasColumnName("number");
                b.Property<Guid>("LeaseToken").HasColumnType("uuid").HasColumnName("lease_token");
                b.Property<DateTime>("StartedAt").HasColumnType("timestamp with time zone").HasColumnName("started_at");
                b.Property<DateTime?>("FinishedAt").HasColumnType("timestamp with time zone").HasColumnName("finished_at");
                b.Property<string>("Outcome").HasMaxLength(50).HasColumnType("character varying(50)").HasColumnName("outcome");
                b.HasKey("Id");
                b.HasIndex("RequestId", "Ordinal", "Number").IsUnique();
                b.ToTable("learning_provider_attempts", null, t => t.HasCheckConstraint("CK_learning_provider_attempts_state",
                    "number BETWEEN 1 AND 3 AND ((finished_at IS NULL AND outcome IS NULL) OR (finished_at IS NOT NULL AND outcome IS NOT NULL))"));
                b.HasOne("OwnDay.Application.Learning.Requests.LearningProviderStep", null).WithMany()
                    .HasForeignKey("RequestId", "Ordinal").OnDelete(DeleteBehavior.Restrict).IsRequired();
            });

        modelBuilder.HasSequence<long>("telegram_outbox_delivery_order_seq");
        modelBuilder.Entity("OwnDay.Infrastructure.Persistence.TelegramOutboxMessage", b =>
            {
                b.Property<long>("DeliveryOrder").ValueGeneratedOnAdd().HasColumnType("bigint").HasColumnName("delivery_order")
                    .HasDefaultValueSql("nextval('telegram_outbox_delivery_order_seq')");
                b.HasIndex("DeliveryOrder").IsUnique();
            });

        modelBuilder.Entity("OwnDay.Application.Learning.Requests.LearningRequest", b =>
            b.HasIndex("ReceivedAt", "UserId", "Sequence").HasDatabaseName("IX_learning_requests_active_head").HasFilter("status IN (1, 2)"));
        modelBuilder.Entity("OwnDay.Application.Learning.Requests.LearningProviderStep", b =>
            b.ToTable("learning_provider_steps", null, t =>
            {
                t.HasCheckConstraint("CK_learning_provider_steps_identity", "ordinal BETWEEN 0 AND 1 AND version > 0");
                t.HasCheckConstraint("CK_learning_provider_steps_result", "result IS NULL OR failure IS NULL");
            }));

        modelBuilder.Entity("OwnDay.Domain.Learning.LearningProgram", b =>
            b.Property<string>("EnglishDiagnostic").HasColumnType("jsonb"));
        modelBuilder.Entity("OwnDay.Domain.Dialogue.DialogueContext", b =>
            b.Property<string>("DiagnosticTarget").HasColumnType("jsonb").HasColumnName("diagnostic_target"));
        foreach (var target in new[] { "OwnDay.Domain.Learning.Lesson", "OwnDay.Domain.Learning.LearningEvidence" })
        {
            modelBuilder.Entity(target, b =>
            {
                b.Property<int>("Assistance").HasColumnType("integer");
                b.Property<string>("Content").HasColumnType("jsonb");
            });
        }
        modelBuilder.Entity("OwnDay.Domain.Learning.LearningContext", b => b.Property<string>("Practice").HasColumnType("jsonb"));
        modelBuilder.Entity("OwnDay.Domain.Learning.Lesson", b =>
        {
            b.Property<int?>("SuspendedFrom").HasColumnType("integer");
            b.Property<Guid?>("SuspendedForProgramId").HasColumnType("uuid");
            b.HasIndex("UserId", "SuspendedForProgramId").IsUnique().HasFilter("\"Status\" = 4");
            b.HasIndex("SuspendedForProgramId", "UserId");
            b.HasOne("OwnDay.Domain.Learning.LearningProgram", null).WithMany()
                .HasForeignKey("SuspendedForProgramId", "UserId").HasPrincipalKey("Id", "UserId").OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("FK_learning_lessons_suspended_program");
            b.ToTable("learning_lessons", null, t => t.HasCheckConstraint("CK_learning_lessons_suspension",
                "(\"Status\" = 4 AND \"SuspendedFrom\" IN (0, 1) AND \"SuspendedFrom\" IS NOT NULL AND \"SuspendedForProgramId\" IS NOT NULL) OR (\"Status\" <> 4 AND \"SuspendedFrom\" IS NULL AND \"SuspendedForProgramId\" IS NULL)"));
        });
#pragma warning restore 612, 618
    }
}
