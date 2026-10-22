using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Limpiador.Windows;

public sealed record SQLiteCompactionInfo(string Path, bool CanCompact, string Reason, long ReclaimableBytes,
    long PageSize, long PageCount, long FreePages, string SQLiteVersion, int TableCount);
public sealed record SQLiteCompactionResult(string Path, long BeforeAllocatedBytes, long AfterAllocatedBytes,
    long ReclaimedBytes, string LogicalSha256, string SQLiteVersion);

[SupportedOSPlatform("windows10.0.17763")]
public static class SqliteCompactor
{
    public const long MinimumReclaimableBytes = 1024 * 1024;
    private const int MinimumVersion = 3037000; // table_list reports true WITHOUT ROWID and virtual/shadow status.

    public static SQLiteCompactionInfo Inspect(string path)
    {
        path = WindowsFileSystem.NormalizeLocalPath(path);
        try
        {
            RequireVersion();
            using var lease = new NativePathLease(path);
            // Prevent writes/deletion throughout read-only inspection. sqlite3 itself is also opened READONLY.
            using var guard = WindowsFileSystem.Open(path, NativeMethods.GenericRead, NativeMethods.ShareRead);
            var state = WindowsFileSystem.ReadMetadata(guard, path); WindowsFileSystem.RequireMutableFile(state);
            RequireRollbackDatabase(path, guard);
            using var db = new Database(path, readOnly: true);
            var tables = ValidateSchema(db);
            CheckIntegrity(db);
            return Describe(path, db, tables.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException or OverflowException)
        { return new(path, false, ex.Message, 0, 0, 0, 0, SafeVersion(), 0); }
    }

    public static SQLiteCompactionResult Compact(string path, NativeFileMetadata? expected = null)
    {
        path = WindowsFileSystem.NormalizeLocalPath(path); RequireVersion();
        using var lease = new NativePathLease(path);
        RestartManager.RequireUnlocked([path]);
        // SQLite needs its own RW handle. This identity guard prevents renames/deletion while SQLite holds its byte-range locks.
        using var guard = WindowsFileSystem.Open(path, NativeMethods.GenericRead, NativeMethods.ShareRead | NativeMethods.ShareWrite);
        var before = WindowsFileSystem.ReadMetadata(guard, path);
        WindowsFileSystem.RequireMutableFile(before); WindowsFileSystem.RequireExpected(before, expected);
        RequireRollbackDatabase(path, guard);
        var disk = WindowsFileSystem.GetDiskSpace(path);
        var required = checked(before.LogicalBytes * 2 + 64L * 1024 * 1024);
        if (disk.AvailableBytes < required) throw new IOException("SQLite VACUUM necesita hasta dos veces el tamaño original de espacio libre, más 64 MiB de margen.");
        var temporaryDisk = WindowsFileSystem.GetDiskSpace(Path.GetTempPath());
        if (temporaryDisk.AvailableBytes < before.LogicalBytes + 64L * 1024 * 1024)
            throw new IOException("El volumen temporal de Windows no tiene espacio para la copia de trabajo de SQLite.");
        string digest;
        using (var db = new Database(path, readOnly: false))
        {
            if (!db.Scalar("PRAGMA main.locking_mode=EXCLUSIVE").Equals("exclusive", StringComparison.OrdinalIgnoreCase))
                throw new IOException("SQLite no concedió el modo de bloqueo exclusivo.");
            db.Execute("PRAGMA synchronous=EXTRA");
            if (db.Scalar("PRAGMA synchronous") != "3") throw new IOException("SQLite no confirmó la sincronización reforzada.");
            // EXCLUSIVE mode retains the exclusive lock after COMMIT until this connection closes.
            // VACUUM itself must run outside an explicit transaction.
            db.Execute("BEGIN EXCLUSIVE"); db.Execute("COMMIT");
            WindowsFileSystem.RequireExpected(WindowsFileSystem.ReadMetadata(guard, path), before);
            var tables = ValidateSchema(db); CheckIntegrity(db);
            var info = Describe(path, db, tables.Count);
            if (!info.CanCompact) throw new IOException(info.Reason);
            digest = LogicalDigest(db, tables);
            db.Execute("VACUUM main");
            CheckIntegrity(db);
            var afterTables = ValidateSchema(db);
            if (LogicalDigest(db, afterTables) != digest)
                throw new IOException("SQLite completó VACUUM pero la verificación lógica no coincide; revisa la base antes de usarla.");
        }
        var after = WindowsFileSystem.ReadMetadata(guard, path);
        if (after.VolumeSerial != before.VolumeSerial || after.FileId != before.FileId)
            throw new IOException("Cambió la identidad de la base de datos durante la compactación.");
        return new(path, before.AllocatedBytes, after.AllocatedBytes, before.AllocatedBytes - after.AllocatedBytes, digest, SafeVersion());
    }

    private static SQLiteCompactionInfo Describe(string path, Database db, int tableCount)
    {
        var pageSize = Parse(db.Scalar("PRAGMA main.page_size"));
        var pageCount = Parse(db.Scalar("PRAGMA main.page_count"));
        var freePages = Parse(db.Scalar("PRAGMA main.freelist_count"));
        if (pageSize < 512 || pageSize > 65536 || (pageSize & (pageSize - 1)) != 0 || freePages < 0 || freePages > pageCount)
            throw new IOException("La contabilidad de páginas SQLite no es válida.");
        var free = checked(pageSize * freePages);
        return new(path, free >= MinimumReclaimableBytes, free >= MinimumReclaimableBytes ? "Páginas libres recuperables; tablas con claves estables e integridad comprobada." : "Menos de 1 MiB de páginas libres: no compensa compactar.",
            free, pageSize, pageCount, freePages, SafeVersion(), tableCount);
    }
    private static List<Table> ValidateSchema(Database db)
    {
        if (db.Scalar("PRAGMA main.journal_mode") != "delete") throw new IOException("Solo se compactan bases en journal_mode=DELETE; WAL y otros modos se omiten.");
        if (db.Rows("SELECT name FROM main.sqlite_schema WHERE type='trigger'").Count != 0) throw new IOException("La base contiene triggers y se omite.");
        var tables = new List<Table>();
        foreach (var row in db.Rows("PRAGMA main.table_list"))
        {
            if (row.Length < 6 || row[0] != "main") throw new IOException("SQLite devolvió información de tablas incompleta.");
            var name = row[1] ?? throw new IOException("Tabla sin nombre."); var type = row[2];
            if (type is "virtual" or "shadow") throw new IOException("La base contiene tablas virtuales, FTS o shadow; se omite.");
            if (type == "view") continue;
            if (type != "table") throw new IOException("Tipo de tabla no reconocido.");
            if (name == "sqlite_schema") continue;
            var columns = db.Rows("PRAGMA main.table_xinfo(" + Literal(name) + ")");
            if (columns.Count == 0 || columns.Any(x => x.Length < 7)) throw new IOException("No se pudo comprobar el esquema de una tabla.");
            var primary = columns.Where(x => Parse(x[5]) > 0).OrderBy(x => Parse(x[5])).ToArray();
            if (name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
            {
                // Internal sequence/statistics rows are logical data too; order by all columns and hash their values.
                if (name is not ("sqlite_sequence" or "sqlite_stat1" or "sqlite_stat2" or "sqlite_stat3" or "sqlite_stat4"))
                    throw new IOException("Tabla interna de SQLite no reconocida.");
                tables.Add(new(name, columns.Select(x => x[1]!).ToArray())); continue;
            }
            var withoutRowid = row[4] == "1";
            if (!withoutRowid)
            {
                var pkIndex = db.Rows("PRAGMA main.index_list(" + Literal(name) + ")").Any(x => x.Length < 4 || x[3] == "pk");
                if (primary.Length != 1 || !string.Equals(primary[0][2], "INTEGER", StringComparison.OrdinalIgnoreCase) || pkIndex)
                    throw new IOException("Una tabla usa rowid implícito o una clave que no es alias INTEGER PRIMARY KEY (incluye PRIMARY KEY DESC); se omite.");
            }
            else if (primary.Length == 0) throw new IOException("Tabla WITHOUT ROWID sin clave verificable.");
            tables.Add(new(name, primary.Select(x => x[1]!).ToArray()));
        }
        return tables.OrderBy(x => x.Name, StringComparer.Ordinal).ToList();
    }
    private static void CheckIntegrity(Database db)
    {
        var rows = db.Rows("PRAGMA main.integrity_check(100)");
        if (rows.Count != 1 || rows[0].Length != 1 || rows[0][0] != "ok") throw new IOException("La base no superó integrity_check; no se compacta.");
    }
    private static void RequireRollbackDatabase(string path, SafeFileHandle handle)
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            if (WindowsFileSystem.EntryExists(path + suffix)) throw new IOException("La base tiene un WAL, SHM o journal; cierra su aplicación y deja que termine sus transacciones.");
        var header = new byte[100];
        if (RandomAccess.Read(handle, header, 0) != header.Length || !header.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8))
            throw new IOException("No es una base SQLite 3 ordinaria.");
        if (header[18] != 1 || header[19] != 1) throw new IOException("La base utiliza WAL u otro formato de journal; se omite sin crear archivos auxiliares.");
    }
    private static string LogicalDigest(Database db, IReadOnlyList<Table> tables)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var value in new[] { db.Scalar("PRAGMA main.user_version"), db.Scalar("PRAGMA main.application_id"), db.Scalar("PRAGMA main.encoding") }) AppendText(hash, value);
        db.HashQuery("SELECT type,name,tbl_name,sql FROM main.sqlite_schema ORDER BY type COLLATE BINARY,name COLLATE BINARY", hash);
        foreach (var table in tables)
        {
            AppendText(hash, table.Name);
            db.HashQuery("SELECT * FROM main." + Identifier(table.Name) + " ORDER BY " + string.Join(",", table.OrderColumns.Select(Identifier)), hash);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static void AppendText(IncrementalHash hash, string text) { var data = Encoding.UTF8.GetBytes(text); hash.AppendData(BitConverter.GetBytes(data.Length)); hash.AppendData(data); }
    private static string Identifier(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
    private static string Literal(string text) => "'" + text.Replace("'", "''") + "'";
    private static long Parse(string? text) => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : throw new IOException("SQLite devolvió un entero inválido.");
    private static string SafeVersion() { try { return Marshal.PtrToStringUTF8(Sqlite.sqlite3_libversion()) ?? "desconocida"; } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return "no disponible"; } }
    private static void RequireVersion()
    {
        WindowsFileSystem.EnsureWindows();
        try
        {
            if (Sqlite.sqlite3_libversion_number() < MinimumVersion) throw new NotSupportedException("La versión SQLite de Windows es anterior a 3.37; no permite verificar de forma completa estas tablas.");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        { throw new NotSupportedException("La biblioteca SQLite de Windows no ofrece las APIs requeridas; no se instaló ni sustituyó ninguna DLL.", ex); }
    }
    /// <summary>Creates uniquely named SQLite fixtures inside an existing isolated test directory.</summary>
    public static IReadOnlyList<string> RunFixtureTests(string fixtureDirectory)
    {
        WindowsFileSystem.EnsureWindows();
        var results = new List<string>();
        try { RequireVersion(); } catch (NotSupportedException ex) { return ["SKIP SQLite: " + ex.Message]; }
        fixtureDirectory = WindowsFileSystem.NormalizeLocalPath(fixtureDirectory);
        if (!Directory.Exists(fixtureDirectory)) throw new DirectoryNotFoundException(fixtureDirectory);
        var created = new List<string>();
        string NewPath() { var path = Path.Combine(fixtureDirectory, "sqlite-fixture-" + Guid.NewGuid().ToString("N") + ".db"); created.Add(path); return path; }
        void Check(bool condition, string message) { if (!condition) throw new IOException("Prueba SQLite fallida: " + message); }
        void Create(string path, string definition)
        {
            using var db = new Database(path, readOnly: false, createFixture: true);
            db.Execute(definition);
        }
        void Bloat(string path, string name)
        {
            using var db = new Database(path, readOnly: false);
            db.Execute("WITH RECURSIVE numbers(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM numbers WHERE i<2048) INSERT INTO " + Identifier(name) + "(id,payload) SELECT i,randomblob(4096) FROM numbers");
            db.Execute("DELETE FROM " + Identifier(name) + " WHERE id>128");
        }
        var good = NewPath();
        // A quoted identifier verifies that metadata-driven SQL cannot change the intended statement.
        const string table = "fixture'\"name";
        Create(good, "CREATE TABLE " + Identifier(table) + "(id INTEGER PRIMARY KEY,payload BLOB)"); Bloat(good, table);
        string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
        var hash = HashFile(good); var before = WindowsFileSystem.GetMetadata(good);
        var info = Inspect(good);
        Check(info.CanCompact && info.ReclaimableBytes >= MinimumReclaimableBytes, "IPK debe ser apta: " + info.Reason);
        Check(HashFile(good) == hash && before.SameVersion(WindowsFileSystem.GetMetadata(good)), "inspección no altera la base");
        Check(!File.Exists(good + "-wal") && !File.Exists(good + "-shm") && !File.Exists(good + "-journal"), "inspección no crea sidecars");
        void RejectSidecar()
        {
            var blocked = Inspect(good);
            Check(!blocked.CanCompact && blocked.Reason.Contains("WAL, SHM o journal", StringComparison.Ordinal), "sidecar excluye antes de abrir SQLite");
            var refused = false;
            try { _ = Compact(good, before); } catch (IOException) { refused = true; }
            Check(refused && HashFile(good) == hash && before.SameVersion(WindowsFileSystem.GetMetadata(good)), "sidecar impide modificar la base");
        }
        var journalSidecar = good + "-journal";
        using (var sidecar = new FileStream(journalSidecar, FileMode.CreateNew, FileAccess.Write)) sidecar.WriteByte(1);
        RejectSidecar(); File.Delete(journalSidecar);
        results.Add("PASS SQLite: journal existente bloquea inspección y compactación sin cambiar la base");
        var absentSidecarTarget = Path.Combine(fixtureDirectory, "sqlite-absent-" + Guid.NewGuid().ToString("N"));
        if (CreateSymbolicLinkW(journalSidecar, absentSidecarTarget, 2))
        {
            RejectSidecar();
            Check(WindowsFileSystem.EntryExists(journalSidecar) && !WindowsFileSystem.EntryExists(absentSidecarTarget), "journal symlink no se sigue ni crea su destino");
            File.Delete(journalSidecar);
            results.Add("PASS SQLite: journal symlink sin destino se detecta sin seguirlo");
        }
        else results.Add("SKIP SQLite journal symlink: Windows no concedió creación de symlink");
        var compact = Compact(good, before);
        Check(compact.ReclaimedBytes > 0 && compact.LogicalSha256.Length == 64, "compactación debe liberar páginas y verificar contenido");
        using (var db = new Database(good, readOnly: true))
        { Check(db.Scalar("SELECT count(*) FROM " + Identifier(table)) == "128", "número de filas conservado"); Check(db.Scalar("SELECT max(id) FROM " + Identifier(table)) == "128", "claves conservadas"); }
        results.Add("PASS SQLite: lectura sin cambios/sidecars, IPK, identificadores escapados, VACUUM, integridad y digest lógico");
        var implicitRowid = NewPath(); Create(implicitRowid, "CREATE TABLE t(value TEXT)");
        Check(!Inspect(implicitRowid).CanCompact && Inspect(implicitRowid).Reason.Contains("rowid"), "rowid implícito excluido");
        var descending = NewPath(); Create(descending, "CREATE TABLE t(id INTEGER PRIMARY KEY DESC,value TEXT)");
        Check(!Inspect(descending).CanCompact && Inspect(descending).Reason.Contains("rowid"), "INTEGER PRIMARY KEY DESC no es alias rowid");
        var trigger = NewPath(); Create(trigger, "CREATE TABLE t(id INTEGER PRIMARY KEY,value TEXT)");
        using (var db = new Database(trigger, readOnly: false)) db.Execute("CREATE TRIGGER change_t AFTER INSERT ON t BEGIN UPDATE t SET value='changed' WHERE id=NEW.id; END");
        Check(!Inspect(trigger).CanCompact && Inspect(trigger).Reason.Contains("triggers"), "triggers excluidos");
        results.Add("PASS SQLite: rowid implícito, PRIMARY KEY DESC y triggers excluidos");
        var without = NewPath(); Create(without, "CREATE TABLE t(id INTEGER PRIMARY KEY,payload BLOB) WITHOUT ROWID"); Bloat(without, "t");
        Check(Inspect(without).CanCompact, "WITHOUT ROWID aceptada");
        Check(Compact(without, WindowsFileSystem.GetMetadata(without)).ReclaimedBytes > 0, "WITHOUT ROWID compactada");
        results.Add("PASS SQLite: WITHOUT ROWID mantiene claves y contenido");
        var fts = NewPath();
        try
        {
            Create(fts, "CREATE VIRTUAL TABLE t USING fts5(content)");
            Check(!Inspect(fts).CanCompact && Inspect(fts).Reason.Contains("virtuales"), "FTS excluida");
            results.Add("PASS SQLite: FTS/tablas virtuales excluidas");
        }
        catch (IOException ex) when (ex.Message.Contains("no such module", StringComparison.OrdinalIgnoreCase))
        { results.Add("SKIP SQLite FTS: winsqlite3 no incluye el módulo FTS5"); }
        // Remove only files created above, after all assertions passed. A failure leaves evidence in the isolated directory.
        foreach (var path in created)
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        return results;
    }
    private sealed record Table(string Name, string[] OrderColumns);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)] private static extern bool CreateSymbolicLinkW(string link, string target, uint flags);

    private sealed class Database : IDisposable
    {
        private nint handle;
        internal Database(string path, bool readOnly, bool createFixture = false)
        {
            var flags = (readOnly ? 1 : 2) | 0x10000 | 0x01000000 | (createFixture ? 4 : 0); // FULLMUTEX | NOFOLLOW; CREATE only fixtures.
            var status = Sqlite.sqlite3_open_v2(path, out handle, flags, 0);
            try
            {
                Check(status);
                Check(Sqlite.sqlite3_busy_timeout(handle, 250));
                Config(1005, 0); // Disable SQL/API extension loading.
                Config(1010, 1); // Defensive mode.
                Config(1017, 0); // trusted_schema=OFF; check return/result, never ignore an unknown pragma.
                if (Scalar("PRAGMA trusted_schema") != "0") throw new IOException("SQLite no confirmó trusted_schema=OFF.");
                if (readOnly) { Execute("PRAGMA query_only=ON"); if (Scalar("PRAGMA query_only") != "1") throw new IOException("SQLite no confirmó query_only."); }
            }
            catch { Dispose(); throw; }
        }
        private void Config(int option, int value)
        { Check(Sqlite.sqlite3_db_config(handle, option, value, out var result)); if (result != value) throw new IOException("SQLite no confirmó una opción de seguridad requerida."); }
        internal void Execute(string sql) { using var statement = Prepare(sql); while (Step(statement.Handle)) { } }
        internal string Scalar(string sql)
        {
            var rows = Rows(sql);
            if (rows.Count != 1 || rows[0].Length != 1 || rows[0][0] is not { } value) throw new IOException("SQLite no devolvió el valor solicitado.");
            return value;
        }
        internal List<string?[]> Rows(string sql)
        {
            using var statement = Prepare(sql); var result = new List<string?[]>();
            while (Step(statement.Handle))
            {
                if (result.Count >= 100000) throw new IOException("El esquema supera el límite de inspección segura.");
                var columns = Sqlite.sqlite3_column_count(statement.Handle); var row = new string?[columns];
                for (var i = 0; i < columns; i++) row[i] = Marshal.PtrToStringUTF8(Sqlite.sqlite3_column_text(statement.Handle, i));
                result.Add(row);
            }
            return result;
        }
        internal void HashQuery(string sql, IncrementalHash hash)
        {
            using var statement = Prepare(sql); var buffer = new byte[65536];
            while (Step(statement.Handle))
            {
                var columns = Sqlite.sqlite3_column_count(statement.Handle); hash.AppendData(BitConverter.GetBytes(columns));
                for (var i = 0; i < columns; i++)
                {
                    var type = Sqlite.sqlite3_column_type(statement.Handle, i); hash.AppendData([(byte)type]);
                    if (type == 1) { hash.AppendData(BitConverter.GetBytes(Sqlite.sqlite3_column_int64(statement.Handle, i))); continue; }
                    if (type == 2) { hash.AppendData(BitConverter.GetBytes(Sqlite.sqlite3_column_double(statement.Handle, i))); continue; }
                    if (type == 5) continue;
                    if (type is not (3 or 4)) throw new IOException("Tipo de dato SQLite no reconocido.");
                    var pointer = type == 3 ? Sqlite.sqlite3_column_text(statement.Handle, i) : Sqlite.sqlite3_column_blob(statement.Handle, i);
                    var length = Sqlite.sqlite3_column_bytes(statement.Handle, i); hash.AppendData(BitConverter.GetBytes(length));
                    if (length > 0 && pointer == 0) throw new IOException("No se pudo leer una celda SQLite.");
                    for (var offset = 0; offset < length;)
                    { var count = Math.Min(buffer.Length, length - offset); Marshal.Copy(pointer + offset, buffer, 0, count); hash.AppendData(buffer, 0, count); offset += count; }
                }
            }
            hash.AppendData(BitConverter.GetBytes(-1)); // Separate empty tables and result boundaries.
        }
        private Statement Prepare(string sql)
        {
            Check(Sqlite.sqlite3_prepare_v2(handle, sql, -1, out var statement, 0));
            if (statement == 0) throw new IOException("SQLite no preparó la consulta."); return new(statement);
        }
        private bool Step(nint statement) { var result = Sqlite.sqlite3_step(statement); if (result == 100) return true; if (result == 101) return false; Check(result); return false; }
        private void Check(int status)
        {
            if (status != 0) throw new IOException($"SQLite rechazó la operación ({status}): {Marshal.PtrToStringUTF8(Sqlite.sqlite3_errmsg(handle))}");
        }
        public void Dispose() { if (handle != 0) { Sqlite.sqlite3_close_v2(handle); handle = 0; } }
    }
    private sealed class Statement(nint handle) : IDisposable
    {
        internal nint Handle { get; } = handle;
        public void Dispose() => Sqlite.sqlite3_finalize(Handle);
    }

    private static class Sqlite
    {
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int sqlite3_libversion_number();
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern nint sqlite3_libversion();
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint db, int flags, nint vfs);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int sqlite3_close_v2(nint db);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int sqlite3_busy_timeout(nint db, int milliseconds);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int sqlite3_db_config(nint db, int option, int value, out int result);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern nint sqlite3_errmsg(nint db);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int sqlite3_prepare_v2(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out nint statement, nint tail);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int sqlite3_step(nint statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int sqlite3_finalize(nint statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int sqlite3_column_count(nint statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int sqlite3_column_type(nint statement, int column);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern nint sqlite3_column_text(nint statement, int column);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern nint sqlite3_column_blob(nint statement, int column);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int sqlite3_column_bytes(nint statement, int column);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern long sqlite3_column_int64(nint statement, int column);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern double sqlite3_column_double(nint statement, int column);
    }
}
