namespace TicketService.API.DTOs;

public sealed record CreateRequesterInput(Guid ClientId, string Name, string? Email = null, string? Phone = null);
