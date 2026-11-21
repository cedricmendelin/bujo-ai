using System.Security.Claims;
using System.Text.Json;
using Bujo.Application.Chat;
using Bujo.Domain;
using Bujo.Infrastructure.Persistence;

namespace Bujo.Application.Tests;

public sealed class ChatToolRegistryTests
{
    [Fact]
    public void CreateListRejectsNonObjectWeekKeysWithoutCreatingLists()
    {
        var repository = InMemoryJournalRepository.CreateWithFutureLog(Guid.NewGuid());
        var executor = new ChatToolExecutor(new JournalApplicationService(new JournalService(repository)),
            new ReviewApplicationService(repository));
        ChatToolResult Create(string json) => executor.Execute(new ChatToolRequest(
            "create_list", JsonDocument.Parse(json).RootElement.Clone(), Principal()));

        foreach (var malformed in new[] { "\"not a week\"", "[]", "123", "true" })
        {
            var result = Create("""{"name":"Bad","kind":"Monthly","weekKey":""" + malformed +
                ""","periodKey":{"kind":"Monthly","year":2026,"month":1}}""");
            Assert.False(result.Succeeded);
            Assert.Contains("week key", result.Summary, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Single(repository.GetSnapshot().Lists);
        Assert.True(Create("""{"name":"Monthly null","kind":"Monthly","weekKey":null,"periodKey":{"kind":"Monthly","year":2026,"month":1}}""").Succeeded);
        Assert.True(Create("""{"name":"Monthly absent","kind":"Monthly","periodKey":{"kind":"Monthly","year":2026,"month":1}}""").Succeeded);
        Assert.True(Create("""{"name":"Week","kind":"Weekly","weekKey":{"year":2020,"week":53}}""").Succeeded);
        Assert.Equal(new IsoWeekKey(2020, 53), repository.GetSnapshot().Lists.Single(list => list.Name == "Week").WeekKey);
    }

    [Fact]
    public void CreateListChatToolValidatesCalendarKeys()
    {
        var repository = InMemoryJournalRepository.CreateWithFutureLog(Guid.NewGuid());
        var executor = new ChatToolExecutor(new JournalApplicationService(new JournalService(repository)),
            new ReviewApplicationService(repository));
        ChatToolResult Create(string json) => executor.Execute(new ChatToolRequest(
            "create_list", JsonDocument.Parse(json).RootElement.Clone(), Principal()));
        var schema = ChatToolRegistry.ToJsonSchema(ChatToolRegistry.Get("create_list"));
        Assert.Equal("object", schema.GetProperty("properties").GetProperty("periodKey").GetProperty("type").GetString());
        var result = Create("""{"name":"Q4","kind":"Quarterly","periodKey":{"kind":"Quarterly","year":2026,"quarter":4}}""");
        Assert.True(result.Succeeded, result.Summary);
        Assert.Equal(new JournalPeriodKey(JournalListKind.Quarterly, 2026, quarter: 4),
            Assert.IsType<JournalListSnapshot>(result.Value).PeriodKey);
        Assert.False(Create("""{"name":"Bad","kind":"Custom","periodKey":{"kind":"Yearly","year":2026}}""").Succeeded);
        Assert.False(Create("""{"name":"Bad","kind":"Yearly","periodKey":{"kind":"Yearly","year":2026,"month":"bad"}}""").Succeeded);
        Assert.Single(repository.GetSnapshot().Lists, list => list.Kind == JournalListKind.Quarterly);
    }

    [Fact]
    public void RegistryContainsThe22CurrentJournalTools()
    {
        var expected = new[]
        {
            "get_journal",
            "get_daily_start",
            "get_upcoming",
            "find_lists",
            "capture_item",
            "create_list",
            "resolve_occurrence",
            "edit_item",
            "set_planned_period",
            "set_list_goal",
            "close_list",
            "reopen_list",
            "move_item",
            "complete_occurrence",
            "abandon_occurrence",
            "reopen_occurrence",
            "get_list_review",
            "create_review_proposal",
            "get_review_proposal",
            "cancel_review_proposal",
            "reject_review_proposal",
            "apply_review_proposal"
        };

        Assert.Equal(22, ChatToolRegistry.All.Count);
        Assert.Equal(expected.Order(), ChatToolRegistry.All.Select(tool => tool.Name).Order());
        Assert.All(new[]
        {
            "close_list", "move_item", "abandon_occurrence", "create_review_proposal",
            "cancel_review_proposal", "reject_review_proposal", "apply_review_proposal"
        }, name => Assert.True(ChatToolRegistry.Get(name).RequiresApproval, name));
    }

    [Fact]
    public void CaptureAndFindListUseTheSharedApplicationServices()
    {
        var repository = InMemoryJournalRepository.CreateWithFutureLog(
            Guid.Parse("00000000-0000-0000-0000-000000000101"));
        var journal = new JournalApplicationService(new JournalService(repository));
        var reviews = new ReviewApplicationService(repository);
        var executor = new ChatToolExecutor(journal, reviews);
        var principal = Principal();

        var capture = executor.Execute(new ChatToolRequest(
            "capture_item",
            JsonSerializer.SerializeToElement(new
            {
                name = "Chat task",
                plannedPeriod = new { kind = "unplanned" }
            }),
            principal));

        Assert.True(capture.Succeeded);
        Assert.Contains(journal.GetSnapshot().Items, item => item.Name == "Chat task");

        var createdList = journal.CreateList("Projects", JournalListKind.Custom);
        Assert.True(createdList.IsSuccess);
        var find = executor.Execute(new ChatToolRequest(
            "find_lists",
            JsonSerializer.SerializeToElement(new { name = "Projects" }),
            principal));

        Assert.True(find.Succeeded);
        Assert.Equal(createdList.Value!.Id, Assert.IsType<JournalListSnapshot>(find.Value).Id);
    }

    [Fact]
    public void CaptureSchemaAndExecutionHandleTypeAndRejectInvalidInput()
    {
        var schema = ChatToolRegistry.ToJsonSchema(ChatToolRegistry.Get("capture_item"));
        var typeSchema = schema.GetProperty("properties").GetProperty("type");
        Assert.Equal("string", typeSchema.GetProperty("type").GetString());
        Assert.Contains("Thought", typeSchema.GetProperty("description").GetString());
        Assert.DoesNotContain("type", schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));

        var repository = InMemoryJournalRepository.CreateWithFutureLog(Guid.NewGuid());
        var journal = new JournalApplicationService(new JournalService(repository));
        var executor = new ChatToolExecutor(journal, new ReviewApplicationService(repository));
        ChatToolResult Capture(string json) => executor.Execute(new ChatToolRequest(
            "capture_item", JsonDocument.Parse(json).RootElement.Clone(), Principal()));

        foreach (var (json, expected) in new[]
        {
            ("""{"name":"Legacy"}""", ItemType.Task),
            ("""{"name":"Null","type":null}""", ItemType.Task),
            ("""{"name":"Task","type":"tAsK"}""", ItemType.Task),
            ("""{"name":"Reflection","type":"tHoUgHt"}""", ItemType.Thought)
        })
        {
            var result = Capture(json);
            Assert.True(result.Succeeded, result.Summary);
            var saved = Assert.IsType<CaptureResult>(result.Value);
            Assert.Equal(expected, saved.Item.Type);
            Assert.Equal(expected, journal.GetSnapshot().Items.Single(item => item.Id == saved.Item.Id).Type);
        }

        var count = journal.GetSnapshot().Items.Count;
        foreach (var json in new[]
        {
            """{"name":"Bad","type":""}""",
            """{"name":"Bad","type":"1"}""",
            """{"name":"Bad","type":"other"}""",
            """{"name":"Bad","type":1}""",
            """{"name":"Bad","type":true}"""
        })
        {
            var result = Capture(json);
            Assert.False(result.Succeeded, json);
            Assert.Equal(CaptureItemType.InvalidMessage, result.Summary);
            Assert.Equal(count, journal.GetSnapshot().Items.Count);
        }
    }

    [Fact]
    public void ApprovalGatedToolsCannotBeExecutedByChat()
    {
        var repository = InMemoryJournalRepository.CreateWithFutureLog(
            Guid.Parse("00000000-0000-0000-0000-000000000101"));
        var journal = new JournalApplicationService(new JournalService(repository));
        var reviews = new ReviewApplicationService(repository);
        var executor = new ChatToolExecutor(journal, reviews);
        var principal = Principal();

        var list = journal.CreateList("Projects", JournalListKind.Custom);
        Assert.True(list.IsSuccess);
        var captured = journal.Capture("Open task", null, null, null, list.Value!.Id);
        Assert.True(captured.IsSuccess);
        var review = reviews.GetListReview(list.Value.Id);
        Assert.True(review.IsSuccess);

        var proposal = executor.Execute(new ChatToolRequest(
            "create_review_proposal",
            JsonSerializer.SerializeToElement(new
            {
                listId = list.Value.Id,
                expectedListVersion = review.Value!.List.Version,
                requestCloseList = false,
                changes = new[]
                {
                    new
                    {
                        itemId = captured.Value!.Item.Id,
                        occurrenceId = captured.Value.Occurrence.Id,
                        expectedItemVersion = captured.Value.Item.Version,
                        action = "Abandon"
                    }
                }
            }),
            principal));

        Assert.True(proposal.Succeeded, proposal.Summary);
        var created = Assert.IsType<ReviewProposalSnapshot>(proposal.Value);
        Assert.Equal(ReviewProposalStatus.Pending, created.Status);
        Assert.Equal(OccurrenceState.Open,
            journal.GetSnapshot().Occurrences.Single(item => item.Id == captured.Value.Occurrence.Id).State);

        foreach (var descriptor in ChatToolRegistry.All.Where(tool =>
                     tool.RequiresApproval && tool.Name != ChatToolNames.CreateReviewProposal))
        {
            var result = executor.Execute(new ChatToolRequest(
                descriptor.Name,
                JsonSerializer.SerializeToElement(new
                {
                    proposalId = created.Id,
                    listId = list.Value.Id,
                    itemId = captured.Value.Item.Id,
                    expectedOccurrenceId = captured.Value.Occurrence.Id,
                    destinationListId = list.Value.Id,
                    operationId = Guid.NewGuid()
                }),
                principal));

            Assert.False(result.Succeeded, descriptor.Name);
            Assert.Contains("requires an explicit", result.Summary);
        }

        var stored = reviews.GetProposal(created.Id,
            ReviewActor.FromPrincipal(principal, ReviewProposalSource.Ceremony, false));
        Assert.True(stored.IsSuccess);
        Assert.Equal(ReviewProposalStatus.Pending, stored.Value!.Status);
        Assert.Equal(OccurrenceState.Open,
            journal.GetSnapshot().Occurrences.Single(item => item.Id == captured.Value.Occurrence.Id).State);
        Assert.Equal(JournalListState.Open,
            journal.GetSnapshot().Lists.Single(item => item.Id == list.Value.Id).State);
    }

    [Fact]
    public void CloseOnlyReviewProposalRequiresApproval()
    {
        var repository = InMemoryJournalRepository.CreateWithFutureLog(
            Guid.Parse("00000000-0000-0000-0000-000000000101"));
        var journal = new JournalApplicationService(new JournalService(repository));
        var reviews = new ReviewApplicationService(repository);
        var executor = new ChatToolExecutor(journal, reviews);
        var principal = Principal();

        var list = journal.CreateList("Projects", JournalListKind.Custom);
        Assert.True(list.IsSuccess);
        var review = reviews.GetListReview(list.Value!.Id);
        Assert.True(review.IsSuccess);

        var proposal = reviews.CreateProposal(
            new ReviewProposalRequest(
                list.Value.Id,
                review.Value!.List.Version,
                true,
                []),
            ReviewActor.FromPrincipal(principal, ReviewProposalSource.Ceremony, false));

        Assert.True(proposal.IsSuccess, proposal.Error?.Message);
        var created = proposal.Value!;
        Assert.True(created.RequestCloseList);
        Assert.Empty(created.Changes);
        Assert.Equal(JournalListState.Open,
            journal.GetSnapshot().Lists.Single(item => item.Id == list.Value.Id).State);
    }

    private static ClaimsPrincipal Principal() =>
        new(new ClaimsIdentity(
        [
            new Claim("tid", "b3f84e9d-2dfb-4e8d-a1c3-91f7d8f59ca7"),
            new Claim("oid", "2b4d69e1-46a1-4bd0-9a9e-c47be64c6d72")
        ], "Test"));
}
