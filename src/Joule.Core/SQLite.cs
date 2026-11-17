using System.Runtime.InteropServices;
using System.Text;

namespace Joule.Core;

/// <summary>Minimal wrapper around winsqlite3.dll, the SQLite that ships with Windows 10 and 11.</summary>
internal static unsafe partial class Sqlite3
{
    private const string Lib = "winsqlite3.dll";
    public const int OK = 0, ROW = 100, DONE = 101;
    public const int NULL = 5;
    public const int OPEN_READWRITE = 0x2, OPEN_CREATE = 0x4, OPEN_FULLMUTEX = 0x10000;
    public static readonly IntPtr TRANSIENT = new(-1);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] public static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] public static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] public static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr arg, out IntPtr errmsg);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] public static extern void sqlite3_free(IntPtr ptr);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] public static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nbytes, out IntPtr stmt, IntPtr tail);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] public static extern int sqlite3_step(IntPtr stmt);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] public static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] public static extern int sqlite3_bind_double(IntPtr stmt, int index, double value);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] public static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] public static extern int sqlite3_bind_null(IntPtr stmt, int index);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)] public static extern int sqlite3_bind_text16(IntPtr stmt, int index, string value, int nbytes, IntPtr destructor);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] public static extern double sqlite3_column_double(IntPtr stmt, int index);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] public static extern long sqlite3_column_int64(IntPtr stmt, int index);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] public static extern IntPtr sqlite3_column_text16(IntPtr stmt, int index);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] public static extern int sqlite3_column_type(IntPtr stmt, int index);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] public static extern IntPtr sqlite3_errmsg16(IntPtr db);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] public static extern long sqlite3_last_insert_rowid(IntPtr db);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] public static extern int sqlite3_busy_timeout(IntPtr db, int ms);

    public static byte[] Utf8(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        Array.Resize(ref bytes, bytes.Length + 1);
        return bytes;
    }
}

public sealed class SQLiteException(string message) : Exception(message);

public readonly struct Row
{
    private readonly IntPtr _stmt;
    internal Row(IntPtr stmt) { _stmt = stmt; }

    public bool IsNull(int i) => Sqlite3.sqlite3_column_type(_stmt, i) == Sqlite3.NULL;
    public double Double(int i) => Sqlite3.sqlite3_column_double(_stmt, i);
    public long Int(int i) => Sqlite3.sqlite3_column_int64(_stmt, i);
    public string String(int i)
    {
        var p = Sqlite3.sqlite3_column_text16(_stmt, i);
        return p == IntPtr.Zero ? "" : Marshal.PtrToStringUni(p) ?? "";
    }
    public string? OptionalString(int i) => IsNull(i) ? null : String(i);
    public double? OptionalDouble(int i) => IsNull(i) ? null : Double(i);
    public DateTime Date(int i) => Time.FromUnix(Double(i));
}

public sealed class SQLiteDatabase : IDisposable
{
    private IntPtr _db;

    public SQLiteDatabase(string path)
    {
        var rc = Sqlite3.sqlite3_open_v2(Sqlite3.Utf8(path), out _db,
            Sqlite3.OPEN_READWRITE | Sqlite3.OPEN_CREATE | Sqlite3.OPEN_FULLMUTEX, IntPtr.Zero);
        if (rc != Sqlite3.OK) throw new SQLiteException($"Cannot open {path} ({rc})");
        Sqlite3.sqlite3_busy_timeout(_db, 5000);
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
    }

    private string LastError
    {
        get
        {
            var p = Sqlite3.sqlite3_errmsg16(_db);
            return p == IntPtr.Zero ? "unknown error" : Marshal.PtrToStringUni(p) ?? "unknown error";
        }
    }

    public void Execute(string sql)
    {
        var rc = Sqlite3.sqlite3_exec(_db, Sqlite3.Utf8(sql), IntPtr.Zero, IntPtr.Zero, out var err);
        if (rc != Sqlite3.OK)
        {
            var message = err != IntPtr.Zero ? Marshal.PtrToStringUTF8(err) : LastError;
            if (err != IntPtr.Zero) Sqlite3.sqlite3_free(err);
            throw new SQLiteException($"{message} in: {sql}");
        }
    }

    private IntPtr Prepare(string sql, object?[] args)
    {
        if (Sqlite3.sqlite3_prepare_v2(_db, Sqlite3.Utf8(sql), -1, out var stmt, IntPtr.Zero) != Sqlite3.OK)
            throw new SQLiteException($"{LastError} in: {sql}");
        for (var i = 0; i < args.Length; i++)
        {
            var index = i + 1;
            switch (args[i])
            {
                case null: Sqlite3.sqlite3_bind_null(stmt, index); break;
                case double d: Sqlite3.sqlite3_bind_double(stmt, index, d); break;
                case float f: Sqlite3.sqlite3_bind_double(stmt, index, f); break;
                case int n: Sqlite3.sqlite3_bind_int64(stmt, index, n); break;
                case long l: Sqlite3.sqlite3_bind_int64(stmt, index, l); break;
                case bool b: Sqlite3.sqlite3_bind_int64(stmt, index, b ? 1 : 0); break;
                case DateTime t: Sqlite3.sqlite3_bind_double(stmt, index, Time.Unix(t)); break;
                case string s: Sqlite3.sqlite3_bind_text16(stmt, index, s, s.Length * 2, Sqlite3.TRANSIENT); break;
                default:
                    Sqlite3.sqlite3_finalize(stmt);
                    throw new SQLiteException($"Unsupported parameter type {args[i]!.GetType()}");
            }
        }
        return stmt;
    }

    public void Run(string sql, params object?[] args)
    {
        var stmt = Prepare(sql, args);
        try
        {
            var rc = Sqlite3.sqlite3_step(stmt);
            if (rc != Sqlite3.DONE && rc != Sqlite3.ROW) throw new SQLiteException($"{LastError} in: {sql}");
        }
        finally { Sqlite3.sqlite3_finalize(stmt); }
    }

    public void Query(string sql, Action<Row> each, params object?[] args)
    {
        var stmt = Prepare(sql, args);
        try
        {
            while (true)
            {
                var rc = Sqlite3.sqlite3_step(stmt);
                if (rc == Sqlite3.ROW) each(new Row(stmt));
                else if (rc == Sqlite3.DONE) break;
                else throw new SQLiteException($"{LastError} in: {sql}");
            }
        }
        finally { Sqlite3.sqlite3_finalize(stmt); }
    }

    public void Transaction(Action body)
    {
        Execute("BEGIN IMMEDIATE");
        try
        {
            body();
            Execute("COMMIT");
        }
        catch
        {
            try { Execute("ROLLBACK"); } catch { }
            throw;
        }
    }

    public long LastInsertId => Sqlite3.sqlite3_last_insert_rowid(_db);

    public void Dispose()
    {
        if (_db != IntPtr.Zero) { Sqlite3.sqlite3_close_v2(_db); _db = IntPtr.Zero; }
    }
}
