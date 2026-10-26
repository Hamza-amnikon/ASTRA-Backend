namespace Astra.Api.Models;

public sealed record CreateProblemInput(Guid ClientId, Guid? SiteId, string Title, string Description, string Priority = "P3_MEDIUM");
