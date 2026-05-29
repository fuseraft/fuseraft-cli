using fuseraft.Core;

namespace fuseraft.Core.Models;

/// <summary>
/// Configuration for the shared agent chatroom log.
/// </summary>
public record ChatroomConfig
{
    /// <summary>
    /// File path where chatroom messages are appended as JSONL.
    /// The directory is created automatically.
    /// Example: <c>".fuseraft/comms/chatroom.jsonl"</c>
    /// </summary>
    public string Path { get; init; } = FuseraftPaths.LocalChatroom;
}
