using Bujo.Application;
using Bujo.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Bujo.Web.Components;

public abstract class JournalPage : ComponentBase, IDisposable
{
    [Inject] protected JournalApplicationService Journal { get; set; } = null!;
    [Inject] protected ReviewApplicationService Reviews { get; set; } = null!;
    [Inject] protected IJSRuntime JS { get; set; } = null!;
    [Inject] private AuthenticationStateProvider Authentication { get; set; } = null!;
    [Inject] private IAuthorizationService Authorization { get; set; } = null!;
    [Inject] private ILogger<JournalPage> Logger { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;

    private DotNetObjectReference<JournalPage>? _probe;
    private bool _initialized;
    protected bool Busy { get; private set; }
    protected bool Ready { get; private set; }
    protected string? Error { get; private set; }
    protected string? Confirmation { get; private set; }

    protected abstract void Read();

    protected override void OnInitialized() => Authentication.AuthenticationStateChanged += AuthenticationChanged;

    private void AuthenticationChanged(Task<AuthenticationState> state) =>
        _ = InvokeAsync(() => Navigation.NavigateTo("/", forceLoad: true));

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        await RefreshAsync();
        StateHasChanged();
    }

    protected virtual Task InitializeAsync() => Task.CompletedTask;

    protected async Task<ReviewActor> GetReviewActorAsync()
    {
        var principal = (await Authentication.GetAuthenticationStateAsync()).User;
        if (!(await Authorization.AuthorizeAsync(principal, "ApprovedUi")).Succeeded)
        {
            throw new DomainException(DomainErrorCode.ApprovalDenied, "Your journal session is unavailable.");
        }

        return ReviewActor.FromPrincipal(principal, ReviewProposalSource.Pwa, interactive: true);
    }

    protected void SetError(string message) => Error = message;

    [JSInvokable]
    public bool CheckConnection() => true;

    private async Task<bool> IsApprovedAsync() =>
        (await Authorization.AuthorizeAsync((await Authentication.GetAuthenticationStateAsync()).User,
            "ApprovedUi")).Succeeded;

    protected async Task RefreshAsync()
    {
        if (Busy) return;
        Busy = true;
        Error = Confirmation = null;
        try
        {
            if (await IsApprovedAsync())
            {
                if (!_initialized)
                {
                    _probe ??= DotNetObjectReference.Create(this);
                    await JS.InvokeVoidAsync("bujoPwa.registerCircuitProbe", _probe);
                    await InitializeAsync();
                    _initialized = true;
                }
                Read();
                Ready = true;
            }
            else
            {
                Error = "Your journal session is unavailable. Sign in again.";
                Ready = false;
            }
        }
        catch (DomainException ex)
        {
            Error = ex.Message;
        }
        catch (JSDisconnectedException)
        {
            Error = "Connection interrupted while opening the journal. Reconnect and refresh.";
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Journal read failed ({ExceptionType})", ex.GetType().Name);
            Error = "Could not load the journal. Refresh to try again.";
        }
        finally { Busy = false; }
    }

    protected async Task SaveAsync<T>(Func<DomainResult<T>> command, Func<T, string> confirm,
        Action<DomainError>? rejected = null)
    {
        if (Busy || !Ready) return;
        Busy = true;
        Error = Confirmation = null;
        try
        {
            if (!await JS.InvokeAsync<bool>("bujoPwa.writesAvailable"))
            {
                Error = "Not saved. Reconnect, then try again.";
                return;
            }
            if (!await IsApprovedAsync())
            {
                Error = "Not saved. Your journal session is unavailable. Sign in again.";
                return;
            }

            var result = command();
            if (!result.IsSuccess)
            {
                var error = result.Error!;
                Error = error.Code is DomainErrorCode.StaleOccurrence or DomainErrorCode.ConcurrencyConflict
                    ? $"{error.Message} Refresh the journal and review your draft before trying again."
                    : error.Message;
                rejected?.Invoke(error);
                return;
            }

            Confirmation = confirm(result.Value!);
            try { Read(); }
            catch (Exception ex)
            {
                Logger.LogWarning("Journal refresh after save failed ({ExceptionType})", ex.GetType().Name);
                Error = "Saved, but the latest journal could not be loaded. Refresh before making more changes.";
                Ready = false;
            }
        }
        catch (JSDisconnectedException)
        {
            Error = "Save not confirmed. Reconnect and check the journal before trying again.";
        }
        catch (DomainException ex) { Error = ex.Message; }
        catch (Exception ex)
        {
            Logger.LogWarning("Journal save not confirmed ({ExceptionType})", ex.GetType().Name);
            Error = "Save not confirmed. Check the latest journal before trying again.";
        }
        finally { Busy = false; }
    }

    public void Dispose()
    {
        Authentication.AuthenticationStateChanged -= AuthenticationChanged;
        _probe?.Dispose();
    }
}
