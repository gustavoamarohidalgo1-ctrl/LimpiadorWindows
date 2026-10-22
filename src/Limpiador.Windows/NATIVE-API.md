# Capa nativa de Windows

Biblioteca C# sin paquetes de terceros. Compila con .NET 10 en otros sistemas para CI, pero todas las operaciones de archivos nativas requieren Windows 10 1809 o posterior. Una compilación cruzada no acredita la ejecución de las APIs Win32.

## Contratos

- `WindowsFileSystem.GetMetadata(path)`: tamaño lógico y asignado, volumen, ID de archivo de 128 bits, enlaces duros, atributos y tiempos de creación/escritura/cambio; abre con `FILE_READ_ATTRIBUTES` y `FILE_FLAG_OPEN_REPARSE_POINT`, sin leer contenido.
- `GetDiskSpace(path)`: espacio real y clúster del volumen fijo local. La asignación por archivo no prueba propiedad exclusiva de los bloques ReFS.
- `KnownFolders.Get(...)` y `CloudRoots()`: carpetas conocidas del Shell y cuentas OneDrive actuales del usuario.
- `RestartManager.GetLockingProcesses(paths)` y `RequireUnlocked(paths)`: fallan ante errores, resultados inestables o motivos de reinicio, incluso con una lista vacía. Nunca cierran aplicaciones, terminan procesos ni llaman a `RmShutdown`.
- `NtfsCompression.SetCompressed(path, compressed, expected, cancellationToken)`: NTFS, archivo ordinario con un único enlace, sin reparse points en los ancestros, handle exclusivo, comparación de estado, SHA-256 antes/después, `FSCTL_SET_COMPRESSION`, confirmación y reversión del formato si la operación falla. Los datos lógicos y el ID permanecen iguales. No utiliza `compact.exe`, scripts, sustitución de archivos ni instalación de dependencias.
- `RefsBlockCloner.CloneIdentical(source, target, expectedSource, expectedTarget, cancellationToken)`: solo archivos independientes, idénticos y del mismo volumen ReFS. Mantiene los archivos existentes, comparte rangos alineados menores de 4 GiB, deja intacta la cola no alineada y verifica ambos hashes. Una operación parcial sigue teniendo los mismos datos porque las copias ya eran idénticas. Nunca crea hard links.
- `CloudFiles.GetState(path)`: consulta metadatos de Cloud Files sin leer datos. `Dehydrate(path, expected, cancellationToken)` exige raíz OneDrive registrada, in-sync, cero cambios locales, datos validados, archivo no pinned y oplock exclusivo; utiliza `CfDehydratePlaceholder`. Comprueba cancelación durante validaciones y antes de deshidratar; después completa la verificación sin interrupciones. No marca el archivo como sincronizado, no modifica pinning, no borra y no tiene fallback. El proveedor puede negar la operación.
- `RecycleBinService.Recycle(paths, historyDirectory, journalPrepared, expected)`: crea y fuerza a disco un JSON de intención antes de llamar al callback y al Shell. Usa `IFileOperation` en STA con reciclaje obligatorio, cancelación temprana y veto de operaciones que no señalan reciclaje. Confirma cada item mediante el objeto recuperable devuelto por `PostDeleteItem`; guarda su identidad, ruta física y nombre de análisis del namespace del Shell. No vacía la Papelera ni sustituye errores por borrado permanente.
- `Restore(journalPath)`: lee un archivo de historial estable sin seguir enlaces y valida todas sus entradas antes de mover la primera. Vincula cada ruta original con su metadato guardado y comprueba estados, identidades y PIDL; un formato inválido se informa mediante `IOException`. Verifica el ID original del elemento recuperable y rechaza destinos existentes o cuya ausencia no puede comprobarse. El callback comprueba de nuevo origen, carpeta, nombre y volumen de destino. La restauración se realiza mediante el objeto del namespace de la Papelera, conservando su gestión por Windows. Si la app se interrumpe entre el movimiento y la escritura del resultado, la intención persiste y el elemento puede restaurarse desde la Papelera de Windows; no se inventa una ruta recuperable.

Las operaciones fijan ancestros mediante handles sin `FILE_SHARE_DELETE` y rechazan redirecciones; la operación Cloud Files admite exclusivamente tags de Cloud Files. La política de carpetas autorizadas y las reglas de antigüedad del producto se aplican además en Core/Runtime. El servicio del Shell no proporciona una transacción conjunta entre el movimiento y el journal de la aplicación.

NTFS y ReFS aceptan un token opcional de cancelación durante las validaciones y los hashes previos, comprobado entre bloques de 1 MiB y justo antes de la primera modificación. También se comprueba entre las aperturas de ancestros y las consultas de Restart Manager. Las llamadas Win32 síncronas que ya están en curso terminan antes de observar el token. Tras comenzar la modificación se completa la verificación y, cuando corresponda, la reversión o conservación de fecha sin interrupciones por cancelación.

## Verificación en Windows

`WindowsSmokeTests.RunAsync(fixtureParent, includeRecycle: true)` crea una carpeta nueva con GUID y solo opera sobre sus fixtures. Prueba metadatos, volumen, Known Folders, errores de Restart Manager, rechazo de archivos ordinarios en Cloud Files, NTFS ida/vuelta, hashes, bloqueos, enlaces duros, estado desactualizado, symlink cuando Windows lo permite, reciclaje/restauración de archivo y carpeta, historiales corruptos y destinos ocupados. La comprobación de existencia distingue ausencia de errores Win32. En un volumen ReFS prueba clonación, conservación de fecha y escrituras independientes. Conserva los fixtures y journals si una comprobación falla. No prueba deshidratación de datos reales de OneDrive.

Las pruebas de ReFS requieren un volumen ReFS y las de Shell una sesión de usuario con Papelera operativa. La deshidratación necesita una prueba controlada con un proveedor Cloud Files y datos expresamente creados para ella; no basta una compilación ni un archivo ordinario local para acreditar esa operación.

## Fuentes primarias consultadas

- [FILE_ID_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info)
- [FSCTL_SET_COMPRESSION](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_set_compression)
- [Block Cloning y restricciones ReFS](https://learn.microsoft.com/en-us/windows/win32/fileio/block-cloning)
- [RmGetList](https://learn.microsoft.com/en-us/windows/win32/api/restartmanager/nf-restartmanager-rmgetlist)
- [RM_REBOOT_REASON](https://learn.microsoft.com/en-us/windows/win32/api/restartmanager/ne-restartmanager-rm_reboot_reason)
- [GetFileAttributesW y enlaces simbólicos](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfileattributesw)
- [IFileOperation SetOperationFlags](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-setoperationflags)
- [PreDeleteItem](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperationprogresssink-predeleteitem) y [PostDeleteItem](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperationprogresssink-postdeleteitem)
- [PreMoveItem](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperationprogresssink-premoveitem)
- [TRANSFER_SOURCE_FLAGS](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/ne-shobjidl_core-_transfer_source_flags)
- [CfOpenFileWithOplock](https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfopenfilewithoplock) y [CfGetPlaceholderInfo](https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfgetplaceholderinfo)
- [Cabecera cfapi.h de Microsoft](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/cfapi.h), para firmas, tamaños y valores de las enumeraciones.

## Compactación SQLite

`SqliteCompactor.Inspect(path)` abre solo lectura, impide escritores con un handle de Windows, valida la cabecera y excluye WAL/SHM/journal antes de abrir SQLite. Usa únicamente `winsqlite3.dll` del directorio System32. Requiere SQLite 3.37 o posterior para `table_list`: tablas virtuales/shadow/FTS y triggers se excluyen. Toda tabla de usuario debe ser WITHOUT ROWID o tener exactamente un INTEGER PRIMARY KEY que sea alias real de rowid; `index_list` detecta la excepción `PRIMARY KEY DESC`. Deshabilita extensiones y trusted_schema, activa modo defensivo y verifica sus resultados.

`Compact(path, expected)` abre el original READWRITE sin CREATE. Exige espacio libre de dos veces el tamaño lógico más 64 MiB; adquiere `locking_mode=EXCLUSIVE` y `BEGIN EXCLUSIVE; COMMIT`, cuyo bloqueo permanece hasta cerrar la conexión. `VACUUM main` se ejecuta fuera de una transacción explícita y conserva las garantías transaccionales de SQLite. Comprueba `integrity_check` y un SHA-256 lógico de esquema, versión de usuario, identificador de aplicación y todas las celdas (con tipos, longitudes y orden por clave) antes y después. No compara hashes binarios de la base, porque la disposición física cambia. El resultado incluye asignación anterior/posterior; el proveedor debe excluir además credenciales y nubes.

Fuentes: [biblioteca SQLite de Windows](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions), [VACUUM](https://www.sqlite.org/lang_vacuum.html), [rowid e INTEGER PRIMARY KEY](https://www.sqlite.org/rowidtable.html), [excepción PRIMARY KEY DESC](https://www.sqlite.org/lang_createtable.html), [table_list y locking_mode](https://www.sqlite.org/pragma.html), [sqlite3_db_config](https://www.sqlite.org/c3ref/c_dbconfig_defensive.html). Los fixtures se incluyen en `WindowsSmokeTests`; el análisis de comportamiento equivalente con SQLite de otro sistema no sustituye ejecutarlos en Windows.
