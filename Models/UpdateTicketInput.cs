namespace Astra.Api.Models;

public sealed record UpdateTicketInput(string? Status = null, string? Priority = null, Guid? CategoryId = null, Guid[]? TagIds = null, string? Reason = null);
