using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Bujo.Infrastructure.Persistence.Migrations;

[DbContext(typeof(JournalDbContext))]
[Migration("20261118214500_AddReviewProposals")]
public sealed class AddReviewProposals : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ReviewProposals",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ListId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ExpectedListVersion = table.Column<long>(type: "bigint", nullable: false),
                CreatedBy = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                Source = table.Column<int>(type: "int", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                Status = table.Column<int>(type: "int", nullable: false),
                RequestCloseList = table.Column<bool>(type: "bit", nullable: false),
                ChangesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                Digest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                ApprovedBy = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                ApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                ApprovedDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                AppliedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ReviewProposals", proposal => proposal.Id);
                table.ForeignKey(
                    name: "FK_ReviewProposals_JournalLists_ListId",
                    column: proposal => proposal.ListId,
                    principalTable: "JournalLists",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ReviewProposals_ListId_CreatedAt",
            table: "ReviewProposals",
            columns: new[] { "ListId", "CreatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "ReviewProposals");
}
