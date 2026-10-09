@echo off
rem Cree le raccourci "NCWE Studio (FR)" sur le bureau (valable pour toutes les versions de NCWE).
for /f "delims=" %%E in ('powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Lancer-NCWE-FR.ps1" -Afficher') do set "LIGNE=%%E"
echo %LIGNE%
set "EXE=%LIGNE:NCWE utilise : =%"
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ws=New-Object -ComObject WScript.Shell; $l=$ws.CreateShortcut([Environment]::GetFolderPath('Desktop')+'\NCWE Studio (FR).lnk');" ^
  "$l.TargetPath=$env:WINDIR+'\System32\cmd.exe'; $l.Arguments='/c \"\"%~dp0Lancer NCWE en francais.cmd\"\"'; $l.WorkingDirectory='%~dp0';" ^
  "$l.IconLocation=$env:EXE+',0'; $l.WindowStyle=7; $l.Save()"
echo Raccourci "NCWE Studio (FR)" cree sur le bureau.
pause
