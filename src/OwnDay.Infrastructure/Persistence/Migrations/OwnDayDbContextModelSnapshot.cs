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

            b.ToTable("telegram_outbox_messages");
        });
#pragma warning restore 612, 618
    }
}
