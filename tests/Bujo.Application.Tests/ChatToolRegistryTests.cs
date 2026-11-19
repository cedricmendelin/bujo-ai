using System.Security.Claims;
using System.Text.Json;
using Bujo.Application.Chat;
using Bujo.Domain;
using Bujo.Infrastructure.Persistence;

namespace Bujo.Application.Tests;

public sealed class ChatToolRegistryTests
{
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
