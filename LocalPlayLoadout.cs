// WWII Local Play Unlocker
// Copyright (c) 2026 Petsox. Source-available, see LICENSE: contributions welcome, no redistribution of modified versions.
// Injects setPrivateLoadout / setRankedLoadout commands into the game's command buffer
// (same mechanism as the community WW2_Loadout_Editor) and can dump the full
// mp/statstable.csv from game memory so every weapon is selectable.
// Written for C# 5 / .NET Framework 4 (csc.exe that ships with Windows).
// Build: run build.bat (uses the C# compiler that ships with .NET Framework 4, no SDK needed).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace LocalPlayLoadout
{
    static class Native
    {
        public const uint PROCESS_VM_READ = 0x10, PROCESS_VM_WRITE = 0x20, PROCESS_VM_OPERATION = 0x8, PROCESS_QUERY_INFORMATION = 0x400;
        public const uint MEM_COMMIT = 0x1000, MEM_PRIVATE = 0x20000;
        public const uint PAGE_NOACCESS = 0x01, PAGE_READWRITE = 0x04, PAGE_GUARD = 0x100;

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORY_BASIC_INFORMATION
        {
            public ulong BaseAddress;
            public ulong AllocationBase;
            public uint AllocationProtect;
            public uint Pad1;
            public ulong RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
            public uint Pad2;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out MEMORY_BASIC_INFORMATION mbi, IntPtr len);
        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool VirtualProtectEx(IntPtr h, IntPtr addr, IntPtr size, uint newProtect, out uint oldProtect);
        [DllImport("kernel32.dll")]
        public static extern bool FlushInstructionCache(IntPtr h, IntPtr addr, IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, IntPtr size, uint type, uint protect);
    }

    class Game : IDisposable
    {
        public const string ProcessName = "s2_mp64_ship";
        public Process Proc;
        IntPtr handle;
        public long ModuleBase;
        public long ModuleSize;
        public long CbufArray;

        public static Game Attach(out string error)
        {
            error = null;
            Process[] procs = Process.GetProcessesByName(ProcessName);
            if (procs.Length == 0) { error = "Game is not running (" + ProcessName + ".exe not found)."; return null; }
            Game g = new Game();
            g.Proc = procs[0];
            g.handle = Native.OpenProcess(Native.PROCESS_VM_READ | Native.PROCESS_VM_WRITE | Native.PROCESS_VM_OPERATION | Native.PROCESS_QUERY_INFORMATION, false, g.Proc.Id);
            if (g.handle == IntPtr.Zero) { error = "OpenProcess failed (error " + Marshal.GetLastWin32Error() + "). Try running this tool as Administrator."; return null; }
            try
            {
                g.ModuleBase = g.Proc.MainModule.BaseAddress.ToInt64();
                g.ModuleSize = g.Proc.MainModule.ModuleMemorySize;
            }
            catch (Exception ex) { error = "Could not read game module info: " + ex.Message; g.Dispose(); return null; }
            return g;
        }

        public bool HasExited { get { try { return Proc.HasExited; } catch { return true; } } }

        public byte[] Read(long addr, int size)
        {
            byte[] buf = new byte[size];
            IntPtr read;
            if (!Native.ReadProcessMemory(handle, new IntPtr(addr), buf, new IntPtr(size), out read) || read.ToInt64() != size) return null;
            return buf;
        }

        public bool TryReadInt64(long addr, out long v)
        {
            byte[] b = Read(addr, 8);
            v = b == null ? 0 : BitConverter.ToInt64(b, 0);
            return b != null;
        }

        public bool TryReadInt32(long addr, out int v)
        {
            byte[] b = Read(addr, 4);
            v = b == null ? 0 : BitConverter.ToInt32(b, 0);
            return b != null;
        }

        public string ReadCString(long addr, int max)
        {
            if (addr < 0x10000) return null;
            // read page-safe: try full size then shrink
            for (int n = max; n >= 16; n /= 4)
            {
                byte[] b = Read(addr, n);
                if (b == null) continue;
                int z = Array.IndexOf(b, (byte)0);
                if (z < 0) return null;
                return Encoding.ASCII.GetString(b, 0, z);
            }
            return null;
        }

        // Writes into the game's code (normally read-only/executable memory).
        public bool PatchCode(long addr, byte[] data)
        {
            uint old, dummy;
            if (!Native.VirtualProtectEx(handle, new IntPtr(addr), new IntPtr(data.Length), 0x40, out old)) return false;
            bool ok = Write(addr, data);
            Native.VirtualProtectEx(handle, new IntPtr(addr), new IntPtr(data.Length), old, out dummy);
            Native.FlushInstructionCache(handle, new IntPtr(addr), new IntPtr(data.Length));
            return ok;
        }

        public bool Write(long addr, byte[] data)
        {
            IntPtr written;
            return Native.WriteProcessMemory(handle, new IntPtr(addr), data, new IntPtr(data.Length), out written) && written.ToInt64() == data.Length;
        }

        // Copies a NUL-terminated UTF-8 string into new memory in the game; returns its address (0 on failure).
        public long AllocString(string text)
        {
            byte[] b = Encoding.UTF8.GetBytes(text + "\0");
            IntPtr mem = Native.VirtualAllocEx(handle, IntPtr.Zero, new IntPtr(b.Length), 0x3000, 0x04);   // MEM_COMMIT|MEM_RESERVE, PAGE_READWRITE
            if (mem == IntPtr.Zero || !Write(mem.ToInt64(), b)) return 0;
            return mem.ToInt64();
        }

        public delegate bool ChunkVisitor(byte[] buf, int len, long baseAddr);

        // Walks readable committed memory in [start,end) in chunks, overlapping chunks by `overlap` bytes.
        public void ScanMemory(long start, long end, int overlap, ChunkVisitor visit, bool privateReadWriteOnly = false)
        {
            const int chunk = 4 * 1024 * 1024;
            byte[] buf = new byte[chunk + overlap];
            long addr = start;
            while (addr < end)
            {
                Native.MEMORY_BASIC_INFORMATION mbi;
                if (Native.VirtualQueryEx(handle, new IntPtr(addr), out mbi, new IntPtr(Marshal.SizeOf(typeof(Native.MEMORY_BASIC_INFORMATION)))) == IntPtr.Zero) break;
                long rBase = (long)mbi.BaseAddress, rEnd = rBase + (long)mbi.RegionSize;
                if (rEnd <= addr) break;
                bool readable = mbi.State == Native.MEM_COMMIT && (mbi.Protect & Native.PAGE_GUARD) == 0 && (mbi.Protect & 0xFF) != Native.PAGE_NOACCESS && mbi.Protect != 0;
                if (privateReadWriteOnly) readable &= mbi.Type == Native.MEM_PRIVATE && mbi.Protect == Native.PAGE_READWRITE;
                if (readable)
                {
                    long s = Math.Max(addr, rBase), e = Math.Min(end, rEnd);
                    for (long p = s; p < e; p += chunk)
                    {
                        int len = (int)Math.Min(chunk + overlap, e - p);
                        IntPtr read;
                        if (!Native.ReadProcessMemory(handle, new IntPtr(p), buf, new IntPtr(len), out read))
                        {
                            for (long q = p; q < p + len; q += 0x1000)
                            {
                                int pl = (int)Math.Min(0x1000 + overlap, e - q);
                                if (pl > 0 && Native.ReadProcessMemory(handle, new IntPtr(q), buf, new IntPtr(pl), out read))
                                    if (!visit(buf, (int)read.ToInt64(), q)) return;
                            }
                            continue;
                        }
                        if (!visit(buf, (int)read.ToInt64(), p)) return;
                    }
                }
                addr = rEnd;
            }
        }

        public List<long> FindPattern(long start, long end, byte?[] pattern, int maxHits)
        {
            List<long> hits = new List<long>();
            ScanMemory(start, end, pattern.Length, delegate(byte[] buf, int len, long baseAddr)
            {
                byte first = pattern[0].Value;
                for (int i = 0; i + pattern.Length <= len; i++)
                {
                    if (buf[i] != first) continue;
                    bool ok = true;
                    for (int j = 1; j < pattern.Length; j++)
                        if (pattern[j].HasValue && buf[i + j] != pattern[j].Value) { ok = false; break; }
                    if (ok)
                    {
                        long a = baseAddr + i;
                        if (hits.Count == 0 || hits[hits.Count - 1] != a) hits.Add(a);
                        if (hits.Count >= maxHits) return false;
                    }
                }
                return true;
            });
            return hits;
        }

        public List<long> FindQwords(long start, long end, HashSet<long> values, int maxHits)
        {
            List<long> hits = new List<long>();
            long min = values.Min(), max = values.Max();
            ScanMemory(start, end, 0, delegate(byte[] buf, int len, long baseAddr)
            {
                int skew = (int)((8 - (baseAddr & 7)) & 7);
                for (int i = skew; i + 8 <= len; i += 8)
                {
                    long v = BitConverter.ToInt64(buf, i);
                    if (v < min || v > max) continue;
                    if (values.Contains(v)) { hits.Add(baseAddr + i); if (hits.Count >= maxHits) return false; }
                }
                return true;
            });
            return hits;
        }

        // Same signature the community WW2_Loadout_Editor uses to locate cmd_textArray.
        public bool FindCbuf(out string error)
        {
            error = null;
            byte?[] sig = { 0x40, 0x56, 0x41, 0x56, 0x41, 0x57, 0xB8, null, null, null, null };
            List<long> hits = FindPattern(ModuleBase, ModuleBase + ModuleSize, sig, 1);
            if (hits.Count == 0) { error = "Command buffer signature not found in game module."; return false; }
            long ins = hits[0] + 40;
            int disp;
            if (!TryReadInt32(ins + 3, out disp)) { error = "Could not read command buffer reference."; return false; }
            long cbuf = ins + disp + 7;
            long data; int max, cur;
            bool readOk = TryReadInt64(cbuf, out data);
            readOk &= TryReadInt32(cbuf + 8, out max);
            readOk &= TryReadInt32(cbuf + 12, out cur);
            if (!readOk || data < 0x10000 || max < 64 || max > (1 << 24) || cur < 0 || cur > max)
            {
                error = string.Format("Command buffer at 0x{0:X} looks invalid (data=0x{1:X} max={2} cur={3}). Wait until the main menu is loaded and retry.", cbuf, data, max, cur);
                return false;
            }
            CbufArray = cbuf;
            return true;
        }

        public bool SendCommand(string text, out string error)
        {
            error = null;
            if (!text.EndsWith(";")) text += ";";
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            for (int attempt = 0; attempt < 150; attempt++)
            {
                long data; int max, cur;
                if (!TryReadInt64(CbufArray, out data) || !TryReadInt32(CbufArray + 8, out max) || !TryReadInt32(CbufArray + 12, out cur))
                { error = "Lost access to the command buffer."; return false; }
                if (cur + bytes.Length < max)
                {
                    if (!Write(data + cur, bytes) || !Write(CbufArray + 12, BitConverter.GetBytes(cur + bytes.Length)))
                    { error = "WriteProcessMemory failed (error " + Marshal.GetLastWin32Error() + ")."; return false; }
                    return true;
                }
                Thread.Sleep(20); // buffer full, let the game consume it
            }
            error = "Command buffer stayed full; is the game frozen/minimised?";
            return false;
        }

        // Locates a StringTable asset by name in memory and returns its rows.
        // Finds a StringTable asset header (name ptr, int columns, int rows, cells ptr) in the game module's data.
        public bool FindStringTable(string tableName, int expectCols, out int rows, out long cells)
        {
            int foundRows = 0; long foundCells = 0;
            ScanMemory(ModuleBase, ModuleBase + ModuleSize, 24, delegate(byte[] buf, int len, long baseAddr)
            {
                int skew = (int)((8 - (baseAddr & 7)) & 7);
                for (int i = skew; i + 24 <= len; i += 8)
                {
                    if (BitConverter.ToInt32(buf, i + 8) != expectCols) continue;
                    int r = BitConverter.ToInt32(buf, i + 12);
                    if (r < 2 || r > 20000) continue;
                    long namePtr = BitConverter.ToInt64(buf, i);
                    if (namePtr < 0x10000 || namePtr > 0x7FFFFFFFFFFF) continue;
                    if (ReadCString(namePtr, 64) != tableName) continue;
                    foundRows = r;
                    foundCells = BitConverter.ToInt64(buf, i + 16);
                    return false;
                }
                return true;
            });
            rows = foundRows; cells = foundCells;
            return rows > 0 && cells > 0x10000;
        }

        public List<string[]> DumpStringTable(string tableName, Action<string> log)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(tableName + "\0");
            byte?[] pat = nameBytes.Select(b => (byte?)b).ToArray();
            long userMax = 0x7FFFFFFEFFFF;
            log("Searching memory for \"" + tableName + "\"...");
            List<long> nameHits = FindPattern(0x10000, userMax, pat, 64);
            log("  name found at " + nameHits.Count + " location(s).");
            if (nameHits.Count == 0) return null;
            HashSet<long> targets = new HashSet<long>(nameHits);

            // Fast path: asset header is usually close to its name in zone memory.
            List<long> refs = new List<long>();
            foreach (long h in nameHits)
                refs.AddRange(FindQwords(Math.Max(0x10000, h - 0x40000), h + 0x40000, targets, 64));
            List<string[]> rows = TryCandidates(refs, log);
            if (rows != null) return rows;

            log("  near search failed, scanning all memory for references (can take a while)...");
            refs = FindQwords(0x10000, userMax, targets, 512);
            log("  " + refs.Count + " reference(s) found.");
            return TryCandidates(refs, log);
        }

        List<string[]> TryCandidates(List<long> refs, Action<string> log)
        {
            foreach (long c in refs.Distinct())
            {
                List<string[]> rows = TryParseStringTable(c);
                if (rows != null) { log(string.Format("  string table header at 0x{0:X}: {1} rows x {2} cols", c, rows.Count, rows[0].Length)); return rows; }
            }
            return null;
        }

        List<string[]> TryParseStringTable(long hdr)
        {
            byte[] h = Read(hdr, 0x48);
            if (h == null) return null;
            for (int o = 8; o + 8 <= 0x30; o += 4)
            {
                int cols = BitConverter.ToInt32(h, o), rows = BitConverter.ToInt32(h, o + 4);
                if (cols < 20 || cols > 256 || rows < 20 || rows > 50000) continue;
                List<long> ptrs = new List<long>();
                for (int p = 8; p + 8 <= h.Length; p += 8)
                {
                    if (p < o + 8 && p + 8 > o) continue;
                    long v = BitConverter.ToInt64(h, p);
                    if (v > 0x10000 && v < 0x7FFFFFFFFFFF) ptrs.Add(v);
                }
                foreach (long cells in ptrs)
                {
                    foreach (int stride in new[] { 8, 16, 24 })
                    {
                        List<string[]> t = ReadDirect(cells, stride, rows, cols);
                        if (t != null) return t;
                    }
                    foreach (long idx in ptrs)
                    {
                        if (idx == cells) continue;
                        foreach (int idxSize in new[] { 2, 4 })
                            foreach (int stride in new[] { 8, 16 })
                            {
                                List<string[]> t = ReadIndexed(cells, idx, idxSize, stride, rows, cols);
                                if (t != null) return t;
                            }
                    }
                }
            }
            return null;
        }

        static bool LooksLikeStatsTable(List<string[]> t)
        {
            int weapons = t.Count(r => r[0] != null && r[0].StartsWith("weapon_") && r.Length > 18 && r[18] != null && r[18].StartsWith("0x"));
            return weapons >= 10;
        }

        List<string[]> ReadDirect(long cells, int stride, int rows, int cols)
        {
            long total = (long)rows * cols * stride;
            if (total > 64L * 1024 * 1024) return null;
            byte[] arr = Read(cells, (int)total);
            if (arr == null) return null;
            // quick sanity on first column of a few rows
            for (int r = 0; r < Math.Min(rows, 8); r++)
                if (ReadCString(BitConverter.ToInt64(arr, r * cols * stride), 128) == null) return null;
            Dictionary<long, string> cache = new Dictionary<long, string>();
            List<string[]> t = new List<string[]>();
            for (int r = 0; r < rows; r++)
            {
                string[] row = new string[cols];
                for (int c = 0; c < cols; c++)
                    row[c] = CachedString(cache, BitConverter.ToInt64(arr, (r * cols + c) * stride));
                t.Add(row);
            }
            return LooksLikeStatsTable(t) ? t : null;
        }

        List<string[]> ReadIndexed(long values, long indices, int idxSize, int stride, int rows, int cols)
        {
            long n = (long)rows * cols;
            if (n * idxSize > 64L * 1024 * 1024) return null;
            byte[] idx = Read(indices, (int)(n * idxSize));
            if (idx == null) return null;
            int maxIdx = 0;
            for (long i = 0; i < n; i++)
                maxIdx = Math.Max(maxIdx, idxSize == 2 ? BitConverter.ToUInt16(idx, (int)(i * 2)) : BitConverter.ToInt32(idx, (int)(i * 4)));
            if (maxIdx < 0 || (long)(maxIdx + 1) * stride > 64L * 1024 * 1024) return null;
            byte[] vals = Read(values, (maxIdx + 1) * stride);
            if (vals == null) return null;
            Dictionary<long, string> cache = new Dictionary<long, string>();
            List<string[]> t = new List<string[]>();
            for (int r = 0; r < rows; r++)
            {
                string[] row = new string[cols];
                for (int c = 0; c < cols; c++)
                {
                    long i = (long)r * cols + c;
                    int k = idxSize == 2 ? BitConverter.ToUInt16(idx, (int)(i * 2)) : BitConverter.ToInt32(idx, (int)(i * 4));
                    row[c] = CachedString(cache, BitConverter.ToInt64(vals, k * stride));
                }
                t.Add(row);
                if (r == 8 && t.All(x => x[0] == null)) return null;
            }
            return LooksLikeStatsTable(t) ? t : null;
        }

        string CachedString(Dictionary<long, string> cache, long p)
        {
            string s;
            if (!cache.TryGetValue(p, out s)) { s = ReadCString(p, 512); cache[p] = s; }
            return s;
        }

        // Local Play classes live in the "private loadouts" stats buffer (stats group 2, table index 4) of controller 0:
        //   exe+0x1C68624 + mode*0x15D70 + 0xD9F8, 0x9B0 bytes; "loaded" flag at exe+0x1C7E388 + mode*0x15D70.
        // The buffer starts with a 22-byte DDL header; the data after it is kept XOR-encrypted in memory: for each byte at
        // address a: k = key ^ low32(a), c = (k + 3) * k, byte ^= (c >> 8) ^ c. Only the key's low 16 bits matter, so the
        // key is recovered by trying all 65536 and keeping the one that turns the class weapon/perk/equipment fields into
        // known item IDs. A plaintext (currently decrypted) buffer is also accepted.
        public const int PrivateLoadoutsHeader = 22, PrivateLoadoutsSize = 0x9B0;

        // The lobby (party 0, exe+0x8DD8DB0) keeps its selected map as a pointer to an interned string at +0x268
        // (see the game's SetPartyMapName / GetPartyMapName). Interned strings have an 8-byte header in front:
        // u16 reference count, u8 1, u8 length incl. NUL, u32 hash. Hidden maps are switched in by pointing the
        // lobby at that map's interned string and moving one reference from the old string to the new one.
        const long PartyMapSlot = 0x8DD8DB0 + 0x268;

        public string SetLobbyMap(string mapName)
        {
            long slot = ModuleBase + PartyMapSlot, cur;
            if (!TryReadInt64(slot, out cur) || cur < 0x10000) return "no lobby map found - open a Local Play lobby first";
            string curName = ReadCString(cur, 64);
            byte[] curHdr = Read(cur - 8, 8);
            if (curName == null || curHdr == null || curHdr[2] != 1 || curHdr[3] != curName.Length + 1) return "unexpected lobby map data";
            if (curName == mapName) return null;
            long target = FindInternedString(mapName);
            if (target == 0) return "'" + mapName + "' is not in the game's string pool";
            byte[] tHdr = Read(target - 8, 2);
            if (tHdr == null) return "could not read the map string";
            ushort rcTarget = BitConverter.ToUInt16(tHdr, 0), rcCur = BitConverter.ToUInt16(curHdr, 0);
            if (!PatchCode(target - 8, BitConverter.GetBytes((ushort)(rcTarget + 1)))) return "write failed";
            if (!PatchCode(slot, BitConverter.GetBytes(target))) return "write failed";
            if (rcCur > 1) PatchCode(cur - 8, BitConverter.GetBytes((ushort)(rcCur - 1)));
            return null;
        }

        long FindInternedString(string text)
        {
            byte[] pat = Encoding.ASCII.GetBytes(text + "\0");
            long found = 0;
            ScanMemory(ModuleBase, ModuleBase + ModuleSize, pat.Length + 8, delegate(byte[] buf, int len, long baseAddr)
            {
                for (int i = 8; i + pat.Length <= len; i++)
                {
                    if (buf[i] != pat[0] || buf[i - 6] != 1 || buf[i - 5] != pat.Length) continue;
                    int j = 1;
                    while (j < pat.Length && buf[i + j] == pat[j]) j++;
                    if (j == pat.Length) { found = baseAddr + i; return false; }
                }
                return true;
            });
            return found;
        }

        // The Mode menu (ui/s2/mp_game_setup_menu_uc.lua) lists the groups { Cac.GameModes.Group.Standard,
        // Cac.GameModes.Group.Esports }. Cac.GameModes.Data also has a "Party" group (gun, prop, spy, dogfight,
        // dogfight_ffa) that the menu never shows. Each menu-building function keeps its constants as 16-byte Lua values
        // {TString*, tag}, with the field names in the order "Group", "Standard", "Esports", "ipairs". Pointing that
        // "Esports" constant at the game's own "Party" string makes the menu list Party Modes in place of Esports Modes;
        // picking a mode then goes through the game's own SetPartyGameType, so the lobby and other players stay in sync.
        // Havok Script strings: TString* p has the u64 length at p+8, a u32 hash at p+0x10 and the text at p+0x14.
        // The menu scripts are reloaded after every match, so the patch is re-applied by MainForm.PartyModesTick.
        const int LuaStringText = 0x14;
        static readonly string[] PartyMenuNames = { "Group", "Standard", "Esports", "Party", "ipairs" };

        public class PartyMenuPatch
        {
            public long Group, Standard, Esports, Party, Ipairs;
            public List<long> Slots = new List<long>();
        }

        // Finds the interned Lua string objects with the given texts. Lua objects are 8-byte aligned and live in the
        // script heap (private read/write memory), so only the aligned length field is tested. Stops once every name is found.
        Dictionary<string, List<long>> FindLuaStrings(string[] names)
        {
            Dictionary<string, List<long>> found = names.ToDictionary(n => n, n => new List<long>());
            byte[][] pats = names.Select(n => Encoding.ASCII.GetBytes(n + "\0")).ToArray();
            int maxLen = names.Max(n => n.Length);
            ScanMemory(0x10000, 0x7FFFFFFEFFFF, 64, delegate(byte[] buf, int len, long baseAddr)
            {
                int skew = (int)((8 - ((baseAddr + 8) & 7)) & 7);   // obj+8 (the length) is 8-byte aligned
                for (int i = skew; i + 12 + maxLen + 1 <= len; i += 8)
                {
                    long n = BitConverter.ToInt64(buf, i);
                    if (n < 1 || n > maxLen) continue;
                    for (int k = 0; k < pats.Length; k++)
                    {
                        byte[] p = pats[k];
                        if (p.Length - 1 != n) continue;
                        int j = 0;
                        while (j < p.Length && buf[i + 12 + j] == p[j]) j++;
                        if (j < p.Length) continue;
                        long obj = baseAddr + i - 8;
                        if (!found[names[k]].Contains(obj)) found[names[k]].Add(obj);
                    }
                }
                return found.Values.Any(l => l.Count == 0);
            }, true);
            return found;
        }

        // The committed region around `addr` (the script heap is one large allocation).
        bool RegionAround(long addr, out long start, out long end)
        {
            Native.MEMORY_BASIC_INFORMATION mbi;
            start = end = 0;
            if (Native.VirtualQueryEx(handle, new IntPtr(addr), out mbi, new IntPtr(Marshal.SizeOf(typeof(Native.MEMORY_BASIC_INFORMATION)))) == IntPtr.Zero) return false;
            start = (long)mbi.BaseAddress;
            end = start + (long)mbi.RegionSize;
            return true;
        }

        bool IsLuaString(long obj, string text)
        {
            byte[] b = Read(obj + 8, LuaStringText - 8 + text.Length + 1);
            return b != null && BitConverter.ToInt64(b, 0) == text.Length && b[b.Length - 1] == 0
                && Encoding.ASCII.GetString(b, LuaStringText - 8, text.Length) == text;
        }

        static long Nearest(List<long> candidates, long to)
        {
            return candidates.OrderBy(c => Math.Abs(c - to)).First();
        }

        // Locates the Mode menu's "Esports" constants. `previous` (from an earlier search) skips the full-memory string
        // search when its strings are still in place, e.g. after the menu scripts were reloaded.
        public PartyMenuPatch FindPartyMenu(PartyMenuPatch previous, out string error)
        {
            error = null;
            Dictionary<string, List<long>> s;
            if (previous != null && IsLuaString(previous.Group, "Group") && IsLuaString(previous.Standard, "Standard")
                && IsLuaString(previous.Esports, "Esports") && IsLuaString(previous.Party, "Party") && IsLuaString(previous.Ipairs, "ipairs"))
            {
                s = new Dictionary<string, List<long>> {
                    { "Group", new List<long> { previous.Group } }, { "Standard", new List<long> { previous.Standard } },
                    { "Esports", new List<long> { previous.Esports } }, { "Party", new List<long> { previous.Party } },
                    { "ipairs", new List<long> { previous.Ipairs } } };
            }
            else s = FindLuaStrings(PartyMenuNames);
            foreach (string n in PartyMenuNames)
                if (s[n].Count == 0) { error = "menu string \"" + n + "\" not found - wait for the main menu"; return null; }

            HashSet<long> groups = new HashSet<long>(s["Group"]), standards = new HashSet<long>(s["Standard"]), ipairs = new HashSet<long>(s["ipairs"]);
            HashSet<long> values = new HashSet<long>(s["Esports"].Concat(s["Party"]));
            PartyMenuPatch p = new PartyMenuPatch();
            // The function constants sit in the same script heap as the strings; search there first, then around it.
            long heapStart, heapEnd, lo = s["Group"].Min(), hi = s["Group"].Max() + 8;
            if (RegionAround(s["Group"][0], out heapStart, out heapEnd)) { lo = heapStart; hi = heapEnd; }
            ScanForPartyMenuSlots(lo, hi, groups, standards, values, ipairs, p);
            if (p.Slots.Count == 0)
            {
                List<long> all = s.Values.SelectMany(l => l).ToList();
                ScanForPartyMenuSlots(Math.Max(0x10000, all.Min() - 0x40000000), all.Max() + 0x40000000, groups, standards, values, ipairs, p);
            }
            if (p.Slots.Count == 0) { error = "Mode menu not found - open Local Play first"; return null; }
            p.Esports = Nearest(s["Esports"], p.Group);
            p.Party = Nearest(s["Party"], p.Group);
            return p;
        }

        void ScanForPartyMenuSlots(long start, long end, HashSet<long> groups, HashSet<long> standards, HashSet<long> values, HashSet<long> ipairs, PartyMenuPatch p)
        {
            ScanMemory(start, end, 0x40, delegate(byte[] buf, int len, long baseAddr)
            {
                int skew = (int)((8 - (baseAddr & 7)) & 7);
                for (int i = skew + 0x20; i + 0x18 <= len; i += 8)
                {
                    if (!values.Contains(BitConverter.ToInt64(buf, i)) || (buf[i + 8] & 0xF) != 4) continue;
                    long g = BitConverter.ToInt64(buf, i - 0x20), st = BitConverter.ToInt64(buf, i - 0x10), ip = BitConverter.ToInt64(buf, i + 0x10);
                    if (!groups.Contains(g) || !standards.Contains(st) || !ipairs.Contains(ip)) continue;
                    if (!p.Slots.Contains(baseAddr + i)) p.Slots.Add(baseAddr + i);
                    p.Group = g; p.Standard = st; p.Ipairs = ip;
                }
                return true;
            });
        }

        // Shows (or hides again) Party Modes. Returns how many constants were changed, or -1 when a slot no longer holds
        // the menu's constants (the scripts were reloaded somewhere else) and FindPartyMenu has to run again.
        public int ApplyPartyMenu(PartyMenuPatch p, bool show)
        {
            int changed = 0;
            foreach (long slot in p.Slots)
            {
                byte[] b = Read(slot - 0x20, 0x38);
                if (b == null) return -1;
                long cur = BitConverter.ToInt64(b, 0x20);
                if (BitConverter.ToInt64(b, 0) != p.Group || BitConverter.ToInt64(b, 0x10) != p.Standard
                    || BitConverter.ToInt64(b, 0x30) != p.Ipairs || (cur != p.Esports && cur != p.Party)) return -1;
                long want = show ? p.Party : p.Esports;
                if (cur == want) continue;
                if (!Write(slot, BitConverter.GetBytes(want))) return -1;
                changed++;
            }
            return changed;
        }

        // Weapon variants in Local Play: the weapon list's click handler (ui/s2/cac_edit_weapon_uc.lua) equips the base
        // weapon straight away when "... or CONDITIONS.IsPreLaunchDemo() or CONDITIONS.IsE3Build() or
        // CONDITIONS.IsSystemLink() or CONDITIONS.IsRankedPlay() or CONDITIONS.IsMLGMatch()" is true, so the variant menu
        // (cac_edit_weapon_variant) never opens in Local Play. That function's constant "IsSystemLink" (the only constant
        // table with this exact neighbour sequence) is pointed at the game's own "AlwaysFalse" string; clicking a weapon
        // then opens the variant list like online. With the unlock-all fix the variants are unlocked and equip normally.
        static readonly string[] VariantMenuNames = { "IsPreLaunchDemo", "IsE3Build", "IsSystemLink", "IsRankedPlay", "IsMLGMatch", "AlwaysFalse" };

        public class VariantMenuPatch
        {
            public long PreLaunch, E3, SystemLink, Ranked, Mlg, AlwaysFalse;
            public List<long> Slots = new List<long>();
        }

        public VariantMenuPatch FindVariantMenu(VariantMenuPatch previous, out string error)
        {
            error = null;
            Dictionary<string, List<long>> s;
            if (previous != null && IsLuaString(previous.PreLaunch, "IsPreLaunchDemo") && IsLuaString(previous.E3, "IsE3Build")
                && IsLuaString(previous.SystemLink, "IsSystemLink") && IsLuaString(previous.Ranked, "IsRankedPlay")
                && IsLuaString(previous.Mlg, "IsMLGMatch") && IsLuaString(previous.AlwaysFalse, "AlwaysFalse"))
            {
                s = new Dictionary<string, List<long>> {
                    { "IsPreLaunchDemo", new List<long> { previous.PreLaunch } }, { "IsE3Build", new List<long> { previous.E3 } },
                    { "IsSystemLink", new List<long> { previous.SystemLink } }, { "IsRankedPlay", new List<long> { previous.Ranked } },
                    { "IsMLGMatch", new List<long> { previous.Mlg } }, { "AlwaysFalse", new List<long> { previous.AlwaysFalse } } };
            }
            else s = FindLuaStrings(VariantMenuNames);
            foreach (string n in VariantMenuNames)
                if (s[n].Count == 0) { error = "menu string \"" + n + "\" not found - wait for the main menu"; return null; }

            VariantMenuPatch p = new VariantMenuPatch();
            HashSet<long> pre = new HashSet<long>(s["IsPreLaunchDemo"]), e3 = new HashSet<long>(s["IsE3Build"]),
                sys = new HashSet<long>(s["IsSystemLink"]), ranked = new HashSet<long>(s["IsRankedPlay"]),
                mlg = new HashSet<long>(s["IsMLGMatch"]), af = new HashSet<long>(s["AlwaysFalse"]);
            long heapStart, heapEnd, lo = s["IsSystemLink"].Min(), hi = s["IsSystemLink"].Max() + 8;
            if (RegionAround(s["IsSystemLink"][0], out heapStart, out heapEnd)) { lo = heapStart; hi = heapEnd; }
            Action<long, long> scan = (from, to) => ScanMemory(from, to, 0x40, delegate(byte[] buf, int len, long baseAddr)
            {
                int skew = (int)((8 - (baseAddr & 7)) & 7);
                for (int i = skew + 0x20; i + 0x30 <= len; i += 0x8)
                {
                    long cur = BitConverter.ToInt64(buf, i);
                    if (!sys.Contains(cur) && !af.Contains(cur)) continue;
                    if ((buf[i + 8] & 0xF) != 4) continue;
                    long a = BitConverter.ToInt64(buf, i - 0x20), b = BitConverter.ToInt64(buf, i - 0x10),
                        c = BitConverter.ToInt64(buf, i + 0x10), d = BitConverter.ToInt64(buf, i + 0x20);
                    if (!pre.Contains(a) || !e3.Contains(b) || !ranked.Contains(c) || !mlg.Contains(d)) continue;
                    if (!p.Slots.Contains(baseAddr + i)) p.Slots.Add(baseAddr + i);
                    p.PreLaunch = a; p.E3 = b; p.Ranked = c; p.Mlg = d;
                }
                return true;
            });
            scan(lo, hi);
            if (p.Slots.Count == 0)
            {
                List<long> all = s.Values.SelectMany(l => l).ToList();
                scan(Math.Max(0x10000, all.Min() - 0x40000000), all.Max() + 0x40000000);
            }
            if (p.Slots.Count == 0) { error = "weapon menu not found - open Local Play first"; return null; }
            p.SystemLink = Nearest(s["IsSystemLink"], p.Slots[0]);
            p.AlwaysFalse = Nearest(s["AlwaysFalse"], p.Slots[0]);
            return p;
        }

        // Returns how many constants were changed, or -1 when a slot no longer holds the handler's constants (the menu
        // scripts were reloaded somewhere else) and FindVariantMenu has to run again.
        public int ApplyVariantMenu(VariantMenuPatch p)
        {
            int changed = 0;
            foreach (long slot in p.Slots)
            {
                byte[] b = Read(slot - 0x20, 0x50);
                if (b == null) return -1;
                long cur = BitConverter.ToInt64(b, 0x20);
                if (BitConverter.ToInt64(b, 0) != p.PreLaunch || BitConverter.ToInt64(b, 0x10) != p.E3
                    || BitConverter.ToInt64(b, 0x30) != p.Ranked || BitConverter.ToInt64(b, 0x40) != p.Mlg
                    || (cur != p.SystemLink && cur != p.AlwaysFalse)) return -1;
                if (cur == p.AlwaysFalse) continue;
                if (!Write(slot, BitConverter.GetBytes(p.AlwaysFalse))) return -1;
                changed++;
            }
            return changed;
        }

        public byte[] ReadPrivateLoadouts(HashSet<uint> knownIds, int[] checkOffsets, out string error)
        {
            error = null;
            long buf = 0;
            for (int mode = 0; mode < 3 && buf == 0; mode++)
            {
                byte[] flag = Read(ModuleBase + 0x1C7E388 + mode * 0x15D70, 1);
                if (flag != null && flag[0] != 0) buf = ModuleBase + 0x1C68624 + mode * 0x15D70 + 0xD9F8;
            }
            if (buf == 0) { error = "Local Play loadouts are not loaded yet - open Local Play in the game first."; return null; }
            byte[] enc = Read(buf, PrivateLoadoutsSize);
            if (enc == null) { error = "Could not read the loadout buffer."; return null; }

            Func<int, int, uint> decodeDword = (key, off) =>
            {
                uint v = 0;
                for (int b = 0; b < 4; b++)
                {
                    int o = PrivateLoadoutsHeader + off + b;
                    byte x = enc[o];
                    if (key >= 0)
                    {
                        uint k = ((uint)key ^ (uint)(buf + o)) & 0xFFFF;
                        uint c = ((k + 3) * k) & 0xFFFF;
                        x ^= (byte)((c >> 8) ^ c);
                    }
                    v |= (uint)x << (8 * b);
                }
                return v;
            };
            int bestKey = -2, bestScore = -1;
            for (int key = -1; key < 65536; key++)   // -1 = buffer currently decrypted
            {
                int score = 0;
                foreach (int off in checkOffsets)
                    if (knownIds.Contains(decodeDword(key, off))) score++;
                if (score > bestScore) { bestScore = score; bestKey = key; }
            }
            if (bestScore < checkOffsets.Length / 4) { error = "Could not decode the loadout buffer (best match " + bestScore + "/" + checkOffsets.Length + ")."; return null; }

            byte[] data = new byte[PrivateLoadoutsSize - PrivateLoadoutsHeader];
            for (int i = 0; i < data.Length; i++)
            {
                int o = PrivateLoadoutsHeader + i;
                byte x = enc[o];
                if (bestKey >= 0)
                {
                    uint k = ((uint)bestKey ^ (uint)(buf + o)) & 0xFFFF;
                    uint c = ((k + 3) * k) & 0xFFFF;
                    x ^= (byte)((c >> 8) ^ c);
                }
                data[i] = x;
            }
            return data;
        }

        public void Dispose()
        {
            if (handle != IntPtr.Zero) { Native.CloseHandle(handle); handle = IntPtr.Zero; }
        }
    }

    class Item
    {
        public string Type, Internal, Base, Name;
        public int Variant = -1;
        public uint Id;
        public bool IsKeep, IsEmpty, IsDev;
        public override string ToString()
        {
            if (IsKeep) return "(keep current)";
            if (IsEmpty) return "(empty / none)";
            return MainForm.TypeLabel(this) + "  |  " + Name;
        }
    }

    class MainForm : Form
    {
        public const string AppVersion = "1.5", Author = "Petsox";
#if LITE
        // Lite build (build.bat: /define:LITE): only what every player needs - no host-only features (party modes,
        // hidden maps), no dev & hidden items, no raw console command, no "online ranked" target, no "no restrictions".
        public const string AppName = "WWII Local Play Unlocker Lite";
#else
        public const string AppName = "WWII Local Play Unlocker";
#endif
        const int Col0 = 0, ColRef = 1, ColName = 2, ColIcon = 4, ColId = 18;
        static readonly string[] GunTypes = { "weapon_assault", "weapon_smg", "weapon_heavy", "weapon_lmg", "weapon_sniper", "weapon_shotgun", "weapon_pistol", "weapon_projectile" };

        // Patterns from the game's mp/camotable.csv (the only camos valid in multiplayer).
        static readonly string[] MpCamoPatterns = { "greenspot", "brownspot", "heeres", "leibermuster", "peapattern", "oakleaf", "palmtree", "planetree", "panzer", "m1916", "snow" };
        static readonly Dictionary<string, string> GripAliases = new Dictionary<string, string> { {"kar98k","kar98"}, {"toggleaction","winchester1897"} };

        static readonly Dictionary<string, string> PrettyNames = new Dictionary<string, string>
        {
            {"stg44","STG-44"}, {"m1garand","M1 Garand"}, {"m1a1","M1A1 Carbine"}, {"svt40","SVT-40"}, {"bar","BAR"},
            {"fg42","FG-42"}, {"g43","Gewehr 43"}, {"federov","Fedorov Avtomat"}, {"m2carbine","M2 Carbine"}, {"avs36","AVS-36"},
            {"thompson","M1928"}, {"mp40","MP-40"}, {"ppsh41","PPSh-41"}, {"greasegun","Grease Gun"}, {"type100","Type 100"},
            {"mp28","Waffe 28"}, {"sten","Sten"}, {"sterling","Sterling"}, {"beretta","Orso"}, {"erma","Erma"},
            {"bren","Bren"}, {"lewis","Lewis"}, {"mg15","MG 15"}, {"mg42","MG 42"}, {"mg81","MG 81"}, {"m1919","M1919"}, {"breda30","Breda 30"},
            {"kar98","Kar98k"}, {"springfield","M1903"}, {"leeenfield","Lee Enfield"}, {"karabin","Karabin"}, {"arisaka","Arisaka"},
            {"delisle","De Lisle"}, {"mosin","Mosin-Nagant"}, {"ptrs41","PTRS-41"}, {"leveraction","Lever Action"},
            {"m30","M30 Luftwaffe Drilling"}, {"winchester1897","Toggle Action"}, {"model21","Model 21"}, {"walther","Walther"}, {"blunderbuss","Blunderbuss"},
            {"m1911","1911"}, {"luger","P-08"}, {"m712","Machine Pistol"}, {"enfieldno2","Enfield No. 2"}, {"p38","P38"}, {"reich","Reichsrevolver"},
            {"bazooka","M1 Bazooka"}, {"panzerschreck","Panzerschreck"}, {"dp28","DP-28 / Crossbow"},
            {"shovel","Shovel"}, {"combatknife","Combat Knife"}, {"trenchknife","Trench Knife"}, {"baseballbat","Baseball Bat"},
            {"icepick","Ice Pick"}, {"riotshield","Riot Shield"}, {"axe","Axe"}, {"dagger","Dagger"}, {"hammer","Hammer"}, {"sword","Sword"},
        };

        Game game;
        List<Item> items = new List<Item>();
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;

        ComboBox cbTarget, cbClass;
        CheckBox chkAll, chkVariants;
        Label lblStatus, lblItems;
        TextBox txtLog, txtRaw;
        NumericUpDown numLethal, numTactical;
        CheckBox chkLethalCount, chkTacticalCount;
        ComboBox cbPrimary, cbSecondary, cbLethal, cbTactical;
        ComboBox cbPriCamo, cbPriCamo2, cbSecCamo, cbSecCamo2, cbPriCharm, cbSecCharm;
        const int AttachmentSlots = 6;
        ComboBox[] cbAtt = new ComboBox[AttachmentSlots], cbSecAtt = new ComboBox[AttachmentSlots];
        ComboBox cbDevWeapon, cbDevReticle, cbDevMap;
        RadioButton rbDevPrimary, rbDevSecondary;
        Button bUnlockAll;
        Button bPartyModes, bDump;

        static readonly Color ColorGreen = Color.FromArgb(0, 128, 0), ColorRed = Color.FromArgb(192, 0, 0),
            ColorOrange = Color.FromArgb(214, 100, 0), ColorBlue = Color.FromArgb(0, 90, 200);

        // Button text colour; null puts the normal text colour back.
        static void Colorize(Button b, Color? text)
        {
            b.ForeColor = text ?? SystemColors.ControlText;
        }
        ComboBox[] cbPerk = new ComboBox[9];
        List<ComboBox> allSlots = new List<ComboBox>();
        ToolTip toolTip = new ToolTip();

        public MainForm()
        {
            Text = AppName + " " + AppVersion + "  -  made by " + Author;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Font = new Font("Segoe UI", 9f);
#if LITE
            Width = 980; Height = 470;
            MinimumSize = new Size(820, 480);
#else
            Width = 1280; Height = 1020;
            MinimumSize = new Size(1000, 700);
#endif
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = BodyBack;

            // ---- header: title and connection status ----
            TableLayoutPanel header = new TableLayoutPanel { Dock = DockStyle.Top, Height = 62, ColumnCount = 2, BackColor = HeaderBack, Padding = new Padding(16, 8, 16, 8) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            FlowLayoutPanel titles = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
            titles.Controls.Add(new Label { Text = AppName, AutoSize = true, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 14f), Margin = Padding.Empty });
            titles.Controls.Add(new Label { Text = "v" + AppVersion + "  ·  made by " + Author + "  ·  Offline / Local Play only", AutoSize = true, ForeColor = HeaderSubText, Margin = new Padding(2, 0, 0, 0) });
            lblStatus = new Label { AutoSize = true, Anchor = AnchorStyles.Right, Padding = new Padding(12, 6, 12, 6), Font = new Font("Segoe UI Semibold", 9.5f) };
            SetStatus("Not connected", false);
            header.Controls.Add(titles);
            header.Controls.Add(lblStatus);

            // ---- sidebar: game-wide actions ----
            FlowLayoutPanel side = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 252, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = SideBack, Padding = new Padding(12, 6, 12, 10) };
            side.Controls.Add(Caption("GAME"));
            Button bConnect = SideButton("Connect to game");
            bConnect.Click += delegate { Connect(); };
            toolTip.SetToolTip(bConnect, "Connects to the running game. Happens automatically every few seconds as well.");
            bPartyModes = SideButton("Party modes in Local Play");
            bPartyModes.Click += delegate { partyModesWanted = !partyModesWanted; PartyModesChanged(); };
            toolTip.SetToolTip(bPartyModes, "Shows the game's hidden \"Party Modes\" group (Prop Hunt, Gun Game, Spy, Dogfight, Dogfight FFA)\nin Game Setup -> Mode, in place of \"Esports Modes\". Only the host needs this.\nMemory only; re-applied automatically after every match while this tool stays open. Click again to get Esports Modes back.");
            bUnlockAll = SideButton("Unlock everything in game menus");
            bUnlockAll.Click += delegate { UnlockAllClicked(); };
            toolTip.SetToolTip(bUnlockAll, "Unlocks every weapon (incl. all variants: click a weapon to pick one), melee weapon, camo (incl. Challenges camos), charm and reticle in the game's own Local Play menus.\nMemory only: undone by restarting the game; re-applied automatically while this tool stays open.");
#if LITE
            side.Controls.AddRange(new Control[] { bConnect, bUnlockAll });
#else
            side.Controls.AddRange(new Control[] { bConnect, bPartyModes, bUnlockAll });
            side.Controls.Add(Hint("Party modes: Prop Hunt and more under Game Setup → Mode (host only). Green = on."));
#endif
            side.Controls.Add(Caption("ITEM LIST"));
            bDump = SideButton("Load full item list from game");
            bDump.Click += delegate { DumpTable(); };
            toolTip.SetToolTip(bDump, "Reads every weapon, camo and attachment from the game. Needed once; saved next to the tool.");
            side.Controls.Add(bDump);
            lblItems = Hint("");
#if !LITE
            side.Controls.Add(lblItems);   // Lite: the amber item list button already shows whether the list is loaded
#endif

            // ---- tab 1: class editor ----
            TabControl tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(16, 5) };
#if LITE
            TabPage tabClass = new TabPage("Classes") { UseVisualStyleBackColor = false, BackColor = BodyBack };
#else
            TabPage tabClass = new TabPage("Class editor") { UseVisualStyleBackColor = false, BackColor = BodyBack };
#endif
            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(6), AutoScroll = true, BackColor = BodyBack };
            rootPanel = root;
            tabClass.Controls.Add(root);

            cbClass = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
            for (int i = 1; i <= 10; i++) cbClass.Items.Add("Class " + i);
            cbClass.SelectedIndex = 0;
            cbTarget = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 270 };
            cbTarget.Items.AddRange(new object[] { "Local Play / Private match", "Online ranked (original editor behaviour)" });
            cbTarget.SelectedIndex = 0;
            chkVariants = new CheckBox { Text = "Include weapon variants (loot versions, e.g. \"STG-44 - variant 2\")", AutoSize = true, Margin = new Padding(3, 6, 18, 0) };
            chkVariants.CheckedChanged += delegate { FillCombos(); };
            chkAll = new CheckBox { Text = "Show every item in every slot (no restrictions)", AutoSize = true, Margin = new Padding(3, 6, 3, 0) };
            chkAll.CheckedChanged += delegate { FillCombos(); };

            Button bApply = new Button { Text = "Apply to class", AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
            bApply.Click += delegate { Apply(); };
            toolTip.SetToolTip(bApply, "Writes the fields below into the selected class. Fields on (keep current) are not touched.");
            Button bLoad = new Button { Text = "Load class from game", AutoSize = true };
            bLoad.Click += delegate { LoadClassFromGame(LoadScope.All); };
            toolTip.SetToolTip(bLoad, "Reads the selected Local Play class from the game and fills in every field.");
            Button bReset = new Button { Text = "Reset all slots to (keep current)", AutoSize = true };
            bReset.Click += delegate { foreach (ComboBox c in allSlots) if (c.Items.Count > 0) c.SelectedIndex = 0; chkLethalCount.Checked = chkTacticalCount.Checked = false; };
#if LITE
            // Lite: Export reads the selected class straight from the game, Import writes a file straight into it.
            Button bExport = new Button { Text = "Export class to file...", AutoSize = true };
            bExport.Click += delegate { LiteExport(); };
            Button bImport = new Button { Text = "Import class from file...", AutoSize = true };
            bImport.Click += delegate { LiteImport(); };
            toolTip.SetToolTip(bExport, "Reads the selected class from the game and saves it as a .json file you can share.");
            toolTip.SetToolTip(bImport, "Loads a .json class file and writes it straight into the selected class in the game.");
#else
            Button bExport = new Button { Text = "Export class...", AutoSize = true };
            bExport.Click += delegate { ExportClass(); };
            Button bImport = new Button { Text = "Import class...", AutoSize = true };
            bImport.Click += delegate { ImportClass(); };
            toolTip.SetToolTip(bExport, "Saves the fields below to a .json file you can share.");
            toolTip.SetToolTip(bImport, "Fills the fields below from a .json file. Click \"Apply to class\" afterwards to put it in the game.");
#endif
            foreach (Button b in new[] { bApply, bLoad, bReset, bExport, bImport }) StyleButton(b);
            bApply.BackColor = HeaderBack; bApply.ForeColor = Color.White;
            bApply.FlatAppearance.BorderColor = HeaderBack; bApply.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 66, 76);
            Colorize(bLoad, ColorGreen);
            Colorize(bReset, ColorRed);
            Colorize(bExport, ColorOrange);
            Colorize(bImport, ColorBlue);

            TableLayoutPanel classCard = Card(root, "Class", null, 100);
            FlowLayoutPanel pick = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
#if LITE
            cbClass.Width = 280;
            pick.Controls.Add(cbClass);
#else
            pick.Controls.AddRange(new Control[] { cbClass, new Label { Text = "Apply to:", AutoSize = true, Margin = new Padding(18, 7, 3, 0) }, cbTarget });
#endif
            AddRow(classCard, "Custom class:", pick);
            FlowLayoutPanel actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 2) };
#if LITE
            actions.Controls.AddRange(new Control[] { bExport, bImport });
#else
            actions.Controls.AddRange(new Control[] { bApply, bLoad, bReset, bExport, bImport });
#endif
            AddRow(classCard, "", actions);
            FlowLayoutPanel options = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
#if LITE
            // weapon variants are picked in the game's own menu (Unlock everything -> click a weapon), no checkbox needed
            options.FlowDirection = FlowDirection.TopDown;
            options.Controls.Add(new Label { Text = "Export reads the selected class from the game and saves it. Import writes a class file straight into the selected class.", AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = CaptionText, Margin = new Padding(3, 6, 3, 0) });
            options.Controls.Add(new Label { Text = "Weapons, variants, camos and more: press \"Unlock everything in game menus\", then pick them in the game's Soldier -> Divisions menu.", AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = CaptionText, Margin = new Padding(3, 4, 3, 0) });
            AddRow(classCard, "", options);
#else
            options.Controls.AddRange(new Control[] { chkVariants, chkAll });
            AddRow(classCard, "Options:", options);
#endif

#if LITE
            // Lite: the weapon / equipment / perk fields still exist (export and import go through them) but are not shown
            TableLayoutPanel cardsHost = new TableLayoutPanel { ColumnCount = 1 };
#else
            TableLayoutPanel cardsHost = root;
#endif
            TableLayoutPanel weapons = TwoColumns(cardsHost);
            TableLayoutPanel pg = Card(weapons, "Primary weapon", LoadButton("Load primary from game", LoadScope.Primary), 100);
            cbPrimary = Slot(pg, "Weapon:");
            cbPriCamo = Slot(pg, "Camo:");
            cbPriCamo2 = Slot(pg, "Grip:");
            cbPriCharm = Slot(pg, "Charm:");
            for (int i = 0; i < AttachmentSlots; i++) cbAtt[i] = Slot(pg, "Attachment " + (i + 1) + ":");
            TableLayoutPanel sg = Card(weapons, "Secondary weapon", LoadButton("Load secondary from game", LoadScope.Secondary), 100);
            cbSecondary = Slot(sg, "Weapon:");
            cbSecCamo = Slot(sg, "Camo:");
            cbSecCamo2 = Slot(sg, "Grip:");
            cbSecCharm = Slot(sg, "Charm:");
            for (int i = 0; i < AttachmentSlots; i++) cbSecAtt[i] = Slot(sg, "Attachment " + (i + 1) + ":");

            TableLayoutPanel lower = TwoColumns(cardsHost);
            TableLayoutPanel eg = Card(lower, "Equipment", null, 100);
            cbLethal = Slot(eg, "Lethal:");
            cbTactical = Slot(eg, "Tactical:");
            numLethal = new NumericUpDown { Minimum = 0, Maximum = 5, Width = 60 };
            chkLethalCount = new CheckBox { Text = "set to", AutoSize = true };
            numTactical = new NumericUpDown { Minimum = 0, Maximum = 5, Width = 60 };
            chkTacticalCount = new CheckBox { Text = "set to", AutoSize = true };
            FlowLayoutPanel extraLethal = new FlowLayoutPanel { AutoSize = true };
            extraLethal.Controls.AddRange(new Control[] { chkLethalCount, numLethal });
            FlowLayoutPanel extraTactical = new FlowLayoutPanel { AutoSize = true };
            extraTactical.Controls.AddRange(new Control[] { chkTacticalCount, numTactical });
            AddRow(eg, "Extra lethals:", extraLethal);
            AddRow(eg, "Extra tacticals:", extraTactical);
            TableLayoutPanel kg = Card(lower, "Perks", null, 100);
            for (int i = 0; i < 9; i++) cbPerk[i] = Slot(kg, "Perk slot " + (i + 1) + ":");

            cbPrimary.SelectedIndexChanged += delegate { FillCamos(cbPrimary, cbPriCamo, cbPriCamo2); };
            cbSecondary.SelectedIndexChanged += delegate { FillCamos(cbSecondary, cbSecCamo, cbSecCamo2); };
            root.Controls.Add(new Panel { Height = 0, Margin = Padding.Empty });   // takes the spare height so the last cards don't stretch
            tabs.TabPages.Add(tabClass);

#if !LITE
            // ---- tab 2: dev & hidden items, raw command ----
            TabPage tabDev = new TabPage("Dev & hidden items") { UseVisualStyleBackColor = false, BackColor = BodyBack };
            TableLayoutPanel devRoot = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(6), AutoScroll = true, BackColor = BodyBack };
            tabDev.Controls.Add(devRoot);
            devRoot.Controls.Add(new Label { Text = "Experimental: these items may look broken, do nothing, or freeze the game. Test them in the Firing Range first.", AutoSize = true, ForeColor = ColorRed, Margin = new Padding(6, 6, 6, 6) });
            TableLayoutPanel dg = Card(devRoot, "Hidden weapons, reticle and maps", null, 160);
            rbDevPrimary = new RadioButton { Text = "Primary", AutoSize = true, Checked = true };
            rbDevSecondary = new RadioButton { Text = "Secondary", AutoSize = true };
            FlowLayoutPanel devTarget = new FlowLayoutPanel { AutoSize = true };
            devTarget.Controls.AddRange(new Control[] { rbDevPrimary, rbDevSecondary });
            AddRow(dg, "Apply to weapon slot:", devTarget);
            cbDevWeapon = Slot(dg, "Hidden weapon:");
            cbDevReticle = Slot(dg, "Test reticle:");
            Button bDevApply = new Button { Text = "Apply dev items to class", AutoSize = true };
            bDevApply.Click += delegate { ApplyDev(); };
            StyleButton(bDevApply);
            AddRow(dg, "", bDevApply);
            cbDevMap = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
            cbDevMap.Items.AddRange(HiddenMaps);
            cbDevMap.SelectedIndex = 0;
            Button bMap = new Button { Text = "Set as lobby map", AutoSize = true };
            bMap.Click += delegate { SetHiddenMap(); };
            StyleButton(bMap);
            toolTip.SetToolTip(bMap, "Host only: pick any normal map in the Local Play lobby first, then press this and start the match.\nThe lobby menu keeps showing the old map - that's expected.");
            FlowLayoutPanel mapRow = new FlowLayoutPanel { AutoSize = true };
            mapRow.Controls.AddRange(new Control[] { cbDevMap, bMap });
            AddRow(dg, "Hidden map:", mapRow);

            TableLayoutPanel rg = Card(devRoot, "Console command", null, 160);
            txtRaw = new TextBox { Width = 520 };
            Button bRaw = new Button { Text = "Send raw command", AutoSize = true };
            bRaw.Click += delegate { if (EnsureConnected() && txtRaw.Text.Trim().Length > 0) Send(txtRaw.Text.Trim()); };
            StyleButton(bRaw);
            FlowLayoutPanel raw = new FlowLayoutPanel { AutoSize = true };
            raw.Controls.AddRange(new Control[] { txtRaw, bRaw });
            AddRow(rg, "Command:", raw);
            devRoot.Controls.Add(new Panel { Height = 0, Margin = Padding.Empty });
            tabs.TabPages.Add(tabDev);
#endif

            // ---- log, always visible ----
            Panel logPanel = new Panel { Dock = DockStyle.Bottom, Height = 125, Padding = new Padding(10, 0, 10, 10), BackColor = BodyBack };
            txtLog = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 8.5f), BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
            Label logCaption = Caption("LOG");
            logCaption.Dock = DockStyle.Top;
            logPanel.Controls.Add(txtLog);
            logPanel.Controls.Add(logCaption);

            // docking is applied from the last added control: header, then log, then sidebar, then the tabs fill the rest
            Controls.Add(tabs);
            Controls.Add(side);
#if !LITE
            Controls.Add(logPanel);   // Lite has no log; Export / Import failures are shown in a message box
#endif
            Controls.Add(header);

            Log(AppName + " " + AppVersion + " - made by " + Author + ". Offline / Local Play only.");
            DisableWheelOnValueFields(this);
            LoadItemsFromDisk();

            System.Windows.Forms.Timer auto = new System.Windows.Forms.Timer { Interval = 5000 };
            auto.Tick += delegate
            {
                auto.Stop();
                try { AutoConnectTick(); } catch (Exception ex) { Log("Auto-connect: " + ex.Message); }
                auto.Start();
            };
            auto.Start();

            System.Windows.Forms.Timer variants = new System.Windows.Forms.Timer { Interval = 1500 };
            variants.Tick += delegate
            {
                try { VariantMenuTick(); } catch (Exception ex) { Log("Weapon variants: " + ex.Message); }
            };
            variants.Start();

#if !LITE
            System.Windows.Forms.Timer party = new System.Windows.Forms.Timer { Interval = 1500 };
            party.Tick += delegate
            {
                try { PartyModesTick(); } catch (Exception ex) { Log("Party modes: " + ex.Message); }
            };
            party.Start();
#endif
        }

        // Party modes in the Local Play Mode menu (see Game.FindPartyMenu). The menu scripts are reloaded after every
        // match, so while the button is on the patch is checked every 1.5 s and re-applied; when the menu has moved,
        // a background search runs again (backing off up to 2 minutes while it can't be found, e.g. during a match).
        Game.PartyMenuPatch partyMenu;
        int partyMenuPid, partyScanDelay = 10;
        bool partyModesWanted, partyScanBusy, partyMenuShown, partyFailLogged;
        DateTime partyNextScan = DateTime.MinValue;

        void PartyModesChanged()
        {
            Colorize(bPartyModes, partyModesWanted ? ColorGreen : (Color?)null);
            if (partyModesWanted)
            {
                partyNextScan = DateTime.MinValue;
                partyScanDelay = 10;
                partyFailLogged = false;
                if (!EnsureConnected()) { Log("Party modes: will be applied once the game is running."); return; }
                Log("Party modes: looking for the Mode menu in the game...");
                PartyModesTick();
            }
            else if (partyMenu != null && game != null && !game.HasExited && partyMenuPid == game.Proc.Id && !partyScanBusy)
            {
                if (game.ApplyPartyMenu(partyMenu, false) >= 0) Log("Party modes hidden again (Esports Modes restored). Re-open Game Setup -> Mode.");
                partyMenuShown = false;
            }
        }

        void PartyModesTick()
        {
            if (!partyModesWanted || partyScanBusy || game == null || game.HasExited) return;
            if (partyMenu != null && partyMenuPid == game.Proc.Id)
            {
                int n = game.ApplyPartyMenu(partyMenu, true);
                if (n >= 0)
                {
                    if (n > 0)
                    {
                        Log(partyMenuShown ? "Party modes re-applied (the game reloaded its menus)."
                            : "Party modes enabled: Game Setup -> Mode now lists \"Party Modes\" (Prop Hunt, Gun Game, ...) in place of Esports Modes. Re-open the menu if it is open.");
                        partyMenuShown = true;
                    }
                    return;
                }
            }
            if (DateTime.Now < partyNextScan) return;
            partyNextScan = DateTime.Now.AddSeconds(partyScanDelay);
            partyScanBusy = true;
            Game g = game;
            Game.PartyMenuPatch previous = partyMenuPid == g.Proc.Id ? partyMenu : null;
            Thread t = new Thread(delegate()
            {
                string err;
                Game.PartyMenuPatch p;
                try { p = g.FindPartyMenu(previous, out err); } catch (Exception ex) { p = null; err = ex.Message; }
                BeginInvoke((Action)delegate
                {
                    partyScanBusy = false;
                    if (g != game) return;
                    if (p == null)
                    {
                        partyScanDelay = Math.Min(partyScanDelay * 2, 120);
                        if (!partyFailLogged) Log("Party modes: " + err + ". Retrying in the background.");
                        partyFailLogged = true;
                        return;
                    }
                    partyScanDelay = 10;
                    partyFailLogged = false;
                    if (partyMenuPid != g.Proc.Id) partyMenuShown = false;
                    partyMenu = p;
                    partyMenuPid = g.Proc.Id;
                    PartyModesTick();
                });
            }) { IsBackground = true };
            t.Start();
        }

        TableLayoutPanel rootPanel;

        static TableLayoutPanel TwoColumns(TableLayoutPanel parent)
        {
            TableLayoutPanel t = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, AutoSize = true, Margin = Padding.Empty };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            parent.Controls.Add(t);
            return t;
        }

        // ---------- look ----------

        static readonly Color HeaderBack = Color.FromArgb(35, 39, 46), HeaderSubText = Color.FromArgb(170, 176, 186),
            BodyBack = Color.FromArgb(243, 244, 246), SideBack = Color.FromArgb(232, 234, 238),
            CardBack = Color.White, CardBorder = Color.FromArgb(218, 221, 226), CaptionText = Color.FromArgb(105, 112, 122);

        // A white card with a title (and optionally a button at the right of the title) around a label / field grid.
        static TableLayoutPanel Card(TableLayoutPanel parent, string title, Control titleButton, int labelWidth)
        {
            // A Panel sized like the old GroupBox (grid filling it, title docked on top); an AutoSize TableLayoutPanel
            // here makes every dropdown change re-measure the whole page, which slowed start-up from 2 s to 12 s.
            Panel card = new Panel { Dock = DockStyle.Fill, AutoSize = true, BackColor = CardBack, Padding = new Padding(12, 8, 12, 10), Margin = new Padding(5) };
            card.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(CardBorder)) e.Graphics.DrawRectangle(p, 0, 0, card.Width - 1, card.Height - 1);
            };
            TableLayoutPanel grid = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, AutoSize = true, Margin = Padding.Empty };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Panel head = new Panel { Dock = DockStyle.Top, Height = 34 };
            head.Controls.Add(new Label { Text = title, AutoSize = true, Location = new Point(0, 4), Font = new Font("Segoe UI Semibold", 10.5f), ForeColor = HeaderBack });
            if (titleButton != null)
            {
                titleButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                head.Controls.Add(titleButton);
                head.Resize += delegate { titleButton.Location = new Point(head.Width - titleButton.Width, 0); };
            }
            card.Controls.Add(grid);
            card.Controls.Add(head);
            parent.Controls.Add(card);
            return grid;
        }

        // Flat button with a thin border and a hover shade.
        static void StyleButton(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Color.White;
            b.UseVisualStyleBackColor = false;
            b.FlatAppearance.BorderColor = Color.FromArgb(196, 200, 207);
            b.FlatAppearance.BorderSize = 1;
            b.ForeColor = SystemColors.ControlText;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(236, 239, 243);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(222, 226, 232);
            b.Padding = new Padding(6, 2, 6, 2);
            b.Cursor = Cursors.Hand;
        }

        // Amber "do this first" look for the item list button while no full item list is loaded.
        void HighlightDumpButton(bool on)
        {
            StyleButton(bDump);
            bDump.Padding = new Padding(10, 0, 6, 0);
            bDump.Font = new Font(Font, on ? FontStyle.Bold : FontStyle.Regular);
            if (!on) return;
            bDump.BackColor = Color.FromArgb(255, 236, 179);
            bDump.ForeColor = Color.FromArgb(150, 70, 0);
            bDump.FlatAppearance.BorderColor = ColorOrange;
            bDump.FlatAppearance.BorderSize = 2;
            bDump.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 224, 140);
        }

        static Button SideButton(string text)
        {
            Button b = new Button { Text = text, Width = 226, Height = 36, Margin = new Padding(0, 0, 0, 6), TextAlign = ContentAlignment.MiddleLeft };
            StyleButton(b);
            b.Padding = new Padding(10, 0, 6, 0);
            return b;
        }

        static Label Caption(string text)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = CaptionText, Font = new Font("Segoe UI Semibold", 8.25f), Margin = new Padding(0, 12, 0, 6) };
        }

        static Label Hint(string text)
        {
            return new Label { Text = text, AutoSize = true, MaximumSize = new Size(226, 0), ForeColor = CaptionText, Margin = new Padding(0, 0, 0, 4) };
        }

        // The mouse wheel must not change dropdowns / number boxes by accident; it scrolls the window instead.
        // (An opened dropdown list still scrolls normally.)
        void DisableWheelOnValueFields(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is ComboBox || c is NumericUpDown) c.MouseWheel += BlockWheel;
                DisableWheelOnValueFields(c);
            }
        }

        void BlockWheel(object sender, MouseEventArgs e)
        {
            ComboBox cb = sender as ComboBox;
            if (cb != null && cb.DroppedDown) return;
            HandledMouseEventArgs h = e as HandledMouseEventArgs;
            if (h != null) h.Handled = true;
            Point pos = rootPanel.AutoScrollPosition;
            rootPanel.AutoScrollPosition = new Point(-pos.X, Math.Max(0, -pos.Y - e.Delta));
        }

        ComboBox Slot(TableLayoutPanel grid, string label)
        {
            ComboBox cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, MaxDropDownItems = 30 };
            allSlots.Add(cb);
            AddRow(grid, label, cb);
            return cb;
        }

        static void AddRow(TableLayoutPanel grid, string label, Control c)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Padding = new Padding(0, 6, 0, 0) });
            grid.Controls.Add(c);
        }

        string lastLog = "", lastFailure = "";
        int failures;

        void Log(string s)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Log), s); return; }
            lastLog = s;
            if (s.StartsWith("FAILED")) { lastFailure = s; failures++; }
            txtLog.AppendText(s + Environment.NewLine);
        }

        // ---------- item data ----------

        void LoadItemsFromDisk()
        {
            string[] candidates = { Path.Combine(baseDir, "statstable_full.csv"), Path.Combine(baseDir, "statstable.csv") };
            foreach (string f in candidates)
            {
                if (!File.Exists(f)) continue;
                List<string[]> rows = File.ReadAllLines(f).Select(l => l.Split(',')).ToList();
                SetItems(rows, Path.GetFileName(f));
                return;
            }
            SetItems(new List<string[]>(), "nothing (no statstable csv found)");
        }

        static string Cell(string[] r, int i) { return i < r.Length && r[i] != null ? r[i] : ""; }

        static string TitleCase(string s)
        {
            s = System.Text.RegularExpressions.Regex.Replace(s.Replace('_', ' ').Trim(), @"\s+", " ");
            return string.Join(" ", s.Split(' ').Select(w => w.Length == 0 ? w : char.ToUpper(w[0]) + w.Substring(1)));
        }

        static string PrettyBase(string b)
        {
            string p;
            if (PrettyNames.TryGetValue(b, out p)) return p;
            if (System.Text.RegularExpressions.Regex.IsMatch(b, @"^[a-z]{1,4}\d+$")) return b.ToUpper();
            return TitleCase(b);
        }

        public static string TypeLabel(Item i)
        {
            switch (i.Type)
            {
                case "weapon_assault": return "Rifle";
                case "weapon_smg": return "SMG";
                case "weapon_heavy": case "weapon_lmg": return "LMG";
                case "weapon_sniper": return "Sniper";
                case "weapon_shotgun": return "Shotgun";
                case "weapon_pistol": return "Pistol";
                case "weapon_projectile": return "Launcher";
                case "weapon_other": return IsMeleeInternal(i.Base) ? "Melee" : "Special";
                case "weapon_grenade": return "Equipment";
                case "weapon_attachment": return "Attachment";
                case "weapon_camo": return "Camo";
                case "weapon_class_camo": return "Camo (loot)";
                case "universal_camo": return "Camo (loot)";
                case "grip": return "Grip";
                case "weapon_charm": return "Charm";
                case "site_reticle": return "Reticle";
                case "perk": return "Perk";
                default: return i.Type;
            }
        }

        static bool IsMeleeInternal(string b) { return b != null && !b.StartsWith("flamethrower"); }

        void SetItems(List<string[]> rows, string source)
        {
            items.Clear();
            bool full = rows.Any(r => Cell(r, ColName).EndsWith("_mp"));
            HighlightDumpButton(!full);
            HashSet<string> seen = new HashSet<string>();
            System.Text.RegularExpressions.Regex lootRx = new System.Text.RegularExpressions.Regex(@"^(.*)_loot(\d+)$");
            foreach (string[] r in rows)
            {
                string type = Cell(r, Col0), idStr = Cell(r, ColId);
                bool isWeapon = type.StartsWith("weapon_") && type != "weapon_attachment" && type != "weapon_camo" && type != "weapon_class_camo" && type != "weapon_reticle" && type != "weapon_charm";
                bool wanted = isWeapon || type == "perk" || type == "weapon_attachment" || type == "weapon_camo" || type == "weapon_class_camo" || type == "universal_camo" || type == "grip" || type == "weapon_charm" || type == "site_reticle";
                if (!wanted || !idStr.StartsWith("0x")) continue;
                uint id;
                try { id = Convert.ToUInt32(idStr, 16); } catch { continue; }
                if (!seen.Add(type + "|" + id)) continue;

                string intern = Cell(r, ColName);
                bool camoType = type == "weapon_camo" || type == "weapon_class_camo" || type == "universal_camo";
                // zombies items are skipped, except generic zombie camos: those are offered in the dev section
                if (!camoType && (intern.EndsWith("_zm") || intern.Contains("_zm_") || intern.StartsWith("zom_"))) continue;
                if (type == "site_reticle" && intern != "testSiteReticle") continue;
                Item it = new Item { Type = type, Id = id, Internal = intern };

                if (isWeapon)
                {
                    string b;
                    if (full)
                    {
                        b = intern.EndsWith("_mp") ? intern.Substring(0, intern.Length - 3) : intern;
                        System.Text.RegularExpressions.Match m = lootRx.Match(b);
                        if (m.Success) { b = m.Groups[1].Value; it.Variant = int.Parse(m.Groups[2].Value); }
                        it.Base = b;
                        it.Name = PrettyBase(b);
                        if (it.Variant >= 0) it.Name += string.Format("  - variant {0} (tier {1})", it.Variant + 1, id & 0xF);
                    }
                    else
                    {
                        it.Base = Cell(r, ColRef).ToLower().Replace("weapon_", "");
                        it.Name = intern;
                    }
                }
                else if (type == "weapon_camo" || type == "weapon_class_camo" || type == "universal_camo")
                {
                    // Only generic camo IDs work (e.g. "gold" 0x6000F0, "camo_mtx6_05" 0x701001C). The per-weapon
                    // ("stg44 gold") and per-class ("rifle camo_mtx6_05") IDs freeze the game.
                    if (intern.IndexOf(' ') >= 0) continue;
                    it.Base = "*";
                    it.Name = TitleCase(intern);
                }
                else if (type == "site_reticle")
                {
                    it.Name = "Test sight reticle (testSiteReticle)";
                    it.IsDev = true;
                }
                else if (type == "weapon_charm")
                {
                    // charm field = GUID from mp/charmtable.csv; display name from the icon ("charm_ghost" -> "Ghost").
                    string icon = Cell(r, ColIcon);
                    it.Base = "*";
                    it.Name = intern == "charm_none" ? "None" : TitleCase(icon.StartsWith("charm_") ? icon.Substring(6) : intern);
                }
                else if (type == "grip")
                {
                    // camo2 field = grip (mp/camo2table.csv). Weapon grips are "grip_collection_<weapon>[_02]";
                    // tier2/mtx/community/sweetheart grips are generic.
                    string g = intern.StartsWith("grip_collection_") ? intern.Substring(16) : intern;
                    string gb = System.Text.RegularExpressions.Regex.Replace(g, @"_\d+$", "");
                    string alias;
                    if (GripAliases.TryGetValue(gb, out alias)) gb = alias;
                    bool generic = System.Text.RegularExpressions.Regex.IsMatch(gb, @"^(tier2|mtx\d+|community|sweetheart)$");
                    it.Base = generic ? "*" : gb;
                    it.Name = generic ? "Grip " + TitleCase(g) : PrettyBase(gb) + " grip" + (g.EndsWith("_02") ? " 2" : " 1");
                }
                else if (type == "perk")
                {
                    it.Name = full ? TitleCase(intern.Replace("specialty_class_", "").Replace("specialty_", "")) : intern;
                }
                else // attachment
                {
                    it.Name = full ? TitleCase(intern) : intern;
                }
                if (it.Base != null && isWeapon && IsDevWeaponBase(it.Base)) it.IsDev = true;
                if (intern == "testweaponclasscamo") { it.IsDev = true; it.Name = "Test weapon class camo (not in the camo table)"; }
                items.Add(it);
            }
            items = items.OrderBy(i => TypeOrder(i)).ThenBy(i => i.Name).ToList();
            Dictionary<string, int> counts = items.Where(i => i.Variant < 0 || IsCamo(i)).GroupBy(i => TypeLabel(i)).ToDictionary(g => g.Key, g => g.Count());
            lblItems.Text = "Loaded from " + source + ":\n" + string.Join("\n", counts.Select(kv => "  " + kv.Key + ": " + kv.Value))
                + (full ? "" : "\n\nTip: click \"Load full item list from game\" to get every weapon and camo.");
            FillCombos();
        }

        static int TypeOrder(Item i)
        {
            int k = Array.IndexOf(GunTypes, i.Type);
            if (k >= 0) return k;
            if (i.Type == "weapon_other") return IsMeleeInternal(i.Base) ? 20 : 21;
            if (i.Type == "weapon_camo") return 30;
            if (i.Type == "grip") return 33;
            if (i.Type == "weapon_charm") return 34;
            if (i.Type == "weapon_class_camo") return 31;
            if (i.Type == "universal_camo") return 32;
            if (i.Type == "weapon_attachment") return 50;
            if (i.Type == "weapon_grenade") return 60;
            if (i.Type == "perk") return 70;
            return 40;
        }

        // Weapons the game never offers in Create-a-Class: emote prop, event Tesla guns, flamethrowers, riot shield.
        static bool IsDevWeaponBase(string b)
        {
            return b == "emote_weapon" || b == "riotshield" || b.StartsWith("teslagun") || b.StartsWith("flamethrower");
        }

        static bool IsGunOrMelee(Item i)
        {
            if (i.IsDev) return false;
            if (Array.IndexOf(GunTypes, i.Type) >= 0) return true;
            return i.Type == "weapon_other" && IsMeleeInternal(i.Base);
        }

        static bool IsCamo(Item i) { return i.Type == "weapon_camo" || i.Type == "site_reticle" || i.Type == "grip" || i.Type == "weapon_charm" || i.Type == "weapon_class_camo" || i.Type == "universal_camo"; }

        bool VariantOk(Item i) { return chkVariants.Checked || i.Variant < 0 || i.Type == "weapon_camo"; }

        void FillCombos()
        {
            bool all = chkAll.Checked;
            Func<Item, bool> weapon = i => IsGunOrMelee(i) && VariantOk(i);
            Func<Item, bool> melee = i => !i.IsDev && i.Type == "weapon_other" && IsMeleeInternal(i.Base) && VariantOk(i);
            Func<Item, bool> att = i => i.Type == "weapon_attachment";
            Func<Item, bool> equip = i => i.Type == "weapon_grenade";
            Func<Item, bool> perk = i => i.Type == "perk";
            Func<Item, bool> any = i => !IsCamo(i) && !i.IsDev && VariantOk(i);
            Fill(cbPrimary, all ? any : weapon);
            Fill(cbSecondary, all ? any : weapon);
            foreach (ComboBox c in cbAtt.Concat(cbSecAtt)) Fill(c, all ? any : att);
            Fill(cbLethal, all ? any : equip);
            Fill(cbTactical, all ? any : equip);
            foreach (ComboBox c in cbPerk) Fill(c, all ? any : perk);
            FillCamos(cbPrimary, cbPriCamo, cbPriCamo2);
            FillCamos(cbSecondary, cbSecCamo, cbSecCamo2);
            FillDev();
        }

        // Loot camo class prefix used in the stats table for each weapon type.
        static string CamoClass(Item w)
        {
            switch (w.Type)
            {
                case "weapon_assault": return "rifle";
                case "weapon_smg": return "smg";
                case "weapon_heavy": case "weapon_lmg": return "mg";
                case "weapon_sniper": return "sniper";
                case "weapon_shotgun": return "shotgun";
                case "weapon_pistol": return "pistol";
                case "weapon_projectile": return "launcher";
                case "weapon_other": return IsMeleeInternal(w.Base) && w.Base != "riotshield" ? "melee" : null;
                default: return null;
            }
        }

        // Camos and grips are always filtered strictly (even with "show every item"). Unknown weapon -> nothing offered.
        //   camo:  every non-zombie camo in the game's mp/camotable.csv once the "free camo" fix is applied,
        //          otherwise only the 11 base patterns (the only ones that work without the fix)
        //   grip:  the weapon's own grips + generic grips (generic ones only for guns, not melee)
        void FillCamos(ComboBox weaponBox, ComboBox camoBox, ComboBox gripBox)
        {
            Item w = weaponBox.SelectedItem as Item;
            bool known = w != null && !w.IsKeep && !w.IsEmpty && w.Base != null;
            string b = known ? w.Base : null;
            string cls = known ? CamoClass(w) : null;
            Fill(camoBox, i => known && cls != null && IsCamo(i) && !i.IsDev && i.Type != "grip" && i.Type != "weapon_charm" && i.Type != "site_reticle" &&
                (freeCamos != null ? freeCamos.Contains(i.Internal) : Array.IndexOf(MpCamoPatterns, i.Internal) >= 0));
            Fill(gripBox, i => known && i.Type == "grip" &&
                (i.Base == b || (i.Base == "*" && cls != null && cls != "melee")));
            string tip = !known ? "Pick a weapon above to see what fits it." : "";
            toolTip.SetToolTip(camoBox, tip + (known && camoBox.Items.Count <= 2 ? "This weapon has no camos." : ""));
            toolTip.SetToolTip(gripBox, tip + (known && gripBox.Items.Count <= 2 ? "This weapon has no grips." : ""));
            ComboBox charmBox = weaponBox == cbPrimary ? cbPriCharm : cbSecCharm;
            if (charmBox != null)
            {
                Fill(charmBox, i => known && cls != null && i.Type == "weapon_charm");
                toolTip.SetToolTip(charmBox, tip);
            }
        }

        void FillDev()
        {
            if (cbDevWeapon == null) return;
            Fill(cbDevWeapon, i => i.IsDev && i.Base != null && i.Type.StartsWith("weapon_") && !IsCamo(i));
            Fill(cbDevReticle, i => i.Type == "site_reticle");
        }

        // Maps the Local Play map list hides (dev/test maps and the scorestreak training map) that are in the game's
        // string pool, so the lobby can be pointed at them.
        static readonly object[] HiddenMaps = { "mp_house", "mp_sandbox_01", "mp_scorestreak_training" };

        void SetHiddenMap()
        {
            if (!EnsureConnected()) return;
            string map = cbDevMap.SelectedItem as string;
            Cursor = Cursors.WaitCursor;
            string err = game.SetLobbyMap(map);
            Cursor = Cursors.Default;
            if (err != null) { Log("Hidden map FAILED: " + err); return; }
            string e2;
            game.SendCommand("xupdatepartystate", out e2);
            Log("Lobby map set to " + map + ". The lobby menu still shows the old map - just start the match (host only).");
        }

        void ApplyDev()
        {
            if (!EnsureConnected()) return;
            int slot = rbDevSecondary.Checked ? 1 : 0;
            List<string> fields = new List<string>();
            foreach (KeyValuePair<ComboBox, string> kv in new[] {
                new KeyValuePair<ComboBox, string>(cbDevWeapon, "weapon"),
                new KeyValuePair<ComboBox, string>(cbDevReticle, "reticle") })
            {
                Item it = kv.Key.SelectedItem as Item;
                if (it != null && !it.IsKeep) fields.Add("\"weaponSetups\" " + slot + " \"" + kv.Value + "\" " + it.Id);
            }
            if (fields.Count == 0) { Log("Dev section: nothing selected."); return; }
            int cls = cbClass.SelectedIndex;
            foreach (string f in fields) Send(Prefix() + " " + cls + " " + f);
            Log(string.Format("Dev items applied to Class {0} ({1}). If the game freezes when loading, restart it and set that slot back with the normal controls.", cls + 1, slot == 0 ? "primary" : "secondary"));
        }

        void Fill(ComboBox cb, Func<Item, bool> filter)
        {
            Item prev = cb.SelectedItem as Item;
            cb.BeginUpdate();
            cb.Items.Clear();
            cb.Items.Add(new Item { IsKeep = true });
            cb.Items.Add(new Item { IsEmpty = true, Id = 0 });
            foreach (Item i in items.Where(filter).OrderBy(i => TypeOrder(i)).ThenBy(i => i.Variant == 1 && i.Type == "weapon_camo" ? 1 : 0).ThenBy(i => i.Name)) cb.Items.Add(i);
            int idx = 0;
            if (prev != null)
                for (int k = 0; k < cb.Items.Count; k++)
                {
                    Item it = (Item)cb.Items[k];
                    if (it.IsKeep == prev.IsKeep && it.IsEmpty == prev.IsEmpty && it.Id == prev.Id && it.Type == prev.Type) { idx = k; break; }
                }
            cb.SelectedIndex = idx;
            cb.EndUpdate();
        }

        // ---------- game interaction ----------

        bool Connect()
        {
            if (game != null) { game.Dispose(); game = null; }
            string err;
            Game g = Game.Attach(out err);
            if (g == null) { SetStatus(err, false); Log(err); return false; }
            Log(string.Format("Attached to {0}.exe (pid {1}), module 0x{2:X} size 0x{3:X}", Game.ProcessName, g.Proc.Id, g.ModuleBase, g.ModuleSize));
            Cursor = Cursors.WaitCursor;
            bool ok = g.FindCbuf(out err);
            Cursor = Cursors.Default;
            if (!ok) { SetStatus(err, false); Log(err); g.Dispose(); return false; }
            Log(string.Format("Command buffer found at 0x{0:X}", g.CbufArray));
            game = g;
            SetStatus("Connected (pid " + g.Proc.Id + ")", true);
            ApplyFreeCamos();
            return true;
        }

        // Camos the game treats as "free" (column 9 = 1 in mp/camotable.csv) load fine offline. Any other camo
        // makes the game freeze when a match loads (it waits for the online inventory). This marks every non-zombie
        // camo, and every grip in mp/camo2table.csv, as free in memory. It is undone when the game restarts, so it
        // is re-applied on every connect.
        HashSet<string> freeCamos, devCamos;
        int freeCamosPid;
        const int CamoCols = 13, CamoNameCol = 1, CamoMaterialCol = 8, CamoFreeCol = 9, CamoZombieCol = 11, CellSize = 16;
        const int GripCols = 10, GripFreeCol = 9;

        void ApplyFreeCamos()
        {
            freeCamos = null;
            string err = TryApplyFreeCamos();
            if (err != null)
                Log("Camo fix FAILED (" + err + "). Only the 11 base pattern camos will be offered.");
            RefreshCamoLists();
        }

        bool unlockWanted;

        // Weapon variants in the game's own menu (see Game.FindVariantMenu): part of "Unlock everything". The menu
        // scripts are reloaded after every match, so the patch is checked every 1.5 s and re-applied; when the menu has
        // moved, a background search runs again (backing off up to 2 minutes, e.g. during a match).
        Game.VariantMenuPatch variantMenu;
        int variantPid, variantScanDelay = 10;
        bool variantScanBusy, variantShown, variantFailLogged;
        DateTime variantNextScan = DateTime.MinValue;

        void VariantMenuTick()
        {
            if (!unlockWanted || variantScanBusy || game == null || game.HasExited) return;
            if (variantMenu != null && variantPid == game.Proc.Id)
            {
                int n = game.ApplyVariantMenu(variantMenu);
                if (n >= 0)
                {
                    if (n > 0)
                    {
                        Log(variantShown ? "Weapon variants re-applied (the game reloaded its menus)."
                            : "Weapon variants enabled: clicking a weapon in Soldier -> Divisions now opens its variant list.");
                        variantShown = true;
                    }
                    return;
                }
            }
            if (DateTime.Now < variantNextScan) return;
            variantNextScan = DateTime.Now.AddSeconds(variantScanDelay);
            variantScanBusy = true;
            Game g = game;
            Game.VariantMenuPatch previous = variantPid == g.Proc.Id ? variantMenu : null;
            Thread t = new Thread(delegate()
            {
                string err;
                Game.VariantMenuPatch p;
                try { p = g.FindVariantMenu(previous, out err); } catch (Exception ex) { p = null; err = ex.Message; }
                BeginInvoke((Action)delegate
                {
                    variantScanBusy = false;
                    if (g != game) return;
                    if (p == null)
                    {
                        variantScanDelay = Math.Min(variantScanDelay * 2, 120);
                        if (!variantFailLogged) Log("Weapon variants: " + err + ". Retrying in the background.");
                        variantFailLogged = true;
                        return;
                    }
                    variantScanDelay = 10;
                    variantFailLogged = false;
                    if (variantPid != g.Proc.Id) variantShown = false;
                    variantMenu = p;
                    variantPid = g.Proc.Id;
                    VariantMenuTick();
                });
            }) { IsBackground = true };
            t.Start();
        }

        void UnlockAllClicked()
        {
            if (!EnsureConnected()) return;
            unlockWanted = true;
            Cursor = Cursors.WaitCursor;
            string err = TryApplyUnlockAll();
            Cursor = Cursors.Default;
            if (err != null) Log("Unlock-all FAILED (" + err + ").");
            else { bUnlockAll.Text = "Everything unlocked (until game restart)"; bUnlockAll.ForeColor = Color.DarkGreen; }
            variantNextScan = DateTime.MinValue;
            variantScanDelay = 10;
            VariantMenuTick();
        }

        // Unlocks everything in the game's own menus for Local Play (memory only, re-applied on every connect):
        //  - dvar 709: the developers' "unlock all" switch checked throughout the menu scripts
        //  - mp/unlocktable.csv column 10 (UnlockForLANTournament) = 1 on every row: system link treats items as unlocked
        //  - weapon rows get a Challenge value: system link only lets MTX weapons be equipped if they have one
        //  - per-weapon camos ("stg44 gold") get the challenge of that weapon's greenspot row: offline the menu hides
        //    camos with mastery / tier challenges from the Challenges tab
        //  - weapon pack killswitch dvars off, hidden rarity copies of weapon variants shown (ShowHiddenVariants)
        int unlockPid;
        const int UnlockCols = 12, UnlockRefCol = 0, UnlockTypeCol = 1, UnlockChallengeCol = 3, UnlockLanCol = 10;

        // Offline the camo menu only shows the tabs marked Offline=1 in mp/categories_camo.csv: "Special" (operation 6)
        // and "Challenges". Loot camos whose statstable Operation (column 52) is another season tab are never shown
        // offline, so they are moved to operation 6.
        const int StatsCols = 57, StatsTypeCol = 0, StatsOperationCol = 52;

        int MoveLootCamosToSpecialTab()
        {
            int rows; long cells;
            if (!game.FindStringTable("mp/statstable.csv", StatsCols, out rows, out cells)) { Log("Special tab: stats table not found."); return 0; }
            byte[] st = game.Read(cells, rows * StatsCols * CellSize);
            if (st == null) return 0;
            Dictionary<long, string> cache = new Dictionary<long, string>();
            Func<int, int, string> cell = (r, c) =>
            {
                long p = BitConverter.ToInt64(st, (r * StatsCols + c) * CellSize);
                string v;
                if (!cache.TryGetValue(p, out v)) { v = p == 0 ? "" : (game.ReadCString(p, 64) ?? ""); cache[p] = v; }
                return v;
            };
            byte[] donor = null;
            for (int r = 1; r < rows && donor == null; r++)
                if (cell(r, StatsTypeCol) == "universal_camo" && cell(r, StatsOperationCol) == "6")
                {
                    donor = new byte[CellSize];
                    Array.Copy(st, (r * StatsCols + StatsOperationCol) * CellSize, donor, 0, CellSize);
                }
            if (donor == null) return 0;
            int moved = 0;
            for (int r = 1; r < rows; r++)
            {
                if (cell(r, StatsTypeCol) != "weapon_class_camo") continue;
                string op = cell(r, StatsOperationCol);
                if (op == "" || op == "6") continue;
                Array.Copy(donor, 0, st, (r * StatsCols + StatsOperationCol) * CellSize, CellSize);
                moved++;
            }
            if (moved > 0 && !game.PatchCode(cells, st)) { Log("Special tab: write failed."); return 0; }
            return moved;
        }

        // Every weapon skin has two rarity copies (the game adds "I" / "II" to the name from the last digit of the ID). About a
        // third of them have ShowInArmory (column 22) = 0 and are left out of the variant menu; set it to 1.
        const int StatsNameCol = 1, StatsRefCol = 2, StatsIdCol = 18, StatsShowInArmoryCol = 22;
        static readonly string[] VariantTypes = { "weapon_assault", "weapon_smg", "weapon_heavy", "weapon_sniper", "weapon_shotgun", "weapon_pistol", "weapon_projectile", "weapon_other" };

        // Easter egg: Blyskawica "Shredder II" (0x1085202) becomes "Petsoxuv Milacek" (Czech). The game adds "II" itself (from the last digit of the ID).
        const string EggId = "0x1085202", EggName = "Petsox\u016Fv Mil\u00E1\u010Dek";

        int ShowHiddenVariants()
        {
            int rows; long cells;
            if (!game.FindStringTable("mp/statstable.csv", StatsCols, out rows, out cells)) { Log("Variants: stats table not found."); return 0; }
            byte[] st = game.Read(cells, rows * StatsCols * CellSize);
            if (st == null) return 0;
            Dictionary<long, string> cache = new Dictionary<long, string>();
            Func<int, int, string> cell = (r, c) =>
            {
                long p = BitConverter.ToInt64(st, (r * StatsCols + c) * CellSize);
                string v;
                if (!cache.TryGetValue(p, out v)) { v = p == 0 ? "" : (game.ReadCString(p, 64) ?? ""); cache[p] = v; }
                return v;
            };
            Func<int, int, byte[]> get = (r, c) =>
            {
                byte[] b = new byte[CellSize];
                Array.Copy(st, (r * StatsCols + c) * CellSize, b, 0, CellSize);
                return b;
            };
            Action<int, int, byte[]> put = (r, c, src) => Array.Copy(src, 0, st, (r * StatsCols + c) * CellSize, CellSize);

            byte[] showDonor = null;
            int egg = -1;
            for (int r = 1; r < rows; r++)
            {
                if (showDonor == null && Array.IndexOf(VariantTypes, cell(r, StatsTypeCol)) >= 0 && cell(r, StatsRefCol).Contains("_loot") && cell(r, StatsShowInArmoryCol) == "1")
                    showDonor = get(r, StatsShowInArmoryCol);
                if (cell(r, StatsIdCol) == EggId) egg = r;
            }
            if (showDonor == null) return 0;
            int shown = 0;
            for (int r = 1; r < rows; r++)
            {
                if (Array.IndexOf(VariantTypes, cell(r, StatsTypeCol)) < 0 || !cell(r, StatsRefCol).Contains("_loot")) continue;
                if (cell(r, StatsShowInArmoryCol) == "1") continue;
                put(r, StatsShowInArmoryCol, showDonor);
                shown++;
            }
            bool eggDone = false;
            if (egg > 0 && cell(egg, StatsNameCol).StartsWith("WEAPON_"))
            {
                long name = game.AllocString(EggName);
                if (name != 0)
                {
                    Array.Copy(BitConverter.GetBytes(name), 0, st, (egg * StatsCols + StatsNameCol) * CellSize, 8);
                    eggDone = true;
                }
            }
            if ((shown > 0 || eggDone) && !game.PatchCode(cells, st)) { Log("Variants: write failed."); return 0; }
            return shown;
        }

        // Each weapon pack (mtx3 .. mtx12) has a killswitch dvar; when it is on, the menu drops that pack's weapons
        // (Blyskawica, Erma, Austen, ...).
        static readonly string[] MtxKillswitches = { "mtx3", "mtx3_5", "mtx4", "mtx5", "mtx6", "mtx7", "mtx8", "mtx9", "mtx10", "mtx11", "mtx12" };

        string TryApplyUnlockAll()
        {
            string err;
            if (!game.SendCommand("set 709 1", out err)) return "could not send 'set 709 1': " + err;
            // dvar 5887 shows Carentan (winter) in the Local Play / private match map list
            game.SendCommand("set 5887 1", out err);
            foreach (string k in MtxKillswitches) game.SendCommand("set " + k + "_killswitch 0", out err);

            int rows; long cells;
            if (!game.FindStringTable("mp/unlocktable.csv", UnlockCols, out rows, out cells)) return "unlock table not found - wait for the main menu and press Connect again";
            byte[] arr = game.Read(cells, rows * UnlockCols * CellSize);
            if (arr == null) return "unlock table unreadable";
            Dictionary<long, string> cache = new Dictionary<long, string>();
            Func<int, int, string> cell = (r, c) =>
            {
                long p = BitConverter.ToInt64(arr, (r * UnlockCols + c) * CellSize);
                string v;
                if (!cache.TryGetValue(p, out v)) { v = p == 0 ? "" : (game.ReadCString(p, 128) ?? ""); cache[p] = v; }
                return v;
            };
            Func<int, int, byte[]> get = (r, c) =>
            {
                byte[] b = new byte[CellSize];
                Array.Copy(arr, (r * UnlockCols + c) * CellSize, b, 0, CellSize);
                return b;
            };
            Action<int, int, byte[]> put = (r, c, src) => Array.Copy(src, 0, arr, (r * UnlockCols + c) * CellSize, CellSize);

            byte[] lanDonor = null, challengeDonor = null;
            Dictionary<string, byte[]> patternChallenge = new Dictionary<string, byte[]>();
            for (int r = 1; r < rows; r++)
            {
                string name = cell(r, UnlockRefCol);
                if (lanDonor == null && cell(r, UnlockLanCol) == "1") lanDonor = get(r, UnlockLanCol);
                if (challengeDonor == null && name == "kar98_mp" && cell(r, UnlockChallengeCol) != "") challengeDonor = get(r, UnlockChallengeCol);
                if (cell(r, UnlockTypeCol) == "weaponCamo" && name.EndsWith(" greenspot") && cell(r, UnlockChallengeCol).StartsWith("ch_camo_"))
                    patternChallenge[name.Substring(0, name.IndexOf(' '))] = get(r, UnlockChallengeCol);
            }
            if (lanDonor == null || challengeDonor == null) return "unexpected unlock table layout";

            int lan = 0, weapons = 0, camos = 0;
            for (int r = 1; r < rows; r++)
            {
                if (cell(r, UnlockLanCol) != "1") { put(r, UnlockLanCol, lanDonor); lan++; }
                string type = cell(r, UnlockTypeCol), name = cell(r, UnlockRefCol), challenge = cell(r, UnlockChallengeCol);
                if (type == "weapon" && challenge == "") { put(r, UnlockChallengeCol, challengeDonor); weapons++; }
                else if (type == "weaponCamo")
                {
                    int sp = name.IndexOf(' ');
                    if (sp <= 0) continue;
                    string w = name.Substring(0, sp), camo = name.Substring(sp + 1);
                    if (camo.StartsWith("zom_") || camo.StartsWith("camo_pap") || camo == "pap" || !patternChallenge.ContainsKey(w)) continue;
                    if (challenge.StartsWith("ch_camo_" + w + "_mp_")) continue;
                    put(r, UnlockChallengeCol, patternChallenge[w]); camos++;
                }
            }
            if (lan + weapons + camos > 0 && !game.PatchCode(cells, arr)) return "write failed";
            int special = MoveLootCamosToSpecialTab();
            int variants = ShowHiddenVariants();
            unlockPid = game.Proc.Id;
            Log(string.Format("Unlock-all applied: dev unlock switch on, weapon packs enabled, {0} items unlocked for Local Play, {1} weapons made equippable, {2} challenge camos made visible, {3} loot camos moved into the Special tab, {4} hidden weapon variants shown. Re-open the Divisions menu to see it.", lan, weapons, camos, special, variants));
            return null;
        }

        string TryApplyFreeCamos()
        {
            int rows; long cells;
            if (!game.FindStringTable("mp/camotable.csv", CamoCols, out rows, out cells)) return "camo table not found - wait for the main menu and press Connect again";
            byte[] arr = game.Read(cells, rows * CamoCols * CellSize);
            if (arr == null) return "camo table unreadable";
            Func<int, int, string> cell = (r, c) => game.ReadCString(BitConverter.ToInt64(arr, (r * CamoCols + c) * CellSize), 256) ?? "";

            byte[] donor = null;
            for (int r = 0; r < rows && donor == null; r++)
                if (cell(r, CamoNameCol) == "greenspot" && cell(r, CamoFreeCol) == "1")
                {
                    donor = new byte[CellSize];
                    Array.Copy(arr, (r * CamoCols + CamoFreeCol) * CellSize, donor, 0, CellSize);
                }
            if (donor == null) return "unexpected camo table layout";

            HashSet<string> free = new HashSet<string>();
            HashSet<string> zombieCamos = new HashSet<string>();
            Dictionary<string, string> materials = new Dictionary<string, string>();
            int patched = 0;
            for (int r = 1; r < rows; r++)
            {
                string name = cell(r, CamoNameCol);
                if (name.Length == 0) continue;
                if (cell(r, CamoFreeCol) != "1")
                {
                    if (!game.PatchCode(cells + (r * CamoCols + CamoFreeCol) * CellSize, donor)) return "write failed";
                    patched++;
                }
                materials[name] = cell(r, CamoMaterialCol);
                if (cell(r, CamoZombieCol) == "1") zombieCamos.Add(name);
                else free.Add(name);
            }

            int gripRows; long gripCells; int gripsPatched = 0;
            if (game.FindStringTable("mp/camo2table.csv", GripCols, out gripRows, out gripCells))
            {
                byte[] g = game.Read(gripCells, gripRows * GripCols * CellSize);
                for (int r = 1; g != null && r < gripRows; r++)
                {
                    long p = BitConverter.ToInt64(g, (r * GripCols + GripFreeCol) * CellSize);
                    if (game.ReadCString(p, 8) == "1") continue;
                    if (game.PatchCode(gripCells + (r * GripCols + GripFreeCol) * CellSize, donor)) gripsPatched++;
                }
            }
            else Log("Grip table not found - grips may freeze the game.");

            foreach (Item it in items)
            {
                string mat;
                if (IsCamo(it) && it.Type != "grip" && it.Type != "weapon_charm" && materials.TryGetValue(it.Internal, out mat) && mat.StartsWith("camo_"))
                    it.Name = TitleCase(mat.Substring(5));
            }
            freeCamos = free;
            devCamos = zombieCamos;
            freeCamosPid = game.Proc.Id;
            Log(string.Format("Camo fix applied: {0} camos available ({1} newly marked free), {2} grips marked free. Keep this tool open while playing; it re-applies the fix if the game restarts.", free.Count, patched, gripsPatched));
            return null;
        }

        void RefreshCamoLists()
        {
            FillDev();
            FillCamos(cbPrimary, cbPriCamo, cbPriCamo2);
            FillCamos(cbSecondary, cbSecCamo, cbSecCamo2);
        }

        // Re-attaches automatically when the game is (re)started, so the camo fix is in place before
        // a class with a non-free camo gets loaded.
        void AutoConnectTick()
        {
            if (game != null && !game.HasExited && freeCamosPid == game.Proc.Id && (!unlockWanted || unlockPid == game.Proc.Id)) return;
            if (game != null && game.HasExited)
            {
                game.Dispose(); game = null; freeCamos = null;
                RefreshCamoLists();
                SetStatus("Game closed", false);
                Log("Game closed.");
            }
            if (Process.GetProcessesByName(Game.ProcessName).Length == 0) return;
            if (game == null)
            {
                string err, err2;
                Game g = Game.Attach(out err);
                if (g == null) return;
                if (!g.FindCbuf(out err2)) { g.Dispose(); return; }
                game = g;
                SetStatus("Connected (pid " + g.Proc.Id + ")", true);
                Log("Game detected (pid " + g.Proc.Id + "), connected automatically.");
            }
            if (freeCamosPid != game.Proc.Id && TryApplyFreeCamos() == null) RefreshCamoLists();
            if (unlockWanted && unlockPid != game.Proc.Id && TryApplyUnlockAll() == null) Log("Unlock-all re-applied to the restarted game.");
        }

        bool EnsureConnected()
        {
            if (game != null && !game.HasExited) return true;
            return Connect();
        }

        void SetStatus(string s, bool good)
        {
            lblStatus.Text = "●  " + s;
            lblStatus.ForeColor = Color.White;
            lblStatus.BackColor = good ? ColorGreen : ColorRed;
        }

        void DumpTable()
        {
            if (!EnsureConnected()) return;
            Cursor = Cursors.WaitCursor;
            Enabled = false;
            Game g = game;
            Thread t = new Thread(delegate()
            {
                List<string[]> rows = null;
                try { rows = g.DumpStringTable("mp/statstable.csv", Log); }
                catch (Exception ex) { Log("Dump failed: " + ex.Message); }
                BeginInvoke(new Action(delegate
                {
                    Enabled = true;
                    Cursor = Cursors.Default;
                    if (rows == null) { Log("Could not locate the stats table in memory. Using the csv file instead."); return; }
                    string outFile = Path.Combine(baseDir, "statstable_full.csv");
                    try
                    {
                        File.WriteAllLines(outFile, rows.Select(r => string.Join(",", r.Select(c => (c ?? "").Replace(",", ";")))).ToArray());
                        Log("Saved " + rows.Count + " rows to " + outFile);
                    }
                    catch (Exception ex) { Log("Could not save csv: " + ex.Message); }
                    SetItems(rows, "game memory (" + rows.Count + " rows)");
                }));
            });
            t.IsBackground = true;
            t.Start();
        }

        bool Send(string cmd)
        {
            string err;
            if (!game.SendCommand(cmd, out err)) { Log("FAILED: " + cmd + "  -> " + err); return false; }
            Log("> " + cmd);
            return true;
        }

        string Prefix()
        {
            return cbTarget.SelectedIndex == 0 ? "setPrivateLoadout \"privateMatchCustomClasses\"" : "setRankedLoadout \"customClasses\"";
        }

        // Bit layout of one CustomClass in mp/ddl/privateloadouts.ddl (1448 bits = 181 bytes, all fields byte aligned):
        //   name @0 (20 bytes), weaponSetups[2] @192 (416 bits each: camo +0, camo2 +32, charm +64, paintjob +96,
        //   reticle +128, customization +160, attachment[6] +192, weapon +384), perkSlots[9] @1024, equipmentSetups[2] @1312
        //   (equipment +0, numExtra +32).
        const int ClassBytes = 181, WeaponSetupBit = 192, WeaponSetupBits = 416, PerkBit = 1024, EquipBit = 1312;

        static int ClassBit(int cls) { return cls * ClassBytes * 8; }

        int[] LoadoutCheckOffsets()
        {
            List<int> offs = new List<int>();
            for (int c = 0; c < 10; c++)
            {
                int cb = ClassBit(c);
                for (int w = 0; w < 2; w++) offs.Add((cb + WeaponSetupBit + w * WeaponSetupBits + 384) / 8);
                for (int p = 0; p < 9; p++) offs.Add((cb + PerkBit + 32 * p) / 8);
                for (int e = 0; e < 2; e++) offs.Add((cb + EquipBit + 64 * e) / 8);
            }
            return offs.ToArray();
        }

        // ---------- export / import ----------

        // Field order matters on import: weapons first, because they decide which camos/grips/charms are listed.
        List<KeyValuePair<string, ComboBox>> ClassFields()
        {
            List<KeyValuePair<string, ComboBox>> f = new List<KeyValuePair<string, ComboBox>>();
            Action<string, ComboBox> add = (k, c) => f.Add(new KeyValuePair<string, ComboBox>(k, c));
            add("primary", cbPrimary);
            add("secondary", cbSecondary);
            add("primaryCamo", cbPriCamo);
            add("primaryGrip", cbPriCamo2);
            add("primaryCharm", cbPriCharm);
            for (int i = 0; i < AttachmentSlots; i++) add("primaryAttachment" + (i + 1), cbAtt[i]);
            add("secondaryCamo", cbSecCamo);
            add("secondaryGrip", cbSecCamo2);
            add("secondaryCharm", cbSecCharm);
            add("secondaryAttachment", cbSecAtt[0]);   // name kept for files exported by older versions
            for (int i = 1; i < AttachmentSlots; i++) add("secondaryAttachment" + (i + 1), cbSecAtt[i]);
            add("lethal", cbLethal);
            add("tactical", cbTactical);
            for (int i = 0; i < 9; i++) add("perk" + (i + 1), cbPerk[i]);
            return f;
        }

        string LoadoutFolder()
        {
            string dir = Path.Combine(baseDir, "loadouts");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        static string JsonEscape(string v)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char ch in v ?? "")
            {
                if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
                else if (ch < ' ') sb.AppendFormat("\\u{0:x4}", (int)ch);
                else sb.Append(ch);
            }
            return sb.ToString();
        }

        bool ExportClass()
        {
            string className = cbClass.SelectedItem as string ?? "Class";
            string suggested = className.Contains(" - ") ? className.Substring(className.IndexOf(" - ") + 3).Trim() : className;
            using (SaveFileDialog dlg = new SaveFileDialog { Filter = "WWII class (*.json)|*.json", InitialDirectory = LoadoutFolder(), FileName = string.Concat(suggested.Split(Path.GetInvalidFileNameChars())) + ".json" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return false;
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"format\": \"ww2-localplay-class\",");
                sb.AppendLine("  \"version\": 1,");
                sb.AppendLine("  \"name\": \"" + JsonEscape(className) + "\",");
                sb.AppendLine("  \"extraLethal\": " + (chkLethalCount.Checked ? numLethal.Value.ToString() : "null") + ",");
                sb.AppendLine("  \"extraTactical\": " + (chkTacticalCount.Checked ? numTactical.Value.ToString() : "null") + ",");
                sb.AppendLine("  \"fields\": {");
                List<string> lines = new List<string>();
                foreach (KeyValuePair<string, ComboBox> kv in ClassFields())
                {
                    Item it = kv.Key == null ? null : kv.Value.SelectedItem as Item;
                    if (it == null || it.IsKeep) continue;   // "(keep current)" is not exported
                    lines.Add(string.Format("    \"{0}\": {{ \"id\": \"0x{1:X}\", \"name\": \"{2}\" }}", kv.Key, it.Id, JsonEscape(it.IsEmpty ? "(empty)" : it.ToString())));
                }
                sb.AppendLine(string.Join(",\r\n", lines.ToArray()));
                sb.AppendLine("  }");
                sb.AppendLine("}");
                File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
                Log("Exported " + lines.Count + " fields to " + dlg.FileName);
                return true;
            }
        }

        bool ImportClass()
        {
            using (OpenFileDialog dlg = new OpenFileDialog { Filter = "WWII class (*.json)|*.json|All files (*.*)|*.*", InitialDirectory = LoadoutFolder() })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return false;
                Dictionary<string, object> root;
                try
                {
                    root = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(dlg.FileName));
                }
                catch (Exception ex) { Log("Import FAILED: not a valid class file (" + ex.Message + ")"); return false; }
                object fo;
                Dictionary<string, object> fields = root.TryGetValue("fields", out fo) ? fo as Dictionary<string, object> : null;
                if (fields == null) { Log("Import FAILED: the file has no \"fields\" section."); return false; }

                Func<object, uint?> idOf = o =>
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    object v = d != null && d.ContainsKey("id") ? d["id"] : o;
                    string sv = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
                    try { return sv.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToUInt32(sv, 16) : Convert.ToUInt32(sv); }
                    catch { return null; }
                };
                // show weapon variants if the file uses one
                foreach (string k in new[] { "primary", "secondary" })
                {
                    uint? wid = fields.ContainsKey(k) ? idOf(fields[k]) : null;
                    if (wid.HasValue && !chkVariants.Checked && items.Any(i => i.Id == wid.Value && i.Variant >= 0 && i.Type.StartsWith("weapon_") && !IsCamo(i)))
                        chkVariants.Checked = true;
                }
                int set = 0;
                foreach (KeyValuePair<string, ComboBox> kv in ClassFields())
                {
                    uint? id = fields.ContainsKey(kv.Key) ? idOf(fields[kv.Key]) : null;
                    if (id.HasValue) { SelectById(kv.Value, id.Value); set++; }
                    else kv.Value.SelectedIndex = 0;
                }
                object v2;
                chkLethalCount.Checked = root.TryGetValue("extraLethal", out v2) && v2 != null;
                if (chkLethalCount.Checked) numLethal.Value = Math.Min(numLethal.Maximum, Convert.ToDecimal(v2));
                chkTacticalCount.Checked = root.TryGetValue("extraTactical", out v2) && v2 != null;
                if (chkTacticalCount.Checked) numTactical.Value = Math.Min(numTactical.Maximum, Convert.ToDecimal(v2));
                object nm;
#if LITE
                Log(string.Format("Imported {0} fields from {1}{2}.",
                    set, Path.GetFileName(dlg.FileName), root.TryGetValue("name", out nm) && nm != null ? " (" + nm + ")" : ""));
#else
                Log(string.Format("Imported {0} fields from {1}{2}. Pick the target class and click \"Apply to class\".",
                    set, Path.GetFileName(dlg.FileName), root.TryGetValue("name", out nm) && nm != null ? " (" + nm + ")" : ""));
#endif
                return true;
            }
        }

#if LITE
        // Export: read the selected class from the game (every field, extra equipment counts included), then save it.
        void LiteExport()
        {
            if (!LoadClassFromGame(LoadScope.All)) { LiteError("Export"); return; }
            chkLethalCount.Checked = chkTacticalCount.Checked = true;
            ExportClass();
        }

        // Lite has no log: show why Export / Import didn't work.
        void LiteError(string what)
        {
            MessageBox.Show(this, lastLog, what + " failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // Import: fill the (hidden) fields from a file, then write them straight into the selected class.
        void LiteImport()
        {
            if (!EnsureConnected()) { LiteError("Import"); return; }
            string before = lastLog;
            if (!ImportClass()) { if (lastLog != before) LiteError("Import"); return; }   // cancelling the file dialog is not an error
            int failed = failures;
            Apply();
            if (failures != failed) { MessageBox.Show(this, lastFailure, "Import failed", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            MessageBox.Show(this, "The class was written into " + cbClass.SelectedItem + ".\nRe-open the class in the game's Divisions menu to see it.",
                "Import", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
#endif

        enum LoadScope { All, Primary, Secondary }

        Button LoadButton(string text, LoadScope scope)
        {
            Button b = new Button { Text = text, AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(3, 0, 0, 0) };
            StyleButton(b);
            Colorize(b, ColorGreen);
            b.Click += delegate { LoadClassFromGame(scope); };
            toolTip.SetToolTip(b, "Reads only this weapon (with its camo, grip, charm and attachments) of the selected class from the game.");
            return b;
        }

        // Reads the selected Local Play class from the game. Scope Primary/Secondary fills only that weapon's column
        // and leaves every other field as it is.
        bool LoadClassFromGame(LoadScope scope)
        {
            if (!EnsureConnected()) return false;
            if (cbTarget.SelectedIndex != 0) Log("Note: loading reads the Local Play classes.");
            HashSet<uint> known = new HashSet<uint>(items.Where(i => i.Id > 0xFFFF).Select(i => i.Id));
            Cursor = Cursors.WaitCursor;
            string err;
            byte[] data = game.ReadPrivateLoadouts(known, LoadoutCheckOffsets(), out err);
            Cursor = Cursors.Default;
            if (data == null) { Log("Load FAILED: " + err); return false; }

            // class names into the class list
            int selected = cbClass.SelectedIndex;
            for (int c = 0; c < 10; c++)
            {
                string nm = new string(Encoding.ASCII.GetString(data, c * ClassBytes, 20).TakeWhile(ch => ch != '\0').Where(ch => ch >= ' ' && ch < 127).ToArray()).Trim();
                cbClass.Items[c] = "Class " + (c + 1) + (nm.Length > 0 ? "  -  " + nm : "");
            }
            cbClass.SelectedIndex = selected;

            int cls = cbClass.SelectedIndex, cb = ClassBit(cls);
            Func<int, uint> dw = bit => BitConverter.ToUInt32(data, bit / 8);
            var weaponsToLoad = new List<int>();
            if (scope != LoadScope.Secondary) weaponsToLoad.Add(0);
            if (scope != LoadScope.Primary) weaponsToLoad.Add(1);

            // weapons first (they decide which camos/grips/charms are listed); show variants if a variant is equipped
            foreach (int w in weaponsToLoad)
            {
                uint wid = dw(cb + WeaponSetupBit + w * WeaponSetupBits + 384);
                if (!chkVariants.Checked && items.Any(i => i.Id == wid && i.Variant >= 0 && i.Type.StartsWith("weapon_") && !IsCamo(i)))
                    chkVariants.Checked = true;
            }
            string reticles = "";
            foreach (int w in weaponsToLoad)
            {
                int ws = cb + WeaponSetupBit + w * WeaponSetupBits;
                SelectById(w == 0 ? cbPrimary : cbSecondary, dw(ws + 384));
                SelectById(w == 0 ? cbPriCamo : cbSecCamo, dw(ws));
                SelectById(w == 0 ? cbPriCamo2 : cbSecCamo2, dw(ws + 32));
                SelectById(w == 0 ? cbPriCharm : cbSecCharm, dw(ws + 64));
                ComboBox[] atts = w == 0 ? cbAtt : cbSecAtt;
                for (int i = 0; i < AttachmentSlots; i++) SelectById(atts[i], dw(ws + 192 + 32 * i));
                if (dw(ws + 128) != 0) reticles += string.Format(" {0} reticle 0x{1:X}.", w == 0 ? "Primary" : "Secondary", dw(ws + 128));
            }
            if (scope == LoadScope.All)
            {
                SelectById(cbLethal, dw(cb + EquipBit));
                SelectById(cbTactical, dw(cb + EquipBit + 64));
                numLethal.Value = Math.Min(numLethal.Maximum, dw(cb + EquipBit + 32));
                numTactical.Value = Math.Min(numTactical.Maximum, dw(cb + EquipBit + 96));
                chkLethalCount.Checked = chkTacticalCount.Checked = false;
                for (int p = 0; p < 9; p++) SelectById(cbPerk[p], dw(cb + PerkBit + 32 * p));
            }
            string what = scope == LoadScope.All ? "" : (scope == LoadScope.Primary ? "primary weapon of " : "secondary weapon of ");
            Log(string.Format("Loaded {0}{1} from the game.{2}", what, cbClass.Items[cls], reticles));
            return true;
        }

        // Selects the item with this ID; 0 = "(empty / none)". Items not offered in this slot are added so the
        // field still shows what the game has.
        void SelectById(ComboBox cb, uint id)
        {
            if (id == 0) { cb.SelectedIndex = 1; return; }
            for (int k = 2; k < cb.Items.Count; k++)
                if (((Item)cb.Items[k]).Id == id) { cb.SelectedIndex = k; return; }
            Item known = items.FirstOrDefault(i => i.Id == id);
            Item shown = known != null
                ? new Item { Type = known.Type, Internal = known.Internal, Base = known.Base, Name = known.Name + "  (not normally in this slot)", Id = id, Variant = known.Variant, IsDev = known.IsDev }
                : new Item { Type = "unknown", Name = string.Format("Unknown item 0x{0:X}", id), Id = id };
            cb.Items.Add(shown);
            cb.SelectedIndex = cb.Items.Count - 1;
        }

        void Apply()
        {
            if (!EnsureConnected()) return;
            List<string> fields = new List<string>();
            Action<ComboBox, string> add = delegate(ComboBox cb, string path)
            {
                Item it = cb.SelectedItem as Item;
                if (it != null && !it.IsKeep) fields.Add(path + " " + it.Id);
            };
            // Changing a weapon while keeping its camo could leave the old gun's camo on an
            // incompatible weapon, so in that case the camo is cleared (set to 0).
            Action<ComboBox, ComboBox, string> addCamo = delegate(ComboBox weaponCb, ComboBox camoCb, string path)
            {
                Item w = weaponCb.SelectedItem as Item;
                Item c = camoCb.SelectedItem as Item;
                if (c != null && !c.IsKeep) fields.Add(path + " " + c.Id);
                else if (w != null && !w.IsKeep) fields.Add(path + " 0");
            };
            add(cbPrimary, "\"weaponSetups\" 0 \"weapon\"");
            addCamo(cbPrimary, cbPriCamo, "\"weaponSetups\" 0 \"camo\"");
            addCamo(cbPrimary, cbPriCamo2, "\"weaponSetups\" 0 \"camo2\"");
            addCamo(cbPrimary, cbPriCharm, "\"weaponSetups\" 0 \"charm\"");
            for (int i = 0; i < AttachmentSlots; i++) add(cbAtt[i], "\"weaponSetups\" 0 \"attachment\" " + i);
            add(cbSecondary, "\"weaponSetups\" 1 \"weapon\"");
            addCamo(cbSecondary, cbSecCamo, "\"weaponSetups\" 1 \"camo\"");
            addCamo(cbSecondary, cbSecCamo2, "\"weaponSetups\" 1 \"camo2\"");
            addCamo(cbSecondary, cbSecCharm, "\"weaponSetups\" 1 \"charm\"");
            for (int i = 0; i < AttachmentSlots; i++) add(cbSecAtt[i], "\"weaponSetups\" 1 \"attachment\" " + i);
            add(cbLethal, "\"equipmentSetups\" 0 \"equipment\"");
            add(cbTactical, "\"equipmentSetups\" 1 \"equipment\"");
            if (chkLethalCount.Checked) fields.Add("\"equipmentSetups\" 0 \"numExtra\" " + numLethal.Value);
            if (chkTacticalCount.Checked) fields.Add("\"equipmentSetups\" 1 \"numExtra\" " + numTactical.Value);
            for (int i = 0; i < 9; i++) add(cbPerk[i], "\"perkSlots\" " + i);
            if (fields.Count == 0) { Log("Nothing to apply - every slot is set to (keep current)."); return; }

            int cls = cbClass.SelectedIndex;
            int sent = 0;
            foreach (string f in fields)
                if (Send(Prefix() + " " + cls + " " + f)) sent++;
            Log(string.Format("Sent {0} command(s) for Class {1}. Re-open the class in the game menu (or respawn) to see the change.", sent, cls + 1));
        }

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.Run(new MainForm());
        }
    }
}
