using System.Globalization;
using System.Text;

namespace RA01.Core;

// RA01: M43 - THE STOCK INDEX for a new game build. CDPR's address list gives every game function a part number (a hash) that stays the same from build to
// build, plus where that build keeps it. HookRegistry asks here ONLY when the running game is not 2.31 (the build RA01 was made on).
// Where the part numbers come from, in order:
//   1. Settings\RA01_Addresses_<build>.ini - the main way. Made offline by claude_session_tools\RA01_HookCheck.py from a new exe and address list, and
//      shipped with the mod. Small: only RA01's own ~35 part numbers.
//   2. A cyberpunk2077_addresses.json (the RED4ext SDK / RED4ext address list), as a fallback: looked for in the Settings folder, the game's bin\x64 and its
//      red4ext folder - used only if its "Linker map timestamp" is this exact build. RA01 then writes file 1 itself, so the big file is read once per update.
// Nothing here is trusted on its own: HookRegistry checks the game's bytes at every address before anything uses it.
// Builder picture: the hand-written slip in the toolbox first; only if there is none, a walk to the warehouse office to copy from the big ledger.
// Cost - CPU: extremely low on 2.31 (never runs) and after the first start of a new build (one small ini read); medium once per update if only the
// big list is there (it streams the ~130 MB file in 4 MB pieces, about a second); GPU: none.
internal static class AddressList
{
    const int ChunkBytes = 4 << 20, Overlap = 1024;   // RA01: the json is read in pieces; the overlap keeps an entry cut in two by a piece border whole
    static readonly object gate = new();
    static Dictionary<uint, uint>? rvas;               // RA01: part number -> RVA (of the function start), for this build
    static string source = "";

    // RA01: where this build keeps part 'hash' (the function's start, as an RVA). False if no list for this build was found, or it lacks that part.
    // Cost - CPU: extremely low after the first call (a dictionary look-up); the first call loads the list (see the class comment); GPU: none.
    public static bool TryRva(uint hash, out uint rva)
    {
        lock (gate) { rvas ??= Load(); }
        return rvas.TryGetValue(hash, out rva);
    }

    // RA01: one line for the report and the Dev_Rehook tab: where the list came from (or where it was looked for). Cost - CPU: extremely low; GPU: none.
    public static string Status
    {
        get { lock (gate) { return rvas == null ? "not needed (same game build as RA01 was made on)" : source; } }
    }

    // RA01: the small ini file name for this build. Cost - CPU: extremely low; GPU: none.
    public static string IniName => $"RA01_Addresses_{ExeScan.TimeStamp:X8}.ini";

    // RA01: reads the small ini, or else the big json (and writes the small ini from it). Never throws: an empty list just means "not known".
    // Cost - CPU: see the class comment; GPU: none.
    static Dictionary<uint, uint> Load()
    {
        Dictionary<uint, uint> map = new();
        try
        {
            string ini = Path.Combine(ModFolders.Settings, IniName);
            if (File.Exists(ini))
            {
                foreach (string line in File.ReadAllLines(ini))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0 && uint.TryParse(line[..eq].Trim(), out uint hash) && uint.TryParse(line[(eq + 1)..].Trim(), NumberStyles.HexNumber, null, out uint rva)) map[hash] = rva;
                }
                source = $"{map.Count} part numbers from Settings\\{IniName}";
                return map;
            }

            string game = Path.GetDirectoryName(Environment.ProcessPath) ?? "";          // RA01: ...\bin\x64
            string root = Path.GetFullPath(Path.Combine(game, "..", ".."));
            string[] places = { ModFolders.Settings, game, Path.Combine(root, "red4ext"), Path.Combine(root, "red4ext", "bin", "x64") };
            foreach (string place in places)
            {
                string json = Path.Combine(place, "cyberpunk2077_addresses.json");
                if (!File.Exists(json)) continue;
                if (!ReadJson(json, map, out string why)) { Log.Write($"RA01 rehooker: {json} skipped - {why}"); continue; }
                source = $"{map.Count} part numbers from {json} (saved to Settings\\{IniName})";
                WriteIni(ini, map, json);
                return map;
            }
            source = $"NOT FOUND - no Settings\\{IniName}, and no address list for build {ExeScan.TimeStamp:X8} in Settings, bin\\x64 or red4ext";
        }
        catch (Exception e) { source = "could not be read - " + e.Message; map.Clear(); }
        return map;
    }

    // RA01: streams the big list and keeps only the part numbers RA01 uses. False (and why) if it belongs to another build.
    // Each entry looks like {"hash": "887623293", ..., "offset": "0001:00101428"} - section number, then the place inside that section.
    // Cost - CPU: medium (once per update, about a second for ~130 MB); GPU: none.
    static bool ReadJson(string path, Dictionary<uint, uint> map, out string why)
    {
        HashSet<uint> wanted = HookRegistry.Hashes();
        byte[] hashKey = Encoding.ASCII.GetBytes("\"hash\": \""), offsetKey = Encoding.ASCII.GetBytes("\"offset\": \"");
        byte[] buffer = new byte[ChunkBytes + Overlap];
        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);

        int have = file.Read(buffer, 0, Overlap);
        string head = Encoding.ASCII.GetString(buffer, 0, have);
        int stamp = head.IndexOf("\"Linker map timestamp\": \"", StringComparison.Ordinal);
        if (stamp < 0 || !uint.TryParse(head.AsSpan(stamp + 25, 8), NumberStyles.HexNumber, null, out uint build)) { why = "it has no build stamp"; return false; }
        if (build != ExeScan.TimeStamp) { why = $"it is for build {build:X8}, the game is {ExeScan.TimeStamp:X8}"; return false; }

        while (true)
        {
            int read = file.Read(buffer, have, ChunkBytes);
            int end = have + read;
            ReadOnlySpan<byte> text = buffer.AsSpan(0, end);
            int at = 0, safeEnd = read == 0 ? end : end - Overlap;   // RA01: entries starting past safeEnd are read again with the next piece
            while (at < safeEnd)
            {
                int hit = text[at..safeEnd].IndexOf(hashKey);
                if (hit < 0) break;
                at += hit + hashKey.Length;
                if (!ParseUint(text, ref at, 10, out uint hash) || !wanted.Contains(hash)) continue;
                int off = text[at..Math.Min(end, at + 600)].IndexOf(offsetKey);
                if (off < 0) continue;
                int p = at + off + offsetKey.Length;
                if (!ParseUint(text, ref p, 10, out uint section) || p >= end || text[p] != (byte)':') continue;
                p++;
                uint sectionRva = ExeScan.SectionRva((int)section);
                if (ParseUint(text, ref p, 16, out uint inSection) && sectionRva != 0) map[hash] = sectionRva + inSection;
            }
            if (read == 0) break;
            Buffer.BlockCopy(buffer, end - Overlap, buffer, 0, Overlap);   // RA01: keep the tail for the next piece
            have = Overlap;
        }
        why = "";
        return true;
    }

    // RA01: a whole number written in 'radix' (10 or 16) starting at 'at'; 'at' ends just after it. Cost - CPU: extremely low; GPU: none.
    static bool ParseUint(ReadOnlySpan<byte> text, ref int at, int radix, out uint value)
    {
        value = 0; int start = at;
        while (at < text.Length)
        {
            int c = text[at], d = c >= '0' && c <= '9' ? c - '0' : radix == 16 && (c | 0x20) >= 'a' && (c | 0x20) <= 'f' ? (c | 0x20) - 'a' + 10 : -1;
            if (d < 0) break;
            value = unchecked(value * (uint)radix + (uint)d);
            at++;
        }
        return at > start;
    }

    // RA01: saves RA01's part numbers for this build, so the next start reads only this small file. Cost - CPU: extremely low (a ~35-line file, once); GPU: none.
    static void WriteIni(string path, Dictionary<uint, uint> map, string from)
    {
        try
        {
            StringBuilder text = new();
            text.AppendLine($"; RA01 address list for game build {ExeScan.TimeStamp:X8} - part number = RVA (hex) of the function start. Made by RA01 from {from}.");
            foreach (KeyValuePair<uint, uint> part in map) text.AppendLine($"{part.Key}={part.Value:X}");
            File.WriteAllText(path, text.ToString());
        }
        catch { }
    }
}
