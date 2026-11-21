namespace Bujo.Domain;

public sealed record JournalBackupSnapshot(
    int SchemaVersion,
    IReadOnlyList<ItemSnapshot> Items,
    IReadOnlyList<JournalListSnapshot> Lists,
    IReadOnlyList<OccurrenceSnapshot> Occurrences,
    IReadOnlyList<JournalEvent> History,
    KnowledgeBackupSnapshot? Knowledge = null,
    FollowUpBackupSnapshot? FollowUps = null)
{
    public JournalSnapshot ToJournalSnapshot() =>
        JournalSnapshot.Restore(Items, Lists, Occurrences, History);
}
