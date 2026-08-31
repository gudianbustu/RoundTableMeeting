using System.Text.Json.Serialization;

namespace RoundTableMeeting.Abstraction.Enums;

[JsonConverter(typeof(JsonStringEnumConverter<ParticipantKind>))]
public enum ParticipantKind
{
    [JsonStringEnumMemberName("human")] Human,
    [JsonStringEnumMemberName("agent")] Agent
}
