# Análisis y migración a Windows

Fecha: 22 de octubre de 2026.

## Proyecto recibido

El original era una aplicación **exclusiva de macOS 14**, no un proyecto multiplataforma configurado por accidente para Mac. `Package.swift` declaraba Swift 5.10 y macOS 14; `crear-app.sh` compilaba un bundle `.app`, creaba iconos con herramientas de Apple y firmaba con `codesign`. Los 22 archivos Swift sumaban 19.774 líneas. No había repositorio Git ni una batería de pruebas independiente en la carpeta recibida.

Arquitectura original:

1. `Utilidades` capturaba procesos, archivos abiertos, aplicaciones, permisos y características APFS.
2. `Indice` recorría directorios en paralelo y guardaba tamaños, actividad, contenidos sensibles y copias que compartían almacenamiento.
3. `Escaner` y sus extensiones identificaban categorías; `Evaluador` elevaba el riesgo a partir de evidencias.
4. `Maximo` separaba limpieza regenerable, acciones sin borrar, descargas/recompilaciones, selección manual y elementos bloqueados.
5. `Limpiador`, `Compresor`, `Clonador` y `Nube` revalidaban y ejecutaban; `Almacen` coordinaba la interfaz SwiftUI y el historial.

La mayoría del código de sistema dependía de AppKit, Darwin, Security, libproc y APFS. Las vistas dependían de SwiftUI. Reemplazar `/Users` por `C:\Users` no habría producido una aplicación de Windows ni operaciones correctas sobre NTFS.

## Invariantes conservadas

- Analizar es una operación de lectura. El diagnóstico de consola no contiene comandos para borrar.
- Seguro, Revisar y Cuidado son niveles diferentes. Los planes automáticos nunca incorporan Revisar o Cuidado.
- Las rutas de usuario se resuelven con Known Folders: no se presupone que Documentos o Descargas estén en `C:\Users\nombre`.
- Se excluyen credenciales, rutas estructurales, archivos del sistema, repositorios internos, nubes y redirecciones de la limpieza genérica.
- Los reparse points incluyen junctions, enlaces simbólicos y archivos de nube. No basta comprobar extensiones o nombres.
- Antes de limpiar se comparan el conjunto completo de entradas, identidad del archivo, tamaños, fechas, atributos, enlaces y procesos. Si falta evidencia se omite la operación y se explica el motivo.
- Los duplicados necesitan igualdad SHA-256 y una copia conservada que no pueda borrarse en el mismo plan.
- El historial se escribe antes de las operaciones recuperables; se evita sobrescribir un archivo al restaurar.
- Cancelar o cerrar espera al límite de la operación en curso. No se mata una aplicación a la fuerza.
- El espacio libre observado se distingue de bytes estimados. Los archivos en la Papelera siguen ocupando disco. Las modificaciones de otras aplicaciones pueden afectar la medida antes/después.

## Arquitectura Windows

| Componente | Responsabilidad |
|---|---|
| `Limpiador.Core` | Índice, reglas de detección, riesgos, planes y modelos. Su separación permite probar reglas con fixtures sin ejecutar APIs de Windows. |
| `Limpiador.Windows` | Win32: identidad, asignación, disco, Known Folders, Restart Manager, Shell/Papelera, NTFS, ReFS y Cloud Files. Comprueba Windows al ejecutar. |
| `Limpiador.Runtime` | Conecta el análisis a las operaciones reales. Guarda capturas originales, valida la selección, registra historial y aplica acciones. |
| `Limpiador.App` | Interfaz WPF en español, categorías, filtros, detalles, confirmación, progreso y recuperación. |
| `Limpiador.Cli` | Diagnóstico de solo lectura, salida humana o JSON y simulación de modo máximo. |
| `Limpiador.Tests` | Pruebas de reglas con carpetas creadas por la prueba e integración nativa condicional en Windows. |

No hay dependencia de Swift, Xcode, Bash, Python ni una instalación global de .NET para **ejecutar la distribución autocontenida**. Para compilar se usa el SDK .NET 10. Los artefactos de distribución se publican para `win-x64` y `win-arm64`.

## Correspondencia de plataforma

| Original | Equivalente Windows y criterio |
|---|---|
| SwiftUI/AppKit | WPF; interfaz y ciclo de vida nuevos. |
| `~/Library` y `/var/folders` | Local/Roaming AppData y TEMP del usuario. Windows y WinSxS no se tratan como cachés de usuario. |
| `fts`, inodos y bloques APFS | Enumeración administrada + handles Win32, file ID, volumen, asignación y número de enlaces. |
| libproc/archivos abiertos | Procesos por nombre más Restart Manager, handles exclusivos y comprobaciones al ejecutar. La falta de certeza bloquea la operación. |
| `NSWorkspace.recycle` | `IFileOperation` con veto a eliminación definitiva, callbacks e identidad del elemento en la Papelera. |
| `ditto --hfsCompression` | `FSCTL_SET_COMPRESSION` sobre NTFS, comprobación SHA-256, identidad y reversión ante fallo. Descompresión desde historial. |
| Clones APFS | `FSCTL_DUPLICATE_EXTENTS_TO_FILE` en ReFS. Se verifica contenido e independencia. NTFS conserva el detector de duplicados, pero no ofrece esta unión. |
| Copias locales iCloud | Cloud Files para OneDrive registrado: solo marcadores sincronizados, no fijados y antiguos; nunca borrar o truncar como alternativa. |
| Finder/Ajustes | Explorador y Configuración de almacenamiento de Windows. |
| Firma y permisos Apple | Manifiesto Windows `asInvoker`; no se exige administrador. Authenticode de distribución requiere un certificado del editor. |
| Homebrew/Xcode/iOS/Apple caches | Se retiran por ser servicios y productos exclusivos de macOS. No se sustituyen por limpieza de componentes Windows sin equivalencia. |
| Time Machine | No se traduce a borrado de VSS/puntos de restauración: son mecanismos distintos y se administran desde Windows. |

Un hardlink **no** sustituye un clon APFS: compartiría también las escrituras. En ReFS, block cloning conserva dos archivos independientes mediante copia al escribir. El tamaño asignado de un archivo ReFS no demuestra cuántos bloques son exclusivos; el programa no debe prometer ese tamaño como ahorro inmediato.

## Familias que requieren reglas propias

El original contenía quince categorías: emuladores, desarrollo, aplicaciones, temporales, restos, sin uso, compilaciones, herramientas, instaladores, olvidados, duplicados, registros, grandes, Papelera y compresión. La migración mantiene esta organización, adaptando la detección a Windows.

Los puntos que exigen especial prudencia son Android (dispositivos y SDK usados), Maven (`mvn install` produce artefactos únicos), bases de datos, conversaciones de herramientas, cachés de navegador frente a datos de sitios, entregas de proyectos, medios originales, juegos y datos de aplicaciones desinstaladas. La ausencia de una aplicación en el registro no demuestra por sí sola que una carpeta se pueda eliminar.

En Windows los `.exe`, `.msi`, `.msix` y `.appx` son instaladores nativos útiles: nunca se consideran basura solo por su extensión. Una carpeta llamada `build`, `bin`, `cache` o `dist` tampoco demuestra que todos sus datos se regeneren.

## Verificación y límites de la evidencia

Compilar WPF en un anfitrión Mac con `EnableWindowsTargeting` comprueba C#, referencias y XAML; no ejecuta la ventana ni prueba el comportamiento del Shell de Windows. Las pruebas de núcleo no sustituyen las pruebas nativas.

La batería nativa crea sus propios archivos en una carpeta GUID y comprueba metadatos, bloqueos, compresión y reversión, rechazo de enlaces, cambios posteriores al análisis, reciclaje y restauración. La prueba ReFS requiere un volumen ReFS. OneDrive requiere un proveedor conectado y archivos de prueba sincronizados. Estos requisitos deben constar como pendientes si no hay evidencia de ejecución; no se cuentan como pruebas aprobadas por omitirlas.

## Referencias técnicas primarias

- [WPF para .NET](https://learn.microsoft.com/dotnet/desktop/wpf/overview/).
- [Instalación y versiones Windows admitidas por .NET](https://learn.microsoft.com/dotnet/core/install/windows).
- [FSCTL_SET_COMPRESSION](https://learn.microsoft.com/windows/win32/api/winioctl/ni-winioctl-fsctl_set_compression).
- [ReFS block cloning](https://learn.microsoft.com/windows/win32/fileio/block-cloning).
- [Hardlinks y junctions](https://learn.microsoft.com/windows/win32/fileio/hard-links-and-junctions).
- [IFileOperation](https://learn.microsoft.com/windows/win32/api/shobjidl_core/nn-shobjidl_core-ifileoperation).
- [Restart Manager](https://learn.microsoft.com/windows/win32/rstmgr/restart-manager-portal).
- [Cloud Files API](https://learn.microsoft.com/windows/win32/cfapi/build-a-cloud-file-sync-engine).
