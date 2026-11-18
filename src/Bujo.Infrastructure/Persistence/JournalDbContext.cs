using Bujo.Domain;
using Microsoft.EntityFrameworkCore;

namespace Bujo.Infrastructure.Persistence;

public sealed class JournalDbContext(DbContextOptions<JournalDbContext> options) : DbContext(options)
{
    public DbSet<JournalListEntity> Lists => Set<JournalListEntity>();

    public DbSet<ItemEntity> Items => Set<ItemEntity>();

    public DbSet<ItemTagEntity> ItemTags => Set<ItemTagEntity>();

    public DbSet<OccurrenceEntity> Occurrences => Set<OccurrenceEntity>();

    public DbSet<JournalEventEntity> JournalEvents => Set<JournalEventEntity>();

    public DbSet<MoveOperationEntity> MoveOperations => Set<MoveOperationEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<JournalListEntity>(entity =>
        {
            entity.ToTable("JournalLists");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).HasMaxLength(DomainLimits.ListName).IsRequired();
            entity.Property(item => item.NormalizedName)
                .HasMaxLength(DomainLimits.ListName)
                .UseCollation("Latin1_General_100_CI_AS_SC")
                .IsRequired();
            entity.Property(item => item.Kind).HasConversion<int>();
            entity.Property(item => item.State).HasConversion<int>();
            entity.Property(item => item.GoalText).HasMaxLength(DomainLimits.GoalText);
            entity.Property(item => item.Version).IsRequired();
            entity.Property(item => item.RowVersion).IsRowVersion();
            entity.HasIndex(item => item.NormalizedName).IsUnique();
            entity.HasIndex(item => item.Kind)
                .IsUnique()
                .HasFilter("[Kind] = 0");
        });

        modelBuilder.Entity<ItemEntity>(entity =>
        {
            entity.ToTable("Items");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).HasMaxLength(DomainLimits.ItemName).IsRequired();
            entity.Property(item => item.Description).HasColumnType("nvarchar(max)");
            entity.Property(item => item.PlannedPeriodKind).HasConversion<int>();
            entity.Property(item => item.PlannedDay).HasColumnType("date");
            entity.Property(item => item.Version).IsRequired();
            entity.Property(item => item.RowVersion).IsRowVersion();
            entity.HasMany(item => item.Tags)
                .WithOne()
                .HasForeignKey(tag => tag.ItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ItemTagEntity>(entity =>
        {
            entity.ToTable("ItemTags");
            entity.HasKey(tag => new { tag.ItemId, tag.NormalizedValue });
            entity.Property(tag => tag.Value).HasMaxLength(DomainLimits.TagName).IsRequired();
            entity.Property(tag => tag.NormalizedValue).HasMaxLength(DomainLimits.TagName).IsRequired();
        });

        modelBuilder.Entity<OccurrenceEntity>(entity =>
        {
            entity.ToTable("Occurrences");
            entity.HasKey(occurrence => occurrence.Id);
            entity.Property(occurrence => occurrence.State).HasConversion<int>();
            entity.Property(occurrence => occurrence.CreatedAt).HasColumnType("datetimeoffset");
            entity.Property(occurrence => occurrence.UpdatedAt).HasColumnType("datetimeoffset");
            entity.Property(occurrence => occurrence.Version).IsRequired();
            entity.Property(occurrence => occurrence.RowVersion).IsRowVersion();
            entity.HasOne<JournalListEntity>()
                .WithMany()
                .HasForeignKey(occurrence => occurrence.ListId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ItemEntity>()
                .WithMany()
                .HasForeignKey(occurrence => occurrence.ItemId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(occurrence => new { occurrence.ItemId, occurrence.Sequence })
                .IsUnique();
            entity.HasIndex(occurrence => occurrence.ItemId)
                .IsUnique()
                .HasFilter("[State] = 0");
            entity.HasIndex(occurrence => occurrence.PreviousOccurrenceId)
                .IsUnique()
                .HasFilter("[PreviousOccurrenceId] IS NOT NULL");
        });

        modelBuilder.Entity<JournalEventEntity>(entity =>
        {
            entity.ToTable("JournalEvents");
            entity.HasKey(item => item.Sequence);
            entity.Property(item => item.Kind).HasConversion<int>();
            entity.Property(item => item.OccurredAt).HasColumnType("datetimeoffset");
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

public sealed class JournalListEntity
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public required string NormalizedName { get; set; }

    public JournalListKind Kind { get; set; }

    public JournalListState State { get; set; }

    public string? GoalText { get; set; }

    public int? WeekYear { get; set; }

    public int? WeekNumber { get; set; }

    public long Version { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

public sealed class ItemEntity
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public PlannedPeriodKind PlannedPeriodKind { get; set; }

    public DateOnly? PlannedDay { get; set; }

    public int? PlannedWeekYear { get; set; }

    public int? PlannedWeekNumber { get; set; }

    public int? PlannedYear { get; set; }

    public int? PlannedMonth { get; set; }

    public int? PlannedQuarter { get; set; }

    public Guid CurrentOccurrenceId { get; set; }

    public long Version { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public ICollection<ItemTagEntity> Tags { get; } = new List<ItemTagEntity>();
}

public sealed class ItemTagEntity
{
    public Guid ItemId { get; set; }

    public required string NormalizedValue { get; set; }

    public required string Value { get; set; }
}

public sealed class OccurrenceEntity
{
    public Guid Id { get; set; }

    public Guid ItemId { get; set; }

    public Guid ListId { get; set; }

    public int Sequence { get; set; }

    public OccurrenceState State { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? PreviousOccurrenceId { get; set; }

    public Guid? NextOccurrenceId { get; set; }

    public long Version { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

public sealed class JournalEventEntity
{
    public long Sequence { get; set; }

    public JournalEventKind Kind { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public Guid? ItemId { get; set; }

    public Guid? OccurrenceId { get; set; }

    public Guid? PreviousOccurrenceId { get; set; }

    public Guid? NextOccurrenceId { get; set; }

    public Guid? ListId { get; set; }

    public Guid? OperationId { get; set; }
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
