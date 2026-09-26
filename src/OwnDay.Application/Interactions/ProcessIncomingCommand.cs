using OwnDay.Domain;

namespace OwnDay.Application.Interactions;

public sealed record ProcessIncomingCommand(string Name, string Arguments, UserId UserId = default);
