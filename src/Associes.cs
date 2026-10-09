// Ce qui part avec un objet supprime (Nettoyage et boutons de suppression du panneau Selection) :
//  - ses collisions du jeu : celles que NCWE lui associe (linked.collision) et celles posees exactement
//    sur lui sans lien avec un autre objet qui reste (sinon : murs invisibles qui bloquent le joueur) ;
//  - ses sons : emetteurs poses sur lui, sauf s'ils sont aussi sur un autre objet qui reste.
// Rien d'autre n'est touche. Les objets deja supprimes passes dans « dejaSupprimes » comptent comme supprimes.

using System;
using System.Collections.Generic;
using System.Text;

namespace NcweFr
{
    static partial class Plugin
    {
        const double HolderMaxVolume = 50;   // m3 : au-dela (sol, batiment, route…) un objet ne « porte » pas un son

        static void FindAssociated(string client, List<string> ids, List<double[]> dejaSupprimes, List<string> cols, List<string> sounds)
        {
            var boxes = new List<double[]>();
            if (dejaSupprimes != null) boxes.AddRange(dejaSupprimes);
            var targets = new HashSet<string>(ids, StringComparer.Ordinal);
            var haveCols = new HashSet<string>(cols, StringComparer.Ordinal);

            // 1. collisions associees par NCWE + boites des objets a supprimer
            foreach (Dictionary<string, object> d in ObjectDetails(client, ids))
            {
                double[] b = Box(d); if (b != null) boxes.Add(b);
                foreach (string c in LinkedIds(d)) if (haveCols.Add(c)) cols.Add(c);
            }
            if (boxes.Count == 0) return;

            // 2. zone qui couvre toutes les boites
            double[] u = { double.MaxValue, double.MaxValue, double.MaxValue, double.MinValue, double.MinValue, double.MinValue };
            foreach (double[] b in boxes) for (int k = 0; k < 3; k++) { u[k] = Math.Min(u[k], b[k]); u[k + 3] = Math.Max(u[k + 3], b[k + 3]); }
            double cx = (u[0] + u[3]) / 2, cy = (u[1] + u[4]) / 2, cz = (u[2] + u[5]) / 2;
            double radius = Math.Min(200, Math.Sqrt(Math.Pow(u[3] - u[0], 2) + Math.Pow(u[4] - u[1], 2) + Math.Pow(u[5] - u[2], 2)) / 2 + 2);

            // 3. collisions et sons poses sur ces boites
            var colCand = new List<KeyValuePair<string, double[]>>();
            var soundCand = new List<KeyValuePair<string, double[]>>();
            foreach (Dictionary<string, object> d in Query(client, cx, cy, cz, radius, "\"collision\",\"sound\"", false))
            {
                object ed; if (d.TryGetValue("editable", out ed) && ed is bool && !(bool)ed) continue;
                string id = Str(d, "id"); double[] p = Vec(d, "position"); if (id == null || p == null) continue;
                bool sound = Str(d, "kind") == "sound";
                foreach (double[] b in boxes)
                    if (sound ? InBox(p, b, 0.3, 0.5) : InBox(p, b, 0.1, 0.2)) { (sound ? soundCand : colCand).Add(new KeyValuePair<string, double[]>(id, p)); break; }
            }
            if (colCand.Count == 0 && soundCand.Count == 0) return;

            // 4. objets qui restent autour : ils gardent leurs collisions et leurs sons
            var others = new List<KeyValuePair<string, double[]>>();
            foreach (Dictionary<string, object> d in Query(client, cx, cy, cz, radius + 2, "\"mesh\",\"instanced_mesh\",\"entity\",\"door\"", false))
            {
                string id = Str(d, "id"); double[] b = Box(d);
                if (id != null && b != null && !targets.Contains(id)) others.Add(new KeyValuePair<string, double[]>(id, b));
            }

            if (colCand.Count > 0)
            {
                var near = new List<string>();
                foreach (var ob in others) foreach (var cd in colCand) if (InBox(cd.Value, ob.Value, 1.0, 1.0)) { near.Add(ob.Key); break; }
                var keep = new HashSet<string>(StringComparer.Ordinal);
                List<Dictionary<string, object>> det = ObjectDetails(client, near);
                if (near.Count > 0 && det.Count == 0) colCand.Clear();          // prudence : rien si on ne sait pas
                foreach (Dictionary<string, object> d in det) foreach (string c in LinkedIds(d)) keep.Add(c);
                foreach (var cd in colCand) if (!keep.Contains(cd.Key) && haveCols.Add(cd.Key)) cols.Add(cd.Key);
            }

            var haveSounds = new HashSet<string>(sounds, StringComparer.Ordinal);
            foreach (var sd in soundCand)
            {
                bool held = false;
                foreach (var ob in others)
                {
                    double[] b = ob.Value;
                    if ((b[3] - b[0]) * (b[4] - b[1]) * (b[5] - b[2]) > HolderMaxVolume) continue;
                    if (InBox(sd.Value, b, 0.3, 0.5)) { held = true; break; }
                }
                if (!held && haveSounds.Add(sd.Key)) sounds.Add(sd.Key);
            }
        }

        // Details (api.object) par lots de 300 ; liste vide si NCWE ne repond pas.
        static List<Dictionary<string, object>> ObjectDetails(string client, List<string> ids)
        {
            var res = new List<Dictionary<string, object>>();
            for (int i = 0; i < ids.Count; i += 300)
            {
                var sb = new StringBuilder("{\"op\":\"api.object\",\"client\":" + Q(client) + ",\"ids\":[");
                for (int k = i; k < Math.Min(ids.Count, i + 300); k++) { if (k > i) sb.Append(','); sb.Append(Q(ids[k])); }
                var r = Call(sb.Append("]}").ToString(), 30000);
                object list; if (!Ok(r) || !r.TryGetValue("objects", out list) || !(list is List<object>)) return new List<Dictionary<string, object>>();
                foreach (object o in (List<object>)list) { var d = o as Dictionary<string, object>; if (d != null) res.Add(d); }
            }
            return res;
        }

        static IEnumerable<string> LinkedIds(Dictionary<string, object> d)
        {
            object lk, lc;
            if (!d.TryGetValue("linked", out lk) || !(lk is Dictionary<string, object>) || !((Dictionary<string, object>)lk).TryGetValue("collision", out lc) || !(lc is List<object>)) yield break;
            foreach (object id in (List<object>)lc) { string s = id as string; if (s != null) yield return s; }
        }

        static List<Dictionary<string, object>> Query(string client, double x, double y, double z, double radius, string kinds, bool includeDeleted)
        {
            var res = new List<Dictionary<string, object>>();
            var r = Call("{\"op\":\"api.query\",\"client\":" + Q(client) + ",\"center\":" + P(x, y, z) + ",\"radius\":" + V(radius)
                + ",\"kinds\":[" + kinds + "],\"limit\":2000,\"show\":false" + (includeDeleted ? ",\"include_deleted\":true" : "") + "}", 30000);
            object objs; if (!Ok(r) || !r.TryGetValue("objects", out objs) || !(objs is List<object>)) return res;
            foreach (object o in (List<object>)objs) { var d = o as Dictionary<string, object>; if (d != null) res.Add(d); }
            return res;
        }

        static double[] Box(Dictionary<string, object> d)
        {
            object b; if (!d.TryGetValue("bounds", out b) || !(b is Dictionary<string, object>)) return null;
            double[] mn = Vec((Dictionary<string, object>)b, "min"), mx = Vec((Dictionary<string, object>)b, "max");
            if (mn == null || mx == null) return null;
            return new[] { mn[0], mn[1], mn[2], mx[0], mx[1], mx[2] };
        }

        static bool InBox(double[] p, double[] b, double m, double mz)
        {
            return p[0] >= b[0] - m && p[0] <= b[3] + m && p[1] >= b[1] - m && p[1] <= b[4] + m && p[2] >= b[2] - mz && p[2] <= b[5] + mz;
        }

        // Suppression unique (un Ctrl+Z) des objets et de ce qui leur est associe.
        static Dictionary<string, object> DeleteWithAssociated(string client, List<string> ids, List<string> cols, List<string> sounds)
        {
            var all = new List<string>(ids); all.AddRange(cols); all.AddRange(sounds);
            var sb = new StringBuilder("{\"op\":\"api.delete\",\"client\":" + Q(client) + ",\"ids\":[");
            for (int i = 0; i < all.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Q(all[i])); }
            return Call(sb.Append("]}").ToString(), 120000);
        }

        static string AssociatedText(List<string> cols, List<string> sounds)
        {
            var s = new StringBuilder();
            if (cols.Count > 0) s.Append(" + ").Append(cols.Count).Append(cols.Count > 1 ? " collisions" : " collision");
            if (sounds.Count > 0) s.Append(" + ").Append(sounds.Count).Append(sounds.Count > 1 ? " sons" : " son");
            return s.ToString();
        }
    }
}
