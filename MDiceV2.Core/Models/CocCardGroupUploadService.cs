namespace MDiceV2.Models;

public sealed record CocCardGroupUploadResult(
    bool Success,
    bool UsedMdiceFallback,
    OneBotGroupFileUploadResult HtmlAttempt,
    OneBotGroupFileUploadResult? MdiceAttempt = null,
    string? FallbackPreparationError = null,
    string? FallbackFilePath = null);

/// <summary>
/// Uploads the CoC card as HTML first, then retries with an identical .mdice
/// copy when the adapter or QQ rejects/times out on the HTML upload.
/// </summary>
public sealed class CocCardGroupUploadService
{
    private static readonly SemaphoreSlim UploadLock = new(1, 1);
    private readonly Func<long, string, string, Task<OneBotGroupFileUploadResult>> _uploader;

    public CocCardGroupUploadService(
        Func<long, string, string, Task<OneBotGroupFileUploadResult>> uploader)
    {
        _uploader = uploader ?? throw new ArgumentNullException(nameof(uploader));
    }

    public async Task<CocCardGroupUploadResult> UploadAsync(long groupId, string htmlPath)
    {
        await UploadLock.WaitAsync();
        try
        {
            var htmlAttempt = await TryUploadAsync(groupId, htmlPath, Path.GetFileName(htmlPath));
            if (htmlAttempt.Success)
            {
                return new(true, false, htmlAttempt);
            }

            string mdicePath;
            try
            {
                mdicePath = Path.ChangeExtension(htmlPath, ".mdice");
                File.Copy(htmlPath, mdicePath, overwrite: true);
            }
            catch (Exception ex)
            {
                return new(false, true, htmlAttempt, FallbackPreparationError: ex.Message);
            }

            var mdiceAttempt = await TryUploadAsync(groupId, mdicePath, Path.GetFileName(mdicePath));
            return new(
                mdiceAttempt.Success,
                true,
                htmlAttempt,
                mdiceAttempt,
                FallbackFilePath: mdicePath);
        }
        finally
        {
            UploadLock.Release();
        }
    }

    private async Task<OneBotGroupFileUploadResult> TryUploadAsync(
        long groupId,
        string filePath,
        string name)
    {
        try
        {
            return await _uploader(groupId, filePath, name);
        }
        catch (Exception ex)
        {
            return new(false, $"OneBot 上传调用异常：{ex.Message}");
        }
    }

    public static string CompactFailureMessage(string? message, int maxLength = 240)
    {
        if (string.IsNullOrWhiteSpace(message)) return "未知错误";

        var compact = string.Join(' ', message
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim()));

        var invocationDetails = compact.IndexOf(", wrapperSession.", StringComparison.OrdinalIgnoreCase);
        if (invocationDetails >= 0)
        {
            compact = compact[..invocationDetails];
        }

        return compact.Length <= maxLength
            ? compact
            : compact[..maxLength] + "...";
    }
}
