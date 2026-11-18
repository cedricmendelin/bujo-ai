namespace Bujo.Web.Components;

using Microsoft.JSInterop;

public abstract class PlanningPage : JournalPage
{
    private bool _zoneRead;
    protected string? Zone { get; private set; }

    protected async Task ReadZoneAsync()
    {
        if (_zoneRead) return;
        try { Zone = await JS.InvokeAsync<string?>("bujoPwa.getTimeZone"); }
        catch (JSException) { Zone = null; }
        _zoneRead = true;
    }

    protected override Task InitializeAsync() => ReadZoneAsync();
}
