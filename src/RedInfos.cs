// redtool : infos de la bibliotheque (tailles, durees, boucles), lues dans les fichiers du jeu
// avec l'index d'archives de NCWE (NCWE.Formats.GameArchiveIndex) et WolvenKit.
//   dump  <dossier NCWE> <dossier jeu> <chemin depot> [profondeur] : arbre des proprietes d'un fichier
//   infos <dossier NCWE> <dossier jeu> <dossier sortie> [max]      : ecrit
//       tailles-modeles.tsv : chemin .mesh, largeur X, profondeur Y, hauteur Z (m)
//       effets.tsv          : chemin .particle/.effect, boucle (oui/non), duree (s), taille approx. X;Y;Z (m)
//       sons.tsv            : evenement, boucle (oui/non), portee (m), duree min, duree max (s)

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

static partial class RedTool
{
    static object archiveIndex;
    static string gameDir;
    static MethodInfo readFileByPath;

    static void OpenIndex(string game)
    {
        Assembly.LoadFrom(Path.Combine(ncwe, "NCWE.Formats.dll"));
        Type t = T("NCWE.Formats.GameArchiveIndex");
        archiveIndex = Activator.CreateInstance(t, new object[] { game, true });
        MethodInfo build = t.GetMethod("BuildAsync");
        var args = new object[build.GetParameters().Length];
        for (int i = 0; i < args.Length; i++) if (build.GetParameters()[i].ParameterType == typeof(CancellationToken)) args[i] = CancellationToken.None;
        object task = build.Invoke(archiveIndex, args);
        task.GetType().GetMethod("Wait", Type.EmptyTypes).Invoke(task, null);
        foreach (MethodInfo m in t.GetMethods())
            if (m.Name == "ReadFile" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string)) readFileByPath = m;
    }

    static object ReadRoot(string depotPath)
    {
        object f = readFileByPath.Invoke(archiveIndex, new object[] { depotPath });
        return f == null ? null : (P(f, "RootChunk") ?? f);
    }

    static List<string> PathsWith(params string[] ext)
    {
        var res = new List<string>();
        object r = archiveIndex.GetType().GetMethod("ResourcesWithExtension").Invoke(archiveIndex, new object[] { ext });
        foreach (object a in Items(r)) res.Add(S(P(a, "Path")));
        res.Sort(StringComparer.OrdinalIgnoreCase);
        return res;
    }

    // ---------- dump ----------
    static int DumpCmd(string game, string path, int depth)
    {
        OpenIndex(game);
        object root = ReadRoot(path);
        if (root == null) { Console.WriteLine("introuvable : " + path); return 1; }
        var seen = new HashSet<object>();
        DumpValue(root, "", depth, seen);
        return 0;
    }

    static readonly string[] skipProps = { "Chunks", "RenderResourceBlob", "Buffer", "Data", "Seeds" };

    static void DumpValue(object v, string indent, int depth, HashSet<object> seen)
    {
        if (v == null) return;
        Type t = v.GetType();
        if (depth <= 0 || !seen.Add(v)) return;
        if (v is IEnumerable && !(v is string))
        {
            int i = 0;
            foreach (object it in (IEnumerable)v)
            {
                if (i >= 12) { Console.WriteLine(indent + "…"); break; }
                Console.WriteLine(indent + "[" + i + "] " + (it == null ? "null" : it.GetType().Name + " " + Short(it)));
                DumpValue(Unwrap(it), indent + "  ", depth - 1, seen);
                i++;
            }
            return;
        }
        foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || Array.IndexOf(skipProps, p.Name) >= 0) continue;
            if (p.DeclaringType != null && p.DeclaringType.Namespace != null && !p.DeclaringType.Namespace.StartsWith("WolvenKit.RED4.Types")) continue;
            object pv; try { pv = p.GetValue(v, null); } catch { continue; }
            if (pv == null) continue;
            Console.WriteLine(indent + p.Name + " : " + pv.GetType().Name + " " + Short(pv));
            if (!IsLeaf(pv)) DumpValue(Unwrap(pv), indent + "  ", depth - 1, seen);
        }
    }

    static object Unwrap(object v)
    {
        if (v == null) return null;
        object c = P(v, "Chunk"); return c ?? v;                  // handle:, CHandle
    }
    static bool IsLeaf(object v)
    {
        Type t = v.GetType();
        return t.IsPrimitive || v is string || t.Name.StartsWith("CFloat") || t.Name.StartsWith("CUInt") || t.Name.StartsWith("CInt") || t.Name == "CBool" || t.Name == "CName" || t.Name == "CString" || t.Name.StartsWith("CEnum");
    }
    static string Short(object v)
    {
        string s = v.ToString(); if (s.Length > 80) s = s.Substring(0, 80) + "…";
        return s == v.GetType().FullName ? "" : "= " + s;
    }

    // ---------- infos ----------
    static string F3(double d) { return Math.Round(d, 3).ToString("0.###", Inv); }
    static string F2(double d) { return Math.Round(d, 2).ToString("0.##", Inv); }
    static double D(object v)
    {
        if (v == null) return double.NaN;
        double d; return double.TryParse(S(v).Replace(',', '.'), NumberStyles.Float, Inv, out d) ? d : double.NaN;
    }

    static int Infos(string game, string outDir, int max)
    {
        gameDir = game;
        OpenIndex(game);
        Directory.CreateDirectory(outDir);
        DateTime t0 = DateTime.Now;
        if (Environment.GetEnvironmentVariable("REDTOOL_SEULEMENT_MODELES") == null) {
        Sounds(Path.Combine(outDir, "sons.tsv"));
        Console.WriteLine("sons : " + (DateTime.Now - t0).TotalSeconds.ToString("0") + " s");
        t0 = DateTime.Now;
        Effects(Path.Combine(outDir, "effets.tsv"), max);
        Console.WriteLine("effets : " + (DateTime.Now - t0).TotalSeconds.ToString("0") + " s");
        }
        t0 = DateTime.Now;
        MeshSizes(Path.Combine(outDir, "tailles-modeles.tsv"), max);
        Console.WriteLine("modeles : " + (DateTime.Now - t0).TotalSeconds.ToString("0") + " s");
        return 0;
    }

    static void Sounds(string file)
    {
        // hors de l'index NCWE : extrait avec le lecteur d'archives de redtool
        string tmp = Path.Combine(Path.GetTempPath(), "redtool-sons");
        Find(gameDir, new List<string> { @"base\sound\event\eventsmetadata.json" }, tmp);
        object root = ReadCr2w(Path.Combine(tmp, "base_sound_event_eventsmetadata.json"));
        root = Unwrap(P(root, "Root")) ?? root;                // JsonResource -> audioAudioEventArray
        var sb = new StringBuilder("# evenement\tboucle\tportee_m\tduree_min_s\tduree_max_s\tetiquettes\n");
        int n = 0;
        foreach (PropertyInfo p in root.GetType().GetProperties())
        {
            var list = p.GetValue(root, null) as IEnumerable;
            if (list == null || list is string) continue;
            foreach (object e in list)
            {
                object ev = Unwrap(e);
                if (ev == null || P(ev, "IsLooping") == null) continue;
                string name = S(P(ev, "RedId") ?? P(ev, "Name"));
                if (name.Length == 0) continue;
                bool loop = S(P(ev, "IsLooping")) == "True";
                sb.Append(name).Append('\t').Append(loop ? "oui" : "non").Append('\t').Append(F2(D(P(ev, "MaxAttenuation"))))
                  .Append('\t').Append(F2(D(P(ev, "MinDuration")))).Append('\t').Append(F2(D(P(ev, "MaxDuration")))).Append('\t').Append(Tags(P(ev, "Tags"))).Append('\n');
                n++;
            }
        }
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine(n + " evenements sonores");
    }

    static string Tags(object list) { var l = new List<string>(); foreach (object x in Items(list)) l.Add(S(x)); return string.Join(",", l.ToArray()); }

    static void MeshSizes(string file, int max)
    {
        List<string> paths = PathsWith(".mesh");
        if (max > 0 && paths.Count > max) paths = paths.GetRange(0, max);
        var lines = new string[paths.Count];
        int done = 0, errors = 0;
        var opts = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 2) };
        System.Threading.Tasks.Parallel.For(0, paths.Count, opts, delegate (int i)
        {
            try
            {
                object root = ReadRoot(paths[i]);
                object box = P(root, "BoundingBox");
                double x0 = D(P(P(box, "Min"), "X")), y0 = D(P(P(box, "Min"), "Y")), z0 = D(P(P(box, "Min"), "Z"));
                double x1 = D(P(P(box, "Max"), "X")), y1 = D(P(P(box, "Max"), "Y")), z1 = D(P(P(box, "Max"), "Z"));
                if (!double.IsNaN(x0) && x1 >= x0)
                    lines[i] = paths[i] + "\t" + F2(x1 - x0) + "\t" + F2(y1 - y0) + "\t" + F2(z1 - z0) + "\t" + F3(x0) + ";" + F3(y0) + ";" + F3(z0);
            }
            catch { Interlocked.Increment(ref errors); }
            int d = Interlocked.Increment(ref done);
            if (d % 5000 == 0) Console.WriteLine("  modeles " + d + " / " + paths.Count);
        });
        var sb = new StringBuilder("# modele\tlargeur_x\tprofondeur_y\thauteur_z (m)\tmin_x;y;z (origine du modele)\n");
        int ok = 0;
        foreach (string l in lines) if (l != null) { sb.Append(l).Append('\n'); ok++; }
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine(ok + " modeles mesures, " + errors + " illisibles");
    }

    // ---------- effets ----------
    class FxInfo { public bool Loop; public double Duration = double.NaN, Life; public double[] Ext = new double[3]; }

    static void Effects(string file, int max)
    {
        List<string> paths = PathsWith(".particle", ".effect");
        if (max > 0 && paths.Count > max) paths = paths.GetRange(0, max);
        var lines = new string[paths.Count];
        int errors = 0;
        var opts = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 2) };
        System.Threading.Tasks.Parallel.For(0, paths.Count, opts, delegate (int i)
        {
            try
            {
                object root = ReadRoot(paths[i]);
                var fx = new FxInfo();
                if (paths[i].EndsWith(".effect", StringComparison.OrdinalIgnoreCase)) ReadEffect(root, fx);
                else ReadParticleSystem(root, fx);
                string dur = fx.Loop ? "" : (double.IsNaN(fx.Duration) ? (fx.Life > 0 ? F2(fx.Life) : "") : F2(fx.Duration));
                lines[i] = paths[i] + "\t" + (fx.Loop ? "oui" : "non") + "\t" + dur + "\t"
                    + F2(fx.Ext[0]) + ";" + F2(fx.Ext[1]) + ";" + F2(fx.Ext[2]);
            }
            catch { Interlocked.Increment(ref errors); }
        });
        var sb = new StringBuilder("# effet\tboucle\tduree_s\ttaille_approx_x;y;z (m)\n");
        int ok = 0;
        foreach (string l in lines) if (l != null) { sb.Append(l).Append('\n'); ok++; }
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine(ok + " effets, " + errors + " illisibles");
    }

    static void ReadEffect(object root, FxInfo fx)
    {
        // boucle : marqueurs de boucle ; duree : Length ou fin de la derniere piste
        foreach (object l in Items(P(root, "EffectLoops"))) { fx.Loop = true; break; }
        double len = D(P(root, "Length"));
        double end = 0;
        var parts = new List<object>();
        CollectTrackItems(Unwrap(P(root, "TrackRoot")), parts, 0);
        foreach (object it in parts)
        {
            object item = Unwrap(it);
            if (item.GetType().Name == "effectTrackItemLoopMarker") fx.Loop = true;
            double b = D(P(item, "TimeBegin")), d = D(P(item, "TimeDuration"));
            if (!double.IsNaN(b) && !double.IsNaN(d)) end = Math.Max(end, b + d);
            object ps = P(item, "ParticleSystem");
            if (ps != null)
            {
                string dp = Res(ps);
                object sys = null;
                if (dp.Length > 0 && dp.EndsWith(".particle", StringComparison.OrdinalIgnoreCase)) { try { sys = ReadRoot(dp); } catch { } }
                if (sys != null) ReadParticleSystem(sys, fx);
            }
        }
        // un effet ne boucle que s'il a des marqueurs de boucle (pas selon ses particules)
        fx.Loop = false;
        foreach (object l in Items(P(root, "EffectLoops"))) { fx.Loop = true; break; }
        foreach (object it in parts) if (Unwrap(it).GetType().Name == "effectTrackItemLoopMarker") fx.Loop = true;
        fx.Duration = !double.IsNaN(len) && len > 0 ? len : (end > 0 ? end : double.NaN);
    }

    static void CollectTrackItems(object group, List<object> into, int depth)
    {
        if (group == null || depth > 6) return;
        foreach (object t in Items(P(group, "Tracks")))
        {
            object tr = Unwrap(t);
            foreach (object it in Items(P(tr, "Items"))) into.Add(it);
            CollectTrackItems(tr, into, depth + 1);
        }
    }

    static void ReadParticleSystem(object sys, FxInfo fx)
    {
        foreach (object e in Items(P(sys, "Emitters")))
        {
            object em = Unwrap(e);
            double life = 1, size = 0, speed = 0, sphere = 0;
            // EmitterLoops 0 = emission sans fin ; sinon nombre de cycles de EmitterDuration
            double loops = D(P(em, "EmitterLoops"));
            object ds = P(em, "EmitterDurationSettings"), dl = P(em, "EmitterDelaySettings");
            double edur = D(P(ds, "EmitterDuration")), delay = D(P(dl, "EmitterDelay"));
            if (double.IsNaN(loops) || loops <= 0) fx.Loop = true;
            else if (!double.IsNaN(edur)) fx.Duration = Math.Max(double.IsNaN(fx.Duration) ? 0 : fx.Duration, (double.IsNaN(delay) ? 0 : delay) + edur * loops);
            double[] box = new double[3];
            foreach (object m in Items(P(em, "Modules")))
            {
                object mod = Unwrap(m); if (mod == null) continue;
                switch (mod.GetType().Name)
                {
                    case "CParticleInitializerLifeTime": life = MaxFloat(Unwrap(P(mod, "LifeTime"))); break;
                    case "CParticleInitializerSpawnBox":
                    {
                        double[] v = MaxVec(Unwrap(P(mod, "Extents")));
                        if (v != null) for (int k = 0; k < 3; k++) box[k] = Math.Max(box[k], Math.Abs(v[k]) * 2);
                        break;
                    }
                    case "CParticleInitializerSpawnSphere": sphere = Math.Max(sphere, D(P(mod, "OuterRadius")) * 2); break;
                    case "CParticleInitializerSize":
                    {
                        double[] v = MaxVec(Unwrap(P(mod, "Size")));
                        if (v != null) size = Math.Max(size, Math.Max(v[0], v[1]));
                        break;
                    }
                    case "CParticleInitializerVelocity":
                    {
                        double[] v = MaxVec(Unwrap(P(mod, "Velocity")));
                        if (v != null) speed = Math.Max(speed, Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]));
                        break;
                    }
                }
            }
            if (double.IsNaN(life) || life <= 0) life = 1;
            if (double.IsNaN(sphere)) sphere = 0;
            double travel = Math.Min(speed * life, 50);
            fx.Life = Math.Max(fx.Life, life);
            for (int k = 0; k < 3; k++)
                fx.Ext[k] = Math.Max(fx.Ext[k], Math.Max(box[k], sphere) + size + (k == 2 ? travel : travel * 0.5));
        }
    }

    // valeur max d'un evaluateur (constante, aleatoire, courbe)
    static double MaxFloat(object ev)
    {
        if (ev == null) return double.NaN;
        double v = D(P(ev, "Value")); if (!double.IsNaN(v)) return v;
        double mx = D(P(ev, "Max")); if (!double.IsNaN(mx)) return mx;
        double best = double.NaN;
        foreach (object c in Items(P(P(ev, "Curves") ?? P(ev, "Curve"), "Elements") ?? P(ev, "Curves")))
        {
            double x = D(P(c, "Value") ?? c); if (!double.IsNaN(x) && (double.IsNaN(best) || x > best)) best = x;
        }
        return best;
    }

    static double[] MaxVec(object ev)
    {
        if (ev == null) return null;
        object v = P(ev, "Value") ?? P(ev, "Max");
        if (v == null) return null;
        double x = D(P(v, "X")), y = D(P(v, "Y")), z = D(P(v, "Z"));
        if (double.IsNaN(x)) return null;
        return new[] { x, double.IsNaN(y) ? x : y, double.IsNaN(z) ? x : z };
    }
}
