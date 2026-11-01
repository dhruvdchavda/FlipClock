@echo off
rem Builds FlipClock.scr using the C# compiler included with Windows. No install needed.
setlocal
cd /d "%~dp0"
set NET=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%NET%\csc.exe" set NET=%WINDIR%\Microsoft.NET\Framework\v4.0.30319
"%NET%\csc.exe" /nologo /target:winexe /optimize+ /platform:x86 /out:FlipClock.scr ^
  /r:"%NET%\WPF\PresentationFramework.dll" ^
  /r:"%NET%\WPF\PresentationCore.dll" ^
  /r:"%NET%\WPF\WindowsBase.dll" ^
  /r:"%NET%\System.Xaml.dll" ^
  FlipClock.cs
if errorlevel 1 exit /b 1
echo Built FlipClock.scr
