using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using static RA01.Core.Win32;

namespace RA01.Core;

// RA01: M4 - knows the game's EXE: which build it is, and where a run of code bytes sits inside it.
// it only reads memory, it never changes anything.
// Cost - CPU: medium (only FindAll is heavy: it searches the code sections of the game's roughly 60 MB exe, once or a few times at start-up; everything else is extremely low and also start-up only);
// GPU: none (it only reads CPU memory).
internal static unsafe class ExeScan
{
    // RA01: positions inside the Windows EXE format (the PE header). A script checks every one of these against the Windows headers.
    const int DosNtOffset = 0x3C;            // RA01: offsetof(IMAGE_DOS_HEADER, e_lfanew)
    const int NtSectionCount = 6;            // RA01: offsetof(IMAGE_NT_HEADERS64, FileHeader.NumberOfSections)
    const int NtTimeStamp = 8;               // RA01: offsetof(IMAGE_NT_HEADERS64, FileHeader.TimeDateStamp)
    const int NtOptionalSize = 20;           // RA01: offsetof(IMAGE_NT_HEADERS64, FileHeader.SizeOfOptionalHeader)
    const int NtOptionalHeader = 24;         // RA01: offsetof(IMAGE_NT_HEADERS64, OptionalHeader)
    const int NtImageSize = 80;              // RA01: offsetof(IMAGE_NT_HEADERS64, OptionalHeader.SizeOfImage)
    const int NtCheckSum = 88;               // RA01: offsetof(IMAGE_NT_HEADERS64, OptionalHeader.CheckSum)
    const int SectionEntrySize = 40;         // RA01: sizeof(IMAGE_SECTION_HEADER)
    const int SectionSize = 8;               // RA01: offsetof(IMAGE_SECTION_HEADER, Misc.VirtualSize)
    const int SectionAddress = 12;           // RA01: offsetof(IMAGE_SECTION_HEADER, VirtualAddress)
    const int SectionFlags = 36;             // RA01: offsetof(IMAGE_SECTION_HEADER, Characteristics)
    const uint SectionIsCode = 0x20000000;   // RA01: IMAGE_SCN_MEM_EXECUTE
    const uint NtSignature = 0x00004550;     // RA01: IMAGE_NT_SIGNATURE
    const ushort Magic64 = 0x20B;            // RA01: IMAGE_NT_OPTIONAL_HDR64_MAGIC

    // RA01: what VirtualQuery says about a block of memory: only committed, readable blocks may be searched.
    const uint Committed = 0x1000;           // RA01: MEM_COMMIT
    const uint Readable = 0xEE;              // RA01: PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY
    const uint Blocked = 0x101;              // RA01: PAGE_NOACCESS | PAGE_GUARD

    public static nint Base;                 // RA01: where the EXE sits in memory
    public static uint TimeStamp, CheckSum, ImageSize;
    static int sectionCount;
    static byte* sections;                   // RA01: the list of section headers inside the EXE's header

    // RA01: reads the header of the module at 'module'. False if it does not look like a 64-bit EXE.
    // Cost - CPU: extremely low (a handful of header reads, runs once at start-up); GPU: none (only reads memory).
    public static bool Load(nint module)
    {
        if (module == 0) return false;
        byte* image = (byte*)module;
        if (image[0] != 'M' || image[1] != 'Z') return false;

        byte* nt = image + *(int*)(image + DosNtOffset);
        if (*(uint*)nt != NtSignature) return false;
        byte* optional = nt + NtOptionalHeader;
        if (*(ushort*)optional != Magic64) return false;

        Base = module;
        TimeStamp = *(uint*)(nt + NtTimeStamp);
        ImageSize = *(uint*)(nt + NtImageSize);
        CheckSum = *(uint*)(nt + NtCheckSum);
        sectionCount = *(ushort*)(nt + NtSectionCount);
        sections = optional + *(ushort*)(nt + NtOptionalSize);
        return true;
    }

    // RA01: writes which build of the game this is: the file, its size and version, and the numbers that identify the build.
    // Cost - CPU: low (runs once at start-up: a file size and file version lookup, which read the exe from disk, and a few log lines; a few milliseconds in total); GPU: none (file and memory reads and logging only).
    public static void LogIdentity()
    {
        string path = Environment.ProcessPath ?? "unknown";
        long bytes = 0;
        string version = "unavailable";
        try { bytes = new FileInfo(path).Length; } catch { }
        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            version = $"file version {info.FileVersion}, product version {info.ProductVersion}";
        }
        catch { }

        DateTime stamp = DateTimeOffset.FromUnixTimeSeconds(TimeStamp).UtcDateTime;
        Log.Write($"RA01 game exe: {path}, {bytes} bytes, {version}");
        Log.Write($"RA01 game exe header: timestamp {stamp:yyyy-MM-dd HH:mm:ss} UTC (0x{TimeStamp:X8}), image size 0x{ImageSize:X}, checksum 0x{CheckSum:X8}, base 0x{Base:X}");

        foreach ((string name, nint start, long size) in CodeSections())
            Log.Write($"RA01 game exe code section {name}: RVA 0x{start - Base:X}, size 0x{size:X}");
    }

    // RA01: the name, address and size of every section of the EXE that holds code.
    // RA01: Cost - CPU: extremely low (a loop over the exe's section headers, about 10 to 20, and one small list; only called at start-up); GPU: none (only reads memory).
    // RA01: M21-1 - 'code: false' gives every section that does NOT hold code instead (the game's data: strings, tables, its settings' objects).
    internal static List<(string Name, nint Start, long Size)> CodeSections(bool code = true)
    {
        List<(string, nint, long)> list = new();
        for (int i = 0; i < sectionCount; i++)
        {
            byte* header = sections + i * SectionEntrySize;
            if (((*(uint*)(header + SectionFlags) & SectionIsCode) != 0) != code) continue;
            string name = Encoding.ASCII.GetString(header, 8).TrimEnd('\0');
            list.Add((name, Base + (nint)(*(uint*)(header + SectionAddress)), *(uint*)(header + SectionSize)));
        }
        return list;
    }

    // RA01: every place in the EXE's code where 'needle' occurs. Only readable parts of the code sections are searched.
    // Cost - CPU: medium (once per search at start-up: walks the code sections of the roughly 60 MB exe with VirtualQuery and searches them with a fast vectorised byte search, tens of milliseconds per needle);
    // GPU: none (only reads CPU memory).
    // M21-1 - 'code: false' searches the data sections instead (same cost).
    public static List<nint> FindAll(ReadOnlySpan<byte> needle, bool code = true)
    {
        List<nint> found = new();
        foreach ((nint runStart, long runLength) in ReadableRuns(code)) FindInRange(runStart, runLength, needle, found);
        return found;
    }

    // RA01: M43 - the readable stretches of the code (or data) sections, neighbours joined so a match on a border is not missed. FindAll and CallersOf search these.
    // RA01: (M43 moved this walk out of FindAll unchanged, so both share it.)
    // RA01: Cost - CPU: low (one VirtualQuery per memory block of the sections, a few hundred, once per search at start-up); GPU: none (only reads CPU memory).
    static List<(nint Start, long Length)> ReadableRuns(bool code)
    {
        List<(nint, long)> runs = new();
        foreach ((_, nint sectionStart, long sectionSize) in CodeSections(code))
        {
            long start = sectionStart;
            long end = start + sectionSize;
            long runStart = 0, runEnd = 0; // RA01: the current stretch of readable memory
            while (start < end)
            {
                MemoryBasicInformation info;
                if (VirtualQuery((nint)start, &info, (nuint)sizeof(MemoryBasicInformation)) == 0) break;

                long regionEnd = Math.Min((long)info.BaseAddress + (long)info.RegionSize, end);
                if (regionEnd <= start) break;
                if (IsReadable(info))
                {
                    if (runEnd == start) runEnd = regionEnd;
                    else { if (runEnd > runStart) runs.Add(((nint)runStart, runEnd - runStart)); runStart = start; runEnd = regionEnd; }
                }
                else
                {
                    if (runEnd > runStart) runs.Add(((nint)runStart, runEnd - runStart));
                    runStart = runEnd = 0;
                }
                start = regionEnd;
            }
            if (runEnd > runStart) runs.Add(((nint)runStart, runEnd - runStart));
        }
        return runs;
    }

    // RA01: M43 - every place in the code that calls 'target' directly (a 5-byte "call" whose distance lands on it); each entry is where that call RETURNS to (the
    // byte after it) - the same kind of address AimProbe compares. Used only on a game build other than 2.31, to re-find the aim line's caller.
    // Builder picture: walk every corridor and note each door that opens straight onto one room.
    // Cost - CPU: medium (once, only on a new game build: walks the whole code section, about 40 MB, stopping at every E8 byte - roughly 50-100 ms); GPU: none.
    public static List<nint> CallersOf(nint target)
    {
        List<nint> found = new();
        foreach ((nint runStart, long runLength) in ReadableRuns(true))
        {
            ReadOnlySpan<byte> range = new((void*)runStart, (int)Math.Min(runLength, int.MaxValue));
            int at = 0;
            while (at <= range.Length - 5)
            {
                int hit = range.Slice(at, range.Length - 4 - at).IndexOf((byte)0xE8);
                if (hit < 0) break;
                at += hit;
                nint back = runStart + at + 5;
                if (back + *(int*)(runStart + at + 1) == target) found.Add(back);
                at++;
            }
        }
        return found;
    }

    // RA01: M43 - where section number 'number' (1 = the first, as CDPR's address list counts them: "0001:..." is .text) starts, as an RVA; 0 if there is no such section.
    // Cost - CPU: extremely low (one header read); GPU: none.
    public static uint SectionRva(int number) =>
        number >= 1 && number <= sectionCount ? *(uint*)(sections + (number - 1) * SectionEntrySize + SectionAddress) : 0;

    // RA01: true if the block of memory is committed, readable and not marked no-access or guard.
    // Cost - CPU: extremely low (three bit tests, called once per memory block during a search); GPU: none (a check on a CPU-side structure).
    static bool IsReadable(MemoryBasicInformation info) =>
        info.State == Committed && (info.Protect & Readable) != 0 && (info.Protect & Blocked) == 0;

    // RA01: searches one block of memory that is known to be readable.
    // Cost - CPU: medium (once per readable block during a search at start-up: a vectorised byte search over the whole block, memory-bound, so the time grows with the size of the code, tens of milliseconds
    // for the whole exe per needle); GPU: none (only reads CPU memory).
    internal static void FindInRange(nint start, long length, ReadOnlySpan<byte> needle, List<nint> found)
    {
        if (length > int.MaxValue) length = int.MaxValue; // RA01: code sections are far smaller than 2 GB
        ReadOnlySpan<byte> range = new((void*)start, (int)length);
        int at = 0;
        while (at <= range.Length - needle.Length)
        {
            int hit = range.Slice(at).IndexOf(needle);
            if (hit < 0) break;
            found.Add(start + at + hit);
            at += hit + 1;
        }
    }

    // RA01: M6-0 - where the EXE's own import table sits, and the shapes of its two small structures (a script checks every one of these against the Windows headers, the same as the offsets above).
    const int NtDataDirectory = NtOptionalHeader + 112; // RA01: offsetof(IMAGE_OPTIONAL_HEADER64, DataDirectory) - sixteen 8-byte entries, an RVA then a size
    const int ImportDirectoryIndex = 1;                 // RA01: IMAGE_DIRECTORY_ENTRY_IMPORT
    const int ImportDescriptorSize = 20;                // RA01: sizeof(IMAGE_IMPORT_DESCRIPTOR): OriginalFirstThunk, TimeDateStamp, ForwarderChain, Name, FirstThunk, 4 bytes each
    const ulong OrdinalFlag = 0x8000000000000000UL;      // RA01: IMAGE_ORDINAL_FLAG64 - the thunk names a function by number, not by name

    // RA01: M6-0 - the address inside the EXE's own import address table (IAT) that holds the resolved pointer for one function the EXE imports by name from one DLL, or 0 if it does not.
    // this is the slot a hook patches, the same "overwrite one pointer in a table" trick PresentHook.Patch already does to a COM vtable slot - here the table is the PE import directory instead.
    // Cost - CPU: low (walks the EXE's list of imported DLLs, and for the matching one its list of imported names - typically a handful to a few dozen - a handful of string compares; runs only
    // at start-up, when a hook is being installed); GPU: none (memory reads only).
    public static nint FindImport(string dllName, string functionName)
    {
        byte* image = (byte*)Base;
        byte* nt = image + *(int*)(image + DosNtOffset);
        uint importRva = *(uint*)(nt + NtDataDirectory + ImportDirectoryIndex * 8);
        if (importRva == 0) return 0;

        byte* descriptor = image + importRva;
        while (*(uint*)descriptor != 0) // RA01: OriginalFirstThunk - an all-zero descriptor ends the list
        {
            string dll = Marshal.PtrToStringAnsi((nint)(image + *(uint*)(descriptor + 12))) ?? "";
            if (string.Equals(dll, dllName, StringComparison.OrdinalIgnoreCase))
            {
                uint originalFirstThunkRva = *(uint*)descriptor;
                if (originalFirstThunkRva == 0) return 0; // RA01: no name-preserving thunk was kept for this DLL; by now the loader has long since overwritten FirstThunk with real pointers, not RVA-encoded names, so there is nothing left here to search by name
                ulong* names = (ulong*)(image + originalFirstThunkRva);
                nint* slots = (nint*)(image + *(uint*)(descriptor + 16)); // RA01: FirstThunk - the IAT itself, same order as the names
                for (int i = 0; names[i] != 0; i++)
                {
                    if ((names[i] & OrdinalFlag) != 0) continue; // RA01: imported by ordinal number, not by name - not what we are looking for
                    string function = Marshal.PtrToStringAnsi((nint)(image + (uint)names[i] + 2)) ?? ""; // RA01: IMAGE_IMPORT_BY_NAME: a 2-byte hint, then the name
                    if (string.Equals(function, functionName, StringComparison.Ordinal)) return (nint)(&slots[i]);
                }
                return 0; // RA01: this DLL is imported, but not this function by name
            }
            descriptor += ImportDescriptorSize;
        }
        return 0;
    }

    // RA01: 'count' bytes starting at 'address' as text like "F3 0F 11", for the log. It only reads memory that is really readable.
    // Cost - CPU: extremely low (one VirtualQuery and a short loop over a few bytes, called only when a log line about found code is written, a few times at start-up); GPU: none (only reads memory).
    public static string Bytes(nint address, int count)
    {
        MemoryBasicInformation info;
        if (address < Base || address + count > Base + (nint)ImageSize
            || VirtualQuery(address, &info, (nuint)sizeof(MemoryBasicInformation)) == 0
            || !IsReadable(info) || (long)address + count > (long)info.BaseAddress + (long)info.RegionSize)
            return "(not readable)";

        StringBuilder text = new();
        for (int i = 0; i < count; i++)
            text.Append(i == 0 ? "" : " ").Append(((byte*)address)[i].ToString("X2"));
        return text.ToString();
    }
}
