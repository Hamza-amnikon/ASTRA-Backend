namespace TicketService.API.DTOs;

public sealed record TicketAttachmentInput(string FileName, string ContentType, string ContentBase64, string? Visibility = null);
