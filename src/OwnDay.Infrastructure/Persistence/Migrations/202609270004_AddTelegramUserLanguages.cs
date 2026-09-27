using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("202609270004_AddTelegramUserLanguages")]
public partial class AddTelegramUserLanguages : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE telegram_user_languages (
                user_id bigint CONSTRAINT "PK_telegram_user_languages" PRIMARY KEY,
                locale character varying(2) NULL,
                selection_expires_at timestamp with time zone NULL,
                CONSTRAINT "CK_telegram_user_languages_user" CHECK (user_id > 0),
                CONSTRAINT "CK_telegram_user_languages_locale" CHECK (locale IS NULL OR locale IN ('en', 'ru'))
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE telegram_user_languages;");
    }
}
