namespace RoundTableMeeting.Abstraction.Contracts.Authentication;

/// <summary>
/// 表示应用启动后必须存在的种子账号
/// </summary>
public sealed record SeedAccountInput(string Username, string Nickname, string Password);
