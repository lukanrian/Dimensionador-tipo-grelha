@echo off
setlocal
title Iniciando Dimensionador Tipo Grelha 0.37

rem Pasta do programa (PROGRAMA); os dados (IFC, normas, catálogo em PDF) ficam na pasta acima.
set "APP_ROOT=%~dp0"
if "%APP_ROOT:~-1%"=="\" set "APP_ROOT=%APP_ROOT:~0,-1%"
for %%I in ("%APP_ROOT%\..") do set "DATA_ROOT=%%~fI"
set "UI_PROJECT=%APP_ROOT%\Grelha.IfcInspector.UI\Grelha.IfcInspector.UI.csproj"
set "UI_EXE=%APP_ROOT%\Grelha.IfcInspector.UI\bin\Release\net10.0-windows\Grelha.IfcInspector.UI.exe"
set "GRELHA_PROJECT_ROOT=%APP_ROOT%"
set "GRELHA_DEFAULT_IFC=%DATA_ROOT%\IFC\786-INC-PRE-0000-MOD-GERL-AC-MODELO_IFC-00.ifc"
set "GRELHA_LOG_DIR=%DATA_ROOT%\logs"
set "GRELHA_CLI_PROJECT=%APP_ROOT%\Grelha.IfcInspector\Grelha.IfcInspector.csproj"

echo Compilando Dimensionador Tipo Grelha 0.37...
dotnet build "%UI_PROJECT%" --configuration Release
if errorlevel 1 (
    echo.
    echo Nao foi possivel compilar. Confira se o .NET 10 SDK esta instalado.
    pause
    exit /b 1
)

if "%~1"=="" (
    start "" "%UI_EXE%"
) else (
    start "" "%UI_EXE%" "%~1"
)
