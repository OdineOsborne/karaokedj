@echo off
setlocal
rem Uso: publish.cmd [versione]   es. publish.cmd 1.0.1
set VER=%1
if "%VER%"=="" set VER=1.0.0
echo === Publish self-contained ===
dotnet publish src\KaraokeDJ\KaraokeDJ.csproj -c Release -r win-x64 --self-contained true -p:Version=%VER% -o build\publish
if errorlevel 1 exit /b 1
echo === Velopack pack (installer + pacchetti update) ===
vpk pack -u KaraokeDJ -v %VER% -p build\publish -e KaraokeDJ.exe --packTitle "KaraokeDJ" --packAuthors "OdineOsborne" -o build\Releases
if errorlevel 1 exit /b 1
echo.
echo Pronto in build\Releases:  KaraokeDJ-win-Setup.exe (installer), *.nupkg + releases.win.json (per gli aggiornamenti)
echo Per pubblicare su GitHub:  vpk upload github --repoUrl https://github.com/OdineOsborne/karaokedj --token TUO_TOKEN --publish --releaseName "KaraokeDJ %VER%" --tag v%VER% -o build\Releases
