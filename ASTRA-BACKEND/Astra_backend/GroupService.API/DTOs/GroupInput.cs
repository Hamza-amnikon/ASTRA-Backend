namespace GroupService.API.DTOs;

public sealed record GroupInput
{
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public required string Kind { get; init; }
    public Guid? ClientId { get; init; }
    public required string[] Permissions { get; init; }
    public required Guid[] MemberIds { get; init; }
    public required bool Active { get; init; }
    public int? Revision { get; init; }
}
