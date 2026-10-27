namespace TicketService.API.DTOs;

public sealed class TicketQuery
{
    public string? Q { get; set; }
    public string? Scope { get; set; }
    public string? Status { get; set; }
    public string? Priority { get; set; }
    public Guid? ClientId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? AssignedUserId { get; set; }
    public Guid? CategoryId { get; set; }
    public Guid? TagId { get; set; }
    public string? Source { get; set; }
    public string? SlaStatus { get; set; }
    public string? DashboardFilter { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
    public int? Page { get; set; }
    public int? PageSize { get; set; }
}
