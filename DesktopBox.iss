; DesktopBox Inno Setup 安装脚本
; 用法:用 ISCC.exe 编译本文件,生成 release\DesktopBoxSetup.exe

#define MyAppVersion GetFileVersion("publish-installer\DesktopBox.exe")

[Setup]
; 新的 AppId:老安装包没有写 AppId,Inno 用 AppName("DesktopBox")当 id,于是机器上可能留着
; "为所有用户安装"的登记;同 id 的按用户安装会要求管理员才能升级。换新 GUID 让两者互不干扰。
AppId={{5E7A9C31-4D82-4F1B-9A63-2C8F1E0B7D54}
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
OutputBaseFilename=DesktopBoxSetupCore
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 权限:核心包由外层 DesktopBoxSetup.exe(带 requireAdministrator 清单的启动器)启动,
; 所以它**一定运行在已提权上下文**里;Inno 自己不必再自我提权,临时目录也能正常创建。
; 之所以不能让 Inno 自己提权:老版本实测 Inno 的启动器会在"提权之前"就先去 %TEMP% 建 is-*.tmp,
; 那一步被系统拒绝(Error 5),双击安装直接失败;而右键以管理员身份运行则正常 —— 说明缺的正是
; "进入提权上下文之后再写临时目录"。外层启动器解决的就是这个次序问题。
; 安装模式设为 admin,是为了让下面的 runasoriginaluser 生效(否则装完自动启动的主程序会继承
; 管理员令牌,它的桌面层/盒子窗口与 explorer 的交互会全乱)。
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
; 核心包是在提权上下文里跑的,直接启动会把主程序也带成管理员进程 ——
; runasoriginaluser 让它用"原用户"(未提权)身份启动,桌面层/盒子窗口与 explorer 的交互才正常。
Filename: "{app}\DesktopBox.exe"; Description: "Launch DesktopBox"; Flags: nowait postinstall skipifsilent runasoriginaluser
