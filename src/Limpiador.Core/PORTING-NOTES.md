# Análisis y traslado del núcleo a Windows

## Punto de partida

Se revisaron aproximadamente 19.774 líneas del proyecto Swift original. `Indice.swift` realizaba una pasada paralela por el perfil; `Escaner.swift` y sus extensiones aplicaban reglas por categoría; `Evaluador.swift` elevaba riesgos; `Maximo.swift` construía el plan; `Apps.swift` identificaba paquetes y verificaba firmas. El código dependía de SwiftUI, AppKit, Darwin, Security, SQLite3, APFS y herramientas de macOS. No era una aplicación Windows con rutas diferentes.

El original queda conservado fuera del árbol activo, en el respaldo de macOS indicado en el README del proyecto. Las referencias Swift de este documento describen ese original, no dependencias del producto nuevo.

## Diseño nuevo

- `Models.cs`: contratos de análisis, candidatos, instantáneas, acciones y planes normal/máximo.
- `SafetyPolicy.cs`: límites de perfil y Known Folders explícitos, nube, junctions, puntos de análisis, credenciales, Git y raíces estructurales protegidas.
- `FileIndex.cs`: índice de tamaños, árbol, actividad y contenido; registra zonas incompletas y nunca desciende a reparse points. Conserva instantáneas de **todas** las entradas de cada candidato, incluidos directorios.
- `Scanner.cs`: orquestación de solo lectura y evaluación común que puede elevar, pero nunca reducir, el riesgo.
- `Scanner.Development.cs`: Android, SDK, Gradle, lenguajes, gestores de paquetes, modelos y bases de datos.
- `Scanner.Applications.cs`: Chromium/Electron, Firefox, aplicaciones conocidas, temporales e inventario de posibles restos.
- `Scanner.Projects.cs`: proyectos con manifiesto, dependencias, compilaciones, herramientas, instaladores y registros.
- `Scanner.Files.cs`: duplicados con SHA-256, archivos propios, antiguos y grandes, y propuestas de compresión.
- `GitIndexReader.cs`: lectura directa del índice Git; no ejecuta hooks, filtros ni comandos durante el análisis.

El núcleo no depende de paquetes NuGet externos ni de APIs de UI. `net10.0` permite ejecutar fixtures de seguridad en el equipo de desarrollo. **El producto y sus operaciones reales son exclusivamente Windows**: la capa `Limpiador.Windows` y el ejecutor `Limpiador.Runtime` implementan las llamadas nativas. Un fixture portátil no sustituye las pruebas de integración Windows.

## Matriz de capacidades

| Familia original | Adaptación Windows y criterio |
|---|---|
| Emuladores | AVD completos y datos para restablecer: revisión; Quick Boot antiguo: regenerable; snapshots nombrados: revisión. Se examinan AVD encontrados en el perfil, incluso fuera de `.android`. Si una ubicación configurada de AVD no se pudo indexar, las imágenes del sistema no se consideran sobrantes automáticamente. |
| SDK Android | `%LOCALAPPDATA%\Android\Sdk`, ubicaciones del perfil y variables Android dentro de raíces autorizadas. Imágenes no referenciadas solo si se leyeron las configuraciones; versiones anteriores de plataformas, NDK, CMake y build-tools: revisión, porque un proyecto puede fijarlas. |
| Desarrollo | Cachés npm, Yarn, pnpm, Bun, pip, uv, Poetry, NuGet, Cargo, Go, Gradle, Playwright/Cypress, Unreal. La descarga/recompilación costosa se reserva al modo máximo. Un archivo personal o secreto dentro eleva el riesgo. |
| Gradle | Separación por versión y subcaché, dependencias y wrappers. La presencia de procesos Java/Gradle/IDE bloquea con criterio conservador; no se afirma conocer la versión exacta de todos los daemons. |
| Maven y entornos | Artefactos sin procedencia remota comprobada: Cuidado. Conda, paquetes globales npm —también con scope—, paquetes `pip --user`, runtimes antiguos y `.jdks`: revisión; no se deduce desuso solo de una versión antigua. |
| Modelos IA | Ollama: manifiestos y referencias a blobs; piezas no referenciadas solo si el inventario es completo; parciales separados. Los modelos completos se administran con Ollama. Hugging Face: comparación de snapshots con refs, manteniendo revisión porque un proyecto puede fijar un hash. |
| Git | Detección de proyectos y volumen de objetos sueltos; mantenimiento mediante Git con `--no-prune`. Los archivos versionados en un supuesto artefacto pasan a Cuidado. Índice desconocido o incompleto: revisión, nunca selección automática. |
| Navegadores | Chrome, Edge y canales, Brave, Chromium, Vivaldi, Opera/GX y Arc; perfiles Default/Profile N/Guest/System. Caché de código/GPU/service workers separada de cookies, contraseñas e IndexedDB. IndexedDB antiguo se muestra para revisar, no como basura. |
| Electron y apps | Raíces con Local State y Partitions; cachés conocidas de Code, Cursor, Discord, Slack, Teams, Zoom y ChatGPT. Versiones anteriores de programas: revisión. |
| Medios y gráficos | Cachés de Adobe, GPU NVIDIA/AMD/DirectX, Unreal, shadercache de Steam, vistas previas Lightroom y caché Resolve. Las Smart Previews de Lightroom siempre requieren revisión; no se confunden con vistas previas comunes. |
| Microsoft Store y Office | Se separan TempState y cachés reconocidas de LocalState. No se borran paquetes completos ni OfficeFileCache, que puede contener documentos sin sincronizar. |
| Temporales Windows | Solo Temp del usuario o ubicación explícita autorizada. Antigüedad mínima de 24 h, procesos relacionados y comprobación de contenido. Documentos, bases, datos científicos, modelos, trazas y entregas no se autoseleccionan por estar en Temp. |
| Restos de aplicaciones | Inventario de desinstalación HKCU/HKLM de 32/64 bits. Ausencia de coincidencia significa «sin aplicación identificada», no «seguro que sobra». Se exige revisión y se excluyen raíces Microsoft/Packages/Programs y carpetas con ejecutables propios. Sin inventario fiable, solo se informa. |
| Proyectos | Se exige manifiesto correspondiente: Node, Python, Gradle, .NET, Unity, Flutter, Rust, Maven, CMake, Terraform, Composer y Ruby. Intermedios separados de entregas/informes; `.next/cache` separado de `.next` y servidores standalone; `bin`, `dist`, APK y resultados históricos requieren revisión. |
| Herramientas y conversaciones | Logs separados de ajustes/sesiones. Conversaciones de Codex/Claude antiguas son datos propios para revisar; se pueden proponer para compresión sin borrar. Nombres genéricos `cache/tmp` en configuración no bastan para considerarlos seguros. |
| Instaladores | MSI/MSIX/Appx/ISO/APK y programas de instalación EXE se reconocen como material utilizable en Windows. Nunca se conserva la antigua conclusión «Windows no puede ejecutarse en este Mac». Revisión y siete días mínimos. |
| Registros | Logs reconocidos, volcados Java/Windows y WER. El informe de fallo puede ser valioso y se conserva para revisión; logs arbitrarios del usuario no se autoseleccionan. |
| Sin uso / olvidados / grandes | Material propio con umbral configurable, respaldos, descargas parciales e intermedios LaTeX con fuente al lado. Los umbrales de tiempo y tamaño no demuestran que algo sobre. No se ofrece el original que conserva un grupo de duplicados. |
| Duplicados | Igual tamaño y SHA-256 con versión de archivo comprobada antes/después de leer. Se conserva un original, preferentemente fuera de Descargas. No se confunden archivos de igual tamaño y distinto contenido ni enlaces físicos con copias independientes. |
| Papelera e historial | La interfaz remite a la Papelera Windows y Runtime administra las operaciones propias con registro y restauración. El núcleo no vacía una Papelera global durante el análisis. |
| Compresión | Texto antiguo, conversaciones, material propio y programas instalados por el usuario. NTFS y archivos cerrados se comprueban en la capa nativa. El escáner no suma el tamaño entero como supuesto ahorro: el ahorro se mide al ejecutar. |
| Sin borrar, avanzado | Runtime/Windows adapta compresión NTFS, clonación de bloques ReFS, deshidratación Cloud Files y compactación SQLite verificada. Estas operaciones tienen requisitos propios y nunca se convierten en una eliminación genérica como alternativa. |

## Invariantes de seguridad

1. Analizar y planificar no elimina, mueve, comprime ni ejecuta comandos de mantenimiento.
2. Solo `Risk.Safe` puede entrar automáticamente. `Review` y `Caution` exigen elección explícita; un elemento bloqueado no entra ni seleccionado manualmente.
3. El modo máximo agrega costes de descarga/recompilación y operaciones sin borrar; no elimina la distinción de riesgos.
4. Perfil, AppData y otras raíces estructurales no son objetivos de limpieza. Tampoco llaves SSH, certificados, keystores, contraseñas, `.git`, estado Terraform o datos de identidad de Windows.
5. No se siguen symlinks, junctions ni otros reparse points. El caso Cloud Files se maneja por su API específica, fuera de la eliminación normal.
6. Una carpeta incompleta, ilegible o con rutas excluidas no se ofrece para borrado recursivo.
7. Hay que revalidar el conjunto de entradas del candidato, metadatos e identidad, originales de duplicados y uso por procesos inmediatamente antes de operar. Esa responsabilidad está implementada en Runtime y Windows, no se sustituye por la evaluación inicial.
8. El plan elimina solapamientos y evita seleccionar una carpeta que contenga el original preservado de un duplicado.
9. No se promete que fechas de acceso identifiquen aplicaciones o archivos sin uso. No se presume que una aplicación portátil esté desinstalada porque no aparece en el registro.

## Sustituciones que no serían equivalentes

- APFS y ReFS pueden mantener independencia tras clonar; **hardlinks NTFS no equivalen a clones**: una escritura cambia el mismo archivo visible por todos sus nombres. No se usan como sustituto. Véase [Microsoft: hard links](https://learn.microsoft.com/en-us/windows/win32/fileio/hard-links-and-junctions) y [block cloning](https://learn.microsoft.com/en-us/windows/win32/fileio/block-cloning).
- La compresión por archivo requiere soporte del volumen; Windows expone [FSCTL_SET_COMPRESSION](https://learn.microsoft.com/en-us/windows/desktop/api/winioctl/ni-winioctl-fsctl_set_compression). No debe ofrecerse como si todos los discos fueran NTFS.
- La lectura Git v2/v3 sigue el [formato de índice oficial](https://git-scm.com/docs/gitformat-index). Índices v4, SHA-256, split/sparse o corruptos llevan a revisión hasta disponer de soporte específico, sin dar por no versionados los archivos.

Se retiran del producto Windows las operaciones exclusivas de macOS: Xcode/simctl/iOS, Homebrew, contenedores y bibliotecas de Apple, Metal, `/var/folders`, TCC/Gestión de apps, firmas Apple, APFS/ditto/renamex_np y Time Machine. No se redirigen esos comandos a WinSxS, VSS o al registro. El respaldo conserva el original para consultar las decisiones anteriores.

## Verificación realizada

- Compilación `Limpiador.Core` con .NET 10: sin errores ni advertencias.
- Nueve pruebas de núcleo/integración portátil iniciales: raíces, credenciales/nube, enlaces, instantáneas con modificaciones/altas, planes con originales, historial, análisis sin cambios, SHA-256 y cancelación.
- Auditoría Git adicional con repositorios efímeros reales: índice válido conserva elegibilidad de `obj` no versionado; `git add obj/...` eleva a Cuidado; v4 y corrupción impiden selección automática. El repositorio del usuario no se modifica para estas comprobaciones.
- La ejecución de APIs nativas y la interfaz requieren las pruebas Windows del proyecto. Una compilación cruzada no se presenta como evidencia de que esas llamadas hayan sido ejecutadas.

Las heurísticas se han adaptado a evidencia que Windows permite obtener con seguridad. La matriz describe qué hace cada regla; no afirma una identidad línea por línea con las numerosas heurísticas específicas del Mac original.
