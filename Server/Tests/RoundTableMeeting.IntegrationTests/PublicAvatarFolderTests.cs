using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using RoundTableMeeting.Domain.Repositories;

namespace RoundTableMeeting.IntegrationTests;

public sealed class PublicAvatarFolderTests
{
    [Fact]
    public async Task UpgradeExportsPublicUploadsAndPreservesChoicesAfterRenameAndRemoval()
    {
        using var template = new TestApplicationFactory();
        using var templateClient = template.CreateClient();
        var directory = Directory.CreateTempSubdirectory("rt-avatar-upgrade-");
        try
        {
            var database = Path.Combine(directory.FullName, "upgrade.db");
            using (var source = new SqliteConnection($"Data Source={template.DatabasePath};Pooling=False"))
            using (var target = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                source.Open(); target.Open(); source.BackupDatabase(target);
                using var seed = target.CreateCommand();
                seed.CommandText = """
                    INSERT INTO AvatarAssets VALUES ('old1','重复',NULL,X'010203'),('old2','重复',NULL,X'040506');
                    INSERT INTO AvatarSelections VALUES ('00000000-0000-0000-0000-000000000001',1,'/api/avatars/old1/image'),
                    ('00000000-0000-0000-0000-000000000002',1,'/avatars/builtin/gpt.png');
                    """;
                seed.ExecuteNonQuery();
            }
            using (var app = new TestApplicationFactory(database))
            using (var admin = app.CreateClient())
            {
                admin.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
                (await admin.PostAsJsonAsync("/api/auth/login", new { username = "alice", password = "test-password-alice" }, cancellationToken: TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
                async Task Edit(string action, string group, string? name = null, int direction = 0, string? target = null, bool after = false) =>
                    (await admin.PostAsJsonAsync("/api/avatars/groups", new { action, group, name, direction, target, after })).EnsureSuccessStatusCode();
                async Task<AvatarEntry[]> Catalog() => (await admin.GetFromJsonAsync<AvatarEntry[]>("/api/avatars"))!;
                var original = (await Catalog()).Where(a => a.Category == "自定义").ToArray();
                Assert.Equal(2, original.Length);
                Assert.Equal(new byte[] { 1, 2, 3 }, await admin.GetByteArrayAsync(original[0].ImageUrl, TestContext.Current.CancellationToken));
                await Edit("renameGroup", "自定义", "新名字");
                var group = Path.Combine(directory.FullName, "Avatars", "新名字");
                await File.WriteAllBytesAsync(Path.Combine(group, "手工添加.webp"), [8], TestContext.Current.CancellationToken);
                Assert.Equal("手工添加", (await Catalog()).Last().Name);
                await Edit("moveAvatar", "新名字", "手工添加.webp", -1);
                Assert.Equal("手工添加", (await Catalog())[^2].Name);
                await Edit("createGroup", "空组");
                await Edit("moveGroup", "空组", direction: -1);
                var groups = await admin.GetFromJsonAsync<RoundTableMeeting.Avatars.AvatarGroup[]>("/api/avatars/groups", cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal("空组", groups![^2].Name);
                var first = (await Catalog()).First(a => a.Category == "新名字");
                await Edit("reorderGroup", "空组", target: "模型");
                Assert.Equal("空组", (await admin.GetFromJsonAsync<RoundTableMeeting.Avatars.AvatarGroup[]>("/api/avatars/groups", cancellationToken: TestContext.Current.CancellationToken))![0].Name);
                await Edit("reorderAvatar", "新名字", "重复.png", target: "重复 (2).png", after: true);
                await Edit("renameAvatar", "新名字", "重复.png", target: "改名");
                first = (await Catalog()).Last();
                Assert.Equal("改名", first.Name);
                Assert.Equal(new byte[] { 1, 2, 3 }, await admin.GetByteArrayAsync(first.ImageUrl, TestContext.Current.CancellationToken));
                await Edit("deleteAvatar", "新名字", "改名.png");
                Assert.DoesNotContain(await Catalog(), a => a.Id == first.Id);
                Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(first.ImageUrl, TestContext.Current.CancellationToken)).StatusCode);
                await Edit("deleteGroup", "新名字");
                Assert.Equal(new byte[] { 1, 2, 3 }, await admin.GetByteArrayAsync(first.ImageUrl, TestContext.Current.CancellationToken));
                using var inspect = new SqliteConnection($"Data Source={database};Pooling=False");
                inspect.Open();
                using var check = inspect.CreateCommand();
                check.CommandText = "SELECT Url FROM AvatarSelections ORDER BY OwnerId";
                using var rows = check.ExecuteReader();
                Assert.True(rows.Read()); Assert.Equal(first.ImageUrl, rows.GetString(0));
                Assert.True(rows.Read()); Assert.Equal((await Catalog()).Single(a => a.Category == "模型" && a.Name == "GPT").ImageUrl, rows.GetString(0));
            }
            using var restarted = new TestApplicationFactory(database);
            using var client = restarted.CreateClient();
            Assert.False(Directory.Exists(Path.Combine(directory.FullName, "Avatars", "自定义")));
            Assert.False(Directory.Exists(Path.Combine(directory.FullName, "Avatars", "新名字")));
            using var verify = new SqliteConnection($"Data Source={database};Pooling=False");
            verify.Open();
            using var count = verify.CreateCommand();
            count.CommandText = "SELECT count(*) FROM AvatarAssets WHERE OwnerId IS NULL";
            Assert.Equal(0L, count.ExecuteScalar());
        }
        finally { directory.Delete(true); }
    }
}
