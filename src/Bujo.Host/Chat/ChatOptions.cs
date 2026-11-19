namespace Bujo.Host.Chat;

public sealed class ChatOptions
{
    public const string SectionName = "Chat";

    public bool Enabled { get; set; }
    public string Provider { get; set; } = "Mock";
    public string? Endpoint { get; set; }
    public string? Deployment { get; set; }
    public string ApiVersion { get; set; } = "2024-10-21";
    public string RecognitionLanguage { get; set; } = "en-US";
    public string? SpeechEndpoint { get; set; }
    public string? SpeechRegion { get; set; }
    public int MaxToolCallsPerRun { get; set; } = 8;
}
