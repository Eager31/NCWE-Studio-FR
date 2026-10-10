// Fenetre Bibliotheque : categories plus fines (Assises, Tables, Cuisine…) ajoutees a la navigation.
// Les modeles concernes passent de leur categorie NCWE (Mobilier, Decoration…) a la nouvelle ;
// rien n'est ecrit sur disque, la bibliotheque de NCWE reste intacte au prochain lancement sans plugin.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace NcweFr
{
    static partial class Plugin
    {
        // nom, morceaux du chemin (minuscules) ; le premier qui correspond gagne
        static readonly string[][] LibCategories =
        {
            new[] { "Assises", "chair", "stool", "sofa", "bench", "seat", "couch", "armchair", "pouf" },
            new[] { "Lits", "\\bed", "_bed", "bed_", "mattress", "futon" },
            new[] { "Tables et bureaux", "table", "desk" },
            new[] { "Comptoirs et bars", "counter", "bar_", "_bar", "kiosk", "reception" },
            new[] { "Rangements", "shelf", "shelves", "cabinet", "locker", "rack", "wardrobe", "drawer", "cupboard", "dresser" },
            new[] { "Cuisine", "kitchen", "fridge", "oven", "stove", "sink", "microwave", "cooker" },
            new[] { "Salle de bain", "toilet", "bath", "shower", "washbasin", "urinal" },
            new[] { "Éclairage", "lamp", "light", "chandelier", "neon", "lantern" },
            new[] { "Électronique", "\\electronics\\", "tv_", "_tv", "screen", "monitor", "computer", "laptop", "speaker", "arcade", "radio" },
            new[] { "Vaisselle et nourriture", "plate", "glass", "bottle", "cup_", "_cup", "food", "mug", "cutlery", "bowl", "can_" },
            new[] { "Enseignes et pubs", "sign", "logo", "letters", "advert", "poster", "billboard", "banner", "flag" },
            new[] { "Caisses et conteneurs", "crate", "container", "barrel", "pallet", "cardboard", "box_", "_box" },
            new[] { "Déchets", "trash", "garbage", "junk", "litter", "rubbish", "dumpster" },
            new[] { "Câbles et tuyaux", "cable", "wire", "pipe", "hose" },
            new[] { "Plantes en pot", "plant", "flower", "bonsai", "bamboo", "pot_" },
        };
        // categories NCWE dont les modeles peuvent etre reclasses
        static readonly HashSet<string> LibRecategorize = new HashSet<string> { "Furniture", "Decoration", "Devices", "Environment", "Other" };

        // recherche en francais : mots anglais du chemin -> mots francais ajoutes a la recherche du modele
        static readonly string[][] FrWords =
        {
            new[] { "chair", "chaise siège" }, new[] { "stool", "tabouret" }, new[] { "sofa", "canapé" }, new[] { "couch", "canapé" },
            new[] { "armchair", "fauteuil" }, new[] { "bench", "banc" }, new[] { "table", "table" }, new[] { "desk", "bureau" },
            new[] { "bed", "lit" }, new[] { "mattress", "matelas" }, new[] { "pillow", "oreiller coussin" }, new[] { "cushion", "coussin" },
            new[] { "shelf", "étagère" }, new[] { "shelves", "étagères" }, new[] { "cabinet", "meuble placard" }, new[] { "wardrobe", "armoire" },
            new[] { "drawer", "tiroir commode" }, new[] { "locker", "casier" }, new[] { "counter", "comptoir" }, new[] { "kitchen", "cuisine" },
            new[] { "fridge", "frigo réfrigérateur" }, new[] { "oven", "four" }, new[] { "stove", "cuisinière" }, new[] { "sink", "évier lavabo" },
            new[] { "toilet", "toilettes wc" }, new[] { "bath", "baignoire bain" }, new[] { "shower", "douche" }, new[] { "mirror", "miroir" },
            new[] { "lamp", "lampe" }, new[] { "light", "lumière éclairage" }, new[] { "chandelier", "lustre" }, new[] { "neon", "néon" },
            new[] { "screen", "écran" }, new[] { "monitor", "écran moniteur" }, new[] { "computer", "ordinateur" }, new[] { "laptop", "ordinateur portable" },
            new[] { "speaker", "enceinte haut-parleur" }, new[] { "phone", "téléphone" }, new[] { "keyboard", "clavier" }, new[] { "radio", "radio" },
            new[] { "plate", "assiette" }, new[] { "glass", "verre vitre" }, new[] { "bottle", "bouteille" }, new[] { "cup", "tasse gobelet" },
            new[] { "mug", "tasse" }, new[] { "bowl", "bol" }, new[] { "food", "nourriture" }, new[] { "can", "canette" },
            new[] { "box", "boîte carton caisse" }, new[] { "crate", "caisse" }, new[] { "barrel", "tonneau baril" }, new[] { "pallet", "palette" },
            new[] { "trash", "poubelle déchets" }, new[] { "garbage", "poubelle ordures" }, new[] { "bin", "poubelle" }, new[] { "dumpster", "benne" },
            new[] { "cable", "câble" }, new[] { "wire", "fil câble" }, new[] { "pipe", "tuyau" }, new[] { "hose", "tuyau" },
            new[] { "plant", "plante" }, new[] { "flower", "fleur" }, new[] { "tree", "arbre" }, new[] { "pot", "pot" },
            new[] { "sign", "enseigne panneau" }, new[] { "poster", "affiche" }, new[] { "billboard", "panneau pub" }, new[] { "flag", "drapeau" },
            new[] { "door", "porte" }, new[] { "window", "fenêtre" }, new[] { "wall", "mur" }, new[] { "floor", "sol" },
            new[] { "ceiling", "plafond" }, new[] { "stairs", "escalier" }, new[] { "railing", "rambarde garde-corps" }, new[] { "fence", "clôture grillage" },
            new[] { "car", "voiture" }, new[] { "bike", "moto vélo" }, new[] { "truck", "camion" }, new[] { "weapon", "arme" },
            new[] { "gun", "arme pistolet" }, new[] { "book", "livre" }, new[] { "paper", "papier" }, new[] { "clock", "horloge" },
            new[] { "carpet", "tapis moquette" }, new[] { "rug", "tapis" }, new[] { "curtain", "rideau" }, new[] { "blind", "store" },
            new[] { "vending", "distributeur" }, new[] { "machine", "machine" }, new[] { "fan", "ventilateur" }, new[] { "aircon", "climatiseur clim" },
            new[] { "statue", "statue" }, new[] { "painting", "tableau peinture" }, new[] { "frame", "cadre" }, new[] { "vase", "vase" },
        };

        static string FrSearch(string path)
        {
            string p = (path ?? "").ToLowerInvariant();
            var sb = new System.Text.StringBuilder();
            foreach (string[] w in FrWords) if (p.Contains(w[0])) sb.Append(' ').Append(w[1]);
            string s = sb.ToString(), plain = NoAccents(s);
            return plain == s ? s : s + plain;
        }

        static string NoAccents(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char ch in s.Normalize(System.Text.NormalizationForm.FormD))
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(ch);
            return sb.ToString();
        }
        static object libDone;           // liste _all deja reclassee

        static string LibCategoryOf(string path)
        {
            string p = (path ?? "").ToLowerInvariant();
            foreach (string[] c in LibCategories)
                for (int k = 1; k < c.Length; k++) if (p.Contains(c[k])) return c[0];
            return null;
        }

        static void LibraryCategoriesTick(object w)
        {
            try
            {
                var all = XGetField(w, "_all") as IList;
                if (all == null || all.Count == 0) return;
                if (all == libDone)
                {
                    // NCWE a pu refaire sa navigation : on remet nos categories
                    var cats = XGetField(w, "_categories") as IList;
                    bool ours = false;
                    if (cats != null) foreach (object c in cats) if ((XGetProp(c, "Value") as string) == LibCategories[0][0]) { ours = true; break; }
                    if (!ours) AddLibraryNav(w);
                    return;
                }
                FieldInfo catField = null, searchField = null;
                int moved = 0;
                foreach (object it in all)
                {
                    if (catField == null) catField = it.GetType().GetField("<Category>k__BackingField", AnyInst);
                    if (catField == null) return;
                    string path0 = XGetProp(XGetProp(it, "Resource"), "Path") as string;
                    if (searchField == null) searchField = it.GetType().GetField("<SearchName>k__BackingField", AnyInst);
                    if (searchField != null)
                    {
                        string fr = FrSearch(path0);
                        string sn = searchField.GetValue(it) as string ?? "";
                        if (fr.Length > 0 && sn.IndexOf('|') < 0) searchField.SetValue(it, sn + " |" + fr);
                    }
                    string cur = catField.GetValue(it) as string;
                    if (cur == null || !LibRecategorize.Contains(cur)) continue;
                    string path = XGetProp(XGetProp(it, "Resource"), "Path") as string;
                    string nc = LibCategoryOf(path);
                    if (nc == null) continue;
                    catField.SetValue(it, nc); moved++;
                }
                libDone = all;
                AddLibraryNav(w);
                Log("bibliotheque : " + moved + " modeles ranges dans " + LibCategories.Length + " categories fines");
            }
            catch (Exception e) { LogOnce("bibliotheque categories: " + e.GetBaseException().Message); libDone = XGetField(w, "_all"); }
        }

        // ajoute nos categories a la liste de navigation (sous celles de NCWE)
        static void AddLibraryNav(object w)
        {
            var cats = XGetField(w, "_categories") as IList;
            object list = XGetField(w, "CategoryList");
            if (cats == null || list == null || cats.Count == 0) return;
            Type navType = cats[0].GetType();
            string glyph = XGetProp(cats[0], "Glyph") as string ?? "";
            foreach (object c in cats) if ((XGetProp(c, "Value") as string) == "Furniture") glyph = XGetProp(c, "Glyph") as string ?? glyph;
            var have = new HashSet<string>();
            foreach (object c in cats) have.Add(XGetProp(c, "Value") as string ?? "");
            foreach (string[] c in LibCategories)
                if (!have.Contains(c[0])) cats.Add(Activator.CreateInstance(navType, new object[] { "Category", c[0], glyph, c[0] }));
            FieldInfo sync = w.GetType().GetField("_syncing", AnyInst);
            if (sync != null) sync.SetValue(w, true);
            try
            {
                var copy = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(navType));
                foreach (object c in cats) copy.Add(c);
                XSetProp(list, "ItemsSource", copy);
            }
            finally { if (sync != null) sync.SetValue(w, false); }
            MethodInfo counts = w.GetType().GetMethod("UpdateCounts", AnyInst);
            if (counts != null) counts.Invoke(w, null);
        }
    }
}
