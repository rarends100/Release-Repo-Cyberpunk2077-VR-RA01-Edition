using System.Runtime.InteropServices;
using System.Text;
using RA01.Core;
using Xunit;

namespace RA01.Tests;

// RA01: M47 - ExeScan (reading the game exe's header, searching its bytes), the Rehooker's FOUND / MOVED / OFF logic, and AddressList (where a new game build
// keeps each function). No game is running, so each test builds a small FAKE exe header in memory - the same shape as Cyberpunk2077.exe 2.31 (its stamp
// and its three sections) - and points ExeScan at it.
// Builder picture: a mock-up wall with the same stud spacing as the real house, so the stud finder can be tested on the bench.
// Cost - CPU: low (one ~4.2 MB test file is written and read once; everything else is a few compares); GPU: none.
public sealed unsafe class ExeScanAndAddressListTests : IDisposable
{
    const uint Stamp231 = 0x68AF45EA, OtherStamp = 0x12345678;
    readonly nint fake = Marshal.AllocHGlobal(0x400);
    readonly List<string> made = new();

    // RA01: a fake exe header: 'MZ', the PE header at 0x80 with the given stamp, and the 2.31 sections .text 0x1000 (code), .rdata 0x2A8D000, .data 0x32E6000.
    // RA01: Cost - CPU: extremely low (about 50 stores into 1 KB); GPU: none.
    nint FakeExe(uint stamp)
    {
        byte* p = (byte*)fake;
        new Span<byte>(p, 0x400).Clear();
        p[0] = (byte)'M'; p[1] = (byte)'Z';
        *(int*)(p + 0x3C) = 0x80;
        byte* nt = p + 0x80;
        *(uint*)nt = 0x00004550;
        *(ushort*)(nt + 6) = 3;                 // RA01: three sections
        *(uint*)(nt + 8) = stamp;
        *(ushort*)(nt + 20) = 0xF0;             // RA01: size of the optional header (PE32+)
        *(ushort*)(nt + 24) = 0x20B;            // RA01: PE32+ magic
        *(uint*)(nt + 24 + 56) = 0x3A00000;     // RA01: size of image
        *(uint*)(nt + 24 + 64) = 0xC0FFEE;      // RA01: checksum
        (string name, uint rva, uint size, uint flags)[] sections = { (".text", 0x1000, 0x2A8B000, 0x60000020), (".rdata", 0x2A8D000, 0x858000, 0x40000040), (".data", 0x32E6000, 0x500000, 0xC0000040) };
        byte* s = nt + 24 + 0xF0;
        for (int i = 0; i < sections.Length; i++, s += 40)
        {
            Encoding.ASCII.GetBytes(sections[i].name).CopyTo(new Span<byte>(s, 8));
            *(uint*)(s + 8) = sections[i].size;
            *(uint*)(s + 12) = sections[i].rva;
            *(uint*)(s + 36) = sections[i].flags;
        }
        Assert.True(ExeScan.Load(fake), "ExeScan.Load refused the fake exe header");
        return fake;
    }

    // RA01: puts every shared piece of state back as a fresh start has it, so no other test sees this one's fake exe. Cost - CPU: extremely low; GPU: none.
    public void Dispose()
    {
        ResetShared();
        foreach (string f in made) try { File.Delete(f); } catch { }
        Marshal.FreeHGlobal(fake);
    }
    internal static void ResetShared()
    {
        ExeScan.Base = 0; ExeScan.TimeStamp = 0; ExeScan.CheckSum = 0; ExeScan.ImageSize = 0;
        Kit.Set(typeof(ExeScan), "sectionCount", 0);
        foreach (string name in new[] { "state", "at", "how", "rvaCache" }) Array.Clear(Kit.Get<Array>(typeof(HookRegistry), name));
        Kit.Set(typeof(HookRegistry), "reportWritten", false);
        Kit.Set(typeof(AddressList), "rvas", null);
        Kit.Set(typeof(AddressList), "source", "");
    }

    // RA01: the header is read: stamp, image size, checksum, and the sections numbered as CDPR's address list numbers them (1 = .text).
    // Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Reads_the_exe_header_and_sections()
    {
        nint b = FakeExe(Stamp231);
        Assert.Equal(b, ExeScan.Base);
        Assert.Equal(Stamp231, ExeScan.TimeStamp);
        Assert.Equal(0x3A00000u, ExeScan.ImageSize);
        Assert.Equal(0xC0FFEEu, ExeScan.CheckSum);
        Assert.Equal(new uint[] { 0, 0x1000, 0x2A8D000, 0x32E6000, 0 }, new[] { 0, 1, 2, 3, 4 }.Select(ExeScan.SectionRva).ToArray());
        Assert.Equal(new[] { ".text" }, ExeScan.CodeSections().Select(s => s.Name).ToArray());
        Assert.Equal(new[] { ".rdata", ".data" }, ExeScan.CodeSections(code: false).Select(s => s.Name).ToArray());
        Assert.Equal(b + 0x1000, ExeScan.CodeSections()[0].Start);
    }

    // RA01: anything that is not a 64-bit exe header is refused. Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Refuses_what_is_not_an_exe()
    {
        Assert.False(ExeScan.Load(0));
        FakeExe(Stamp231);
        ((byte*)fake)[0] = (byte)'X';
        Assert.False(ExeScan.Load(fake), "no MZ");
        FakeExe(Stamp231);
        *(ushort*)((byte*)fake + 0x80 + 24) = 0x10B;
        Assert.False(ExeScan.Load(fake), "a 32-bit header");
    }

    // RA01: the byte search finds every place, overlapping ones too, and the very last bytes of a block. Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Byte_search_finds_every_place()
    {
        byte[] block = { 0xAA, 0xAA, 0xAA, 0xAA, 0x00, 0x48, 0x8B, 0xC4, 0x00, 0x48, 0x8B, 0xC4 };
        fixed (byte* p = block)
        {
            List<nint> found = new();
            ExeScan.FindInRange((nint)p, block.Length, new byte[] { 0xAA, 0xAA, 0xAA }, found);
            Assert.Equal(new nint[] { (nint)p, (nint)p + 1 }, found.ToArray());
            found.Clear();
            ExeScan.FindInRange((nint)p, block.Length, new byte[] { 0x48, 0x8B, 0xC4 }, found);
            Assert.Equal(new nint[] { (nint)p + 5, (nint)p + 9 }, found.ToArray());
            found.Clear();
            ExeScan.FindInRange((nint)p, block.Length, new byte[] { 0x48, 0x8B, 0xC5 }, found);
            Assert.Empty(found);
        }
    }

    // RA01: on 2.31 each row's function and place come from its 2.31 numbers (no address list needed). Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void On_231_places_come_from_the_table()
    {
        FakeExe(Stamp231);
        Assert.True(HookRegistry.SameBuild);
        Assert.Equal(0x2397F8, HookRegistry.FunctionRva(HookRegistry.MultiHitReturn));
        Assert.Equal(0x2B37B28, HookRegistry.Rva(HookRegistry.StackVtable));
        Assert.Equal(0x342AC00, HookRegistry.Rva(HookRegistry.Engine));
        Assert.Contains("2.31", HookRegistry.BuildLine);
        Assert.Equal("not needed (same game build as RA01 was made on)", AddressList.Status);
    }

    // RA01: FOUND where 2.31 had it, MOVED anywhere else, OFF with its reason; a field row reports a "+0x" place; only player rows count as features off.
    // Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Found_moved_and_off_are_reported()
    {
        nint b = FakeExe(Stamp231);
        Assert.Equal("not checked yet", HookRegistry.Line(HookRegistry.CameraLocate));
        HookRegistry.Found(HookRegistry.CameraLocate, b + 0x127F97, "by its pattern");
        Assert.Equal("FOUND at RVA 0x127F97", HookRegistry.Line(HookRegistry.CameraLocate));
        HookRegistry.Found(HookRegistry.Pose, b + 0x4B7000, "by its pattern");
        Assert.Equal("MOVED RVA 0x4B6968 -> 0x4B7000 (by its pattern)", HookRegistry.Line(HookRegistry.Pose));
        HookRegistry.Found(HookRegistry.VehicleModel, b + 0x1C8, "by name");
        Assert.Equal("FOUND at +0x1C8", HookRegistry.Line(HookRegistry.VehicleModel));
        HookRegistry.Off(HookRegistry.CameraFinal, "test");           // RA01: a Dev row - not a player feature
        Assert.Empty(HookRegistry.FeaturesOff());
        HookRegistry.Off(HookRegistry.Pose, "test");
        Assert.Equal("OFF - test", HookRegistry.Line(HookRegistry.Pose));
        Assert.Equal(new[] { "Body tracking (VRIK)" }, HookRegistry.FeaturesOff().ToArray());
    }

    // RA01: a row with no 2.31 place and no part number (an import) cannot be re-found by number: Rva says 0 and the row is OFF.
    // Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void A_row_with_no_place_is_off()
    {
        FakeExe(Stamp231);
        Assert.Equal(0, HookRegistry.Rva(HookRegistry.XInput));
        Assert.StartsWith("OFF", HookRegistry.Line(HookRegistry.XInput));
    }

    // RA01: a new build reads Settings\RA01_Addresses_<stamp>.ini: "part number = hex RVA" lines; junk lines are skipped. Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void New_build_reads_the_small_ini()
    {
        FakeExe(OtherStamp);
        Assert.False(HookRegistry.SameBuild);
        string ini = Path.Combine(ModFolders.Settings, $"RA01_Addresses_{OtherStamp:X8}.ini");
        made.Add(ini);
        File.WriteAllText(ini, "; comment\n1692210232=12A0F7\nnot a line\n4039912614 = 23A000\n=5\n");
        Assert.True(AddressList.TryRva(1692210232u, out uint rva));
        Assert.Equal(0x12A0F7u, rva);
        Assert.True(AddressList.TryRva(4039912614u, out rva));
        Assert.Equal(0x23A000u, rva);
        Assert.False(AddressList.TryRva(1343032985u, out _));
        Assert.Contains("2 part numbers", AddressList.Status);
        Assert.Equal(0x23A000 + 0x13D, HookRegistry.Rva(HookRegistry.MultiHitReturn));   // RA01: function start from the list + the row's distance into it
    }

    // RA01: a new build with no ini but a cyberpunk2077_addresses.json for that exact build: the json is streamed in 4 MB pieces, only RA01's part numbers are
    // kept (section start + place in section), entries cut in two by a piece border are still read, and the small ini is written for next time.
    // Cost - CPU: low (writes and reads one ~4.2 MB file); GPU: none.
    [Fact]
    public void New_build_reads_the_big_json_across_piece_borders()
    {
        FakeExe(OtherStamp);
        uint[] wanted = HookRegistry.Hashes().OrderBy(h => h).ToArray();
        string json = Path.Combine(ModFolders.Settings, "cyberpunk2077_addresses.json");
        made.Add(json); made.Add(Path.Combine(ModFolders.Settings, $"RA01_Addresses_{OtherStamp:X8}.ini"));
        StringBuilder text = new($"{{\n  \"Linker map timestamp\": \"{OtherStamp:x8}\",\n  \"Addresses\": [\n");
        static string Entry(uint hash, uint inText) => $"    {{\"hash\": \"{hash}\", \"secondary hash\": \"0\", \"offset\": \"0001:{inText:X8}\", \"symbol\": \"\"}},\n";
        int border = (4 << 20) - 1500;                                       // RA01: just before the first piece border (1 KB head + 4 MB piece - 1 KB overlap)
        while (text.Length < border) text.Append(Entry(999u, 0x10));          // RA01: filler entries RA01 does not use
        for (int i = 0; i < wanted.Length; i++) text.Append(Entry(wanted[i], 0x100000u + (uint)i * 0x10));
        while (text.Length < (4 << 20) + 200_000) text.Append(Entry(999u, 0x10));
        text.Append("  ]\n}\n");
        File.WriteAllText(json, text.ToString());

        for (int i = 0; i < wanted.Length; i++)
        {
            Assert.True(AddressList.TryRva(wanted[i], out uint rva), $"part number {wanted[i]} (entry {i} around the piece border) was not read");
            Assert.Equal(0x1000u + 0x100000u + (uint)i * 0x10, rva);
        }
        Assert.False(AddressList.TryRva(999u, out _), "a part number RA01 does not use must not be kept");
        Assert.True(File.Exists(Path.Combine(ModFolders.Settings, $"RA01_Addresses_{OtherStamp:X8}.ini")), "the small ini is written from the json for the next start");
    }

    // RA01: a json made for another build is never used. Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void A_json_for_another_build_is_refused()
    {
        FakeExe(OtherStamp);
        string json = Path.Combine(ModFolders.Settings, "cyberpunk2077_addresses.json");
        made.Add(json);
        File.WriteAllText(json, "{\n  \"Linker map timestamp\": \"68af45ea\",\n  \"Addresses\": [\n    {\"hash\": \"1692210232\", \"offset\": \"0001:00126F97\"}\n  ]\n}\n");
        Assert.False(AddressList.TryRva(1692210232u, out _));
        Assert.StartsWith("NOT FOUND", AddressList.Status);
    }

    // RA01: the number reader inside the json reader: decimal and hex, stops at the first non-digit. Cost - CPU: extremely low; GPU: none.
    delegate bool ParseFn(ReadOnlySpan<byte> text, ref int at, int radix, out uint value);
    [Fact]
    public void Number_reader_reads_decimal_and_hex()
    {
        ParseFn parse = Kit.Method<ParseFn>(typeof(AddressList), "ParseUint");
        int at = 0;
        Assert.True(parse("4039912614\"x"u8, ref at, 10, out uint v));
        Assert.Equal(4039912614u, v);
        Assert.Equal(10, at);
        at = 0;
        Assert.True(parse("002397f8:"u8, ref at, 16, out v));
        Assert.Equal(0x2397F8u, v);
        at = 0;
        Assert.False(parse("x12"u8, ref at, 10, out _));
    }
}
