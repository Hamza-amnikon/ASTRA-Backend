namespace ClientDomainService.API.DTOs;

public sealed record DomainInput
{
    public required Guid ClientId { get; init; }
    public required string Domain { get; init; }
    public Guid? SiteId { get; init; }
    public required bool Active { get; init; }
    public int? Revision { get; init; }
}
