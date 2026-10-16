; DesktopBox Inno Setup 安装脚本
; 用法:用 ISCC.exe 编译本文件,生成 release\DesktopBoxSetup.exe

#define MyAppVersion GetFileVersion("publish-installer\DesktopBox.exe")

[Setup]
AppName=DesktopBox
AppVersion={#MyAppVersion}
AppVerName=DesktopBox {#MyAppVersion}
AppPublisher=DesktopBox
DefaultDirName={localappdata}\Programs\DesktopBox
DefaultGroupName=DesktopBox
DisableProgramGroupPage=yes
DisableDirPage=yes
SetupIconFile=src\DesktopBox\Resources\DesktopBox.ico
UninstallDisplayIcon={app}\DesktopBox.exe
OutputDir=release
OutputBaseFilename=DesktopBoxSetup
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 安装包自己请求管理员权限:双击 → 一次 UAC → 直接装完,不用再右键"以管理员身份运行"。
; 之前是 PrivilegesRequired=lowest(纯当前用户、完全不提权),但用户机器上常留有老版本
; "为所有用户安装"的登记(HKLM),清理它需要管理员,于是表现为"必须右键以管理员身份运行才能装"。
;
; 注意:DefaultDirName 仍然放在 {localappdata}(提权后仍是同一个用户的目录),
; 这样① 用户数据(boxes.json / desktop-layer.json / logs / icons)原地保留不丢;
;      ② 程序运行期照旧以普通权限写自己的目录,不需要管理员。
; PrivilegesRequiredOverridesAllowed=commandline:不弹"安装模式"选择框(/CURRENTUSER 仍可用),
; 保证默认流程就是"双击 → UAC → 装完"。
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline
WizardStyle=modern
CompressionThreads=auto
VersionInfoVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Additional icons:"

[Files]
; 多文件自包含:启动不必把单文件包解压到 %TEMP%\.net(首次启动慢、被清理工具清了还要再付一次),
; 仍然免 .NET。单文件绿色版由 release.ps1 单独打成 zip。
Source: "publish-installer\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: files; Name: "{app}\DesktopBox.ShellMenu.dll"

; 卸载时不做额外删除:Inno 会清掉自己装过的文件,而 boxes.json / desktop-layer.json / logs / icons
; 是用户数据,必须留下(安装目录在 %LocalAppData%,不归卸载程序管)。

[Icons]
Name: "{group}\DesktopBox"; Filename: "{app}\DesktopBox.exe"
Name: "{group}\Uninstall DesktopBox"; Filename: "{uninstallexe}"
Name: "{autodesktop}\DesktopBox"; Filename: "{app}\DesktopBox.exe"; Tasks: desktopicon

[Run]
; runasoriginaluser:安装包是提权运行的,直接启动会把主程序也带成管理员进程 ——
; 那样它的桌面层/盒子窗口与 explorer 的交互、以及它自己目录的读写都会带上管理员上下文,
; 所以必须用"原用户"身份启动(未提权的那份令牌)。
Filename: "{app}\DesktopBox.exe"; Description: "Launch DesktopBox"; Flags: nowait postinstall skipifsilent runasoriginaluser
