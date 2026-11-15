using Bujo.Domain;

namespace Bujo.Domain.Tests;

public sealed class TagAndGoalTests
{
    [Fact]
    public void Tags_are_trimmed_normalized_and_case_insensitively_unique()
    {
        var fixture = new JournalTestFixture();

        var capture = fixture.Capture("Tags", tags: ["  Work ", "work", "Ｆａｍｉｌｙ"]);

        Assert.Equal(2, capture.Item.Tags.Count);
        Assert.Equal("Work", capture.Item.Tags[0].Value);
        Assert.Equal("Family", capture.Item.Tags[1].Value);
        Assert.Equal("WORK", capture.Item.Tags[0].NormalizedValue);
    }

    [Fact]
    public void Tag_count_and_length_limits_are_enforced_without_partial_item_edits()
    {
        var fixture = new JournalTestFixture();
        var capture = fixture.Capture("Tag limits", tags: ["Keep"]);
        var tooMany = Enumerable.Range(1, DomainLimits.TagsPerItem + 1).Select(value => $"tag-{value}").ToArray();

        var countError = fixture.Journal.EditItem(capture.Item.Id, "New", null, tooMany);
        var lengthError = fixture.Journal.EditItem(
            capture.Item.Id,
            "New",
            null,
            [new string('x', DomainLimits.TagName + 1)]);

        Assert.Equal(DomainErrorCode.InvalidInput, countError.Error!.Code);
        Assert.Equal(DomainErrorCode.InvalidInput, lengthError.Error!.Code);
        Assert.Equal("Tag limits", fixture.Journal.GetSnapshot().Items.Single().Name);
        Assert.Equal("Keep", Assert.Single(fixture.Journal.GetSnapshot().Items.Single().Tags).Value);
    }

    [Fact]
    public void List_goal_text_can_be_set_cleared_and_is_length_limited()
    {
        var fixture = new JournalTestFixture();
        var list = fixture.CreateList("Goals", goalText: "  Ship v1  ");
        Assert.Equal("Ship v1", list.GoalText);

        var changed = fixture.Journal.SetListGoal(list.Id, "Make space");
        Assert.Equal("Make space", changed.Value!.GoalText);
        var cleared = fixture.Journal.SetListGoal(list.Id, null);
        Assert.Null(cleared.Value!.GoalText);
        var tooLong = fixture.Journal.SetListGoal(list.Id, new string('g', DomainLimits.GoalText + 1));

        Assert.Equal(DomainErrorCode.InvalidInput, tooLong.Error!.Code);
        Assert.Null(fixture.Journal.GetSnapshot().Lists.Single(item => item.Id == list.Id).GoalText);
    }

    [Fact]
    public void Domain_has_no_goal_hierarchy_types()
    {
        var types = typeof(JournalService).Assembly.GetTypes();

        Assert.DoesNotContain(
            types,
            type => type.Name.Contains("Goal", StringComparison.OrdinalIgnoreCase)
                && !type.Name.EndsWith("Command", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Input_length_limits_are_applied_to_names_and_descriptions()
    {
        var fixture = new JournalTestFixture();
        var nameResult = fixture.Journal.Capture(new string('n', DomainLimits.ItemName + 1));
        var descriptionResult = fixture.Journal.Capture("Name", new string('d', DomainLimits.ItemDescription + 1));

        Assert.Equal(DomainErrorCode.InvalidInput, nameResult.Error!.Code);
        Assert.Equal(DomainErrorCode.InvalidInput, descriptionResult.Error!.Code);
        Assert.Empty(fixture.Journal.GetSnapshot().Items);
    }
}
