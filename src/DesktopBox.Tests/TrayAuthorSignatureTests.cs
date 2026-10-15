using System.IO;
using FluentAssertions;

namespace DesktopBox.Tests;

/// <summary>托盘菜单底部的制作人署名:中英资源键都要有,且必须是灰色不可点的信息行。</summary>
public class TrayAuthorSignatureTests
{
    private const string AuthorQq = "790544312";

    [Theory]
    [InlineData("Strings.zh-CN.xaml")]
    [InlineData("Strings.en-US.xaml")]
    public void AuthorKey_ExistsInBothLanguages(string fileName)
    {
        var source = ReadRepositoryFile("src", "DesktopBox", "Resources", fileName);

        source.Should().Contain("x:Key=\"menu.author\"");
        source.Should().Contain(AuthorQq);
    }

    [Fact]
    public void TrayMenu_ShowsAuthorAsDisabledInfoRow()
    {
        var source = ReadRepositoryFile("src", "DesktopBox", "Views", "MainWindow.xaml.cs");
        var body = Between(source, "private void RebuildTrayMenu()", "private void ShowBoxes()");

        body.Should().Contain("_localizer[\"menu.author\"]");
        body.Should().Contain("Enabled = false", "署名只是信息行,不该能点");
    }

    private static string ReadRepositoryFile(params string[] parts) =>
        File.ReadAllText(FindRepositoryFile(parts));

    private static string FindRepositoryFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        from.Should().BeGreaterThanOrEqualTo(0, $"应该找得到 {start}");
        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        to.Should().BeGreaterThan(from, $"应该找得到 {end}");
        return source[from..to];
    }
}
