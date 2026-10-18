using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using PZModInspector.Core.Analysis;
using PZModInspector.Core.Environment;
using PZModInspector.Core.Model;
using PZModInspector.Core.Workshop;

namespace PZModInspector.Tests;

public class GameJarVersionTests
{
    [Fact]
    public void Reads_the_version_assigned_to_Core_gameVersion()
    {
        Assert.Equal(new GameVersion(42, 21, ""), GameJarVersion.FromCoreClass(CoreClass(bipushMinor: 21, suffix: "")));
    }

    [Fact]
    public void Reads_small_minors_and_a_suffix()
    {
        Assert.Equal(new GameVersion(42, 3, "rc"), GameJarVersion.FromCoreClass(CoreClass(bipushMinor: null, suffix: "rc")));
    }

    [Fact]
    public void Reads_it_from_the_jar()
    {
        using var tree = new TestTree();
        var jar = Path.Combine(tree.Root, "projectzomboid.jar");
        using (var zip = ZipFile.Open(jar, ZipArchiveMode.Create))
        {
            using var stream = zip.CreateEntry("zombie/core/Core.class").Open();
            stream.Write(CoreClass(bipushMinor: 21, suffix: ""));
        }

        Assert.Equal(new GameVersion(42, 21, ""), GameJarVersion.Read(jar));
        Assert.Null(GameJarVersion.Read(Path.Combine(tree.Root, "missing.jar")));
    }

    [Theory]
    [InlineData(GameProblem.GameNotFound)]
    [InlineData(GameProblem.VersionUnreadable)]
    [InlineData(GameProblem.VersionTooOld)]
    public void Without_a_usable_game_nothing_is_loaded(GameProblem problem)
    {
        using var tree = new TestTree();
        var env = new PzEnvironment { ZomboidDirectory = tree.Zomboid, GameVersion = new GameVersion(41, 78, ""), GameProblem = problem };
        var workshop = new WorkshopService(new SteamWorkshopClient(), Path.Combine(tree.Root, "cache.json"));

        var thrown = Assert.Throws<GameCheckException>(() => Inspector.Load(env, workshop));
        Assert.Equal(problem, thrown.Environment.GameProblem);
    }

    [Fact]
    public void Anything_else_gives_no_version()
    {
        Assert.Null(GameJarVersion.FromCoreClass([0xCA, 0xFE, 0xBA, 0xBE, 0, 0]));
        Assert.Null(GameJarVersion.FromCoreClass(Encoding.ASCII.GetBytes("not a class file at all")));
    }

    /// <summary>
    /// A minimal Core.class whose static initializer first builds another GameVersion (42.0, stored in another field, like
    /// getBreakModGameVersion) and then gameVersion: only the second one counts. Without <paramref name="bipushMinor"/> the
    /// minor is pushed with iconst_3.
    /// </summary>
    private static byte[] CoreClass(int? bipushMinor, string suffix)
    {
        var pool = new List<byte[]>();
        int Add(params byte[][] parts)
        {
            pool.Add(parts.SelectMany(p => p).ToArray());
            return pool.Count;
        }
        int Utf8(string s) => Add([1], U2(Encoding.UTF8.GetByteCount(s)), Encoding.UTF8.GetBytes(s));
        int Ref(byte tag, int a, int b) => Add([tag], U2(a), U2(b));

        var core = Add([7], U2(Utf8("zombie/core/Core")));
        var obj = Add([7], U2(Utf8("java/lang/Object")));
        var gameVersion = Add([7], U2(Utf8("zombie/core/GameVersion")));
        var init = Ref(10, gameVersion, Ref(12, Utf8("<init>"), Utf8("(IILjava/lang/String;)V")));
        var type = Utf8("Lzombie/core/GameVersion;");
        var versionField = Ref(9, core, Ref(12, Utf8("gameVersion"), type));
        var otherField = Ref(9, core, Ref(12, Utf8("breakModVersion"), type));
        var empty = Add([8], U2(Utf8("")));
        var suffixString = Add([8], U2(Utf8(suffix)));
        var clinit = Utf8("<clinit>");
        var voidDescriptor = Utf8("()V");
        var codeName = Utf8("Code");

        var code = new List<byte>();
        void Build(byte[] minorPush, int suffixIndex, int field)
        {
            code.Add(0xBB); code.AddRange(U2(gameVersion)); // new
            code.Add(0x59); // dup
            code.Add(0x10); code.Add(42); // bipush 42
            code.AddRange(minorPush);
            code.Add(0x12); code.Add((byte)suffixIndex); // ldc
            code.Add(0xB7); code.AddRange(U2(init)); // invokespecial
            code.Add(0xB3); code.AddRange(U2(field)); // putstatic
        }
        Build([0x03], empty, otherField); // iconst_0
        Build(bipushMinor is { } m ? [0x10, (byte)m] : [0x06], suffixString, versionField);
        code.Add(0xB1); // return

        var bytes = new List<byte>();
        bytes.AddRange([0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 65]);
        bytes.AddRange(U2(pool.Count + 1));
        foreach (var entry in pool)
            bytes.AddRange(entry);
        bytes.AddRange(U2(0x21)); bytes.AddRange(U2(core)); bytes.AddRange(U2(obj));
        bytes.AddRange(U2(0)); // interfaces
        bytes.AddRange(U2(0)); // fields
        bytes.AddRange(U2(1)); // methods
        bytes.AddRange(U2(0x08)); bytes.AddRange(U2(clinit)); bytes.AddRange(U2(voidDescriptor)); bytes.AddRange(U2(1));
        bytes.AddRange(U2(codeName)); bytes.AddRange(U4(12 + code.Count));
        bytes.AddRange(U2(4)); bytes.AddRange(U2(0)); bytes.AddRange(U4(code.Count));
        bytes.AddRange(code);
        bytes.AddRange(U2(0)); bytes.AddRange(U2(0)); // exception table, attributes
        bytes.AddRange(U2(0)); // class attributes
        return bytes.ToArray();
    }

    private static byte[] U2(int value)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)value);
        return b;
    }

    private static byte[] U4(int value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)value);
        return b;
    }
}
