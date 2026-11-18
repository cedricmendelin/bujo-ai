using Bujo.Domain;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Bujo.Infrastructure.Persistence.Migrations;

[DbContext(typeof(JournalDbContext))]
[Migration("20261118135000_EnforceJournalListUniqueness")]
public sealed class EnforceJournalListUniqueness : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "NormalizedName",
            table: "JournalLists",
            type: "nvarchar(100)",
            maxLength: DomainLimits.ListName,
            nullable: true,
            collation: "Latin1_General_100_CI_AS_SC");

        migrationBuilder.Sql("UPDATE [JournalLists] SET [NormalizedName] = [Name];");

        migrationBuilder.AlterColumn<string>(
            name: "NormalizedName",
            table: "JournalLists",
            type: "nvarchar(100)",
            maxLength: DomainLimits.ListName,
            nullable: false,
            collation: "Latin1_General_100_CI_AS_SC",
            oldClrType: typeof(string),
            oldType: "nvarchar(100)",
            oldMaxLength: DomainLimits.ListName,
            oldNullable: true,
            oldCollation: "Latin1_General_100_CI_AS_SC");

        migrationBuilder.Sql("""
            IF EXISTS (
                SELECT [NormalizedName]
                FROM [JournalLists]
                GROUP BY [NormalizedName]
                HAVING COUNT(*) > 1)
            BEGIN
                THROW 51000, 'Existing journal list names must be unique before this migration.', 1;
            END;

            IF EXISTS (
                SELECT [WeekYear], [WeekNumber]
                FROM [JournalLists]
                WHERE [Kind] = 1 AND [WeekYear] IS NOT NULL AND [WeekNumber] IS NOT NULL
                GROUP BY [WeekYear], [WeekNumber]
                HAVING COUNT(*) > 1)
            BEGIN
                THROW 51001, 'Existing weekly lists must have unique ISO week keys before this migration.', 1;
            END;
            """);

        migrationBuilder.CreateIndex(
            name: "IX_JournalLists_NormalizedName",
            table: "JournalLists",
            column: "NormalizedName",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_JournalLists_WeekYear_WeekNumber",
            table: "JournalLists",
            columns: new[] { "WeekYear", "WeekNumber" },
            unique: true,
            filter: "[Kind] = 1 AND [WeekYear] IS NOT NULL AND [WeekNumber] IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_JournalLists_NormalizedName",
            table: "JournalLists");

        migrationBuilder.DropIndex(
            name: "IX_JournalLists_WeekYear_WeekNumber",
            table: "JournalLists");

        migrationBuilder.DropColumn(
            name: "NormalizedName",
            table: "JournalLists");
    }
}
