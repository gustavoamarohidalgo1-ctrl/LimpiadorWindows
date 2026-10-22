# Limpiador Ultra Windows

Versión **2.2.0**.

[![Comprobación en Windows](https://github.com/gustavoamarohidalgo1-ctrl/LimpiadorWindows/actions/workflows/windows.yml/badge.svg)](https://github.com/gustavoamarohidalgo1-ctrl/LimpiadorWindows/actions/workflows/windows.yml)

Aplicación nativa en español para analizar espacio y revisar qué puede limpiarse en **Windows**. Interfaz WPF, motor C# y operaciones Win32. No se ejecuta en macOS ni Linux.

## Descargar

- [Windows x64 — Intel y AMD](https://github.com/gustavoamarohidalgo1-ctrl/LimpiadorWindows/releases/download/v2.2.0/LimpiadorUltraWindows-win-x64.zip)
- [Windows ARM64](https://github.com/gustavoamarohidalgo1-ctrl/LimpiadorWindows/releases/download/v2.2.0/LimpiadorUltraWindows-win-arm64.zip)
- [Versión 2.2.0, código fuente y sumas SHA-256](https://github.com/gustavoamarohidalgo1-ctrl/LimpiadorWindows/releases/tag/v2.2.0)

Esta primera publicación es una **versión de evaluación**. Consulta el [estado de validación](docs/ESTADO-VALIDACION.md) antes de usar operaciones de limpieza; la revisión visual en un equipo Windows sigue pendiente.

## Llevarla a tu equipo Windows

1. Copia `LimpiadorUltraWindows-win-x64.zip` para Intel/AMD, o `LimpiadorUltraWindows-win-arm64.zip` para Windows ARM.
2. Extrae todo el ZIP en una carpeta local de Windows.
3. Abre `LimpiadorUltraWindows.exe`. La distribución autocontenida incluye .NET; no necesita instalar el SDK.
4. En **Configurar**, revisa la antigüedad, la carpeta adicional y las carpetas excluidas. Solo se guardan estas preferencias; las casillas de limpieza nunca se guardan.
5. Pulsa **Analizar equipo**, revisa las razones y marca elementos. **Revisar y limpiar** prepara un plan con acciones aceptadas y elementos omitidos, cada uno con su motivo. Confirma únicamente las acciones aceptadas. Analizar y preparar el plan no modifican tus archivos.

Se recomienda Windows 11. La base técnica admite Windows 10 1809 o posterior, aunque el soporte oficial de .NET 10 para Windows 10 se limita a las ediciones LTSC/Enterprise indicadas por [Microsoft](https://learn.microsoft.com/dotnet/core/install/windows). La aplicación no exige administrador. Las ubicaciones sin acceso se indican y se omiten. Los ejecutables locales no llevan firma Authenticode comercial; el proyecto no incluye un certificado del editor.

## Funciones

- Quince categorías: Android, desarrollo, cachés de aplicaciones, temporales, restos, compilaciones, herramientas, instaladores, registros, duplicados, archivos grandes o antiguos, Papelera y compresión.
- Riesgos **Seguro**, **Revisar** y **Cuidado**, con razones y consecuencias por elemento.
- Búsqueda, filtros, orden por tamaño, selección manual y **Liberar al máximo** con costes visibles.
- Previsualización del plan real: acciones aceptadas, elementos omitidos y motivos. La cifra del plan suma solo las acciones aceptadas y nunca añade elementos que no marcaste.
- Preferencias persistentes de antigüedad, carpeta adicional y exclusiones de carpetas, sin guardar selecciones de limpieza.
- Papelera real de Windows, historial por operación y restauración sin sobrescribir archivos existentes.
- Compresión y descompresión transparente NTFS, verificando SHA-256 e identidad.
- Compactación SQLite con `winsqlite3` cuando su esquema, bloqueo, integridad y claves permiten conservar los datos.
- Explorador de espacio con tamaños, navegación por carpetas y avisos del índice.
- Duplicados SHA-256 con copia conservada. En ReFS, alternativa para compartir bloques manteniendo archivos independientes; nunca se crean hardlinks.
- Liberación de descargas antiguas de OneDrive mediante Cloud Files, solo si están sincronizadas y no fijadas en el dispositivo.
- Cierre amable de aplicaciones que bloquean resultados, con confirmación y oportunidad de guardar; después se vuelve a analizar.
- Diagnóstico de consola, salida JSON, cancelación y simulación de modo máximo.
- Vista compacta con pestañas para pantallas pequeñas, diálogos desplazables y ajuste inicial a la escala del monitor.
- Exportación manual del análisis a JSON, con rutas, riesgos, avisos y fecha. El informe indica si el análisis dejó de estar vigente.

Se analizan las carpetas conocidas de Windows y una carpeta adicional opcional. Descargas o Documentos redirigidos se resuelven con las APIs del sistema. Las carpetas excluidas y su contenido quedan fuera del análisis. Con exclusiones configuradas, los comandos globales que no pueden respetarlas se bloquean y muestran el motivo.

**Atajos:** `F5` inicia un análisis; `Ctrl+F` enfoca la búsqueda; `Esc` limpia la búsqueda enfocada; `Espacio` marca o desmarca una fila disponible y `Enter` abre sus detalles. En modo compacto, `Ctrl+1`, `Ctrl+2` y `Ctrl+3` cambian de panel. La limpieza sigue requiriendo revisar y confirmar las acciones. Solo se permite una ventana principal por usuario y sesión de Windows.

La versión 2.2 explica las omisiones del plan antes de confirmar, respeta las exclusiones personales y comprueba la cancelación durante la revalidación de archivos y el cálculo de hashes por bloques. Una operación nativa que ya esté en curso puede necesitar terminar su unidad actual antes de detenerse; esto incluye las comprobaciones internas de Papelera y SQLite. Se mantienen la protección contra operaciones solapadas, las coincidencias de duplicados ya verificadas al alcanzar el límite de lectura y el historial con errores aislados. Las salidas extensas de mantenimiento se limitan en memoria conservando sus mensajes iniciales y finales.

## Reglas de limpieza

**Revisar y Cuidado nunca se seleccionan automáticamente.** El tamaño o la antigüedad no demuestran que un archivo sobre. Los `.exe` y `.msi` son instaladores útiles en Windows y requieren revisión. Los artefactos versionados en Git tampoco son cachés automáticamente.

La limpieza genérica no sigue junctions/enlaces ni entra en carpetas de nube, credenciales, rutas estructurales o componentes de Windows. OneDrive usa una operación independiente que conserva el archivo remoto. No se limpian el registro, WinSxS, puntos de restauración ni discos virtuales como si fueran cachés.

Antes de actuar se comprueban otra vez rutas, entradas del árbol, identidad, fechas, atributos y uso. Si algo cambió, falta evidencia o Windows rechaza una operación, se omite y se registra el motivo. Los archivos en la Papelera siguen ocupando espacio hasta vaciarla desde Windows. El cambio de espacio libre observado puede incluir actividad de otras aplicaciones.

Las preferencias quedan en `%LOCALAPPDATA%\LimpiadorUltraWindows\preferences.json`. **Configurar → Guardar preferencias** conserva solo antigüedad, carpeta adicional y carpetas excluidas. Al guardarlas se desmarca la selección y se exige analizar de nuevo. Si el archivo guardado es inválido o no se puede leer, la aplicación exige revisar la configuración antes de analizar; no sustituye silenciosamente las exclusiones por una lista vacía.

El historial queda en `%LOCALAPPDATA%\LimpiadorUltraWindows\History`. **Historial → Restaurar** recupera movimientos registrados y descomprime archivos que todavía coinciden con su identidad original. No recupera archivos que ya vaciaste de la Papelera. Después de limpiar o restaurar se exige un análisis nuevo.

## Compilar en Windows

Requisito de desarrollo: **SDK .NET 10**. Visual Studio es opcional; se puede abrir `LimpiadorUltraWindows.slnx` en un IDE compatible.

```powershell
.\crear-app.cmd
# Windows ARM:
.\crear-app.cmd -Arquitectura win-arm64
```

El script compila, ejecuta las pruebas y publica en `dist\win-x64` o `dist\win-arm64`, con ZIP y suma SHA-256 en la salida. Si falla una prueba no publica. `-SinPruebas` permite repetir solo el empaquetado después de verificar el mismo código.

```powershell
dotnet build LimpiadorUltraWindows.slnx -c Release
dotnet run --project tests/Limpiador.Tests -c Release --no-build
dotnet run --project src/Limpiador.App
```

## Diagnóstico sin limpiar

```powershell
.\diagnostico\LimpiadorUltraWindows.Diagnostico.exe --diagnostico --motivos --rutas
.\diagnostico\LimpiadorUltraWindows.Diagnostico.exe --diagnostico --maximo
.\diagnostico\LimpiadorUltraWindows.Diagnostico.exe --diagnostico --json > analisis.json
.\diagnostico\LimpiadorUltraWindows.Diagnostico.exe --diagnostico --dias 180 --carpeta "D:\Proyectos"
.\diagnostico\LimpiadorUltraWindows.Diagnostico.exe --diagnostico --excluir "D:\Proyectos\Privado" --excluir "D:\Respaldo"
```

`--dias` admite 7, 30, 90, 180 o 365 para categorías de antigüedad. El diagnóstico parte de las preferencias guardadas; `--dias` y `--carpeta` las sustituyen solo para esa ejecución. `--excluir RUTA` se puede repetir y añade exclusiones a las guardadas, sin modificar el archivo de preferencias. Las rutas deben ser absolutas y locales. `--maximo` solo muestra el plan. Este ejecutable no tiene modo de borrado. Ctrl+C cancela el análisis; `--json` incluye las opciones aplicadas.

## Estructura

| Carpeta | Contenido |
|---|---|
| `src/Limpiador.Core` | Índice, detección, riesgos y planes |
| `src/Limpiador.Windows` | APIs nativas de disco, bloqueos, Papelera, NTFS, ReFS y Cloud Files |
| `src/Limpiador.Runtime` | Revalidación, ejecución, historial y preferencias |
| `src/Limpiador.App` | Interfaz WPF |
| `src/Limpiador.Cli` | Diagnóstico de solo lectura |
| `tests/Limpiador.Tests` | Fixtures de seguridad e integración Windows |
| `scripts/compilar.ps1` | Compilación y distribución |

Las reglas pueden probarse desde otro sistema de desarrollo; eso no convierte el producto en multiplataforma. Las APIs nativas y la interfaz requieren Windows. ReFS necesita un volumen ReFS y OneDrive necesita un proveedor conectado. Una compilación cruzada no demuestra ejecución nativa.

La distribución también incluye `verificar.cmd`: ejecuta las pruebas con archivos propios, sin instalar el SDK, y guarda `resultado-verificacion.txt`. Si falla una prueba nativa, conserva su carpeta temporal para investigar el error.

Consulta [el análisis de arquitectura y migración](docs/ANALISIS-Y-MIGRACION.md) y [el protocolo de validación Windows](docs/VALIDACION-WINDOWS.md). El código fuente también incluye la matriz detallada en `src/Limpiador.Core/PORTING-NOTES.md`.

El original se conserva fuera del proyecto, en la carpeta hermana `LimpiadorUltraWindows-respaldo-mac-2026-10-22`. `fuentes-originales-mac.zip` fue verificado y tiene SHA-256. No se necesita para ejecutar Windows.
