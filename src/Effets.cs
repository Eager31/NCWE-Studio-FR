// Onglet Effets du panneau Ajouter : liste deroulante « Type d'effet » au-dessus de la recherche
// (fumée, vapeur, feu, étincelles…). Le type est deduit du chemin du fichier .particle / .effect.
// Ne touche qu'a l'affichage de la liste : la recherche de NCWE reste active et se combine au type.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace NcweFr
{
    static partial class Plugin
    {
        // nom affiche, morceaux du chemin (minuscules) ; le premier type qui correspond gagne
        static readonly string[][] EffectTypes =
        {
            new[] { "Vapeur", "steam" },
            new[] { "Fumée", "smoke", "fumes" },
            new[] { "Feu et flammes", "fire", "flame", "pyro", "burn", "torch", "candle" },
            new[] { "Explosions", "explosion", "explo", "blast", "grenade" },
            new[] { "Étincelles", "spark", "welding" },
            new[] { "Électricité", "electric", "elec", "short_circuit", "shortcircuit", "lightning" },
            new[] { "Poussière", "dust", "sand" },
            new[] { "Eau et pluie", "water", "rain", "drip", "splash", "puddle", "leak", "fountain" },
            new[] { "Météo, brume, nuages", "weather", "cloud", "fog", "mist", "haze" },
            new[] { "Hologrammes", "holo" },
            new[] { "Lumières, néons, halos", "lights_env", "neon", "flare", "glow", "light", "lamp" },
            new[] { "Débris et destruction", "debris", "destruct", "glass", "shatter", "crumble" },
            new[] { "Sang", "blood" },
            new[] { "Insectes et animaux", "insect", "flies", "fly_", "bird", "animal" },
            new[] { "Végétation", "vegetation", "leaves", "leaf" },
            new[] { "Écrans et interface", "\\ui\\", "screen", "glitch", "hack" },
            new[] { "Véhicules", "\\vehicles\\" },
            new[] { "Armes", "\\weapons\\" },
            new[] { "Personnages", "\\characters\\", "\\player\\" },
            new[] { "Appareils", "\\devices\\" },
            new[] { "Quêtes", "\\quest\\" },
        };
        const string EffectOther = "Autres";

        static object fxCombo, fxSearch, fxList, fxLastSource, fxCatalogRef;
        static int fxShownIndex = -2;
        static bool fxShownInfos;
        static object sndLastSource;
        static List<string> fxNames = new List<string>();             // index du ComboBox -1 -> type
        static Dictionary<object, string> fxTypeOf;                    // ElementResourceItem -> type
        static bool fxFailed;

        static string EffectTypeOf(string path)
        {
            string p = (path ?? "").ToLowerInvariant();
            foreach (string[] t in EffectTypes)
                for (int k = 1; k < t.Length; k++) if (p.Contains(t[k])) return t[0];
            return EffectOther;
        }

        static object WinField(object window, string name)
        {
            FieldInfo f = window.GetType().GetField(name, AnyInst);
            return f == null ? null : f.GetValue(window);
        }

        static void EffectsTick(object window)
        {
            if (fxFailed) return;
            try
            {
                if (fxSearch == null)
                {
                    fxSearch = WinField(window, "AddSearch"); fxList = WinField(window, "ElementCatalogList");
                    if (fxSearch == null || fxList == null) { fxFailed = true; LogOnce("effets : panneau Ajouter introuvable"); return; }
                }
                string cat = WinField(window, "_addCategory") as string;
                if (cat == "Sound") SoundsTick(window);
                if (cat != "Vfx")
                {
                    if (fxCombo != null && XGetProp(fxSearch, "Header") == fxCombo) XSetProp(fxSearch, "Header", null);
                    return;
                }
                IEnumerable catalog = WinField(window, "_vfxCatalog") as IEnumerable;
                if (catalog == null) return;
                if (fxCatalogRef != catalog) BuildEffectTypes(catalog);
                if (XGetProp(fxSearch, "Header") != fxCombo) XSetProp(fxSearch, "Header", fxCombo);

                // la liste affichee est toujours la notre (type + recherche + infos) ; refaite quand
                // NCWE la remplace (recherche), quand le type change ou quand les infos arrivent
                int idx = Convert.ToInt32(XGetProp(fxCombo, "SelectedIndex"));
                object src = XGetProp(fxList, "ItemsSource");
                bool infos = InfosReady();
                if (idx == fxShownIndex && src == fxLastSource && infos == fxShownInfos) return;
                fxShownIndex = idx; fxShownInfos = infos;
                ApplyEffectFilter(window, catalog, idx <= 0 || idx >= fxNames.Count ? null : fxNames[idx]);
            }
            catch (Exception e) { LogOnce("effets: " + e.GetBaseException().Message); }
        }

        static void BuildEffectTypes(IEnumerable catalog)
        {
            fxCatalogRef = catalog;
            fxTypeOf = new Dictionary<object, string>();
            var counts = new Dictionary<string, int>();
            int total = 0;
            foreach (object it in catalog)
            {
                string t = EffectTypeOf(XGetProp(it, "Path") as string);
                fxTypeOf[it] = t; total++;
                int n; counts.TryGetValue(t, out n); counts[t] = n + 1;
            }
            int keep = 0;
            if (fxCombo != null) keep = Math.Max(0, Convert.ToInt32(XGetProp(fxCombo, "SelectedIndex")));
            string keepName = keep > 0 && keep < fxNames.Count ? fxNames[keep] : null;

            fxNames = new List<string> { null };
            var x = new System.Text.StringBuilder("<ComboBox xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' HorizontalAlignment='Stretch' Margin='0,0,0,2' ToolTipService.ToolTip='Type d&apos;effet (déduit du dossier du fichier) ; la recherche filtre en plus'>");
            x.Append("<x:String xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>Tous les types (" + total.ToString("N0") + ")</x:String>");
            var order = new List<string>();
            foreach (string[] t in EffectTypes) order.Add(t[0]);
            order.Add(EffectOther);
            foreach (string name in order)
            {
                int n; if (!counts.TryGetValue(name, out n) || n == 0) continue;
                fxNames.Add(name);
                x.Append("<x:String xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" + X(name) + " (" + n.ToString("N0") + ")</x:String>");
            }
            x.Append("</ComboBox>");
            fxCombo = Load(x.ToString());
            int sel = keepName != null ? Math.Max(0, fxNames.IndexOf(keepName)) : 0;
            XSetProp(fxCombo, "SelectedIndex", sel);
            fxShownIndex = -2;                                        // force l'application
            Log("effets : " + (fxNames.Count - 1) + " types pour " + total + " fichiers");
        }

        // onglet Sons : boucle / ponctuel et portee devant le detail de chaque emetteur du jeu
        static void SoundsTick(object window)
        {
            object src = XGetProp(fxList, "ItemsSource");
            if (src == null || src == sndLastSource || !InfosReady()) return;
            var items = src as IList; if (items == null || items.Count == 0) return;
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(items[0].GetType()));
            int n = 0;
            foreach (object it in items)
            {
                string info = SoundInfo(SoundEventOf(it));
                if (info != null) n++;
                list.Add(Enrich(it, info));
            }
            sndLastSource = src;
            if (n == 0) return;
            FieldInfo sync = window.GetType().GetField("_syncingElements", AnyInst);
            if (sync != null) sync.SetValue(window, true);
            try { XSetProp(fxList, "ItemsSource", list); }
            finally { if (sync != null) sync.SetValue(window, false); }
            sndLastSource = list;
        }

        static void ApplyEffectFilter(object window, IEnumerable catalog, string type)
        {
            string text = (XGetProp(fxSearch, "Text") as string) ?? "";
            string[] terms = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            // liste du meme type que celle de NCWE (List<ElementResourceItem>)
            Type itemType = null;
            foreach (object it in catalog) { itemType = it.GetType(); break; }
            if (itemType == null) return;
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType));
            int found = 0;
            foreach (object it in catalog)
            {
                string t; if (type != null && (!fxTypeOf.TryGetValue(it, out t) || t != type)) continue;
                string ft; fxTypeOf.TryGetValue(it, out ft);
                string name = (XGetProp(it, "Name") as string) ?? "", detail = ((XGetProp(it, "Detail") as string) ?? "") + " " + ft + " " + NoAccents(ft ?? "");
                bool ok = true;
                foreach (string term in terms)
                    if (name.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0 && detail.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0) { ok = false; break; }
                if (!ok) continue;
                found++;
                string fi = FxInfo(XGetProp(it, "Path") as string);
                if (list.Count < 3000) list.Add(Enrich(it, ft + (fi != null ? " · " + fi : "")));
            }
            FieldInfo sync = window.GetType().GetField("_syncingElements", AnyInst);
            if (sync != null) sync.SetValue(window, true);
            try { XSetProp(fxList, "ItemsSource", list); }
            finally { if (sync != null) sync.SetValue(window, false); }
            fxLastSource = list;
            object count = WinField(window, "AddCount");
            if (count != null)
                XSetProp(count, "Text", found.ToString("N0", Fr) + " effet" + (found > 1 ? "s" : "") + (type != null ? " « " + type + " »" : "")
                    + (found > 3000 ? " (3 000 premiers)" : "") + " · choisissez-en un, puis cliquez dans le monde");
        }
    }
}
