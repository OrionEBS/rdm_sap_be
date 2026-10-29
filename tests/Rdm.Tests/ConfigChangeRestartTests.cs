using FluentAssertions;
using Rdm.Infrastructure.Hosting;

namespace Rdm.Tests;

public class ConfigChangeRestartTests
{
    [Theory]
    [InlineData("{ \"ConnectionStrings\": { \"SqlServer\": \"Server=x\" } }", true)]
    [InlineData("{\n  // chú thích\n  \"A\": 1,\n}", true)]          // comment + dấu phẩy cuối như appsettings
    [InlineData("{ \"A\": 1", false)]                                  // đang gõ dở
    [InlineData("[1, 2]", false)]                                      // gốc không phải object
    [InlineData("", false)]
    public void Only_valid_json_objects_trigger_restart(string content, bool valid)
    {
        var file = Path.Combine(Path.GetTempPath(), $"rdm-cfg-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, content);
        try
        {
            (ConfigChangeRestart.Validate(file) is null).Should().Be(valid);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
