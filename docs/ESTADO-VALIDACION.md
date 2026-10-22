# Estado de validación — 22 de octubre de 2026

Versión **2.2.1**.

## Comprobado en macOS

- **41/41 pruebas portátiles aprobadas**. Incluyen exclusiones, configuración dañada, selección explícita, planificación con **12.000 candidatos**, duplicados, enlaces, cancelación por bloques, historial e informes JSON.
- La regresión del historial comprueba 64 escrituras desde varias instancias junto con lecturas concurrentes. Un destino no reemplazable debe conservar sus datos, informar del error y no dejar temporales.
- Las pruebas portátiles no ejecutan WPF ni las APIs Win32. Compilar XAML y referencias desde macOS tampoco acredita la ejecución de la interfaz.
- Ayuda del diagnóstico comprobada. En macOS, `--diagnostico` se rechaza con código 2 y no analiza archivos.

## Verificación automática en Windows

El [workflow Windows](https://github.com/gustavoamarohidalgo1-ctrl/LimpiadorWindows/actions/workflows/windows.yml) compila la solución, ejecuta las pruebas portátiles y nativas y, solo si pasan, genera paquetes x64 y ARM64. Conserva los registros incluso si falla; los fixtures nativos propios de una prueba fallida se guardan durante siete días para investigarlos.

La primera ejecución real detectó una carrera al reemplazar simultáneamente el historial y un rechazo de la restauración desde la Papelera. La versión 2.2.1 incorpora correcciones; su confirmación en una nueva ejecución Windows sigue pendiente. No se consideran aprobadas las operaciones nativas mientras esa comprobación no termine correctamente.

## Pendiente en equipos Windows

- Revisión visual y de interacción de WPF: escalas de pantalla, navegación, configuración, plan previo, confirmación, explorador e historial.
- Ejecución en Windows ARM64; publicar para esa arquitectura no prueba su ejecución.
- ReFS sobre un volumen ReFS y OneDrive con archivos de prueba sincronizados.
- Completar el protocolo [VALIDACION-WINDOWS.md](VALIDACION-WINDOWS.md) y conservar el resultado de `verificar.cmd` en el equipo donde se vaya a usar.

Los `SKIP` indican falta de una capacidad o requisito y no cuentan como aprobación. La validación solo utiliza archivos creados para las pruebas. Los archivos personales no se usan como fixtures.

La distribución de evaluación incluirá la aplicación, el diagnóstico y el verificador autocontenidos, con sumas SHA-256. El código y los artefactos originales de macOS se conservan fuera del proyecto, en su carpeta de respaldo.
