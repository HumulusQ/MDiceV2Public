using System.Text.Json;
using FluentAssertions;
using MDiceV2.Models;
using Xunit;

namespace MDiceV2.Tests.Unit;

public class OneBotGroupFileUploadTests
{
    [Fact]
    public void ParseResponse_AcceptsSuccessfulOneBotResponse()
    {
        using var document = JsonDocument.Parse("""
        {"status":"ok","retcode":0,"data":null,"echo":"upload-test"}
        """);

        var result = WSconnection.ParseUploadGroupFileResponse(document.RootElement);

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("上传成功");
    }

    [Fact]
    public void ParseResponse_ReportsRetCodeAndWordingOnFailure()
    {
        using var document = JsonDocument.Parse("""
        {"status":"failed","retcode":1200,"wording":"群文件上传被拒绝","echo":"upload-test"}
        """);

        var result = WSconnection.ParseUploadGroupFileResponse(document.RootElement);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("retcode=1200").And.Contain("群文件上传被拒绝");
    }

    [Fact]
    public void ParseResponse_ReportsTimeoutWhenNoResponseArrives()
    {
        var result = WSconnection.ParseUploadGroupFileResponse(null);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("超时");
    }

    [Fact]
    public void ParseResponse_RejectsAmbiguousResponse()
    {
        using var document = JsonDocument.Parse("""{"data":null,"echo":"upload-test"}""");

        var result = WSconnection.ParseUploadGroupFileResponse(document.RootElement);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("status/retcode");
    }

    [Fact]
    public async Task CocCardUploader_DoesNotFallbackWhenHtmlSucceeds()
    {
        var fixture = CreateCardFixture();
        try
        {
            var attempts = new List<(string Path, string Name)>();
            var uploader = new CocCardGroupUploadService((_, path, name) =>
            {
                attempts.Add((path, name));
                return Task.FromResult(new OneBotGroupFileUploadResult(true, "ok"));
            });

            var result = await uploader.UploadAsync(123, fixture.HtmlPath);

            result.Success.Should().BeTrue();
            result.UsedMdiceFallback.Should().BeFalse();
            attempts.Should().ContainSingle();
            attempts[0].Name.Should().EndWith(".html");
            File.Exists(fixture.MdicePath).Should().BeFalse();
        }
        finally
        {
            CleanupCardFixture(fixture);
        }
    }

    [Fact]
    public async Task CocCardUploader_RetriesWithIdenticalMdiceCopyAfterHtmlFailure()
    {
        var fixture = CreateCardFixture();
        try
        {
            var attempts = new List<(string Path, string Name)>();
            var uploader = new CocCardGroupUploadService((_, path, name) =>
            {
                attempts.Add((path, name));
                var success = attempts.Count == 2;
                return Task.FromResult(new OneBotGroupFileUploadResult(success, success ? "ok" : "html failed"));
            });

            var result = await uploader.UploadAsync(123, fixture.HtmlPath);

            result.Success.Should().BeTrue();
            result.UsedMdiceFallback.Should().BeTrue();
            attempts.Select(x => x.Name).Should().Equal("card.html", "card.mdice");
            attempts[1].Path.Should().Be(fixture.MdicePath);
            File.ReadAllBytes(fixture.MdicePath).Should().Equal(File.ReadAllBytes(fixture.HtmlPath));
        }
        finally
        {
            CleanupCardFixture(fixture);
        }
    }

    [Fact]
    public async Task CocCardUploader_ReturnsFailureOnlyAfterBothAttemptsFail()
    {
        var fixture = CreateCardFixture();
        try
        {
            var attempts = 0;
            var uploader = new CocCardGroupUploadService((_, _, _) =>
            {
                attempts++;
                return Task.FromResult(new OneBotGroupFileUploadResult(false, $"failure {attempts}"));
            });

            var result = await uploader.UploadAsync(123, fixture.HtmlPath);

            result.Success.Should().BeFalse();
            attempts.Should().Be(2);
            result.HtmlAttempt.Message.Should().Be("failure 1");
            result.MdiceAttempt!.Message.Should().Be("failure 2");
        }
        finally
        {
            CleanupCardFixture(fixture);
        }
    }

    [Fact]
    public async Task CocCardUploader_StillFallsBackWhenHtmlAttemptThrows()
    {
        var fixture = CreateCardFixture();
        try
        {
            var attempts = 0;
            var uploader = new CocCardGroupUploadService((_, _, _) =>
            {
                attempts++;
                if (attempts == 1) throw new InvalidOperationException("adapter failed");
                return Task.FromResult(new OneBotGroupFileUploadResult(true, "ok"));
            });

            var result = await uploader.UploadAsync(123, fixture.HtmlPath);

            result.Success.Should().BeTrue();
            result.UsedMdiceFallback.Should().BeTrue();
            attempts.Should().Be(2);
            result.HtmlAttempt.Message.Should().Contain("adapter failed");
        }
        finally
        {
            CleanupCardFixture(fixture);
        }
    }

    [Fact]
    public void CompactFailureMessage_RemovesVerboseNativeInvocationArguments()
    {
        var message = "OneBot 上传群文件失败（retcode=1200）：invoke timeout, wrapperSession.getMsgService().sendMsg, [large native arguments]";

        CocCardGroupUploadService.CompactFailureMessage(message)
            .Should().Be("OneBot 上传群文件失败（retcode=1200）：invoke timeout");
    }

    private static (string Directory, string HtmlPath, string MdicePath) CreateCardFixture()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MDiceV2.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var htmlPath = Path.Combine(directory, "card.html");
        File.WriteAllText(htmlPath, "<html><body>card</body></html>");
        return (directory, htmlPath, Path.ChangeExtension(htmlPath, ".mdice"));
    }

    private static void CleanupCardFixture((string Directory, string HtmlPath, string MdicePath) fixture)
    {
        if (File.Exists(fixture.HtmlPath)) File.Delete(fixture.HtmlPath);
        if (File.Exists(fixture.MdicePath)) File.Delete(fixture.MdicePath);
        if (Directory.Exists(fixture.Directory)) Directory.Delete(fixture.Directory);
    }
}
