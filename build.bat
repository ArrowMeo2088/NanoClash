@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "SLN=NanoClash.slnx"
set "CSPROJ=Src\NanoClash.csproj"
set "CONFIG=Release"
set "RID=win-x64"
rem Intermediate NativeAOT/managed publish tree (junk OK here).
set "STAGEDIR=%~dp0Build\pub"
rem Final AOT artifacts only (exe + sidecar content).
set "OUTDIR=%~dp0Publish"
set "CMD="

if /I "%~1"=="" (
  set "CMD=publish"
  goto :dispatch
)
if /I "%~1"=="restore" set "CMD=restore" & goto :rid2
if /I "%~1"=="build" set "CMD=build" & goto :rid2
if /I "%~1"=="publish" set "CMD=publish" & goto :rid2
if /I "%~1"=="clean" set "CMD=clean" & goto :rid2
if /I "%~1"=="help" goto :help

echo %~1| findstr /R ".-" >nul
if not errorlevel 1 (
  set "CMD=publish"
  set "RID=%~1"
  goto :dispatch
)

echo Unknown command: %~1
goto :help

:rid2
if not "%~2"=="" set "RID=%~2"
goto :dispatch

:dispatch
if /I "%CMD%"=="restore" goto :restore
if /I "%CMD%"=="build" goto :build
if /I "%CMD%"=="publish" goto :publish
if /I "%CMD%"=="clean" goto :clean
goto :help

:restore
echo == restore ==
dotnet restore "%SLN%"
if errorlevel 1 exit /b 1
goto :eof

:build
echo == restore ==
dotnet restore "%SLN%"
if errorlevel 1 exit /b 1
echo == build (%CONFIG%, managed) ==
dotnet build "%SLN%" -c %CONFIG% --no-restore
if errorlevel 1 exit /b 1
goto :eof

:publish
echo == restore ==
dotnet restore "%SLN%"
if errorlevel 1 exit /b 1
if not exist "%STAGEDIR%" mkdir "%STAGEDIR%"
if not exist "%OUTDIR%" mkdir "%OUTDIR%"
set "MEWUI_BACKEND="
echo %RID%| findstr /I /B "win-" >nul
if not errorlevel 1 set "MEWUI_BACKEND=-p:MewUIBackend=Direct2D"
echo == publish NativeAOT + full Trim ^(%CONFIG% %RID%^) -^> Build\pub\ ==
dotnet publish "%CSPROJ%" -c %CONFIG% -r %RID% -o "%STAGEDIR%" --self-contained true ^
  -p:PublishAot=true ^
  -p:TrimMode=full ^
  -p:OptimizationPreference=Size ^
  -p:IlcFoldIdenticalMethodBodies=true ^
  -p:InvariantGlobalization=true ^
  -p:StripSymbols=true ^
  -p:DebuggerSupport=false ^
  -p:StackTraceSupport=false ^
  -p:EventSourceSupport=false ^
  -p:HttpActivityPropagationSupport=false ^
  -p:MetadataUpdaterSupport=false ^
  -p:UseSystemResourceKeys=true ^
  -p:NullabilityInfoContextSupport=false ^
  -p:DebugType=None -p:DebugSymbols=false ^
  -p:AppendRuntimeIdentifierToOutputPath=false ^
  %MEWUI_BACKEND%
if errorlevel 1 exit /b 1
call :sync_final
if errorlevel 1 exit /b 1
call :try_upx
echo == done: %OUTDIR%\NanoClash.exe ==
goto :eof

:sync_final
echo == sync final AOT artifacts -^> Publish\ ==
rem Drop previous framework/AOT intermediate leftovers; keep user data (config/log/data/README).
del /q "%OUTDIR%\*.dll" 2>nul
del /q "%OUTDIR%\*.so" 2>nul
del /q "%OUTDIR%\*.dylib" 2>nul
del /q "%OUTDIR%\icon.ico" 2>nul
del /q "%OUTDIR%\NanoClash.deps.json" 2>nul
del /q "%OUTDIR%\NanoClash.runtimeconfig.json" 2>nul
del /q "%OUTDIR%\createdump.exe" 2>nul
del /q "%OUTDIR%\clretwrc.dll" 2>nul
if exist "%STAGEDIR%\NanoClash.exe" (
  copy /Y "%STAGEDIR%\NanoClash.exe" "%OUTDIR%\" >nul
) else if exist "%STAGEDIR%\NanoClash" (
  copy /Y "%STAGEDIR%\NanoClash" "%OUTDIR%\" >nul
) else (
  echo ERROR: NativeAOT binary not found in Build\pub
  exit /b 1
)
exit /b 0

:try_upx
where upx >nul 2>&1
if errorlevel 1 (
  echo == upx: not in PATH, skip ==
  exit /b 0
)
set "UPX_BIN="
if exist "%OUTDIR%\NanoClash.exe" set "UPX_BIN=%OUTDIR%\NanoClash.exe"
if not defined UPX_BIN if exist "%OUTDIR%\NanoClash" set "UPX_BIN=%OUTDIR%\NanoClash"
if not defined UPX_BIN (
  echo == upx: binary not found, skip ==
  exit /b 0
)
echo == upx --best --lzma -f ==
upx --best --lzma -f -q "%UPX_BIN%"
if not errorlevel 1 (
  echo == upx: ok ==
  exit /b 0
)
echo == upx: failed, keeping uncompressed binary ==
exit /b 0

:clean
echo == clean ==
dotnet clean "%SLN%" -c %CONFIG%
if exist "Build" rmdir /s /q "Build"
if exist "Src\Publish" rmdir /s /q "Src\Publish"
rem Clean Publish app binaries only
del /q "%OUTDIR%\NanoClash.exe" 2>nul
del /q "%OUTDIR%\NanoClash" 2>nul
del /q "%OUTDIR%\*.dll" 2>nul
del /q "%OUTDIR%\NanoClash.deps.json" 2>nul
del /q "%OUTDIR%\NanoClash.runtimeconfig.json" 2>nul
del /q "%OUTDIR%\createdump.exe" 2>nul
rem Rules.bin.gz / wintun.dll are embedded; runtime extract under %%APPDATA%%\ArrowMeo\NanoClash.
goto :eof

:help
echo Usage: build.bat [publish^|build^|restore^|clean^|help] [RID]
echo   ^(no args^) / publish  - NativeAOT -^> Build\pub, sync finals -^> Publish\
echo   build                 - restore + managed Release build only
echo   restore               - restore NuGet packages into Packages\
echo   clean                 - clean Build\ and Publish\ app binaries
echo   RID                   - optional, e.g. win-x64 / linux-x64 / osx-arm64
exit /b 0
