using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("202609270003_AlignActionIdentitySequence")]
public partial class AlignActionIdentitySequence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$
            DECLARE
                sequence_name text := pg_get_serial_sequence('actions', 'id');
                sequence_value bigint;
                sequence_called boolean;
                largest_id bigint;
            BEGIN
                EXECUTE format('SELECT last_value, is_called FROM %s', sequence_name)
                    INTO sequence_value, sequence_called;
                SELECT max(id) INTO largest_id FROM actions;
                IF largest_id IS NOT NULL AND
                    (largest_id > sequence_value OR (largest_id = sequence_value AND NOT sequence_called)) THEN
                    PERFORM setval(sequence_name, largest_id, true);
                END IF;
            END $$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Advancing an identity sequence does not need a data rollback.
    }
}
