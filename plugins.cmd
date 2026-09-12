@echo off
setlocal
rem Compila i plugin (non inclusi nell'installer) in build\plugins\<nome>\ e crea lo zip installabile da Impostazioni → Plugin.
echo === Plugin yt-dlp ===
dotnet build plugins\VOXA.Plugin.YtDlp\VOXA.Plugin.YtDlp.csproj -c Release -o build\plugins\VOXA.Plugin.YtDlp
if errorlevel 1 exit /b 1
del /q build\plugins\VOXA.Plugin.YtDlp\*.pdb 2>nul
del /q build\plugins\VOXA.Plugin.YtDlp\VOXA.Plugins.dll 2>nul
powershell -NoProfile -Command "Compress-Archive -Force -Path 'build\plugins\VOXA.Plugin.YtDlp\*' -DestinationPath 'build\plugins\VOXA.Plugin.YtDlp.zip'"
echo Pronto: build\plugins\VOXA.Plugin.YtDlp.zip  (Impostazioni → Plugin e fonti → Installa plugin)
