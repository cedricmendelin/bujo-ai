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
            name: "JournalDocuments",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_JournalDocuments", item => item.Id));

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
            name: "OpenOccurrences",
            columns: table => new
            {
                OccurrenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ListId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                State = table.Column<int>(type: "int", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_OpenOccurrences", item => item.OccurrenceId));

        migrationBuilder.CreateIndex(
            name: "IX_OpenOccurrences_ItemId",
            table: "OpenOccurrences",
            column: "ItemId",
            unique: true,
            filter: "[State] = 0");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "JournalDocuments");
        migrationBuilder.DropTable(name: "MoveOperations");
        migrationBuilder.DropTable(name: "OpenOccurrences");
    }
}
