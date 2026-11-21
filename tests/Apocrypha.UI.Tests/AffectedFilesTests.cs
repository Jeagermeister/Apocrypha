using Apocrypha.App.UI.Dialog;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
namespace Apocrypha.UI.Tests;
public class AffectedFilesTests
{
    public class Startup { public void ConfigureServices(IServiceCollection services) { } }
    [Theory] [InlineData(0)] [InlineData(2)] [InlineData(5000)]
    public void ExportIncludesAllEntriesAndUnavailableSizes(int count)
    {
        var files = Enumerable.Range(0,count).Select(i => new AffectedFile($"directory/{new string('x',300)}/{i}.json", i % 2 == 0 ? null : (ulong)i, "Additional / unrecognized")).ToArray();
        Assert.Equal(count, files.Length);
        var export = AffectedFile.Export(files);
        Assert.Equal(count+1, export.Split('\n').Length);
        var exportedPaths = export.Split('\n').Skip(1).Select(line => line.Split('\t')[0]).ToHashSet(StringComparer.Ordinal);
        foreach (var file in files) Assert.Contains(file.Path, exportedPaths);
        if (count > 0) { Assert.Contains("unknown", export); Assert.Contains("Size unavailable", files[0].Display); }
    }
}
