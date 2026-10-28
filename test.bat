@echo off
rem Builds CalcStudioEPM and its tests (Release, x64) without raising the build number, then runs
rem the tests. Results go to TestResults\. Extra arguments go to vstest, e.g.
rem   test.bat /TestCaseFilter:"TestCategory!=Corpus"
rem The Corpus tests run when CALCSTUDIO_TEST_DATA (a pulled connection folder) and / or
rem CALCSTUDIO_TEST_IPM (an IPM export .zip or folder) are set; otherwise they are skipped.
setlocal
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
    echo vswhere.exe not found - install Visual Studio 2022 or later, or the Build Tools, with MSBuild.
    exit /b 1
)
set "MSBUILD="
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do if not defined MSBUILD set "MSBUILD=%%i"
set "VSTEST="
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -prerelease -products * -find **\TestPlatform\vstest.console.exe`) do if not defined VSTEST set "VSTEST=%%i"
if not defined MSBUILD (
    echo MSBuild.exe not found.
    exit /b 1
)
if not defined VSTEST (
    echo vstest.console.exe not found - install the Visual Studio test tools.
    exit /b 1
)
"%MSBUILD%" "%~dp0CalcStudioEPM.Tests\CalcStudioEPM.Tests.csproj" /restore /p:Configuration=Release /p:Platform=x64 /p:KeepBuildNumber=true /t:Build /v:minimal /nologo
if errorlevel 1 exit /b %ERRORLEVEL%
"%VSTEST%" "%~dp0CalcStudioEPM.Tests\bin\x64\Release\net48\CalcStudioEPM.Tests.dll" /Platform:x64 /logger:trx /ResultsDirectory:"%~dp0TestResults" %*
exit /b %ERRORLEVEL%
