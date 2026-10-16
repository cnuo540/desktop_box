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
OutputBaseFilename=DesktopBoxSetup
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 权限:**不提权**(双击直接装,连 UAC 都不弹)。
; 曾经改成 PrivilegesRequired=admin(自动提权)是为了清理老版本"为所有用户安装"的登记(HKLM),
; 但实测在提权上下文里 Inno 建自己的临时目录会被拒(Error 5,拒绝访问;普通权限下同一目录可写,
; ACL 也没有拒绝项 —— 指向第三方安全软件拦截未签名的提权安装程序)。而不提权的安装在用户机器上
; 一直是成功的,所以回到 lowest,并换一个全新的 AppId 来避开"所有用户"旧登记触发的升级冲突。
; 代价:程序与功能里会残留一条旧的 DesktopBox 记录(指向同一个 unins000.exe),可手动删:
;   管理员 PowerShell: reg delete "HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\DesktopBox_is1" /f
PrivilegesRequired=lowest
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
; 当前是"不提权"安装,这里直接启动即可。
; (若将来改回 PrivilegesRequired=admin,这里必须加 runasoriginaluser —— 否则被自动启动的主程序
;  会继承管理员令牌,它的桌面层/盒子窗口与 explorer 的交互就全乱了。)
Filename: "{app}\DesktopBox.exe"; Description: "Launch DesktopBox"; Flags: nowait postinstall skipifsilent
