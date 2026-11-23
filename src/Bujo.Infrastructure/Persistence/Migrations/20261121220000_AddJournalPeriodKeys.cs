using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Bujo.Infrastructure.Persistence.Migrations;

[DbContext(typeof(JournalDbContext))]
[Migration("20261121220000_AddJournalPeriodKeys")]
public sealed class AddJournalPeriodKeys : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>("PeriodYear", "JournalLists", type: "int", nullable: true);
        migrationBuilder.AddColumn<int>("PeriodMonth", "JournalLists", type: "int", nullable: true);
        migrationBuilder.AddColumn<int>("PeriodQuarter", "JournalLists", type: "int", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("PeriodYear", "JournalLists");
        migrationBuilder.DropColumn("PeriodMonth", "JournalLists");
        migrationBuilder.DropColumn("PeriodQuarter", "JournalLists");
    }
}
