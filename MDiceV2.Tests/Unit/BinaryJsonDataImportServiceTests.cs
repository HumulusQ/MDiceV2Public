using System.Text.Json;
using FluentAssertions;
using MDiceV2.Models;
using Xunit;

namespace MDiceV2.Tests.Unit;

public class BinaryJsonDataImportServiceTests
{
    [Fact]
    public void Import_preserves_local_custom_text_when_uploaded_template_uses_a_default()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mdice-import-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "source.db");
        var targetPath = Path.Combine(root, "target.db");
        DataIO? source = null;
        DataIO? target = null;

        try
        {
            var defaults = GlobalFeedbackMessages.GetDefaultFeedbackTemplates();
            source = new DataIO(sourcePath);
            source.SaveData(BinaryJsonDataImportService.TableName, "FeedbackTemplate", JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["RollResult"] = defaults["RollResult"],
                ["RollParamOutOfRange"] = "uploaded custom text"
            }));
            source.SaveData(BinaryJsonDataImportService.TableName, "OtherSetting", "uploaded value");
            source.Close();
            source = null;

            target = new DataIO(targetPath);
            target.SaveData(BinaryJsonDataImportService.TableName, "FeedbackTemplate", JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["RollResult"] = "local custom text",
                ["RollParamOutOfRange"] = "old local text"
            }));

            var service = new BinaryJsonDataImportService();
            var preview = service.TryCreatePlan(File.ReadAllBytes(sourcePath), target, out var plan);
            preview.Success.Should().BeTrue();
            preview.DefaultEntriesSkipped.Should().Be(1);
            plan.Should().NotBeNull();

            var applied = service.Apply(plan!, target);
            applied.Success.Should().BeTrue();
            applied.RowsWritten.Should().Be(2);

            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(target.ReadData(BinaryJsonDataImportService.TableName, "FeedbackTemplate")!);
            saved!["RollResult"].Should().Be("local custom text");
            saved["RollParamOutOfRange"].Should().Be("uploaded custom text");
            target.ReadData(BinaryJsonDataImportService.TableName, "OtherSetting").Should().Be("uploaded value");
        }
        finally
        {
            source?.Close();
            target?.Close();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Plan_rejects_database_without_binary_json_data_table()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mdice-import-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "source.db");
        var targetPath = Path.Combine(root, "target.db");
        DataIO? source = null;
        DataIO? target = null;

        try
        {
            source = new DataIO(sourcePath);
            source.SaveData("OtherTable", "key", "value");
            source.Close();
            source = null;
            target = new DataIO(targetPath);

            var result = new BinaryJsonDataImportService().TryCreatePlan(File.ReadAllBytes(sourcePath), target, out var plan);
            result.Success.Should().BeFalse();
            plan.Should().BeNull();
        }
        finally
        {
            source?.Close();
            target?.Close();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
