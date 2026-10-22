# Estado de validación — 22 de octubre de 2026

Versión **2.2.0**.

## Comprobado en el equipo de desarrollo

- Solución completa C#/WPF 2.2.0: compilación cruzada, **0 errores y 0 advertencias**.
- **40/40 pruebas portátiles aprobadas**. Cubren planes solapados y sus motivos, selección explícita sin elementos añadidos, exclusiones de carpetas y comandos globales, preferencias persistentes y dañadas, argumentos del diagnóstico, cancelación durante comprobaciones y hashes, duplicados, enlaces, historial, salida acotada de procesos y exportación de informes. Incluyen una regresión de planificación con **12.000 candidatos**. Estas pruebas no ejecutan WPF ni las APIs Win32.
- Ayuda del diagnóstico comprobada. En macOS, `--diagnostico` se rechaza con código 2; no ejecuta el análisis de producción.
- Publicados aplicación, diagnóstico y verificador 2.2.0 para **win-x64** y **win-arm64**, sin advertencias. Se comprobaron las cabeceras PE y la arquitectura de los seis ejecutables. Esto valida los paquetes generados, no su ejecución en Windows.
- Código y artefactos originales de macOS resguardados en la carpeta hermana de respaldo. El ZIP de fuentes original fue comprobado y tiene SHA-256.

La interfaz 2.2 incorpora un plan previo con acciones aceptadas, omitidas y razones; la confirmación calcula el tamaño solo de las aceptadas y solo sus identificadores pueden pasar a ejecución. El validador de esa previsualización se prueba como código portable. El diálogo de configuración guarda antigüedad, carpeta adicional y exclusiones, nunca la selección; un error de lectura exige configurar antes de analizar.

Se mantienen el modo compacto, el ajuste al área útil del monitor, los atajos, la búsqueda diferida y la carga de resultados por lote. El exportador JSON real se compila también en las pruebas portátiles: se verificaron contenido, vigencia, cancelación, conservación del archivo previo y retirada de temporales. El informe incluye las opciones del análisis, incluidas sus exclusiones. La integración visual de todas estas funciones sigue pendiente de Windows.

Se añadieron fixtures nativos para rechazar historiales de Papelera inválidos antes de restaurar, conservar destinos ocupados, distinguir errores de acceso de rutas ausentes, rechazar sidecars SQLite y comprobar cancelaciones previas de NTFS, ReFS, Cloud Files, hashes y consultas de bloqueos. Están compilados, **no ejecutados** en este anfitrión.

La compilación integrada se realizó en la carpeta externa `LimpiadorUltraWindows-herramientas-migracion/build-2.2.0`. Los registros `artifacts/build-release.txt`, `artifacts/tests-core.txt` y `artifacts/publish-*.txt` conservan los resultados del cierre en el equipo de desarrollo. Las sumas SHA-256 acompañan a cada ZIP; `artifacts/verificacion-entrega.json` registra la comprobación final de los paquetes y del archivo de fuentes.

## Pendiente: ejecución en Windows

Este anfitrión de desarrollo es macOS. **No se han ejecutado aquí las ventanas WPF ni las operaciones Win32.** La compilación y los fixtures portátiles no acreditan el comportamiento real de COM, NTFS, ReFS, Cloud Files ni winsqlite3.

Para cerrar la validación de la migración:

1. Extraer el paquete adecuado en Windows y ejecutar `verificar.cmd`; conservar `resultado-verificacion.txt`, que incluye versión de la aplicación, sistema, arquitectura, runtime y resultados.
2. Completar los pasos visuales y de interacción de `VALIDACION-WINDOWS.md`.
3. Comprobar OneDrive con archivos de prueba sincronizados, ReFS sobre un volumen ReFS y ARM64 sobre Windows ARM si se van a usar esas capacidades.

Los `SKIP` indican falta de una capacidad o requisito de prueba y no deben contarse como aprobación. Las operaciones reales sobre los archivos del usuario no se ejecutaron durante la migración.
