@echo off
setlocal
cd /d "%~dp0"
if exist "artifacts\publish\Toolbox.exe" (
    start "" "artifacts\publish\Toolbox.exe"
    exit /b 0
)
if exist ".tools\dotnet\dotnet.exe" (
    ".tools\dotnet\dotnet.exe" run --project src\Toolbox.App -c Release -p:Platform=x64
) else (
    dotnet run --project src\Toolbox.App -c Release -p:Platform=x64
)
if errorlevel 1 pause
