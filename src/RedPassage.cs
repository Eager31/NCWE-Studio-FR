// redtool passage : fabrique un mod ArchiveXL qui ajoute a l'ascenseur commun (common_lift.ent)
// une apparence faite de meshes choisis (ex. le « passage » qui monte comme une porte).
//   passage <dossier NCWE> <common_lift_appearances.app extrait> <common_lift.ent extrait> <spec.tsv> <dossier mod>
// spec.tsv : 1re ligne « nom <TAB> chemin .app <TAB> chemin .ent patch » ; puis une ligne par mesh :
//   mesh <TAB> apparence <TAB> x;y;z (locaux) <TAB> i;j;k;r (quaternion local) <TAB> sx;sy;sz
// Les apparences existantes de l'ascenseur ne sont pas modifiees : le patch n'ajoute qu'un nom.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

static partial class RedTool
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly string[] KeepComponents = { "anim", "animController", "Slot", "terminalMesh", "ui" };

    static int Passage(string appFile, string entFile, string specFile, string modDir)
    {
        string[] lines = File.ReadAllLines(specFile, Encoding.UTF8);
        string[] head = lines[0].Split('\t');
        string name = head[0], appPath = head[1], entPath = head[2];

        // ---------- .app : une seule apparence « name » ----------
        object appCr2w = ReadCr2wFile(appFile);
        object appRoot = P(appCr2w, "RootChunk");
        IList appearances = (IList)P(appRoot, "Appearances");
        object model = null;
        foreach (object h in appearances) { object d = P(h, "Chunk") ?? h; if (S(P(d, "Name")) == "common_a_1_door") model = h; }
        if (model == null) throw new Exception("apparence modele common_a_1_door introuvable");
        appearances.Clear(); appearances.Add(model);
        object def = P(model, "Chunk") ?? model;
        Set(def, "Name", name);

        IList comps = (IList)P(def, "Components");
        object template = null; var keep = new List<object>();
        foreach (object c in comps)
        {
            string n = S(P(c, "Name"));
            if (Array.IndexOf(KeepComponents, n) >= 0) keep.Add(c);
            if (template == null && c.GetType().Name == "entMeshComponent") template = c;
        }
        if (template == null) throw new Exception("pas de entMeshComponent modele");
        comps.Clear(); foreach (object c in keep) comps.Add(c);

        var meshes = new List<string>();
        for (int i = 1; i < lines.Length; i++)
        {
            string[] c = lines[i].Split('\t'); if (c.Length < 5) continue;
            object mc = Copy(template);
            Set(mc, "Name", "passage_" + i);
            SetRes(mc, "Mesh", c[0]);
            Set(mc, "MeshAppearance", c[1]);
            double[] p = D(c[2]), q = D(c[3]), s = D(c[4]);
            object lt = P(mc, "LocalTransform");
            object pos = P(lt, "Position"); Set(pos, "X", p[0]); Set(pos, "Y", p[1]); Set(pos, "Z", p[2]); Set(lt, "Position", pos);
            object rot = P(lt, "Orientation"); Set(rot, "I", q[0]); Set(rot, "J", q[1]); Set(rot, "K", q[2]); Set(rot, "R", q[3]); Set(lt, "Orientation", rot);
            Set(mc, "LocalTransform", lt);
            object vs = P(mc, "VisualScale"); if (vs != null) { Set(vs, "X", s[0]); Set(vs, "Y", s[1]); Set(vs, "Z", s[2]); Set(mc, "VisualScale", vs); }
            comps.Add(mc);
            if (!meshes.Contains(c[0])) meshes.Add(c[0]);
        }
        AddDependencies(def, meshes);
        WriteCr2w(appCr2w, Path.Combine(modDir, "source", appPath));
        Console.WriteLine(".app : apparence « " + name + " », " + (comps.Count - keep.Count) + " meshes + " + keep.Count + " composants gardes");

        // ---------- .ent patch : une seule entree d'apparence, aucun composant ----------
        object entCr2w = ReadCr2wFile(entFile);
        object entRoot = P(entCr2w, "RootChunk");
        IList entApps = (IList)P(entRoot, "Appearances");
        object entry = entApps[0];
        entApps.Clear(); entApps.Add(entry);
        Set(entry, "Name", name);
        Set(entry, "AppearanceName", name);
        SetRes(entry, "AppearanceResource", appPath);
        IList entComps = P(entRoot, "Components") as IList; if (entComps != null) entComps.Clear();
        foreach (string lst in new[] { "ResolvedDependencies", "Includes", "Bindings", "VisualTagsSchema" }) { IList l = P(entRoot, lst) as IList; if (l != null) l.Clear(); }
        WriteCr2w(entCr2w, Path.Combine(modDir, "source", entPath));
        Console.WriteLine(".ent patch : entree « " + name + " » -> " + appPath);

        // ---------- archive + .xl ----------
        string arch = Path.Combine(modDir, @"archive\pc\mod"); Directory.CreateDirectory(arch);
        string archive = Path.Combine(arch, "ncwe_fr_passage.archive");
        Pack(Path.Combine(modDir, "source"), archive);
        File.WriteAllText(archive + ".xl",
            "resource:\n  patch:\n    " + entPath + ":\n      - base\\gameplay\\devices\\elevators\\common_elevator\\common_lift.ent\n", new UTF8Encoding(false));
        Console.WriteLine("mod : " + archive + " (+ .xl)");
        return 0;
    }

    // Reference de ressource (CResourceAsyncReference / raRef…) : nouvelle instance construite sur le chemin.
    static object NewRes(Type t, string path)
    {
        foreach (ConstructorInfo c in t.GetConstructors())
        {
            ParameterInfo[] ps = c.GetParameters();
            if (ps.Length != 1) continue;
            object arg; try { arg = Conv(path, ps[0].ParameterType); } catch { continue; }
            return c.Invoke(new[] { arg });
        }
        return Conv(path, t);
    }

    static void SetRes(object o, string prop, string path)
    {
        PropertyInfo p = o.GetType().GetProperty(prop);
        p.SetValue(o, NewRes(p.PropertyType, path), null);
    }

    static double[] D(string s) { string[] p = s.Split(';'); var r = new double[p.Length]; for (int i = 0; i < p.Length; i++) r[i] = double.Parse(p[i], Inv); return r; }

    static object ReadCr2wFile(string file)
    {
        Type cr = T("WolvenKit.RED4.Archive.IO.CR2WReader");
        MethodInfo read = null;
        foreach (MethodInfo m in cr.GetMethods()) if (m.Name == "ReadFile" && m.GetParameters().Length == 2) read = m;
        var ms = new MemoryStream(File.ReadAllBytes(file));
        object[] ra = { null, true };
        read.Invoke(Activator.CreateInstance(cr, new object[] { ms }), ra);
        return ra[0];
    }

    static void WriteCr2w(object cr2w, string outFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outFile));
        Type wt = T("WolvenKit.RED4.Archive.IO.CR2WWriter");
        using (var fs = File.Create(outFile))
        {
            object w = Activator.CreateInstance(wt, new object[] { fs });
            wt.GetMethod("WriteFile").Invoke(w, new[] { cr2w });
            var d = w as IDisposable; if (d != null) d.Dispose();
        }
    }

    static void Pack(string inDir, string outArchive)
    {
        Type aw = T("WolvenKit.RED4.Archive.IO.ArchiveWriter");
        object writer = null;
        foreach (ConstructorInfo c in aw.GetConstructors())
        {
            ParameterInfo[] ps = c.GetParameters();
            var args = new object[ps.Length];
            bool ok = true;
            for (int i = 0; i < ps.Length; i++)
            {
                Type pt = ps[i].ParameterType;
                if (pt.Name.Contains("HashService")) { Type hs = ByName("HashService"); ConstructorInfo light = hs == null ? null : hs.GetConstructor(new[] { typeof(bool) }); args[i] = light != null ? light.Invoke(new object[] { false }) : null; }
                else if (pt.IsInterface || pt.IsAbstract) { args[i] = null; }
                else ok = false;
            }
            if (!ok) continue;
            try { writer = c.Invoke(args); break; } catch (Exception e) { Console.WriteLine("ArchiveWriter(" + ps.Length + ") : " + e.GetBaseException().Message); }
        }
        if (writer == null) { foreach (ConstructorInfo c in aw.GetConstructors()) { var n = new List<string>(); foreach (ParameterInfo p in c.GetParameters()) n.Add(p.ParameterType.FullName); Console.WriteLine("ctor ArchiveWriter(" + string.Join(", ", n.ToArray()) + ")"); } throw new Exception("ArchiveWriter non construit"); }
        using (var fs = File.Create(outArchive))
            aw.GetMethod("WriteArchive").Invoke(writer, new object[] { new DirectoryInfo(inDir), fs });
    }

    static Type ByName(string name)
    {
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!a.GetName().Name.StartsWith("WolvenKit")) continue;
            Type[] ts; try { ts = a.GetTypes(); } catch (ReflectionTypeLoadException e) { ts = Array.FindAll(e.Types, delegate (Type x) { return x != null; }); }
            foreach (Type t in ts) if (t != null && t.Name == name && !t.IsAbstract) return t;
        }
        return null;
    }

    static object Copy(object o)
    {
        MethodInfo dc = o.GetType().GetMethod("DeepCopy", Type.EmptyTypes);
        if (dc != null) return dc.Invoke(o, null);
        MethodInfo mc = typeof(object).GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance);
        return mc.Invoke(o, null);
    }

    // Affecte une valeur en passant par les conversions implicites des types WolvenKit (CName, CFloat, ResourcePath…).
    static void Set(object o, string prop, object value)
    {
        PropertyInfo p = o.GetType().GetProperty(prop);
        if (p == null) throw new Exception(o.GetType().Name + "." + prop + " introuvable");
        if (!p.CanWrite) throw new Exception(o.GetType().Name + "." + prop + " en lecture seule (" + p.PropertyType.Name + ")");
        p.SetValue(o, Conv(value, p.PropertyType), null);
    }

    static object Conv(object v, Type target)
    {
        if (v == null || target.IsInstanceOfType(v)) return v;
        foreach (Type src in new[] { v.GetType(), typeof(string), typeof(float), typeof(double) })
        {
            object val = v;
            if (src == typeof(float) && v is double) val = (float)(double)v;
            else if (src != v.GetType()) continue;
            foreach (MethodInfo m in target.GetMethods(BindingFlags.Public | BindingFlags.Static))
                if ((m.Name == "op_Implicit" || m.Name == "op_Explicit") && m.ReturnType == target && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == val.GetType())
                    return m.Invoke(null, new[] { val });
        }
        if (v is double) { object f = Conv((float)(double)v, target); if (f != null && target.IsInstanceOfType(f)) return f; }
        // conversion en deux temps : double -> CFloat/FixedPoint, string -> CName/ResourcePath
        foreach (MethodInfo m in target.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "op_Implicit" || m.ReturnType != target || m.GetParameters().Length != 1) continue;
            Type mid = m.GetParameters()[0].ParameterType;
            object inner = null;
            try { inner = Conv(v, mid); } catch { }
            if (inner != null && mid.IsInstanceOfType(inner)) return m.Invoke(null, new[] { inner });
        }
        throw new Exception("conversion impossible " + v.GetType().Name + " -> " + target.Name);
    }

    // Les meshes doivent figurer dans les dependances de l'apparence pour etre charges.
    static void AddDependencies(object def, List<string> meshes)
    {
        foreach (string prop in new[] { "ResolvedDependencies", "LooseDependencies" })
        {
            IList deps = P(def, prop) as IList; if (deps == null) continue;
            Type et = deps.GetType().IsGenericType ? deps.GetType().GetGenericArguments()[0] : null;
            if (et == null) continue;
            foreach (string m in meshes)
            {
                deps.Add(NewRes(et, m));
            }
        }
    }
}
