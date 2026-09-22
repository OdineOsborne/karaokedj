@echo off
setlocal
rem Compila i plugin (non inclusi nell'installer) in build\plugins\<nome>\ e crea lo zip installabile da Impostazioni → Plugin.
echo === Plugin yt-dlp ===
dotnet build plugins\Mixfonia.Plugin.YtDlp\Mixfonia.Plugin.YtDlp.csproj -c Release -o build\plugins\Mixfonia.Plugin.YtDlp
if errorlevel 1 exit /b 1
del /q build\plugins\Mixfonia.Plugin.YtDlp\*.pdb 2>nul
del /q build\plugins\Mixfonia.Plugin.YtDlp\Mixfonia.Plugins.dll 2>nul
powershell -NoProfile -Command "Compress-Archive -Force -Path 'build\plugins\Mixfonia.Plugin.YtDlp\*' -DestinationPath 'build\plugins\Mixfonia.Plugin.YtDlp.zip'"
echo Pronto: build\plugins\Mixfonia.Plugin.YtDlp.zip  (Impostazioni → Plugin e fonti → Installa plugin)
