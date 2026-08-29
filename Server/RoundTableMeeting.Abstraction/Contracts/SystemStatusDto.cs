namespace RoundTableMeeting.Abstraction.Contracts;

public sealed record SystemStatusDto(string Status, DatabaseStatusDto Database, string Environment);
