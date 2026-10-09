// Outil autonome (sans interface) pour les fichiers du jeu.
//   find <dossier jeu> <chemin depot>…                  : dans quelle archive se trouve chaque fichier
//   extract <dossier jeu> <dossier sortie> <chemin>…     : extrait les fichiers bruts (CR2W)
//   ent <dossier NCWE> <fichier .ent extrait>           : apparences d'une entite (nom -> .app)
//   app <dossier NCWE> <fichier .app extrait> [nom]      : composants des apparences (meshes, transformations)
//   passage …                                          : mod ArchiveXL (voir RedPassage.cs)
// Lecture des archives : index RDAR lu directement, decompression avec la DLL Oodle du jeu.
// Lancement : dotnet exec --runtimeconfig redtool.runtimeconfig.json redtool.exe …

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

static partial class RedTool
{
    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("usage : redtool find|extract|ent|app …"); return 1; }
        try
        {
            switch (args[0])
            {
                case "find": return Find(args[1], Rest(args, 2), null);
                case "extract": return Find(args[1], Rest(args, 3), args[2]);
                case "ent": Wk(args[1]); return Ent(args[2]);
                case "app": Wk(args[1]); return App(args[2], args.Length > 3 ? args[3] : null);
                case "passage": Wk(args[1]); return Passage(args[2], args[3], args[4], args[5]);
            }
        }
        catch (Exception e) { Console.WriteLine("ERREUR " + e.GetBaseException()); return 2; }
        return 1;
    }

    static List<string> Rest(string[] a, int from) { var l = new List<string>(); for (int i = from; i < a.Length; i++) l.Add(a[i]); return l; }

    // ---------------- archives RDAR ----------------
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibrary(string path);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int OodleLZ_Decompress(byte[] src, long srcLen, byte[] dst, long dstLen, int fuzz, int crc, int verbose, IntPtr dstBase, long dstBaseSize, IntPtr cb, IntPtr cbData, IntPtr decoderMem, long decoderMemSize, int threadPhase);
    [DllImport("kernel32")] static extern IntPtr GetProcAddress(IntPtr h, string name);
    static OodleLZ_Decompress oodle;

    static ulong Fnv(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (byte b in Encoding.UTF8.GetBytes(s.ToLowerInvariant())) { h ^= b; h *= 1099511628211UL; }
        return h;
    }

    class Entry { public ulong Hash; public uint SegStart, SegEnd; }
    class Segment { public ulong Offset; public uint ZSize, Size; }

    static int Find(string game, List<string> paths, string outDir)
    {
        IntPtr lib = LoadLibrary(Path.Combine(game, @"bin\x64\oo2ext_7_win64.dll"));
        if (lib != IntPtr.Zero) oodle = (OodleLZ_Decompress)Marshal.GetDelegateForFunctionPointer(GetProcAddress(lib, "OodleLZ_Decompress"), typeof(OodleLZ_Decompress));
        var want = new Dictionary<ulong, string>();
        foreach (string p in paths) want[Fnv(p)] = p;
        var archives = new List<string>();
        foreach (string sub in new[] { "content", "ep1" }) { string d = Path.Combine(game, @"archive\pc\" + sub); if (Directory.Exists(d)) archives.AddRange(Directory.GetFiles(d, "*.archive")); }
        string mods = Path.Combine(game, @"archive\pc\mod"); if (Directory.Exists(mods)) archives.AddRange(Directory.GetFiles(mods, "*.archive"));
        foreach (string arc in archives)
        {
            if (want.Count == 0) break;
            using (var fs = File.OpenRead(arc))
            using (var br = new BinaryReader(fs))
            {
                if (br.ReadUInt32() != 0x52414452) continue;            // "RDAR"
                br.ReadUInt32();                                        // version
                ulong indexPos = br.ReadUInt64();
                fs.Position = (long)indexPos;
                br.ReadUInt32(); br.ReadUInt32(); br.ReadUInt64();      // fileTableOffset, fileTableSize, crc
                uint nFiles = br.ReadUInt32(), nSegs = br.ReadUInt32(); br.ReadUInt32();
                var entries = new Dictionary<ulong, Entry>();
                for (uint i = 0; i < nFiles; i++)
                {
                    var e = new Entry { Hash = br.ReadUInt64() };
                    br.ReadInt64(); br.ReadUInt32();                    // timestamp, inline segments
                    e.SegStart = br.ReadUInt32(); e.SegEnd = br.ReadUInt32();
                    br.ReadUInt32(); br.ReadUInt32(); br.ReadBytes(20); // dependances, sha1
                    if (want.ContainsKey(e.Hash)) entries[e.Hash] = e;
                }
                if (entries.Count == 0) continue;
                var segs = new Segment[nSegs];
                for (uint i = 0; i < nSegs; i++) segs[i] = new Segment { Offset = br.ReadUInt64(), ZSize = br.ReadUInt32(), Size = br.ReadUInt32() };
                foreach (Entry e in entries.Values)
                {
                    string path = want[e.Hash]; want.Remove(e.Hash);
                    Console.WriteLine("TROUVE " + path + " -> " + Path.GetFileName(arc));
                    if (outDir == null) continue;
                    var ms = new MemoryStream();
                    // 1er segment : le fichier CR2W (decompresse) ; suivants : ses buffers, gardes tels quels (compresses)
                    for (uint s = e.SegStart; s < e.SegEnd; s++)
                    {
                        byte[] data = s == e.SegStart ? ReadSegment(fs, segs[s]) : ReadRaw(fs, segs[s]);
                        ms.Write(data, 0, data.Length);
                    }
                    Directory.CreateDirectory(outDir);
                    File.WriteAllBytes(Path.Combine(outDir, path.Replace('\\', '_')), ms.ToArray());
                }
            }
        }
        foreach (string p in want.Values) Console.WriteLine("INTROUVABLE " + p);
        return 0;
    }

    static byte[] ReadRaw(FileStream fs, Segment s)
    {
        fs.Position = (long)s.Offset;
        var buf = new byte[s.ZSize]; fs.Read(buf, 0, buf.Length);
        return buf;
    }

    static byte[] ReadSegment(FileStream fs, Segment s)
    {
        fs.Position = (long)s.Offset;
        var buf = new byte[s.ZSize]; fs.Read(buf, 0, buf.Length);
        if (s.ZSize == s.Size) return buf;
        // en-tete "KARK" + taille, puis donnees Kraken
        int off = (buf.Length >= 8 && buf[0] == 'K' && buf[1] == 'A' && buf[2] == 'R' && buf[3] == 'K') ? 8 : 0;
        var src = new byte[buf.Length - off]; Buffer.BlockCopy(buf, off, src, 0, src.Length);
        var dst = new byte[s.Size];
        int n = oodle(src, src.Length, dst, dst.Length, 1, 0, 0, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, 3);
        if (n != s.Size) throw new Exception("decompression : " + n + " / " + s.Size);
        return dst;
    }

    // ---------------- lecture CR2W avec WolvenKit ----------------
    static string ncwe;
    static void Wk(string dir)
    {
        ncwe = dir;
        AppDomain.CurrentDomain.AssemblyResolve += delegate (object s, ResolveEventArgs e)
        {
            string f = Path.Combine(ncwe, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(f) ? Assembly.LoadFrom(f) : null;
        };
        foreach (string dll in new[] { "WolvenKit.Core.dll", "WolvenKit.Common.dll", "WolvenKit.RED4.dll" }) Assembly.LoadFrom(Path.Combine(ncwe, dll));
        // donnees internes compressees (buffers) : Oodle du jeu, dossier dans REDTOOL_GAME
        string game = Environment.GetEnvironmentVariable("REDTOOL_GAME");
        if (!string.IsNullOrEmpty(game))
        {
            T("WolvenKit.Core.Compression.Oodle").GetMethod("Load").Invoke(null, new object[] { Path.Combine(game, @"bin\x64\oo2ext_7_win64.dll") });
            object settings = T("WolvenKit.Core.Compression.CompressionSettings").GetMethod("Get").Invoke(null, null);
            settings.GetType().GetProperty("UseOodle").SetValue(settings, true, null);
        }
    }
    static Type T(string full)
    {
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) { Type t = a.GetType(full, false); if (t != null) return t; }
        throw new Exception("type introuvable : " + full);
    }
    static object P(object o, string n) { if (o == null) return null; PropertyInfo p = o.GetType().GetProperty(n); return p == null ? null : p.GetValue(o, null); }
    static string S(object o) { return o == null ? "" : o.ToString(); }

    static object ReadCr2w(string file)
    {
        Type cr = T("WolvenKit.RED4.Archive.IO.CR2WReader");
        MethodInfo read = null;
        foreach (MethodInfo m in cr.GetMethods()) if (m.Name == "ReadFile" && m.GetParameters().Length == 2) read = m;
        using (var fs = File.OpenRead(file))
        {
            object[] ra = { null, true };
            read.Invoke(Activator.CreateInstance(cr, new object[] { fs }), ra);
            return P(ra[0], "RootChunk");
        }
    }

    static IEnumerable Items(object o) { var e = o as IEnumerable; return e ?? new object[0]; }
    static string Res(object r) { object dp = P(r, "DepotPath"); return S(dp ?? r); }

    static int Ent(string file)
    {
        object root = ReadCr2w(file);
        Console.WriteLine("classe : " + root.GetType().Name);
        foreach (object a in Items(P(root, "Appearances")))
            Console.WriteLine("APPARENCE " + S(P(a, "Name")) + " -> " + Res(P(a, "AppearanceResource")) + " [" + S(P(a, "AppearanceName")) + "]");
        foreach (object c in Items(P(root, "Components")))
            Console.WriteLine("COMPOSANT " + c.GetType().Name + " " + S(P(c, "Name")) + Extra(c));
        return 0;
    }

    static string Extra(object c)
    {
        var sb = new StringBuilder();
        object mesh = P(c, "Mesh"); if (mesh != null) sb.Append(" mesh=" + Res(mesh));
        object lt = P(c, "LocalTransform"); if (lt != null) sb.Append(" pos=" + Vec(P(lt, "Position")) + " rot=" + Quat(P(lt, "Orientation")));
        object pt = P(c, "ParentTransform"); if (pt != null) { object h = P(pt, "Chunk") ?? pt; sb.Append(" parent=" + S(P(P(h, "BindName") ?? h, "Value") ?? P(h, "BindName")) + "/" + S(P(h, "SlotName"))); }
        return sb.ToString();
    }
    static string Vec(object p) { if (p == null) return ""; return Num(P(p, "X")) + ";" + Num(P(p, "Y")) + ";" + Num(P(p, "Z")); }
    static string Quat(object q) { if (q == null) return ""; return Num(P(q, "I")) + ";" + Num(P(q, "J")) + ";" + Num(P(q, "K")) + ";" + Num(P(q, "R")); }
    static string Num(object v)
    {
        if (v == null) return "?";
        object bits = P(v, "Bits");                          // FixedPoint (positions) : 1/131072 m
        if (bits != null) return (double.Parse(S(bits), System.Globalization.CultureInfo.InvariantCulture) / 131072.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        double d; return double.TryParse(S(v), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d) ? d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : S(v);
    }

    static int App(string file, string only)
    {
        object root = ReadCr2w(file);
        Console.WriteLine("classe : " + root.GetType().Name);
        foreach (object a in Items(P(root, "Appearances")))
        {
            object app = P(a, "Chunk") ?? a;
            string name = S(P(app, "Name"));
            if (only != null && name != only) { Console.WriteLine("APPARENCE " + name); continue; }
            Console.WriteLine("APPARENCE " + name + " (" + app.GetType().Name + ")");
            foreach (object c in Items(P(app, "Components")))
                Console.WriteLine("  " + c.GetType().Name + " " + S(P(c, "Name")) + Extra(c));
        }
        return 0;
    }
}
