# Validación en Windows

Versión **2.2.0**.

Compilar WPF desde Mac valida referencias, código y XAML. No ejecuta el Shell ni la ventana. En el anfitrión macOS se aprobaron **40/40 pruebas portátiles**, incluida una planificación con **12.000 candidatos**; esto no acredita ejecución WPF o Win32. Las pruebas siguientes siguen pendientes de Windows y requieren archivos creados para la prueba.

## Pruebas automatizadas

En la distribución portátil basta ejecutar `verificar.cmd`, sin SDK. El resultado queda en `resultado-verificacion.txt`. Desde el código fuente:

```powershell
dotnet build LimpiadorUltraWindows.slnx -c Release
dotnet run --project tests/Limpiador.Tests -c Release --no-build
```

El runner debe terminar con código 0. Las pruebas nativas crean una carpeta GUID propia bajo TEMP; la conservan con sus historiales si fallan. No reemplaces sus rutas por documentos reales. Comprueban metadatos, bloqueos, compresión NTFS y reversión, rechazo de enlaces, cambios posteriores al análisis, Papelera y restauración de archivos/carpetas. Los casos omitidos aparecen como SKIP, no como aprobados.

`.github/workflows/windows.yml` prepara esas pruebas para GitHub Actions. El archivo de workflow no prueba que se hayan ejecutado: hace falta el resultado de una ejecución real.

## Interfaz

1. Abrir el EXE como usuario estándar: discos visibles y sin pedir administrador.
2. Analizar, redimensionar la ventana, detener y volver a analizar. Un análisis cancelado no permite limpiar resultados obsoletos.
3. Probar categorías, búsqueda, orden, Tab, lector de pantalla, alto contraste y escalas 100/150/200 %.
4. Revisar un candidato Seguro y uno Revisar. Abrir el plan previo y comprobar acciones aceptadas, elementos omitidos y sus motivos; el total debe sumar solo las aceptadas. Enter no ejecuta. Cancelar conserva los archivos y no añade casillas a la selección.
5. Con un fixture abierto, comprobar que se omite. Cerrar la app y reanalizar permite reevaluarlo.
6. Reciclar solo fixtures; restaurarlos y comparar hashes. Crear un archivo nuevo en la ruta original: Restaurar debe negarse a sobrescribirlo.
7. Pulsar Detener durante la revalidación de un árbol grande y durante una comprobación de hash; verificar que se atiende la cancelación entre entradas o bloques y no se inicia la siguiente acción. Cerrar la ventana durante una operación debe esperar una detención segura, sin matar procesos. Una llamada nativa ya iniciada puede terminar su unidad actual antes de detenerse.
8. Probar una pantalla de 1366×768 con escalas 150 % y 200 %: las pestañas Almacenamiento, Resultados y Detalles deben permitir alcanzar todos los controles. Comprobar también cada diálogo y el paso entre modo compacto y tres columnas.
9. Probar `F5`, `Ctrl+F`, `Esc`, `Espacio` y `Enter`. Enter muestra detalles y nunca ejecuta limpieza desde la confirmación.
10. Exportar un análisis a JSON, revisar riesgos y avisos; después de una operación, el informe del análisis anterior debe indicar `AnalysisCurrent: false`. Cancelar una exportación conserva cualquier archivo previo.
11. Abrir el EXE por segunda vez en la misma sesión: debe informar que ya está abierto. Cerrar la primera ventana debe permitir iniciarlo de nuevo.
12. En Configurar, guardar edad, carpeta adicional y varias exclusiones; cerrar y abrir la aplicación. Deben conservarse las opciones y ninguna casilla de limpieza. Guardar cambios con un análisis vigente debe desmarcarlo y exigir un análisis nuevo; cancelar el diálogo conserva las opciones anteriores.
13. Con la aplicación cerrada y una copia del archivo de preferencias de prueba, probar un JSON inválido, incompleto (por ejemplo, `{}`), inaccesible o redirigido por un enlace. Analizar debe quedar deshabilitado hasta revisar y guardar la configuración; no deben desaparecer las exclusiones silenciosamente. Un guardado fallido debe informar del error y permitir corregir el borrador.
14. Crear carpetas vecinas dentro de una raíz de prueba, excluir una y analizar la raíz. El contenido excluido no debe recorrerse ni aparecer como limpiable; sus carpetas hermanas deben seguir analizándose. Revisar también una exclusión dentro de la carpeta adicional.
15. Con exclusiones configuradas, revisar candidatos de mantenimiento global: si el comando no puede respetarlas, debe aparecer bloqueado con el motivo y no poder ejecutarse. La restauración tampoco debe escribir dentro de una exclusión actual. En una restauración parcial, excluir una entrada ya recuperada no debe impedir recuperar las demás entradas pendientes fuera de esa exclusión.
16. Marcar candidatos de prueba solapados, abrir el plan y comprobar que cada solicitado aparece una sola vez entre aceptados y omitidos. No deben aparecer acciones añadidas automáticamente. Si todos quedan omitidos, no debe existir una ejecución habilitada. En una prueba con acciones aceptadas, verificar que únicamente sus IDs llegan a ejecución.

## Diagnóstico de consola

```powershell
.\diagnostico\LimpiadorUltraWindows.Diagnostico.exe --diagnostico --carpeta "D:\Fixture" --excluir "D:\Fixture\Privado" --excluir "D:\Fixture\Respaldo" --json > diagnostico-prueba.json
```

Comprobar que `ScanOptions` contiene las exclusiones guardadas y las dos añadidas para esa ejecución, que no se analiza su contenido y que `preferences.json` queda intacto. Probar también `--excluir` sin valor y una ruta relativa: deben producir un error claro. `--maximo` sigue siendo una simulación; el diagnóstico nunca limpia. Ctrl+C debe cancelar con código 130 y sin modificar los fixtures.

## Volúmenes y nube

- NTFS: compresión/descompresión conserva hash y file ID.
- ReFS: copias con file IDs distintos; escribir en una no cambia la otra. NTFS no ofrece unión mediante hardlinks.
- OneDrive de pruebas: archivo antiguo sincronizado y no fijado. Liberar conserva marcador y copia remota; volver a abrir recupera el contenido. Archivos fijados, abiertos o con cambios pendientes se omiten.
- Junctions y acceso denegado: omisión sin seguir enlaces ni tomar posesión de archivos.

Guardar versión de Windows, arquitectura, tipo de volumen, salida del runner y pasos de interfaz comprobados. Una ejecución x64 no prueba ARM64, ReFS ni OneDrive. Cualquier capacidad sin ejecución comprobada mantiene pendiente su validación.
