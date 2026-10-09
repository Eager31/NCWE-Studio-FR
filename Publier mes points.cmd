@echo off
chcp 65001 >nul
title Publier mes points sur GitHub
cd /d "%~dp0"
echo Copie de vos Mes points dans points-projet.tsv...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0outils\publier-points.ps1"
if errorlevel 1 goto fin
echo.
echo Envoi sur GitHub...
git push
if errorlevel 1 (echo. & echo ECHEC de l'envoi : verifiez la connexion GitHub ^(gh auth login^).) else (echo. & echo OK : vos points arriveront chez l'equipe au prochain lancement de NCWE.)
:fin
echo.
pause
