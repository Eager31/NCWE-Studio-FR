@echo off
rem Lance la version la plus recente de NCWE Studio avec l'interface en francais.
powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0Lancer-NCWE-FR.ps1"
if errorlevel 1 pause
