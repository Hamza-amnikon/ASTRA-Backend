namespace ProblemService.API.DTOs;

public sealed record UpdateProblemInput(int Revision, string Title, string Description, string Priority, string Status, string RootCause, string Workaround, string Resolution, Guid OwnerUserId);
