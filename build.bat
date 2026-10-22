@echo off
chcp 65001 >nul
setlocal EnableExtensions

set "ROOT=%~dp0"
set "SRC=%ROOT%src"
set "OUT=%ROOT%bin"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "PF86=%ProgramFiles(x86)%"
if not defined PF86 set "PF86=%ProgramFiles%"
set "NET48=%PF86%\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8"

if not exist "%CSC%" goto :missing_csc
if not exist "%NET48%\WindowsBase.dll" goto :missing_net48

set "MISSING_REF="
for %%F in (System.dll System.Core.dll System.Drawing.dll System.Windows.Forms.dll System.Xaml.dll System.Web.Extensions.dll System.Security.dll WindowsBase.dll PresentationCore.dll PresentationFramework.dll) do if not exist "%NET48%\%%F" set "MISSING_REF=%%F"
if defined MISSING_REF goto :missing_ref

if not exist "%SRC%\app.manifest" goto :missing_manifest
if not exist "%SRC%\Program.cs" goto :missing_sources
if not exist "%SRC%\Segments\SegmentVisual.cs" goto :missing_segments
if not exist "%SRC%\Providers\IProvider.cs" goto :missing_providers
if not exist "%SRC%\Providers\AgentProvider.cs" goto :missing_agent
if not exist "%SRC%\EventBus\EventProtocol.cs" goto :missing_eventbus
if not exist "%SRC%\EventBus\EventServer.cs" goto :missing_eventbus
if not exist "%SRC%\EventBus\HookInstaller.cs" goto :missing_eventbus
if not exist "%SRC%\Cli\CliMain.cs" goto :missing_cli
if not exist "%OUT%" mkdir "%OUT%"

echo [BUILD] Compiling bin\VibeBar.exe
"%CSC%" /nologo /codepage:65001 /utf8output /target:winexe /platform:anycpu /optimize+ /out:"%OUT%\VibeBar.exe" /win32manifest:"%SRC%\app.manifest" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Xaml.dll /r:System.Web.Extensions.dll /r:System.Security.dll /r:"%NET48%\WindowsBase.dll" /r:"%NET48%\PresentationCore.dll" /r:"%NET48%\PresentationFramework.dll" "%SRC%\*.cs" "%SRC%\Segments\*.cs" "%SRC%\Providers\*.cs" "%SRC%\EventBus\*.cs"
if errorlevel 1 goto :build_failed

echo [OK] %OUT%\VibeBar.exe

echo [BUILD] Compiling bin\VibeBar.Cli.exe
"%CSC%" /nologo /codepage:65001 /utf8output /target:exe /platform:anycpu /optimize+ /out:"%OUT%\VibeBar.Cli.exe" /r:System.dll /r:System.Core.dll "%SRC%\Cli\CliMain.cs" "%SRC%\EventBus\EventProtocol.cs"
if errorlevel 1 goto :build_failed

echo [OK] %OUT%\VibeBar.Cli.exe
exit /b 0

:missing_csc
echo [ERROR] csc.exe not found: %CSC%
echo [ERROR] Install .NET Framework 4.8.
exit /b 1

:missing_net48
echo [ERROR] .NET Framework 4.8 reference assemblies not found: %NET48%
exit /b 1

:missing_ref
echo [ERROR] Missing reference assembly: %NET48%\%MISSING_REF%
exit /b 1

:missing_manifest
echo [ERROR] app.manifest not found: %SRC%\app.manifest
exit /b 1

:missing_sources
echo [ERROR] source files not found in %SRC%
exit /b 1

:missing_segments
echo [ERROR] segment sources not found in %SRC%\Segments
exit /b 1

:missing_providers
echo [ERROR] provider sources not found in %SRC%\Providers
exit /b 1

:missing_agent
echo [ERROR] AgentProvider.cs not found in %SRC%\Providers
exit /b 1

:missing_eventbus
echo [ERROR] event bus sources not found in %SRC%\EventBus
exit /b 1

:missing_cli
echo [ERROR] CLI source not found: %SRC%\Cli\CliMain.cs
exit /b 1

:build_failed
echo [ERROR] Build failed.
exit /b 1
