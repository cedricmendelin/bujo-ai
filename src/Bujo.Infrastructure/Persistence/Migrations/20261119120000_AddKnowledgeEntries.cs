using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Bujo.Infrastructure.Persistence.Migrations;

[DbContext(typeof(JournalDbContext))]
[Migration("20261119120000_AddKnowledgeEntries")]
public sealed class AddKnowledgeEntries : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "KnowledgeEntries",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                SourceReference = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                ArchivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_KnowledgeEntries", entry => entry.Id));

        migrationBuilder.CreateTable(
            name: "KnowledgeEntryTags",
            columns: table => new
            {
                KnowledgeEntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                NormalizedValue = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Value = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_KnowledgeEntryTags", tag => new { tag.KnowledgeEntryId, tag.NormalizedValue });
                table.ForeignKey(
                    name: "FK_KnowledgeEntryTags_KnowledgeEntries_KnowledgeEntryId",
                    column: tag => tag.KnowledgeEntryId,
                    principalTable: "KnowledgeEntries",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_KnowledgeEntries_ArchivedAt_UpdatedAt_Id",
            table: "KnowledgeEntries",
            columns: new[] { "ArchivedAt", "UpdatedAt", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_KnowledgeEntryTags_NormalizedValue",
            table: "KnowledgeEntryTags",
            column: "NormalizedValue");

        migrationBuilder.Sql("""
            IF FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') = 1
            BEGIN
                EXEC(N'
                    IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = N''KnowledgeCatalog'')
                        CREATE FULLTEXT CATALOG [KnowledgeCatalog];

                    IF NOT EXISTS (
                        SELECT 1 FROM sys.fulltext_indexes
                        WHERE object_id = OBJECT_ID(N''dbo.KnowledgeEntries''))
                    BEGIN
                        CREATE FULLTEXT INDEX ON dbo.KnowledgeEntries
                        (
                            Content LANGUAGE 1033,
                            Title LANGUAGE 1033
                        )
                        KEY INDEX PK_KnowledgeEntries
                        ON [KnowledgeCatalog]
                        WITH CHANGE_TRACKING AUTO;
                    END
                ');
            END
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            IF FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') = 1
            BEGIN
                EXEC(N'
                    IF OBJECT_ID(N''dbo.KnowledgeEntries'', N''U'') IS NOT NULL
                       AND EXISTS (
                           SELECT 1 FROM sys.fulltext_indexes
                           WHERE object_id = OBJECT_ID(N''dbo.KnowledgeEntries''))
                        DROP FULLTEXT INDEX ON dbo.KnowledgeEntries;

                    IF EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = N''KnowledgeCatalog'')
                        DROP FULLTEXT CATALOG [KnowledgeCatalog];
                ');
            END
            """);
        migrationBuilder.DropTable(name: "KnowledgeEntryTags");
        migrationBuilder.DropTable(name: "KnowledgeEntries");
    }
}
