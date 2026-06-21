@echo off
setlocal
cd /d "%~dp0"
dotnet restore
if errorlevel 1 pause && exit /b 1
dotnet run -c Release
pause
