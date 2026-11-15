using Bujo.Domain;

namespace Bujo.Domain.Tests;

public sealed class CanonicalEditTests
{
    [Fact]
    public void Historical_occurrences_resolve_to_the_current_canonical_text_and_tags()
    {
        var fixture = new JournalTestFixture();
        var week47 = fixture.CreateWeekly(2026, 47);
        var week48 = fixture.CreateWeekly(2026, 48);
        var capture = fixture.Capture("Old name", tags: ["Old tag"]);
        var firstMove = fixture.Move(capture.Item, capture.Occurrence.Id, week47.Id);
        fixture.Move(firstMove.Resolution.Item, firstMove.Resolution.CurrentOccurrence.Id, week48.Id);

        var edited = fixture.Journal.EditItem(capture.Item.Id, "New name", "Updated description", ["New tag"]);

        Assert.True(edited.IsSuccess);
        var historical = fixture.Journal.Resolve(capture.Occurrence.Id);
        Assert.Equal("New name", historical.Value!.Item.Name);
        Assert.Equal("Updated description", historical.Value.Item.Description);
        Assert.Equal("New tag", Assert.Single(historical.Value.Item.Tags).Value);
    }

    [Fact]
    public void Invalid_canonical_edit_does_not_partially_update_the_item()
    {
        var fixture = new JournalTestFixture();
        var capture = fixture.Capture("Original", tags: ["existing"]);
        var tooLong = new string('x', DomainLimits.TagName + 1);

        var result = fixture.Journal.EditItem(capture.Item.Id, "Changed", "Changed too", [tooLong]);

        Assert.False(result.IsSuccess);
        var unchanged = fixture.Journal.GetSnapshot().Items.Single();
        Assert.Equal("Original", unchanged.Name);
        Assert.Null(unchanged.Description);
        Assert.Equal("existing", Assert.Single(unchanged.Tags).Value);
    }
}
