@echo off
setlocal
cd /d "%~dp0"
if not exist "%~dp0verificacion\Limpiador.Tests.exe" (
    echo No se encontro el verificador. Extrae todo el ZIP antes de ejecutar este archivo.
    pause
    exit /b 2
)
"%~dp0verificacion\Limpiador.Tests.exe" > "%~dp0resultado-verificacion.txt" 2>&1
set "resultado=%errorlevel%"
type "%~dp0resultado-verificacion.txt"
echo.
echo Codigo de salida: %resultado%
echo El informe se guardo en resultado-verificacion.txt
pause
exit /b %resultado%
