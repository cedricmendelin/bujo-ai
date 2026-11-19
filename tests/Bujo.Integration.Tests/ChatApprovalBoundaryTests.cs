using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Bujo.Application;
using Bujo.Application.Chat;
using Bujo.Domain;
using Bujo.Host.Chat;
using Bujo.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Bujo.Integration.Tests;

public sealed class ChatApprovalBoundaryTests
{
    [Fact]
    public async Task FoundryToolCallsCannotCloseListsOrApplyPendingProposals()
    {
        var (journal, reviews) = Services();
        var list = journal.CreateList("Projects", JournalListKind.Custom).Value!;
        var principal = Principal();
        var proposal = reviews.CreateProposal(
            new ReviewProposalRequest(list.Id, list.Version, true, []),
            ReviewActor.FromPrincipal(principal, ReviewProposalSource.Ceremony, false)).Value!;
        var handler = new ModelHandler(list.Id, proposal.Id);
        var runtime = new ChatRuntime(journal, reviews, Options.Create(new ChatOptions
        {
            Provider = "Foundry",
            Endpoint = "https://example.invalid",
            Deployment = "test"
        }), new TestHttpClientFactory(handler), new TestCredential());

        var result = await runtime.RunAsync(new ChatRunRequest("Review the list", "UTC", []), principal);

        Assert.Equal(2, result.ToolCalls.Count);
        Assert.All(result.ToolCalls, call => Assert.False(call.Succeeded, call.Name));
        Assert.Equal(JournalListState.Open, journal.GetSnapshot().Lists.Single(item => item.Id == list.Id).State);
        var stored = reviews.GetProposal(proposal.Id,
            ReviewActor.FromPrincipal(principal, ReviewProposalSource.Ceremony, false));
        Assert.True(stored.IsSuccess);
        Assert.Equal(ReviewProposalStatus.Pending, stored.Value!.Status);
    }

    [Fact]
    public async Task MockMoveCannotBypassApprovalGate()
    {
        var (journal, reviews) = Services();
        var destination = journal.CreateList("Projects", JournalListKind.Custom).Value!;
        var item = journal.Capture("Report", null, null, null, null).Value!;
        var runtime = new ChatRuntime(journal, reviews, Options.Create(new ChatOptions()),
            new TestHttpClientFactory(new ModelHandler(destination.Id, Guid.NewGuid())));

        var result = await runtime.RunAsync(new ChatRunRequest("move Report to Projects", "UTC", []), Principal());

        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("move_item", call.Name);
        Assert.False(call.Succeeded);
        Assert.Contains("requires an explicit", result.Text);
        var snapshot = journal.GetSnapshot();
        Assert.Equal(item.Occurrence.Id, snapshot.Items.Single(entry => entry.Id == item.Item.Id).CurrentOccurrenceId);
        Assert.DoesNotContain(snapshot.MoveHistory, move => move.ItemId == item.Item.Id);
    }

    private static (JournalApplicationService Journal, ReviewApplicationService Reviews) Services()
    {
        var repository = InMemoryJournalRepository.CreateWithFutureLog(Guid.NewGuid());
        return (new JournalApplicationService(new JournalService(repository)), new ReviewApplicationService(repository));
    }

    private static ClaimsPrincipal Principal() => new(new ClaimsIdentity(
    [
        new Claim("tid", "b3f84e9d-2dfb-4e8d-a1c3-91f7d8f59ca7"),
        new Claim("oid", "2b4d69e1-46a1-4bd0-9a9e-c47be64c6d72")
    ], "Test"));

    private sealed class TestCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-token", DateTimeOffset.UtcNow.AddMinutes(5));

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ModelHandler(Guid listId, Guid proposalId) : HttpMessageHandler
    {
        private int _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = _calls++ == 0
                ? JsonSerializer.Serialize(new
                {
                    choices = new[]
                    {
                        new
                        {
                            message = new
                            {
                                role = "assistant",
                                tool_calls = new[]
                                {
                                    new { id = "one", type = "function", function = new { name = "close_list", arguments = JsonSerializer.Serialize(new { listId }) } },
                                    new { id = "two", type = "function", function = new { name = "apply_review_proposal", arguments = JsonSerializer.Serialize(new { proposalId }) } }
                                }
                            }
                        }
                    }
                })
                : """{"choices":[{"message":{"role":"assistant","content":"No changes made."}}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
