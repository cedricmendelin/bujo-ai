using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Bujo.Infrastructure.Persistence.Migrations;

[DbContext(typeof(JournalDbContext))]
[Migration("20261121220000_AddFollowUps")]
public sealed class AddFollowUps : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "FollowUps",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Condition = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                DueAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                TimeZoneId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Source = table.Column<int>(type: "int", nullable: false),
                Status = table.Column<int>(type: "int", nullable: false),
                Draft = table.Column<string>(type: "nvarchar(max)", maxLength: 10000, nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FollowUps", item => item.Id);
                table.ForeignKey("FK_FollowUps_Items_ItemId", item => item.ItemId, "Items", "Id",
                    onDelete: ReferentialAction.Restrict);
            });
        migrationBuilder.CreateTable(
            name: "FollowUpEvidence",
            columns: table => new
            {
                FollowUpId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Sequence = table.Column<long>(type: "bigint", nullable: false),
                Source = table.Column<int>(type: "int", nullable: false),
                Observation = table.Column<int>(type: "int", nullable: false),
                CheckedScope = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FollowUpEvidence", item => new { item.FollowUpId, item.Sequence });
                table.ForeignKey("FK_FollowUpEvidence_FollowUps_FollowUpId", item => item.FollowUpId,
                    "FollowUps", "Id", onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateTable(
            name: "FollowUpChanges",
            columns: table => new
            {
                FollowUpId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Sequence = table.Column<long>(type: "bigint", nullable: false),
                Kind = table.Column<int>(type: "int", nullable: false),
                OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                PreviousDueAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                NewDueAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FollowUpChanges", item => new { item.FollowUpId, item.Sequence });
                table.ForeignKey("FK_FollowUpChanges_FollowUps_FollowUpId", item => item.FollowUpId,
                    "FollowUps", "Id", onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex("IX_FollowUps_ItemId", "FollowUps", "ItemId");
        migrationBuilder.CreateIndex("IX_FollowUps_Status_DueAtUtc_Id", "FollowUps",
            new[] { "Status", "DueAtUtc", "Id" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("FollowUpChanges");
        migrationBuilder.DropTable("FollowUpEvidence");
        migrationBuilder.DropTable("FollowUps");
    }
}
