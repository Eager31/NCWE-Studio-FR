// Infos lues dans les fichiers du jeu (dossier infos\, produit par « redtool infos ») et affichees dans NCWE :
//   bibliotheque (fenetre) : taille du modele dans le detail ;
//   panneau Ajouter, onglets Effets et Sons : boucle / duree, taille ou portee devant le detail de chaque ligne.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;

namespace NcweFr
{
    static partial class Plugin
    {
        static Dictionary<string, string> infoSizes, infoFx, infoSounds;   // chemin / evenement (minuscules) -> texte
        static volatile bool infosLoaded;
        static int infosLoading;

        static readonly CultureInfo Fr = new CultureInfo("fr-FR");

        static string Num(string s)
        {
            double d; if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return s;
            return d.ToString(d >= 10 ? "0.#" : "0.##", Fr);
        }

        // lecture en arriere-plan au premier besoin (100 000 lignes)
        static bool InfosReady()
        {
            if (infosLoaded) return true;
            if (Interlocked.Exchange(ref infosLoading, 1) == 0)
            {
                var t = new Thread(LoadInfos); t.IsBackground = true; t.Name = "ncwe-fr-infos"; t.Start();
            }
            return false;
        }

        static void LoadInfos()
        {
            try
            {
                string d = Path.Combine(dir, "infos");
                var sizes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string[] c in Rows(Path.Combine(d, "tailles-modeles.tsv"), 4))
                    sizes[c[0]] = Num(c[1]) + " × " + Num(c[2]) + " × " + Num(c[3]) + " m";
                var fx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string[] c in Rows(Path.Combine(d, "effets.tsv"), 4))
                {
                    string when = c[1] == "oui" ? "∞ en boucle" : (c[2].Length > 0 ? Num(c[2]) + " s" : "ponctuel");
                    string[] e = c[3].Split(';');
                    string size = e.Length == 3 && c[3] != "0;0;0" ? " · ≈ " + Num(e[0]) + " × " + Num(e[1]) + " × " + Num(e[2]) + " m" : "";
                    fx[c[0]] = when + size;
                }
                var snd = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string[] c in Rows(Path.Combine(d, "sons.tsv"), 5))
                {
                    string when = c[1] == "oui" ? "∞ en boucle" : "ponctuel" + (c[4] != "0" ? " " + Num(c[4]) + " s" : "");
                    snd[c[0]] = when + (c[2] != "0" ? " · portée " + Num(c[2]) + " m" : "");
                }
                infoSizes = sizes; infoFx = fx; infoSounds = snd;
                infosLoaded = true;
                Log("infos : " + sizes.Count + " tailles, " + fx.Count + " effets, " + snd.Count + " sons");
            }
            catch (Exception e) { LogOnce("infos: " + e.Message); }
        }

        static IEnumerable<string[]> Rows(string file, int min)
        {
            if (!File.Exists(file)) yield break;
            foreach (string l in File.ReadLines(file))
            {
                if (l.Length == 0 || l[0] == '#') continue;
                string[] c = l.Split('\t');
                if (c.Length >= min) yield return c;
            }
        }

        static string SizeOf(string path) { string s; return infosLoaded && path != null && infoSizes.TryGetValue(path, out s) ? s : null; }
        static string FxInfo(string path) { string s; return infosLoaded && path != null && infoFx.TryGetValue(path, out s) ? s : null; }
        static string SoundInfo(string evt) { string s; return infosLoaded && evt != null && infoSounds.TryGetValue(evt, out s) ? s : null; }

        // ---------- bibliotheque : taille dans le detail ----------
        static object libShownItem; static string libShownText;

        static void LibraryTick(object w)
        {
            LibraryCategoriesTick(w);
            if (!InfosReady()) return;
            try
            {
                object cur = XGetField(w, "_current");
                object info = XGetField(w, "DetailInfo");
                if (cur == null || info == null) return;
                string text = XGetProp(info, "Text") as string ?? "";
                if (cur == libShownItem && text == libShownText) return;
                object res = XGetProp(cur, "Resource");
                string size = SizeOf(XGetProp(res, "Path") as string);
                libShownItem = cur;
                if (size == null) { libShownText = text; return; }
                if (text.Length > 0 && !text.StartsWith("Taille", StringComparison.Ordinal)) { libShownText = text; return; }   // message de NCWE
                libShownText = "Taille : " + size + " (largeur × profondeur × hauteur)";
                XSetProp(info, "Text", libShownText);
            }
            catch (Exception e) { LogOnce("bibliotheque infos: " + e.GetBaseException().Message); }
        }

        static object XGetField(object o, string name)
        {
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, AnyInst | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(o);
            }
            return null;
        }

        // ---------- panneau Ajouter : copie d'un element avec le detail enrichi ----------
        static readonly Dictionary<object, object> enriched = new Dictionary<object, object>();
        static ConstructorInfo elementCtor;

        static object Enrich(object item, string prefix)
        {
            if (prefix == null) return item;
            object done; if (enriched.TryGetValue(item, out done) && (XGetProp(done, "Detail") as string ?? "").StartsWith(prefix + " · ", StringComparison.Ordinal)) return done;
            if (elementCtor == null) foreach (ConstructorInfo c in item.GetType().GetConstructors()) if (c.GetParameters().Length == 5) elementCtor = c;
            if (elementCtor == null) return item;
            object copy = elementCtor.Invoke(new[] { XGetProp(item, "Name"), prefix + " · " + XGetProp(item, "Detail"), XGetProp(item, "Path"), XGetProp(item, "Kind"), XGetProp(item, "Template") });
            enriched[item] = copy;
            return copy;
        }

        static string SoundEventOf(object item)
        {
            object tpl = XGetProp(item, "Template");
            object audio = XGetProp(XGetProp(tpl, "Element"), "Audio");
            return XGetProp(audio, "Sound") as string;
        }
    }
}
