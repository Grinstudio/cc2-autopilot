@echo off
rem Builds build\CC2Autopilot.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
if not exist build mkdir build
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" -nologo -target:winexe -platform:x64 -out:build\CC2Autopilot.exe src\CC2Autopilot.cs
