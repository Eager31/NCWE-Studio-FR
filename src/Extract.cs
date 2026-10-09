// Extraction unique des lieux du jeu (NCWE_FR_EXTRACT=1) : positions des marqueurs de carte
// (03_night_city.mappins / .poimappins) reliees aux entrees du journal (nom, type de marqueur).
// Ecrit lieux-bruts.tsv dans le dossier du plugin. Inactif en usage normal.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace NcweFr
{
    static partial class Plugin
    {
        static bool extractStarted;
        static readonly DateTime started = DateTime.Now;

        static void ExtractTick(object app)
        {
            if (extractStarted || Environment.GetEnvironmentVariable("NCWE_FR_EXTRACT") != "1") return;
            if ((DateTime.Now - started).TotalSeconds < 20) return;
            extractStarted = true;
            object index = FindObjectOfType(app, "GameArchiveIndex");
            var t = new Thread(delegate () { try { ExtractPlaces(index); } catch (Exception e) { Log("extract: " + e); } });
            t.IsBackground = true; t.Start();
        }

        static object FindObjectOfType(object root, string typeName)
        {
            var seen = new HashSet<object>(new RefEq());
            var q = new Queue<KeyValuePair<object, int>>();
            q.Enqueue(new KeyValuePair<object, int>(root, 0));
            foreach (object w in windows) q.Enqueue(new KeyValuePair<object, int>(w, 0));
            while (q.Count > 0)
            {
                var it = q.Dequeue(); object o = it.Key;
                if (o == null || !seen.Add(o)) continue;
                if (o.GetType().Name == typeName) return o;
                if (it.Value >= 6) continue;
                if (!(o.GetType().Namespace ?? "").StartsWith("NCWE")) continue;
                for (Type t = o.GetType(); t != null && (t.Namespace ?? "").StartsWith("NCWE"); t = t.BaseType)
                    foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (f.FieldType.IsValueType || f.FieldType == typeof(string)) continue;
                        object v; try { v = f.GetValue(o); } catch { continue; }
                        if (v != null) q.Enqueue(new KeyValuePair<object, int>(v, it.Value + 1));
                    }
            }
            return null;
        }

        static object GetAny(object o, string name)
        {
            if (o == null) return null;
            PropertyInfo p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            return p == null ? null : p.GetValue(o, null);
        }

        static string Str(object o) { return o == null ? "" : o.ToString(); }

        // ---------- hachages candidats pour JournalPathHash ----------
        static uint Fnv1a32(string s) { uint h = 2166136261; foreach (byte b in Encoding.UTF8.GetBytes(s)) { h ^= b; h *= 16777619; } return h; }
        static uint Crc32(string s)
        {
            uint c = 0xFFFFFFFF;
            foreach (byte b in Encoding.UTF8.GetBytes(s)) { c ^= b; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320 : c >> 1; }
            return ~c;
        }
        static uint Murmur3(string s, uint seed)
        {
            byte[] d = Encoding.UTF8.GetBytes(s); uint h = seed; int n = d.Length / 4;
            for (int i = 0; i < n; i++)
            {
                uint k = BitConverter.ToUInt32(d, i * 4); k *= 0xcc9e2d51; k = (k << 15) | (k >> 17); k *= 0x1b873593;
                h ^= k; h = (h << 13) | (h >> 19); h = h * 5 + 0xe6546b64;
            }
            uint t = 0; int r = d.Length & 3, o = n * 4;
            if (r == 3) t ^= (uint)d[o + 2] << 16; if (r >= 2) t ^= (uint)d[o + 1] << 8;
            if (r >= 1) { t ^= d[o]; t *= 0xcc9e2d51; t = (t << 15) | (t >> 17); t *= 0x1b873593; h ^= t; }
            h ^= (uint)d.Length; h ^= h >> 16; h *= 0x85ebca6b; h ^= h >> 13; h *= 0xc2b2ae35; h ^= h >> 16; return h;
        }

        class JEntry { public string Path, Type, Mappin, Title, Extra; }
        class Pin { public uint Hash; public double X, Y, Z; public string Source; }

        static void ExtractPlaces(object index)
        {
            if (index == null) { Log("extract: index introuvable"); return; }
            if (Environment.GetEnvironmentVariable("NCWE_FR_TDB") == "probe") { ProbeTweakDb(index); return; }
            if (Environment.GetEnvironmentVariable("NCWE_FR_TDB") == "instance") { ProbeInstance(index); return; }
            if (Environment.GetEnvironmentVariable("NCWE_FR_TDB") == "record") { ProbeRecord(index); return; }
            if (Environment.GetEnvironmentVariable("NCWE_FR_TDB") == "names") { ResolveNames(index); return; }
            string probe = Environment.GetEnvironmentVariable("NCWE_FR_PROBE");
            if (!string.IsNullOrEmpty(probe)) { ProbeSector(index, probe); return; }
            if (Environment.GetEnvironmentVariable("NCWE_FR_EXTRACT_FT") == "1") { ScanFastTravel(index); return; }
            string pattern = Environment.GetEnvironmentVariable("NCWE_FR_SCAN");
            if (!string.IsNullOrEmpty(pattern)) { ScanPattern(index, pattern); return; }
            MethodInfo read = index.GetType().GetMethod("ReadFile");
            Func<string, object> Root = delegate (string p) { return GetAny(read.Invoke(index, new object[] { p }), "RootChunk"); };

            // 1) positions
            var pins = new List<Pin>();
            object mp = Root(@"base\worlds\03_night_city\_compiled\default\03_night_city.mappins");
            foreach (object d in (IEnumerable)GetAny(mp, "CookedData")) AddPin(pins, GetAny(d, "JournalPathHash"), GetAny(d, "Position"), "mappin");
            foreach (object d in (IEnumerable)GetAny(mp, "CookedMultiData")) foreach (object p in (IEnumerable)GetAny(d, "Positions")) AddPin(pins, GetAny(d, "JournalPathHash"), p, "multi");
            foreach (object d in (IEnumerable)GetAny(mp, "CookedGpsData")) foreach (object p in (IEnumerable)GetAny(d, "Positions")) AddPin(pins, GetAny(d, "JournalPathHash"), p, "gps");
            object pp = Root(@"base\worlds\03_night_city\_compiled\default\03_night_city.poimappins");
            foreach (object d in (IEnumerable)GetAny(pp, "CookedData")) AddPin(pins, GetAny(d, "JournalPathHash"), GetAny(d, "Position"), "poi");
            var hashes = new HashSet<uint>(); foreach (Pin p in pins) hashes.Add(p.Hash);
            Log("extract: " + pins.Count + " marqueurs, " + hashes.Count + " codes");

            // 2) journal
            var entries = new List<JEntry>();
            foreach (string j in new[] { @"base\journal\cooked_journal.journal", @"ep1\journal\cooked_journal.journal" })
            {
                object root; try { root = Root(j); } catch { continue; }
                Walk(GetAny(GetAny(root, "Entry"), "Chunk"), "", entries, 0);
            }
            Log("extract: " + entries.Count + " entrees de journal");

            // 3) quel hachage ?
            var funcs = new Dictionary<string, Func<string, uint>>();
            funcs["fnv1a32"] = Fnv1a32; funcs["crc32"] = Crc32;
            funcs["murmur0"] = delegate (string s) { return Murmur3(s, 0); };
            funcs["murmurSeed"] = delegate (string s) { return Murmur3(s, 0x5EEDBA5E); };
            string best = null; int bestN = -1; bool bestTrim = false;
            foreach (var f in funcs)
                foreach (bool trim in new[] { false, true })
                {
                    int n = 0;
                    foreach (JEntry e in entries) if (hashes.Contains(f.Value(trim ? TrimRoot(e.Path) : e.Path))) n++;
                    Log("extract: hachage " + f.Key + (trim ? " (sans racine)" : "") + " -> " + n);
                    if (n > bestN) { bestN = n; best = f.Key; bestTrim = trim; }
                }
            var byHash = new Dictionary<uint, JEntry>();
            foreach (JEntry e in entries) byHash[funcs[best](bestTrim ? TrimRoot(e.Path) : e.Path)] = e;

            // 4) sortie
            var sb = new StringBuilder("source\tx\ty\tz\tchemin\ttype\tmappin\ttitre\textra\n");
            int matched = 0;
            foreach (Pin p in pins)
            {
                JEntry e; byHash.TryGetValue(p.Hash, out e);
                if (e != null) matched++;
                sb.Append(p.Source).Append('\t').Append(F(p.X)).Append('\t').Append(F(p.Y)).Append('\t').Append(F(p.Z)).Append('\t')
                  .Append(e == null ? "?" + p.Hash : e.Path).Append('\t').Append(e == null ? "" : e.Type).Append('\t')
                  .Append(e == null ? "" : e.Mappin).Append('\t').Append(e == null ? "" : e.Title).Append('\t').Append(e == null ? "" : e.Extra).Append('\n');
            }
            File.WriteAllText(Path.Combine(dir, "lieux-bruts.tsv"), sb.ToString(), new UTF8Encoding(false));
            Log("extract: termine, " + matched + "/" + pins.Count + " marqueurs relies (" + best + ")");
        }

        // Bornes de fast travel : parcours de tous les secteurs, position exacte du noeud dans le secteur.
        static void ScanFastTravel(object index)
        {
            Type it = index.GetType();
            var sectors = (Array)it.GetProperty("Sectors").GetValue(index, null);
            MethodInfo inspect = it.GetMethod("InspectEntities"), dumpNodes = it.GetMethod("DumpNodes");
            MethodInfo dumpInst = it.GetMethod("DumpInstanceData");
            var rows = new List<string[]>();          // secteur, fiche, x, y, z, modele
            var seenRecord = new HashSet<string>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int n = 0, found = 0, failed = 0;
            var seenPos = new HashSet<string>();
            foreach (object s in sectors)
            {
                n++;
                if (n % 2000 == 0) Log("extract ft: " + n + "/" + sectors.Length + " secteurs, " + found + " bornes, " + sw.Elapsed.TotalMinutes.ToString("0.0") + " min");
                string path = Str(GetAny(s, "Path"));
                ulong hash = Convert.ToUInt64(Str(GetAny(s, "Hash")));
                var nodes = new List<KeyValuePair<int, string>>();
                try
                {
                    foreach (object line in (IEnumerable)inspect.Invoke(index, new object[] { hash, "fast_travel" }))
                    {
                        string l = Str(line);
                        if (l.IndexOf("data_term", StringComparison.OrdinalIgnoreCase) < 0 && l.IndexOf("DataTerm", StringComparison.Ordinal) < 0) continue;
                        int h = l.IndexOf(" #"); if (h < 0) continue;
                        int e = l.IndexOf(' ', h + 2);
                        int idx; if (!int.TryParse(l.Substring(h + 2, e - h - 2), out idx)) continue;
                        int ent = l.IndexOf(".ent"); int st = ent < 0 ? -1 : l.LastIndexOf(' ', ent);
                        nodes.Add(new KeyValuePair<int, string>(idx, ent < 0 ? "" : l.Substring(st + 1, ent + 4 - st - 1)));
                    }
                }
                catch { continue; }
                if (nodes.Count == 0) continue;
                // positions : lecteur de secteurs de NCWE (DumpNodes) -> "... pos (x,y,z) ..." puis "EntityTemplate = ..."
                try
                {
                    string pos = null; int placement = -1;
                    var hits = new List<KeyValuePair<int, string[]>>();
                    foreach (object lo in (IEnumerable)dumpNodes.Invoke(index, new object[] { hash, "worldEntityNode", 100000 }))
                    {
                        string l = Str(lo);
                        int pi = l.IndexOf(" pos (");
                        if (l.StartsWith("---") && pi > 0)
                        {
                            pos = l.Substring(pi + 6, l.IndexOf(')', pi) - pi - 6);
                            int pl = l.IndexOf("placement "); placement = -1;
                            if (pl > 0) int.TryParse(l.Substring(pl + 10).Split(' ')[0], out placement);
                            continue;
                        }
                        if (pos != null && l.TrimStart().StartsWith("EntityTemplate") && l.IndexOf("fast_travel", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            string[] c = pos.Split(',');
                            hits.Add(new KeyValuePair<int, string[]>(placement, new[] { F(D(c[0])), F(D(c[1])), F(D(c[2])), l.Substring(l.IndexOf('=') + 1).Trim() }));
                            pos = null;
                        }
                    }
                    foreach (var h in hits)
                    {
                        // fiche officielle de la borne : premiere ligne "PointRecord = FastTravelPoints.xxx"
                        string record = "";
                        try
                        {
                            foreach (object lo in (IEnumerable)dumpInst.Invoke(index, new object[] { hash, h.Key, 6 }))
                            {
                                string l = Str(lo).Trim();
                                if (l.StartsWith("PointRecord = ")) { record = l.Substring(14).Split(' ')[0]; break; }
                            }
                        }
                        catch { }
                        string key = record.Length > 0 ? record : Math.Round(D(h.Value[0])) + "," + Math.Round(D(h.Value[1]));
                        if (!seenRecord.Add(key)) continue;   // une ligne par point officiel
                        rows.Add(new[] { path, record, h.Value[0], h.Value[1], h.Value[2], h.Value[3] });
                        found++;
                    }
                }
                catch (Exception ex) { failed++; Log("extract ft: secteur illisible " + path + " : " + ex.GetBaseException().Message); }
            }
            var raw = new StringBuilder("secteur\tfiche\tx\ty\tz\tmodele\n");
            foreach (string[] r in rows) raw.Append(string.Join("\t", r)).Append('\n');
            File.WriteAllText(Path.Combine(dir, "fast-travel-bruts.tsv"), raw.ToString(), new UTF8Encoding(false));
            Log("extract ft: termine, " + found + " bornes, " + failed + " secteurs illisibles, en " + sw.Elapsed.TotalMinutes.ToString("0.0") + " min");
        }

        // Etape 2 (NCWE_FR_TDB=names, au demarrage de NCWE quand la memoire est libre) :
        // fiche TweakDB -> LocKey -> textes EN / FR, ajoutes a fast-travel-bruts.tsv -> fast-travel-noms.tsv
        static void ResolveNames(object index)
        {
            Type it = index.GetType();
            var rows = new List<string[]>();
            foreach (string line in File.ReadAllLines(Path.Combine(dir, "fast-travel-bruts.tsv"), Encoding.UTF8))
            {
                string[] c = line.Split('\t');
                if (c.Length >= 6 && c[0] != "secteur") rows.Add(new[] { c[0], c[1], c[2], c[3], c[4], c[5] });
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int found = rows.Count, failed = 0;
            string game = Str(it.GetProperty("GameDirectory").GetValue(index, null));
            object db = null; MethodInfo getFlat = null;
            try { db = LoadTweakDb(game); getFlat = db.GetType().GetMethod("GetFlatValue"); } catch (Exception e) { Log("tweakdb: " + e.GetBaseException().Message); }
            Dictionary<ulong, string> en = new Dictionary<ulong, string>(), fr = new Dictionary<ulong, string>();
            try { en = LoadLocalization(game, "en", "en-us"); } catch (Exception e) { Log("loc en: " + e.GetBaseException()); }
            try { fr = LoadLocalization(game, "fr", "fr-fr"); } catch (Exception e) { Log("loc fr: " + e.GetBaseException()); }
            Func<string, string> Flat = delegate (string k)
            {
                if (getFlat == null) return "";
                try { object v = getFlat.Invoke(db, new object[] { k }); return v == null ? "" : Str(v).Split(new[] { " <TweakDBID" }, StringSplitOptions.None)[0]; } catch { return ""; }
            };
            var sb = new StringBuilder("secteur\tfiche\tx\ty\tz\tmodele\tnom_en\tnom_fr\tquartier\tdescription\tmetro\tlockey\n");
            foreach (string[] r in rows)
            {
                string rec = r[1];
                string loc = rec.Length > 0 ? Flat(rec + ".displayName") : "";
                sb.Append(string.Join("\t", r)).Append('\t').Append(LocText(en, loc)).Append('\t').Append(LocText(fr, loc)).Append('\t')
                  .Append(rec.Length > 0 ? Flat(rec + ".district") : "").Append('\t').Append(rec.Length > 0 ? Flat(rec + ".description") : "").Append('\t')
                  .Append(rec.Length > 0 ? Flat(rec + ".subwayStation") : "").Append('\t').Append(loc).Append('\n');
            }
            File.WriteAllText(Path.Combine(dir, "fast-travel-noms.tsv"), sb.ToString(), new UTF8Encoding(false));
            db = null; en = null; fr = null; GC.Collect();
            Log("extract noms: termine, " + found + " bornes, " + failed + " secteurs illisibles, en " + sw.Elapsed.TotalMinutes.ToString("0.0") + " min");
        }

        // Recherche generique (NCWE_FR_SCAN=mot1|mot2) : position de tout noeud dont la description contient un des mots.
        // Lecteur de secteurs de NCWE uniquement (DumpNodes), secteurs exterieurs/interieurs. Sortie : scan-<mot>.tsv
        static void ScanPattern(object index, string pattern)
        {
            Type it = index.GetType();
            var sectors = (Array)it.GetProperty("Sectors").GetValue(index, null);
            MethodInfo dumpNodes = it.GetMethod("DumpNodes");
            string[] words = pattern.ToLowerInvariant().Split('|');
            string[] types = { "worldStaticMeshNode", "worldMeshNode", "worldInstancedMeshNode", "worldEntityNode", "worldStaticDecalNode" };
            var sb = new StringBuilder("secteur\ttype\tx\ty\tz\tressource\n");
            var seen = new HashSet<string>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int n = 0, found = 0, failed = 0;
            foreach (object s in sectors)
            {
                n++;
                if (n % 2000 == 0) Log("scan " + pattern + ": " + n + "/" + sectors.Length + " secteurs, " + found + " trouves, " + sw.Elapsed.TotalMinutes.ToString("0.0") + " min");
                string path = Str(GetAny(s, "Path"));
                if (path.IndexOf("exterior", StringComparison.OrdinalIgnoreCase) < 0 && path.IndexOf("interior", StringComparison.OrdinalIgnoreCase) < 0) continue;
                ulong hash = Convert.ToUInt64(Str(GetAny(s, "Hash")));
                foreach (string type in types)
                {
                    try
                    {
                        string pos = null;
                        foreach (object lo in (IEnumerable)dumpNodes.Invoke(index, new object[] { hash, type, 100000 }))
                        {
                            string l = Str(lo);
                            int pi = l.IndexOf(" pos (");
                            if (l.StartsWith("---")) pos = pi > 0 ? l.Substring(pi + 6, l.IndexOf(')', pi) - pi - 6) : null;
                            string low = l.ToLowerInvariant();
                            bool hit = false;
                            foreach (string w in words) if (low.IndexOf(w, StringComparison.Ordinal) >= 0) { hit = true; break; }
                            if (!hit || pos == null) continue;
                            string res = l.Trim();
                            if (!seen.Add(pos + "|" + res)) continue;
                            string[] c = pos.Split(',');
                            sb.Append(path).Append('\t').Append(type).Append('\t').Append(F(D(c[0]))).Append('\t').Append(F(D(c[1]))).Append('\t').Append(F(D(c[2]))).Append('\t').Append(res.Replace('\t', ' ')).Append('\n');
                            found++;
                        }
                    }
                    catch (Exception ex) { if (++failed < 20) Log("scan: " + path + " " + type + " : " + ex.GetBaseException().Message); }
                }
            }
            string name = "scan-" + words[0] + ".tsv";
            File.WriteAllText(Path.Combine(dir, name), sb.ToString(), new UTF8Encoding(false));
            Log("scan " + pattern + ": termine, " + found + " trouves, " + failed + " erreurs, en " + sw.Elapsed.TotalMinutes.ToString("0.0") + " min -> " + name);
        }

        static string Members(Type t)
        {
            var sb = new StringBuilder("TYPE " + t.FullName + "\n");
            foreach (ConstructorInfo c in t.GetConstructors()) { var ps = new List<string>(); foreach (ParameterInfo p in c.GetParameters()) ps.Add(p.ParameterType.Name); sb.AppendLine("  ctor(" + string.Join(", ", ps.ToArray()) + ")"); }
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)) { var ps = new List<string>(); foreach (ParameterInfo p in m.GetParameters()) ps.Add(p.ParameterType.Name + (p.IsOut ? "&" : "")); sb.AppendLine("  " + (m.IsStatic ? "static " : "") + m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", ps.ToArray()) + ")"); }
            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)) sb.AppendLine("  prop " + p.PropertyType.Name + " " + p.Name);
            return sb.ToString();
        }

        static Type FindTypeByName(string full)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) { Type t = a.GetType(full, false); if (t != null) return t; }
            return null;
        }

        static object LoadTweakDb(string gameDir)
        {
            Type rt = FindTypeByName("WolvenKit.RED4.TweakDB.TweakDBReader");
            string file = Path.Combine(gameDir, @"r6\cache\tweakdb_ep1.bin");
            if (!File.Exists(file)) file = Path.Combine(gameDir, @"r6\cache\tweakdb.bin");
            using (var fs = File.OpenRead(file))
            {
                object reader = Activator.CreateInstance(rt, new object[] { fs });
                MethodInfo rf = rt.GetMethod("ReadFile");
                object[] args = { null };
                object code = rf.Invoke(reader, args);
                Log("tweakdb: " + file + " -> " + code);
                return args[0];
            }
        }

        // Textes du jeu d'une langue : cle numerique (LocKey#N) -> texte. Lit lang_<lang>_text.archive avec WolvenKit.
        static Dictionary<ulong, string> LoadLocalization(string gameDir, string lang, string locFolder)
        {
            var map = new Dictionary<ulong, string>();
            foreach (string folder in new[] { "content", "ep1" })
            {
                string arc = Path.Combine(gameDir, @"archive\pc\" + folder + @"\lang_" + lang + "_text.archive");
                if (File.Exists(arc)) LoadLocArchive(arc, lang, locFolder, map);
            }
            Log("loc: " + lang + " " + map.Count + " textes");
            return map;
        }

        static void LoadLocArchive(string arc, string lang, string locFolder, Dictionary<ulong, string> map)
        {
            Type art = FindTypeByName("WolvenKit.RED4.Archive.IO.ArchiveReader");
            Type hashSvcType = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] ts; try { ts = a.GetTypes(); } catch (ReflectionTypeLoadException e) { ts = Array.FindAll(e.Types, delegate (Type x) { return x != null; }); }
                foreach (Type t in ts) if (t.Name == "HashService" && !t.IsAbstract) { hashSvcType = t; break; }
                if (hashSvcType != null) break;
            }
            object hashSvc = null;
            try { if (hashSvcType != null) hashSvc = Activator.CreateInstance(hashSvcType); } catch (Exception e) { Log("loc: HashService " + e.GetBaseException().Message); }
            object reader = Activator.CreateInstance(art);
            object[] args = { arc, hashSvc, null };
            object code = art.GetMethod("ReadArchive").Invoke(reader, args);
            object archive = args[2];
            if (archive == null) { Log("loc: lecture archive " + code); return; }
            var files = (IDictionary)GetAny(archive, "Files");
            Type cr = FindTypeByName("WolvenKit.RED4.Archive.IO.CR2WReader");
            foreach (string sub in new[] { "base", "ep1" })
            {
                ulong h = Fnv1a64(sub + @"\localization\" + locFolder + @"\onscreens\onscreens.json");
                if (!files.Contains(h)) { Log("loc: " + sub + " onscreens absent (" + lang + ")"); continue; }
                var ms = new MemoryStream();
                archive.GetType().GetMethod("ExtractFile").Invoke(archive, new object[] { files[h], ms });
                ms.Position = 0;
                object crr = Activator.CreateInstance(cr, new object[] { ms });
                object[] rargs = { null, true };
                MethodInfo rf = null;
                foreach (MethodInfo m in cr.GetMethods()) if (m.Name == "ReadFile" && m.GetParameters().Length == 2) rf = m;
                rf.Invoke(crr, rargs);
                object root = GetAny(rargs[0], "RootChunk");
                IEnumerable entries = FindList(root, "PrimaryKey", 0);
                if (entries == null) continue;
                foreach (object e in entries)
                {
                    ulong key = Convert.ToUInt64(Str(GetAny(e, "PrimaryKey")));
                    string text = Str(GetAny(e, "FemaleVariant"));
                    if (text.Length == 0) text = Str(GetAny(e, "MaleVariant"));
                    if (text.Length > 0) map[key] = text;
                }
            }
        }

        static string LocText(Dictionary<ulong, string> map, string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            ulong n; string s;
            if (key.StartsWith("LocKey#") && ulong.TryParse(key.Substring(7), out n) && map.TryGetValue(n, out s)) return s;
            return key.StartsWith("LocKey#") ? "" : key;
        }

        static void ProbeRecord(object index)
        {
            var sb = new StringBuilder();
            string game = Str(index.GetType().GetProperty("GameDirectory").GetValue(index, null));
            object db = LoadTweakDb(game);
            MethodInfo getFlat = db.GetType().GetMethod("GetFlatValue");
            foreach (string id in new[] { "FastTravelPoints.std_arr_metro_ftp_04.displayName", "FastTravelPoints.std_arr_metro_ftp_04.district", "FastTravelPoints.std_arr_metro_ftp_04.description" })
            {
                object v = null; try { v = getFlat.Invoke(db, new object[] { id }); } catch (Exception e) { v = "err " + e.GetBaseException().Message; }
                sb.AppendLine(id + " = " + (v == null ? "null" : v.GetType().Name + " : " + v));
            }
            try
            {
                var en = LoadLocalization(game, "en", "en-us"); var fr = LoadLocalization(game, "fr", "fr-fr");
                sb.AppendLine("EN = " + LocText(en, "LocKey#52556") + " | FR = " + LocText(fr, "LocKey#52556") + " | textes EN " + en.Count + ", FR " + fr.Count);
            }
            catch (Exception e) { sb.AppendLine("loc err " + e.GetBaseException()); }
            File.WriteAllText(Path.Combine(dir, "extract-record.txt"), sb.ToString());
            Log("extract: sonde fiche ecrite");
            if (sb.Length > 0) return;
            // types utiles pour lire les archives de langue
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!a.GetName().Name.StartsWith("WolvenKit") && !a.GetName().Name.StartsWith("NCWE")) continue;
                Type[] ts; try { ts = a.GetTypes(); } catch (ReflectionTypeLoadException e) { ts = Array.FindAll(e.Types, delegate (Type x) { return x != null; }); }
                foreach (Type t in ts)
                    if (t.IsPublic && (t.Name == "Parser" || t.Name == "Archive" || t.Name == "ArchiveReader" || t.Name == "ArchiveManager" || t.Name == "LocalizationPersistenceOnScreenEntries" || t.Name == "localizationPersistenceOnScreenEntry"))
                        sb.AppendLine(a.GetName().Name + " :: " + Members(t));
            }
            File.WriteAllText(Path.Combine(dir, "extract-record.txt"), sb.ToString());
            Log("extract: sonde fiche ecrite");
        }

        // Donnees d'instance d'une borne de fast travel (pour trouver sa fiche TweakDB).
        static void ProbeInstance(object index)
        {
            Type it = index.GetType();
            string path = @"base\worlds\03_night_city\_compiled\default\exterior_-17_-23_0_0.streamingsector";
            ulong hash = Fnv1a64(path);
            var sb = new StringBuilder();
            int placement = -1;
            foreach (object lo in (IEnumerable)it.GetMethod("DumpNodes").Invoke(index, new object[] { hash, "worldEntityNode", 100000 }))
            {
                string l = Str(lo);
                if (l.StartsWith("---")) { int p = l.IndexOf("placement "); if (p > 0) int.TryParse(l.Substring(p + 10).Split(' ')[0], out placement); sb.AppendLine(l); }
                else if (l.IndexOf("fast_travel", StringComparison.OrdinalIgnoreCase) >= 0) { sb.AppendLine(l); break; }
            }
            sb.AppendLine("placement = " + placement);
            int n = 0;
            foreach (object lo in (IEnumerable)it.GetMethod("DumpInstanceData").Invoke(index, new object[] { hash, placement, 12 }))
            { sb.AppendLine(Str(lo)); if (++n > 400) break; }
            File.WriteAllText(Path.Combine(dir, "extract-instance.txt"), sb.ToString());
            Log("extract: sonde instance ecrite");
        }

        static void ProbeTweakDb(object index)
        {
            var sb = new StringBuilder();
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!a.GetName().Name.StartsWith("WolvenKit")) continue;
                Type[] ts; try { ts = a.GetTypes(); } catch (ReflectionTypeLoadException e) { ts = Array.FindAll(e.Types, delegate (Type x) { return x != null; }); }
                foreach (Type t in ts)
                    if (t.Name == "TweakDBReader" || t.Name == "TweakDB" || t.Name == "TweakDBIDPool" || t.Name == "TweakDBService" || t.Name == "gamedataFastTravelPoint_Record" || t.Name == "TweakDBStringHelper")
                        sb.AppendLine(a.GetName().Name + " :: " + Members(t));
            }
            MethodInfo contains = index.GetType().GetMethod("Contains");
            foreach (string p in new[] { @"base\localization\en-us\onscreens\onscreens.json", @"ep1\localization\en-us\onscreens\onscreens.json" })
                sb.AppendLine("archive " + p + " : " + contains.Invoke(index, new object[] { p }));
            File.WriteAllText(Path.Combine(dir, "extract-tdb.txt"), sb.ToString());
            Log("extract: sonde tweakdb ecrite");
        }

        static ulong Fnv1a64(string path)
        {
            ulong h = 14695981039346656037UL;
            foreach (byte b in Encoding.UTF8.GetBytes(path.ToLowerInvariant().Replace('/', '\\'))) { h ^= b; h *= 1099511628211UL; }
            return h;
        }

        static void ProbeSector(object index, string path)
        {
            Type it = index.GetType();
            ulong hash = Fnv1a64(path);
            var sb = new StringBuilder("sonde " + path + " hash " + hash + "\n");
            foreach (string m in new[] { "InspectEntities", "DumpNodes" })
            {
                MethodInfo mi = it.GetMethod(m);
                string ptype = Environment.GetEnvironmentVariable("NCWE_FR_PROBE_TYPE") ?? "worldEntityNode";
                object[] args = m == "InspectEntities" ? new object[] { hash, "fast_travel" } : new object[] { hash, ptype, 2000 };
                sb.AppendLine("== " + m);
                try { int k = 0; foreach (object l in (IEnumerable)mi.Invoke(index, args)) { string s = Str(l); if (m == "InspectEntities" || s.IndexOf("data_term", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("ncpd", StringComparison.OrdinalIgnoreCase) >= 0 || k < 12) sb.AppendLine(s); k++; } sb.AppendLine("lignes: " + k); }
                catch (Exception e) { sb.AppendLine("err " + e.GetBaseException().Message); }
            }
            // lecteur propre de NCWE
            try
            {
                MethodInfo rs = it.GetMethod("ReadSectorConcurrentAsync");
                object task = rs.Invoke(index, new object[] { hash, System.Threading.CancellationToken.None, (long)512 * 1024 * 1024 });
                object res = task.GetType().GetProperty("Result").GetValue(task, null);
                sb.AppendLine("== ReadSectorConcurrentAsync -> " + (res == null ? "null" : res.GetType().FullName));
                if (res != null) foreach (PropertyInfo p in res.GetType().GetProperties()) { object v = null; try { v = p.GetValue(res, null); } catch { } sb.AppendLine("  ." + p.Name + " : " + p.PropertyType.Name + (v is ICollection ? " [" + ((ICollection)v).Count + "]" : "")); }
            }
            catch (Exception e) { sb.AppendLine("ReadSector err " + e.GetBaseException().Message); }
            File.WriteAllText(Path.Combine(dir, "extract-probe.txt"), sb.ToString());
            Log("extract: sonde ecrite");
        }

        static double D(object v) { return Convert.ToDouble(Str(v), CultureInfo.InvariantCulture); }

        // Cherche (profondeur 4) une liste dont les elements ont la propriete donnee.
        static IEnumerable FindList(object o, string prop, int depth)
        {
            if (o == null || depth > 4) return null;
            var l = o as IList;
            if (l != null) { if (l.Count > 0 && l[0] != null && l[0].GetType().GetProperty(prop) != null) return l; return null; }
            if (o is string || o.GetType().IsPrimitive) return null;
            foreach (PropertyInfo p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0 || (p.DeclaringType.Namespace ?? "").StartsWith("System")) continue;
                object v; try { v = p.GetValue(o, null); } catch { continue; }
                IEnumerable r = FindList(v, prop, depth + 1);
                if (r != null) return r;
            }
            return null;
        }

        static string TrimRoot(string p) { int i = p.IndexOf('/'); return i < 0 ? p : p.Substring(i + 1); }
        static string F(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        static void AddPin(List<Pin> pins, object hash, object pos, string src)
        {
            if (hash == null || pos == null) return;
            pins.Add(new Pin
            {
                Hash = Convert.ToUInt32(Str(hash)), Source = src,
                X = Convert.ToDouble(Str(GetAny(pos, "X")), CultureInfo.InvariantCulture),
                Y = Convert.ToDouble(Str(GetAny(pos, "Y")), CultureInfo.InvariantCulture),
                Z = Convert.ToDouble(Str(GetAny(pos, "Z")), CultureInfo.InvariantCulture)
            });
        }

        // Parcours recursif du journal : chemin = ids separes par '/'.
        static void Walk(object entry, string parent, List<JEntry> outList, int depth)
        {
            if (entry == null || depth > 30) return;
            string id = Str(GetAny(entry, "Id"));
            string path = parent.Length == 0 ? id : (id.Length == 0 ? parent : parent + "/" + id);
            var e = new JEntry { Path = path, Type = entry.GetType().Name };
            // type de marqueur (TweakDBID) et titre si presents
            object md = GetAny(entry, "MappinData");
            if (md != null) e.Mappin = Str(GetAny(GetAny(md, "MappinType"), "ResolvedText"));
            object title = GetAny(entry, "Title");
            if (title != null) e.Title = Str(GetAny(title, "Value"));
            object name = GetAny(entry, "Caption") ?? GetAny(entry, "Description");
            if (name != null && GetAny(name, "Value") != null) e.Extra = Str(GetAny(name, "Value"));
            object poiType = GetAny(entry, "PoiType") ?? GetAny(entry, "Type");
            if (poiType != null && string.IsNullOrEmpty(e.Extra)) e.Extra = Str(poiType);
            outList.Add(e);
            object children = GetAny(entry, "Entries");
            if (children is IEnumerable)
                foreach (object h in (IEnumerable)children) Walk(GetAny(h, "Chunk"), path, outList, depth + 1);
        }
    }
}
