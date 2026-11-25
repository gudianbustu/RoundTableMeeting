using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace RoundTableMeeting.IntegrationTests;

public sealed class MeetingInformationTests
{
    [Fact]
    public async Task EditPreservesOriginalCreateReceiptAndValidatesBeforeMutation()
    {
        using var site = new TestApplicationFactory();
        using var owner = site.CreateClient();
        owner.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        using var login = await owner.PostAsJsonAsync("/api/auth/login", new { username = "alice", password = "test-password-alice" });
        login.EnsureSuccessStatusCode();
        var creationRequestId = Guid.NewGuid();
        var original = new { creationRequestId, name = "Original", description = "Before" };
        using var created = await owner.PostAsJsonAsync("/api/meetings", original);
        created.EnsureSuccessStatusCode();
        var data = await created.Content.ReadFromJsonAsync<JsonElement>();
        var endpoint = "/api/meetings/" + data.GetProperty("meeting").GetProperty("meetingId").GetString();
        using var updated = await owner.PutAsJsonAsync(endpoint + "/information", new { name = "  Updated  ", description = "  " });
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        using var invalid = await owner.PutAsJsonAsync(endpoint + "/information", new { name = "Invalid", description = new string('x', 2001) });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var replay = await owner.PostAsJsonAsync("/api/meetings", original);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var result = (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("meeting");
        Assert.Equal("Updated", result.GetProperty("name").GetString());
        Assert.False(result.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String);
        using var mismatch = await owner.PostAsJsonAsync("/api/meetings", new { creationRequestId, name = "Updated" });
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
    }
}
