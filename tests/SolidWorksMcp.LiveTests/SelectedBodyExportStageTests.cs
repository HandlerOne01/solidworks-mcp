using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Files;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.LiveTests;

public sealed class SelectedBodyExportStageTests : IDisposable
{
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("swmcp-body-export-");

    private PathPolicy Paths => new(_folder.FullName, [_folder.FullName], allowNetwork: false,
        backupDir: Path.Combine(_folder.FullName, "backups"));

    [Fact]
    public void Failure_before_output_preserves_a_concurrent_destination()
    {
        string target = Path.Combine(_folder.FullName, "part.stl");
        using (var stage = new ExportService.SelectedBodyExportStage(Paths, target, overwrite: false))
        {
            Assert.NotEqual(target, stage.TranslatorPath);
            File.WriteAllText(target, "concurrent file");
        }
        Assert.Equal("concurrent file", File.ReadAllText(target));
    }

    [Fact]
    public void No_overwrite_refuses_a_destination_created_while_translating()
    {
        string target = Path.Combine(_folder.FullName, "part.stl");
        string translatorPath;
        using (var stage = new ExportService.SelectedBodyExportStage(Paths, target, overwrite: false))
        {
            translatorPath = stage.TranslatorPath;
            Assert.Equal(Path.GetDirectoryName(target), Path.GetDirectoryName(translatorPath));
            Assert.Equal(".stl", Path.GetExtension(translatorPath));
            File.WriteAllText(translatorPath, "verified export");
            File.WriteAllText(target, "concurrent file");
            var error = Assert.Throws<SwException>(() => stage.Publish(translatorPath));
            Assert.Equal(ErrorCodes.IoError, error.Code);
        }
        Assert.False(File.Exists(translatorPath));
        Assert.Equal("concurrent file", File.ReadAllText(target));
    }

    [Fact]
    public void Overwrite_publishes_verified_stage_and_keeps_policy_backup()
    {
        string target = Path.Combine(_folder.FullName, "part.stl");
        File.WriteAllText(target, "existing file");
        using var stage = new ExportService.SelectedBodyExportStage(Paths, target, overwrite: true);
        File.WriteAllText(stage.TranslatorPath, "verified export");
        var result = stage.Publish(stage.TranslatorPath);
        Assert.True(result.Replaced);
        Assert.Equal(target, result.Path);
        Assert.Equal("existing file", File.ReadAllText(result.BackupPath!));
        Assert.Equal("verified export", File.ReadAllText(result.Path));
        Assert.False(File.Exists(stage.TranslatorPath));
    }

    [Fact]
    public void New_file_keeps_the_translator_extension_case()
    {
        string target = Path.Combine(_folder.FullName, "part.stl");
        using var stage = new ExportService.SelectedBodyExportStage(Paths, target, overwrite: false);
        string actualStage = Path.ChangeExtension(stage.TranslatorPath, ".STL");
        File.WriteAllText(actualStage, "verified export");
        var result = stage.Publish(actualStage);
        Assert.False(result.Replaced);
        Assert.Equal(Path.ChangeExtension(target, ".STL"), result.Path);
        Assert.Equal("verified export", File.ReadAllText(result.Path));
    }

    public void Dispose() => _folder.Delete(recursive: true);
}
