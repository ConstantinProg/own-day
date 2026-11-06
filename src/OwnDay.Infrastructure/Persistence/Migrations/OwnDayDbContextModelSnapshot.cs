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

                b.Property<Guid>("AnalysisId")
                    .HasColumnType("uuid");

                b.Property<int?>("CachedInputTokens")
                    .HasColumnType("integer");

                b.Property<decimal?>("CachedInputUsdPerMillion")
                    .HasPrecision(20, 8)
                    .HasColumnType("numeric(20,8)");

                b.Property<long>("ChargedTokens")
                    .HasColumnType("bigint");

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

                b.ToTable("openai_request_attempts", (string)null);
            });

        modelBuilder.Entity("OwnDay.Application.Inbox.Observability.OpenAIRequestAttempt", b =>
            {
                b.HasOne("OwnDay.Application.Inbox.Analysis.InboxAnalysis", null)
                    .WithMany()
                    .HasForeignKey("AnalysisId")
                    .OnDelete(DeleteBehavior.Restrict)
                    .IsRequired();
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
                b.Property<DateTimeOffset>("ObservedAt")
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

#pragma warning restore 612, 618
    }
}
