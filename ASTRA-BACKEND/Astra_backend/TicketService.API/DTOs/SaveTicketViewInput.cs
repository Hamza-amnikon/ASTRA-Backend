using System.Text.Json.Nodes;

namespace TicketService.API.DTOs;

public sealed record SaveTicketViewInput(string Name, JsonObject Query);
