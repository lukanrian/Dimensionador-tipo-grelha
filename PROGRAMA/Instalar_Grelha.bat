@echo off
setlocal
title Instalando Dimensionador Tipo Grelha 0.36
powershell.exe -NoProfile -File "%~dp0Instalar_Grelha.ps1"
if errorlevel 1 (
    echo.
    echo A instalacao falhou. Confira a mensagem acima.
    pause
    exit /b 1
)
echo.
echo Instalacao concluida.
pause
