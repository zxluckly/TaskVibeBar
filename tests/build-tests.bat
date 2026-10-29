@echo off
chcp 65001 >nul
setlocal EnableExtensions

rem 离线自测：只编译 Provider / SegmentFactory / 设置收敛等纯逻辑代码，
rem 不依赖 WPF，也不读写 %APPDATA%\VibeBar\settings.json。

set "ROOT=%~dp0.."
set "SRC=%ROOT%\src"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "PF86=%ProgramFiles(x86)%"
if not defined PF86 set "PF86=%ProgramFiles%"
set "NET48=%PF86%\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8"

if not exist "%CSC%" goto :missing_csc
if not exist "%ROOT%\tests" mkdir "%ROOT%\tests"

echo [TEST] Compiling tests\SelfTest.exe
"%CSC%" /nologo /codepage:65001 /utf8output /target:exe /platform:anycpu /out:"%ROOT%\tests\SelfTest.exe" ^
 /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll /r:System.Security.dll ^
 "%SRC%\Models\*.cs" "%SRC%\Providers\*.cs" "%SRC%\Segments\*.cs" "%SRC%\Segments\Presenters\*.cs" ^
 "%SRC%\App\Json.cs" "%SRC%\App\Log.cs" "%SRC%\App\NativeMethods.cs" "%SRC%\App\SettingsStore.cs" "%SRC%\App\Secrets.cs" ^
 "%SRC%\EventBus\EventProtocol.cs" "%ROOT%\tests\SelfTest.cs"
if errorlevel 1 goto :failed

echo [TEST] Running
"%ROOT%\tests\SelfTest.exe"
if errorlevel 1 goto :failed

echo [OK] Self test passed
exit /b 0

:missing_csc
echo [ERROR] csc.exe not found
exit /b 1

:failed
echo [ERROR] Self test failed
exit /b 1
