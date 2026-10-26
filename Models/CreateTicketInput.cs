namespace Astra.Api.Models;

public sealed record CreateTicketInput(Guid ClientId, Guid RequesterId, Guid CategoryId, string Subject, string Description, string Source, string Priority, Guid? SiteId = null, Guid? DepartmentId = null, string? LocationText = null, Guid? AssignedUserId = null, string? SourceReferenceId = null, string? SourceConversationId = null, string? SourceMessageId = null, string? SourceUrl = null, string? ExternalThreadId = null, Guid[]? TagIds = null, TicketAttachmentInput[]? Attachments = null);
