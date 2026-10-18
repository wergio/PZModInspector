using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using PZModInspector.Core.Model;

namespace PZModInspector.Core.Environment;

/// <summary>
/// The version compiled into the game: zombie.core.Core has
/// <c>private static final GameVersion gameVersion = new GameVersion(42, 21, "");</c>, so the static initializer of
/// Core.class in projectzomboid.jar holds new GameVersion; dup; push major; push minor; ldc suffix; invokespecial; putstatic.
/// It follows the installed files, unlike console.txt, which is as old as the last start of the game.
/// </summary>
public static class GameJarVersion
{
    private const string GameVersionClass = "zombie/core/GameVersion";
    private const string CoreClass = "zombie/core/Core";

    public static GameVersion? Read(string jarPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            if (zip.GetEntry(CoreClass + ".class") is not { } entry)
                return null;
            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return FromCoreClass(memory.ToArray());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Null when the class does not look as expected: better no version than a wrong one.</summary>
    public static GameVersion? FromCoreClass(byte[] bytes)
    {
        try
        {
            var pool = ConstantPool.Read(bytes, out var position);
            if (FindStaticInitializer(bytes, position, pool) is not { } code)
                return null;
            for (var i = 0; i + 5 < code.Length; i++)
            {
                // invokespecial GameVersion.<init>(IILjava/lang/String;)V, then putstatic Core.gameVersion.
                if (code[i] != 0xB7 || code[i + 3] != 0xB3
                    || !pool.IsMember(U2(code, i + 1), GameVersionClass, "<init>", "(IILjava/lang/String;)V")
                    || !pool.IsMember(U2(code, i + 4), CoreClass, "gameVersion", "Lzombie/core/GameVersion;"))
                    continue;
                if (Arguments(code, i, pool) is { } version)
                    return version;
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
        {
        }
        return null;
    }

    /// <summary>Walks back from the invokespecial at <paramref name="call"/>: the pushes of the three arguments, dup and new.</summary>
    private static GameVersion? Arguments(byte[] code, int call, ConstantPool pool)
    {
        foreach (var (suffix, suffixStart) in StringPushesEndingAt(code, call, pool))
        foreach (var (minor, minorStart) in IntPushesEndingAt(code, suffixStart, pool))
        foreach (var (major, majorStart) in IntPushesEndingAt(code, minorStart, pool))
        {
            // dup, preceded by new GameVersion.
            if (majorStart >= 4 && code[majorStart - 1] == 0x59 && code[majorStart - 4] == 0xBB
                && pool.IsClass(U2(code, majorStart - 3), GameVersionClass))
                return new GameVersion(major, minor, suffix);
        }
        return null;
    }

    private static IEnumerable<(string Value, int Start)> StringPushesEndingAt(byte[] code, int end, ConstantPool pool)
    {
        if (end >= 2 && code[end - 2] == 0x12 && pool.String(code[end - 1]) is { } s)
            yield return (s, end - 2);
        if (end >= 3 && code[end - 3] == 0x13 && pool.String(U2(code, end - 2)) is { } w)
            yield return (w, end - 3);
    }

    private static IEnumerable<(int Value, int Start)> IntPushesEndingAt(byte[] code, int end, ConstantPool pool)
    {
        if (end >= 1 && code[end - 1] is >= 0x02 and <= 0x08)
            yield return (code[end - 1] - 0x03, end - 1); // iconst_m1 .. iconst_5
        if (end >= 2 && code[end - 2] == 0x10)
            yield return ((sbyte)code[end - 1], end - 2); // bipush
        if (end >= 3 && code[end - 3] == 0x11)
            yield return ((short)U2(code, end - 2), end - 3); // sipush
        if (end >= 2 && code[end - 2] == 0x12 && pool.Integer(code[end - 1]) is { } i)
            yield return (i, end - 2); // ldc
        if (end >= 3 && code[end - 3] == 0x13 && pool.Integer(U2(code, end - 2)) is { } w)
            yield return (w, end - 3); // ldc_w
    }

    /// <summary>The bytecode of &lt;clinit&gt;, after the constant pool.</summary>
    private static byte[]? FindStaticInitializer(byte[] bytes, int position, ConstantPool pool)
    {
        var p = position + 6; // access flags, this class, super class
        p += 2 + 2 * U2(bytes, p); // interfaces
        p = SkipMembers(bytes, p); // fields
        var methods = U2(bytes, p);
        p += 2;
        for (var m = 0; m < methods; m++)
        {
            var name = pool.Utf8(U2(bytes, p + 2));
            var attributes = U2(bytes, p + 6);
            p += 8;
            for (var a = 0; a < attributes; a++)
            {
                var attributeName = pool.Utf8(U2(bytes, p));
                var length = (int)U4(bytes, p + 2);
                if (name == "<clinit>" && attributeName == "Code")
                {
                    var codeLength = (int)U4(bytes, p + 10);
                    return bytes.AsSpan(p + 14, codeLength).ToArray();
                }
                p += 6 + length;
            }
        }
        return null;
    }

    private static int SkipMembers(byte[] bytes, int p)
    {
        var count = U2(bytes, p);
        p += 2;
        for (var i = 0; i < count; i++)
        {
            var attributes = U2(bytes, p + 6);
            p += 8;
            for (var a = 0; a < attributes; a++)
                p += 6 + (int)U4(bytes, p + 2);
        }
        return p;
    }

    private static int U2(byte[] b, int p) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p, 2));
    private static uint U4(byte[] b, int p) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p, 4));

    /// <summary>Only what is needed here: UTF-8 texts, integers, strings, classes and member references.</summary>
    private sealed class ConstantPool
    {
        private readonly byte[] _tags;
        private readonly string?[] _utf8;
        private readonly int[] _a;
        private readonly int[] _b;

        private ConstantPool(int count)
        {
            _tags = new byte[count];
            _utf8 = new string?[count];
            _a = new int[count];
            _b = new int[count];
        }

        public static ConstantPool Read(byte[] bytes, out int end)
        {
            if (bytes.Length < 10 || U4(bytes, 0) != 0xCAFEBABE)
                throw new InvalidDataException("not a class file");
            var count = U2(bytes, 8);
            var pool = new ConstantPool(count);
            var p = 10;
            for (var i = 1; i < count; i++)
            {
                var tag = bytes[p];
                pool._tags[i] = tag;
                switch (tag)
                {
                    case 1: // Utf8 (modified UTF-8: the same as UTF-8 for these names)
                        var length = U2(bytes, p + 1);
                        pool._utf8[i] = Encoding.UTF8.GetString(bytes, p + 3, length);
                        p += 3 + length;
                        break;
                    case 3: // Integer
                        pool._a[i] = (int)U4(bytes, p + 1);
                        p += 5;
                        break;
                    case 4: // Float
                        p += 5;
                        break;
                    case 5 or 6: // Long, Double: two slots
                        p += 9;
                        i++;
                        break;
                    case 7 or 8 or 16 or 19 or 20: // Class, String, MethodType, Module, Package
                        pool._a[i] = U2(bytes, p + 1);
                        p += 3;
                        break;
                    case 9 or 10 or 11 or 12 or 17 or 18: // member refs, NameAndType, Dynamic, InvokeDynamic
                        pool._a[i] = U2(bytes, p + 1);
                        pool._b[i] = U2(bytes, p + 3);
                        p += 5;
                        break;
                    case 15: // MethodHandle
                        p += 4;
                        break;
                    default:
                        throw new InvalidDataException($"unknown constant tag {tag}");
                }
            }
            end = p;
            return pool;
        }

        public string? Utf8(int index) => Valid(index) && _tags[index] == 1 ? _utf8[index] : null;
        public string? String(int index) => Valid(index) && _tags[index] == 8 ? Utf8(_a[index]) : null;
        public int? Integer(int index) => Valid(index) && _tags[index] == 3 ? _a[index] : null;
        public bool IsClass(int index, string name) => Valid(index) && _tags[index] == 7 && Utf8(_a[index]) == name;

        /// <summary>A Fieldref or Methodref to <paramref name="owner"/>.<paramref name="name"/> with that descriptor.</summary>
        public bool IsMember(int index, string owner, string name, string descriptor)
        {
            if (!Valid(index) || _tags[index] is not (9 or 10) || !IsClass(_a[index], owner))
                return false;
            var nameAndType = _b[index];
            return Valid(nameAndType) && _tags[nameAndType] == 12
                   && Utf8(_a[nameAndType]) == name && Utf8(_b[nameAndType]) == descriptor;
        }

        private bool Valid(int index) => index > 0 && index < _tags.Length;
    }
}
