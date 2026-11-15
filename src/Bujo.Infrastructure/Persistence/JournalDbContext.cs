using Bujo.Domain;
using Microsoft.EntityFrameworkCore;

namespace Bujo.Infrastructure.Persistence;

public sealed class JournalDbContext(DbContextOptions<JournalDbContext> options) : DbContext(options)
{
    public DbSet<JournalDocumentEntity> JournalDocuments => Set<JournalDocumentEntity>();

    public DbSet<OpenOccurrenceEntity> OpenOccurrences => Set<OpenOccurrenceEntity>();

    public DbSet<MoveOperationEntity> MoveOperations => Set<MoveOperationEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<JournalDocumentEntity>(entity =>
        {
            entity.ToTable("JournalDocuments");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Payload).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(item => item.RowVersion).IsRowVersion();
        });

        modelBuilder.Entity<OpenOccurrenceEntity>(entity =>
        {
            entity.ToTable("OpenOccurrences");
            entity.HasKey(item => item.OccurrenceId);
            entity.HasIndex(item => item.ItemId)
                .IsUnique()
                .HasFilter("[State] = 0");
            entity.Property(item => item.State).HasConversion<int>();
        });

        modelBuilder.Entity<MoveOperationEntity>(entity =>
        {
            entity.ToTable("MoveOperations");
            entity.HasKey(item => item.OperationId);
            entity.Property(item => item.Fingerprint).HasMaxLength(200).IsRequired();
            entity.Property(item => item.ResultJson).IsRequired();
        });
    }
}

public sealed class JournalDocumentEntity
{
    public int Id { get; set; }

    public required string Payload { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

public sealed class OpenOccurrenceEntity
{
    public Guid OccurrenceId { get; set; }

    public Guid ItemId { get; set; }

    public Guid ListId { get; set; }

    public OccurrenceState State { get; set; }
}

public sealed class MoveOperationEntity
{
    public Guid OperationId { get; set; }

    public Guid ItemId { get; set; }

    public Guid ExpectedOccurrenceId { get; set; }

    public Guid DestinationListId { get; set; }

    public required string Fingerprint { get; set; }

    public required string ResultJson { get; set; }
}
