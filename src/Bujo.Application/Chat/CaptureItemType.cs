using Bujo.Domain;

namespace Bujo.Application.Chat;

/// <summary>Parses the optional capture type shared by chat and MCP.</summary>
public static class CaptureItemType
{
    public const string InvalidMessage = "The item type must be Task or Thought.";

    public static bool TryParse(string? value, out ItemType type)
    {
        type = ItemType.Task;
        if (value is null || string.Equals(value, "Task", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, "Thought", StringComparison.OrdinalIgnoreCase))
        {
            type = ItemType.Thought;
            return true;
        }

        return false;
    }
}
