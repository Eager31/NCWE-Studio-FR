// Outil autonome (sans interface) : noms officiels des points de fast travel.
// Lit la base du jeu (TweakDB) et les textes EN/FR avec les librairies WolvenKit livrees avec NCWE.
// Lancement : dotnet exec --runtimeconfig noms.runtimeconfig.json noms.exe <dossier NCWE> <dossier jeu> <entree.tsv> <sortie.tsv>
// entree : secteur, fiche, x, y, z, modele (fast-travel-bruts.tsv)

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

static class NomsOutil
{
    static string ncwe;

    static int Main(string[] args)
    {
        if (args.Length < 4) { Console.WriteLine("usage : noms <dossier NCWE> <dossier jeu> <entree.tsv> <sortie.tsv>"); return 1; }
        ncwe = args[0];
        AppDomain.CurrentDomain.AssemblyResolve += delegate (object s, ResolveEventArgs e)
        {
            string f = Path.Combine(ncwe, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(f) ? Assembly.LoadFrom(f) : null;
        };
        foreach (string dll in new[] { "WolvenKit.Core.dll", "WolvenKit.Common.dll", "WolvenKit.RED4.dll" }) Assembly.LoadFrom(Path.Combine(ncwe, dll));
        if (args.Length > 4 && args[4] == "sonde")
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!a.GetName().Name.StartsWith("WolvenKit")) continue;
                Type[] ts; try { ts = a.GetTypes(); } catch (ReflectionTypeLoadException e) { ts = Array.FindAll(e.Types, delegate (Type x) { return x != null; }); }
                foreach (Type t in ts)
                {
                    if (t.Name != "Oodle" && t.Name != "CompressionSettings" && t.Name != "OodleLZNative" && t.Name != "KrakenNative") continue;
                    Console.WriteLine("TYPE " + t.FullName);
                    foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    { var ps = new List<string>(); foreach (ParameterInfo p in m.GetParameters()) ps.Add(p.ParameterType.Name + " " + p.Name); Console.WriteLine("  " + (m.IsStatic ? "static " : "") + m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", ps.ToArray()) + ")"); }
                    foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)) Console.WriteLine("  prop " + p.PropertyType.Name + " " + p.Name);
                }
            }
            return 0;
        }
        try { return Run(args[1], args[2], args[3]); }
        catch (Exception e) { Console.WriteLine("ERREUR " + e); return 2; }
    }

    static Type T(string full)
    {
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) { Type t = a.GetType(full, false); if (t != null) return t; }
        throw new Exception("type introuvable : " + full);
    }
    static object P(object o, string n) { if (o == null) return null; PropertyInfo p = o.GetType().GetProperty(n); return p == null ? null : p.GetValue(o, null); }
    static string S(object o) { return o == null ? "" : o.ToString(); }

    static ulong Fnv(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (byte b in Encoding.UTF8.GetBytes(s.ToLowerInvariant())) { h ^= b; h *= 1099511628211UL; }
        return h;
    }

    static IEnumerable FindList(object o, string prop, int depth)
    {
        if (o == null || depth > 4) return null;
        var l = o as IList;
        if (l != null) return l.Count > 0 && l[0] != null && l[0].GetType().GetProperty(prop) != null ? l : null;
        if (o is string || o.GetType().IsPrimitive) return null;
        foreach (PropertyInfo p in o.GetType().GetProperties())
        {
            if (p.GetIndexParameters().Length > 0 || (p.DeclaringType.Namespace ?? "").StartsWith("System")) continue;
            object v; try { v = p.GetValue(o, null); } catch { continue; }
            IEnumerable r = FindList(v, prop, depth + 1);
            if (r != null) return r;
        }
        return null;
    }

    static Dictionary<ulong, string> Loc(string game, string lang, string folder)
    {
        var map = new Dictionary<ulong, string>();
        Type art = T("WolvenKit.RED4.Archive.IO.ArchiveReader"), cr = T("WolvenKit.RED4.Archive.IO.CR2WReader");
        Type hs = null;
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] ts; try { ts = a.GetTypes(); } catch (ReflectionTypeLoadException e) { ts = Array.FindAll(e.Types, delegate (Type x) { return x != null; }); }
            foreach (Type t in ts) if (t.Name == "HashService" && !t.IsAbstract) hs = t;
        }
        // service de hachage sans chargement des listes de noms (inutile ici : on cherche par hachage)
        object hash = null;
        if (hs != null)
        {
            ConstructorInfo light = hs.GetConstructor(new[] { typeof(bool) });
            try { hash = light != null ? light.Invoke(new object[] { false }) : null; } catch (Exception e) { Console.WriteLine("HashService(false) : " + e.GetBaseException().Message); }
        }
        MethodInfo read = null;
        foreach (MethodInfo m in cr.GetMethods()) if (m.Name == "ReadFile" && m.GetParameters().Length == 2) read = m;
        foreach (string sub in new[] { "content", "ep1" })
        {
            string arc = Path.Combine(game, @"archive\pc\" + sub + @"\lang_" + lang + "_text.archive");
            if (!File.Exists(arc)) continue;
            object[] a = { arc, hash, null };
            art.GetMethod("ReadArchive").Invoke(Activator.CreateInstance(art), a);
            object archive = a[2];
            var files = (IDictionary)P(archive, "Files");
            foreach (string root in new[] { "base", "ep1" })
            {
                ulong h = Fnv(root + @"\localization\" + folder + @"\onscreens\onscreens.json");
                if (!files.Contains(h)) continue;
                var ms = new MemoryStream();
                archive.GetType().GetMethod("ExtractFile").Invoke(archive, new object[] { files[h], ms });
                ms.Position = 0;
                object[] ra = { null, true };
                read.Invoke(Activator.CreateInstance(cr, new object[] { ms }), ra);
                IEnumerable entries = FindList(P(ra[0], "RootChunk"), "PrimaryKey", 0);
                if (entries == null) continue;
                foreach (object e in entries)
                {
                    string t = S(P(e, "FemaleVariant")); if (t.Length == 0) t = S(P(e, "MaleVariant"));
                    if (t.Length > 0) map[Convert.ToUInt64(S(P(e, "PrimaryKey")))] = t;
                }
            }
            archive.GetType().GetMethod("Dispose").Invoke(archive, null);
        }
        Console.WriteLine("textes " + lang + " : " + map.Count);
        return map;
    }

    static string Text(Dictionary<ulong, string> m, string key)
    {
        ulong n; string s;
        if (key.StartsWith("LocKey#") && ulong.TryParse(key.Substring(7), out n) && m.TryGetValue(n, out s)) return s;
        return key.StartsWith("LocKey#") ? "" : key;
    }

    static int Run(string game, string input, string output)
    {
        // decompression : DLL Oodle du jeu (comme NCWE)
        object ok = T("WolvenKit.Core.Compression.Oodle").GetMethod("Load").Invoke(null, new object[] { Path.Combine(game, @"bin\x64\oo2ext_7_win64.dll") });
        object settings = T("WolvenKit.Core.Compression.CompressionSettings").GetMethod("Get").Invoke(null, null);
        settings.GetType().GetProperty("UseOodle").SetValue(settings, true, null);
        Console.WriteLine("Oodle : " + ok);
        // base du jeu
        Type rt = T("WolvenKit.RED4.TweakDB.TweakDBReader");
        string file = Path.Combine(game, @"r6\cache\tweakdb_ep1.bin");
        if (!File.Exists(file)) file = Path.Combine(game, @"r6\cache\tweakdb.bin");
        object db;
        using (var fs = File.OpenRead(file))
        {
            object[] a = { null };
            rt.GetMethod("ReadFile").Invoke(Activator.CreateInstance(rt, new object[] { fs }), a);
            db = a[0];
        }
        MethodInfo flat = db.GetType().GetMethod("GetFlatValue");
        Func<string, string> F = delegate (string k)
        {
            try { object v = flat.Invoke(db, new object[] { k }); return v == null ? "" : S(v).Split(new[] { " <TweakDBID" }, StringSplitOptions.None)[0]; }
            catch { return ""; }
        };
        Console.WriteLine("base du jeu lue : " + file);
        var en = Loc(game, "en", "en-us"); var fr = Loc(game, "fr", "fr-fr");

        var sb = new StringBuilder("secteur\tfiche\tx\ty\tz\tmodele\tnom_en\tnom_fr\tquartier\tdescription\tmetro\tlockey\n");
        int n = 0, named = 0;
        foreach (string line in File.ReadAllLines(input, Encoding.UTF8))
        {
            string[] c = line.Split('\t');
            if (c.Length < 6 || c[0] == "secteur") continue;
            string rec = c[1], loc = rec.Length > 0 ? F(rec + ".displayName") : "";
            string nameEn = Text(en, loc);
            if (nameEn.Length > 0) named++;
            sb.Append(string.Join("\t", c, 0, 6)).Append('\t').Append(nameEn).Append('\t').Append(Text(fr, loc)).Append('\t')
              .Append(rec.Length > 0 ? F(rec + ".district") : "").Append('\t').Append(rec.Length > 0 ? F(rec + ".description") : "").Append('\t')
              .Append(rec.Length > 0 ? F(rec + ".subwayStation") : "").Append('\t').Append(loc).Append('\n');
            n++;
        }
        File.WriteAllText(output, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine(n + " points, " + named + " avec nom officiel -> " + output);
        return 0;
    }
}
