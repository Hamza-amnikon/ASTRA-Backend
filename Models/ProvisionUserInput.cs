namespace Astra.Api.Models;

public sealed record ProvisionUserInput(Guid EntraObjectId, string[] Roles, Guid[] ClientIds, Guid[]? SiteIds = null, string[]? Permissions = null, string? WorkScope = null, string? ChangeAccess = null, Guid? SupervisorUserId = null, Guid[]? DepartmentIds = null, Guid[]? LeadDepartmentIds = null, string? Expertise = null);
