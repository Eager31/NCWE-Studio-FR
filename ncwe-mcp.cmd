@echo off
rem Relais MCP : lance le NCWE.Mcp.exe de la version la plus recente (dossiers NCWE-Studio-* a cote de NCWE-FR).
rem Forcer une version : ecrire le chemin complet de NCWE.Mcp.exe dans chemin-mcp.txt.
setlocal
set "EXE="
if exist "%~dp0chemin-mcp.txt" set /p EXE=<"%~dp0chemin-mcp.txt"
if defined EXE if exist "%EXE%" goto run
set "EXE="
for /f "delims=" %%D in ('dir /b /ad /o-d "%~dp0..\NCWE-Studio-*" 2^>nul') do (
  if not defined EXE if exist "%~dp0..\%%D\NCWE.Mcp.exe" set "EXE=%~dp0..\%%D\NCWE.Mcp.exe"
)
if not defined EXE (
  >&2 echo NCWE.Mcp.exe introuvable a cote de %~dp0
  exit /b 1
)
:run
"%EXE%" %*
