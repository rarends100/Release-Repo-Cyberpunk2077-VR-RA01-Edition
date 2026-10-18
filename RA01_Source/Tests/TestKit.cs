using System.Numerics;
using System.Reflection;
using Xunit;

// RA01: M47 - the tests share static game state (Tuning, HookRegistry, ExeScan...), just as the mod does, so they run one at a time, never side by side.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace RA01.Tests;

// RA01: M47 - the toolbox every test file uses: where the project's source files are, reaching private fields and methods of the mod's classes (the tests must not
// change the mod to see inside it), and "close enough" compares for floating-point numbers.
// Builder picture: the inspector's tool bag - a tape measure (Near), a torch for looking behind panels (Field / Call), and the site plan (Root).
// Cost - CPU: extremely low (reflection look-ups and small file reads, test runs only); GPU: none.
internal static class Kit
{
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    // RA01: the project folder (the one holding RA01.csproj), found by walking up from the test's own output folder.
    // RA01: Cost - CPU: extremely low (a few folder checks, once); GPU: none.
    public static readonly string Root = FindRoot();
    static string FindRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "RA01.csproj"))) return d.FullName;
        throw new InvalidOperationException("RA01 tests: RA01.csproj not found above " + AppContext.BaseDirectory);
    }

    // RA01: every mod source file (Core, Actions, Visuals), as a path relative to Root with forward slashes ("Core/Log.cs").
    // Cost - CPU: extremely low (one folder walk); GPU: none.
    public static IEnumerable<string> SourceFiles() =>
        new[] { "Core", "Actions", "Visuals" }.SelectMany(top => Directory.GetFiles(Path.Combine(Root, top), "*.cs", SearchOption.AllDirectories))
            .Select(f => Path.GetRelativePath(Root, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal);

    // RA01: one source file's text. Cost - CPU: extremely low (one file read); GPU: none.
    public static string Source(string relative) => File.ReadAllText(Path.Combine(Root, relative));

    // RA01: a mod class by its name, in any of the three namespaces. Cost - CPU: extremely low; GPU: none.
    public static Type Type(string name) =>
        typeof(Kit).Assembly.GetType("RA01.Core." + name) ?? typeof(Kit).Assembly.GetType("RA01.Actions." + name) ?? typeof(Kit).Assembly.GetType("RA01.Visuals." + name)
        ?? throw new InvalidOperationException("RA01 tests: no class " + name);

    // RA01: read or write a static field or property, private or not. Cost - CPU: extremely low (a reflection look-up); GPU: none.
    public static T Get<T>(Type t, string name) =>
        (T)(t.GetField(name, Any)?.GetValue(null) ?? t.GetProperty(name, Any)?.GetValue(null) ?? throw new InvalidOperationException($"RA01 tests: {t.Name}.{name} not found or null"));
    public static object? GetObj(Type t, string name) =>
        t.GetField(name, Any) is FieldInfo f ? f.GetValue(null) : t.GetProperty(name, Any) is PropertyInfo p ? p.GetValue(null) : throw new InvalidOperationException($"RA01 tests: {t.Name}.{name} not found");
    public static void Set(Type t, string name, object? value)
    {
        if (t.GetField(name, Any) is FieldInfo f) { f.SetValue(null, value); return; }
        if (t.GetProperty(name, Any) is PropertyInfo p && p.CanWrite) { p.SetValue(null, value); return; }
        throw new InvalidOperationException($"RA01 tests: {t.Name}.{name} not found or read-only");
    }

    // RA01: call a static method, private or not (picked by name and argument count). Cost - CPU: extremely low (a reflection call); GPU: none.
    public static object? Call(Type t, string name, params object?[] args)
    {
        MethodInfo m = t.GetMethods(Any).Single(x => x.Name == name && x.GetParameters().Length == args.Length);
        try { return m.Invoke(null, args); }
        catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
    }

    // RA01: a typed delegate to a static method, private or not - for methods reflection cannot call directly (spans, pointers). Cost - CPU: extremely low; GPU: none.
    public static D Method<D>(Type t, string name) where D : Delegate
    {
        int count = typeof(D).GetMethod("Invoke")!.GetParameters().Length;
        MethodInfo m = t.GetMethods(Any).Single(x => x.Name == name && x.GetParameters().Length == count);
        return m.CreateDelegate<D>();
    }

    // RA01: a getter that hands back a span (the shape the mod's byte patterns are written in: "static ReadOnlySpan<byte> X => new byte[] {...}").
    public delegate ReadOnlySpan<byte> SpanGetter();

    // RA01: the bytes behind a Rehooker pattern name "File.Name" (HookRegistry.Rows' last column): a span property or a byte[] field in that file's class.
    // The file name is the class name, except a partial class's extra file ("GameCall_Ammo" is part of GameCall). Null if there is no such member.
    // Cost - CPU: extremely low (a reflection look-up and a small copy); GPU: none.
    public static byte[]? PatternBytes(string pattern)
    {
        int dot = pattern.IndexOf('.');
        string file = pattern[..dot], member = pattern[(dot + 1)..];
        Type? t = typeof(Kit).Assembly.GetTypes().FirstOrDefault(x => x.Name == file && x.Namespace?.StartsWith("RA01.") == true && x.Namespace != "RA01.Tests")
                  ?? typeof(Kit).Assembly.GetTypes().FirstOrDefault(x => x.Name == file.Split('_')[0] && x.Namespace?.StartsWith("RA01.") == true && x.Namespace != "RA01.Tests");
        if (t == null) return null;
        if (t.GetField(member, Any) is FieldInfo f && f.FieldType == typeof(byte[])) return (byte[]?)f.GetValue(null);
        if (t.GetProperty(member, Any) is PropertyInfo p && p.PropertyType == typeof(ReadOnlySpan<byte>)) return p.GetMethod!.CreateDelegate<SpanGetter>()().ToArray();
        return null;
    }

    // RA01: bytes as "48 8B C4 ..." for messages and snapshots. Cost - CPU: extremely low; GPU: none.
    public static string Hex(byte[] bytes) => string.Join(" ", bytes.Select(b => b.ToString("X2")));

    // RA01: "close enough" for floating-point numbers, with a message that says what was measured. Cost - CPU: extremely low; GPU: none.
    public static void Near(float expected, float actual, float tolerance, string what) =>
        Assert.True(MathF.Abs(expected - actual) <= tolerance, $"{what}: expected {expected}, got {actual} (allowed +/- {tolerance})");
    public static void Near(Vector3 expected, Vector3 actual, float tolerance, string what) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"{what}: expected {expected}, got {actual} (allowed +/- {tolerance})");

    // RA01: two turns are the same turn (q and -q are the same turn). Cost - CPU: extremely low; GPU: none.
    public static void SameTurn(Quaternion expected, Quaternion actual, float tolerance, string what) =>
        Assert.True(MathF.Abs(MathF.Abs(Quaternion.Dot(Quaternion.Normalize(expected), Quaternion.Normalize(actual))) - 1f) <= tolerance, $"{what}: expected {expected}, got {actual}");

    // RA01: remembers every static value of a class (arrays by content) and hands back a way to put them all back - for tests that change Tuning and the
    // like, so the next test starts from the same place. Cost - CPU: extremely low (a few hundred field copies); GPU: none.
    public static Action Remember(Type t)
    {
        List<(FieldInfo f, object? v)> kept = new();
        foreach (FieldInfo f in t.GetFields(Any).Where(f => !f.IsLiteral))
        {
            object? v = f.GetValue(null);
            kept.Add((f, v is Array a ? a.Clone() : v));
        }
        return () =>
        {
            foreach ((FieldInfo f, object? v) in kept)
                if (f.IsInitOnly) { if (v is Array a && f.GetValue(null) is Array live) Array.Copy(a, live, a.Length); }
                else f.SetValue(null, v is Array a ? a.Clone() : v);
        };
    }

    // RA01: a new empty folder for one test's files, under the test run's temp folder. Cost - CPU: extremely low (one folder creation); GPU: none.
    public static string NewFolder(string name)
    {
        string path = Path.Combine(RA01.Core.ModFolders.Root, name + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    // RA01: the message shown when a "snapshot" test fails: it is a tripwire, not necessarily a bug. Cost - CPU: extremely low; GPU: none.
    public static string Tripwire(string what, string where) =>
        $"TRIPWIRE: {what} changed. If you changed it on purpose and the mod works, update the snapshot in Tests\\{where} to match your code.";
}
