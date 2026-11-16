using Bujo.Domain;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Bujo.Infrastructure.Persistence.Migrations;

[DbContext(typeof(JournalDbContext))]
[Migration("20261115220000_InitialJournal")]
public sealed class InitialJournal : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Items",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                PlannedPeriodKind = table.Column<int>(type: "int", nullable: false),
                PlannedDay = table.Column<DateOnly>(type: "date", nullable: true),
                PlannedWeekYear = table.Column<int>(type: "int", nullable: true),
                PlannedWeekNumber = table.Column<int>(type: "int", nullable: true),
                PlannedYear = table.Column<int>(type: "int", nullable: true),
                PlannedMonth = table.Column<int>(type: "int", nullable: true),
                PlannedQuarter = table.Column<int>(type: "int", nullable: true),
                CurrentOccurrenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Items", item => item.Id));

        migrationBuilder.CreateTable(
            name: "JournalLists",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                Kind = table.Column<int>(type: "int", nullable: false),
                State = table.Column<int>(type: "int", nullable: false),
                GoalText = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                WeekYear = table.Column<int>(type: "int", nullable: true),
                WeekNumber = table.Column<int>(type: "int", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_JournalLists", item => item.Id));

        migrationBuilder.CreateTable(
            name: "JournalEvents",
            columns: table => new
            {
                Sequence = table.Column<long>(type: "bigint", nullable: false),
                Kind = table.Column<int>(type: "int", nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                OccurrenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                PreviousOccurrenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                NextOccurrenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ListId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_JournalEvents", item => item.Sequence));

        migrationBuilder.CreateTable(
            name: "MoveOperations",
            columns: table => new
            {
                OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ExpectedOccurrenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                DestinationListId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Fingerprint = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_MoveOperations", item => item.OperationId));

        migrationBuilder.CreateTable(
            name: "ItemTags",
            columns: table => new
            {
                ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                NormalizedValue = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Value = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ItemTags", item => new { item.ItemId, item.NormalizedValue });
                table.ForeignKey(
                    name: "FK_ItemTags_Items_ItemId",
                    column: item => item.ItemId,
                    principalTable: "Items",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Occurrences",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ListId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Sequence = table.Column<int>(type: "int", nullable: false),
                State = table.Column<int>(type: "int", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                PreviousOccurrenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                NextOccurrenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Occurrences", item => item.Id);
                table.ForeignKey(
                    name: "FK_Occurrences_Items_ItemId",
                    column: item => item.ItemId,
                    principalTable: "Items",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_Occurrences_JournalLists_ListId",
                    column: item => item.ListId,
                    principalTable: "JournalLists",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_JournalLists_Kind",
            table: "JournalLists",
            column: "Kind",
            unique: true,
            filter: "[Kind] = 0");

        migrationBuilder.CreateIndex(
            name: "IX_Occurrences_ItemId_Sequence",
            table: "Occurrences",
            columns: new[] { "ItemId", "Sequence" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Occurrences_ItemId_Open",
            table: "Occurrences",
            column: "ItemId",
            unique: true,
            filter: "[State] = 0");

        migrationBuilder.CreateIndex(
            name: "IX_Occurrences_PreviousOccurrenceId",
            table: "Occurrences",
            column: "PreviousOccurrenceId",
            unique: true,
            filter: "[PreviousOccurrenceId] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_Occurrences_ListId",
            table: "Occurrences",
            column: "ListId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ItemTags");
        migrationBuilder.DropTable(name: "JournalEvents");
        migrationBuilder.DropTable(name: "MoveOperations");
        migrationBuilder.DropTable(name: "Occurrences");
        migrationBuilder.DropTable(name: "Items");
        migrationBuilder.DropTable(name: "JournalLists");
    }
}
