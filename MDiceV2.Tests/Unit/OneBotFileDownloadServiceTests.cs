using System.Text.Json;
using FluentAssertions;
using MDiceV2.Models;
using Xunit;

namespace MDiceV2.Tests.Unit;

public sealed class OneBotFileDownloadServiceTests
{
    [Fact]
    public async Task GroupFile_UsesGroupFileUrlBeforeGenericGetFile()
    {
        var source = Path.Combine(Path.GetTempPath(), $"mmod-source-{Guid.NewGuid():N}.mmod");
        await File.WriteAllTextAsync(source, "portable-mod");
        var actions = new List<string>();
        OneBotFileDownloadResult? result = null;
        try
        {
            var service = new OneBotFileDownloadService(
                () => null,
                requestSender: (request, _) =>
                {
                    actions.Add((string)request["action"]);
                    var parameters = (Dictionary<string, object>)request["params"];
                    parameters["group_id"].Should().Be(123L);
                    parameters["file_id"].Should().Be("group-file-id");
                    parameters["busid"].Should().Be(456L);
                    using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
                    {
                        status = "ok",
                        data = new { path = source, name = "MeguminDuel.mmod" }
                    }));
                    return Task.FromResult<JsonElement?>(document.RootElement.Clone());
                });

            result = await service.DownloadAsync(new OneBotFileInfo
            {
                SourceKind = "group_message",
                GroupId = 123,
                UserId = 789,
                BusId = 456,
                FileId = "group-file-id",
                FileName = "MeguminDuel.mmod"
            });

            result.Success.Should().BeTrue(result.ErrorMessage);
            actions.Should().Equal("get_group_file_url");
            (await File.ReadAllTextAsync(result.LocalPath!)).Should().Be("portable-mod");
        }
        finally
        {
            if (File.Exists(source)) File.Delete(source);
            if (result?.LocalPath is { } downloaded && File.Exists(downloaded)) File.Delete(downloaded);
        }
    }
}
