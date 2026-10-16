using FluentAssertions;
using System.IO;
using System.Linq;

namespace DesktopBox.Tests;

public class ReleaseConsistencyTests
{
    [Fact]
    public void InstallerVersion_ComesFromPublishedExecutable()
    {
        var installerPath = FindRepositoryFile("DesktopBox.iss");
        var source = File.ReadAllText(installerPath);

        source.Should().Contain("GetFileVersion");
        source.Should().Contain("{#MyAppVersion}");
        source.Should().NotContain("AppVersion=1.7.0");
        source.Should().NotContain("AppVerName=DesktopBox 1.7.0");
        // 安装包放的是"多文件自包含"整目录(启动不必解压到 %TEMP%),helper 也在里面
        source.Should().Contain("GetFileVersion(\"publish-installer\\DesktopBox.exe\")");
        source.Should().Contain("publish-installer\\*");
        source.Should().Contain("[InstallDelete]");
        source.Should().Contain("Name: \"{app}\\DesktopBox.ShellMenu.dll\"");
    }

    [Fact]
    public void ReleaseScript_PackagesShellMenuHelperExecutable()
    {
        var source = ReadRepositoryFile("release.ps1");

        source.Should().Contain("publish/DesktopBox.ShellMenu.exe");
        source.Should().NotContain("publish/DesktopBox.ShellMenu.dll");
    }

    [Fact]
    public void ContinuousIntegration_BuildsPortableAndInstallerArtifacts()
    {
        var source = ReadRepositoryFile(".github", "workflows", "build.yml");

        source.Should().Contain("publish/DesktopBox.ShellMenu.exe");
        // 安装包走一键脚本(内部先 ISCC 出核心包,再编译外层 UAC 启动器)
        source.Should().Contain("scripts/build-installer.ps1");
        source.Should().Contain("release/DesktopBoxSetup.exe");
        source.Should().NotContain("publish/DesktopBox.ShellMenu.dll");
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("使用说明.md")]
    public void Documentation_DescribesCurrentShellAndDesktopBehavior(string fileName)
    {
        var source = ReadRepositoryFile(fileName);

        source.Should().Contain("DesktopBox.ShellMenu.exe");
        source.Should().NotContain("DesktopBox.ShellMenu.dll");
        source.Should().Contain("失效引用会保留");
        source.Should().Contain("多显示器");
        source.Should().NotContain("仅主显示器");
    }

    [Fact]
    public void Installer_ShipsAnElevatedBootstrapperAroundTheInnoCore()
    {
        // Inno 的启动器会在"提权之前"就去 %TEMP% 建 is-*.tmp,在拦这类写入的机器上直接 Error 5
        // (右键以管理员身份运行却正常)。所以外层必须有带 requireAdministrator 清单的启动器:
        // Windows 先弹 UAC,启动器在已提权上下文里解出并运行核心安装包。
        var manifest = ReadRepositoryFile("src", "DesktopBox.InstallerBootstrapper", "bootstrapper.manifest");
        manifest.Should().Contain("requireAdministrator");

        var bootstrap = ReadRepositoryFile("src", "DesktopBox.InstallerBootstrapper", "DesktopBoxSetupBootstrap.cpp");
        bootstrap.Should().Contain("FindResourceW");           // 核心包作为资源内嵌
        bootstrap.Should().Contain("CreateProcessW");          // 解出来后启动它
        bootstrap.Should().Contain("BuildCommandLine");        // 静默参数(/VERYSILENT)要原样转给核心包
        bootstrap.Should().Contain("DeleteFileW(core.c_str())"); // 装完清理临时文件

        // 核心安装包用自己的输出名,免得和外层启动器互相覆盖
        var iss = ReadRepositoryFile("DesktopBox.iss");
        iss.Should().Contain("OutputBaseFilename=DesktopBoxSetupCore");
        // 核心包一定在提权上下文里运行:安装模式设为 admin,并且启动主程序要用原用户身份
        iss.Should().Contain("PrivilegesRequired=admin");
        iss.Should().Contain("runasoriginaluser");

        // 构建脚本要真的编译启动器
        var script = ReadRepositoryFile("scripts", "build-installer.ps1");
        script.Should().Contain("build-bootstrapper.bat");
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
}
