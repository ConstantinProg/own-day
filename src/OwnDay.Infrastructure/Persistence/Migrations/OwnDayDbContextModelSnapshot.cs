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

        modelBuilder.Entity("OwnDay.Domain.Tasks.TaskItem", b =>
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

            b.HasIndex("UserId", "Status", "CreatedAt", "Id");

            b.ToTable("tasks", t => t.HasCheckConstraint(
                "CK_tasks_status_completed_at",
                "(status = 0 AND completed_at IS NULL) OR (status = 1 AND completed_at IS NOT NULL)"));
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
