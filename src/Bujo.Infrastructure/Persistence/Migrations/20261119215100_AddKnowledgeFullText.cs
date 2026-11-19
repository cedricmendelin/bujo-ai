using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Bujo.Infrastructure.Persistence.Migrations;

[DbContext(typeof(JournalDbContext))]
[Migration("20261119215100_AddKnowledgeFullText")]
public sealed class AddKnowledgeFullText : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
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
            """,
            suppressTransaction: true);
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
            """,
            suppressTransaction: true);
    }
}
