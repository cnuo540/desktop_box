using System.IO;
using DesktopBox.Services;
using FluentAssertions;

namespace DesktopBox.Tests;

public class CategorizerServiceTests
{
    private readonly CategorizerService _sut = new();

    private string TmpWithExt(string ext)
    {
        var f = Path.GetTempFileName();
        var p = Path.ChangeExtension(f, ext);
        File.Move(f, p);
        File.WriteAllText(p, "x");
        return p;
    }

    [Theory]
    [InlineData(".pdf", CategorizerService.Document)]
    [InlineData(".docx", CategorizerService.Document)]
    [InlineData(".md", CategorizerService.Document)]
    [InlineData(".txt", CategorizerService.Document)]
    // 启用宏的 Office 文档(用户要求:.docm/.xlsm 归文档)
    [InlineData(".docm", CategorizerService.Document)]
    [InlineData(".xlsm", CategorizerService.Document)]
    [InlineData(".pptm", CategorizerService.Document)]
    // .tsx 是 TypeScript JSX,仍归文档;.ts 则优先当视频(见下)
    [InlineData(".tsx", CategorizerService.Document)]
    [InlineData(".jpg", CategorizerService.Image)]
    [InlineData(".png", CategorizerService.Image)]
    [InlineData(".zip", CategorizerService.Archive)]
    [InlineData(".7z", CategorizerService.Archive)]
    [InlineData(".mp4", CategorizerService.Video)]
    [InlineData(".mkv", CategorizerService.Video)]
    // .ts 既是 TypeScript 又是 MPEG-TS —— 按用户要求归视频
    [InlineData(".ts", CategorizerService.Video)]
    [InlineData(".mp3", CategorizerService.Audio)]
    [InlineData(".flac", CategorizerService.Audio)]
    [InlineData(".ape", CategorizerService.Audio)]
    [InlineData(".exe", CategorizerService.Program)]
    [InlineData(".msi", CategorizerService.Program)]
    [InlineData(".appx", CategorizerService.Program)]
    [InlineData(".zzz", CategorizerService.Other)]
    public void Categorize_ByExtension(string ext, string expected)
    {
        var p = TmpWithExt(ext);
        try { _sut.Categorize(p).Should().Be(expected); }
        finally { File.Delete(p); }
    }

    [Fact]
    public void Categorize_Folder_ReturnsFolderCategory()
    {
        var dir = Directory.CreateTempSubdirectory("dbx_").FullName;
        try { _sut.Categorize(dir).Should().Be(CategorizerService.FolderCat); }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Categorize_ShortcutFile_ReturnsShortcutCategory()
    {
        // 伪 .lnk 文件:WScript.Shell 解析失败 → 按策略归「快捷方式」
        var lnk = Path.Combine(Path.GetTempPath(), $"dbx_{System.Guid.NewGuid():N}.lnk");
        File.WriteAllText(lnk, "fake");
        try { _sut.Categorize(lnk).Should().Be(CategorizerService.Shortcut); }
        finally { File.Delete(lnk); }
    }
}
