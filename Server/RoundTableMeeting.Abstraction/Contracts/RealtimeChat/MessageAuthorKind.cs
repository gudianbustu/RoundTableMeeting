using System.Text.Json.Serialization;
namespace RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
[JsonConverter(typeof(JsonStringEnumConverter<MessageAuthorKind>))]
public enum MessageAuthorKind { [JsonStringEnumMemberName("human")] Human, [JsonStringEnumMemberName("agent")] Agent, [JsonStringEnumMemberName("system")] System }
