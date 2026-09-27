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

        modelBuilder.Entity("OwnDay.Infrastructure.Persistence.TelegramInboxDraft", b =>
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

        modelBuilder.Entity("OwnDay.Domain.Inbox.InboxItem", b =>
        {
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

            b.HasKey("Id");

            b.HasIndex("Status", "NextAttemptAt");

            b.HasIndex("Status", "SentAt");

            b.ToTable("telegram_outbox_messages");
        });
#pragma warning restore 612, 618
    }
}
