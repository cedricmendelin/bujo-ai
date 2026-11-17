using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Bujo.Host.Authentication;

// Every event is gated too: an already-open circuit cannot keep using a revoked identity.
public sealed class ApprovedCircuitHandler : CircuitHandler, IDisposable
{
    private readonly AuthenticationStateProvider _authenticationState;
    private readonly IAuthorizationService _authorization;
    private Task<AuthenticationState> _state;

    public ApprovedCircuitHandler(AuthenticationStateProvider authenticationState, IAuthorizationService authorization)
    {
        _authenticationState = authenticationState;
        _authorization = authorization;
        authenticationState.AuthenticationStateChanged += OnAuthenticationStateChanged;
        _state = authenticationState.GetAuthenticationStateAsync();
    }

    private void OnAuthenticationStateChanged(Task<AuthenticationState> state) => _state = state;

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken) =>
        DemandAuthorizationAsync();

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken) =>
        DemandAuthorizationAsync();

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next) => async context =>
    {
        await DemandAuthorizationAsync();
        await next(context);
    };

    public async Task DemandAuthorizationAsync()
    {
        var state = await _state;
        if (!(await _authorization.AuthorizeAsync(state.User, BffAuthentication.UiPolicy)).Succeeded)
            throw new UnauthorizedAccessException("The session is no longer authorized. Reload and sign in.");
    }

    public void Dispose() => _authenticationState.AuthenticationStateChanged -= OnAuthenticationStateChanged;
}
