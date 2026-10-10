// Outils clavier et gizmo :
//  - R -> outil Pivoter, T -> outil Deplacer (remappes sur les touches 3 / 2 de l'app, hors champs de saisie)
//  - Maj pendant une rotation au gizmo : aimantation coupee le temps du geste (rotation libre)
//  - G : aligne les objets selectionnes sur le sol (pente) ou sur le mur proche
//  - Auto-aligner : meme alignement automatique quand on lache un objet deplace
// Tout passe par le thread UI (hook WH_GETMESSAGE) et par l'API du Studio pour l'alignement.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace NcweFr
{
    static partial class Plugin
    {
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] static extern short GetKeyState(int vk);

        const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101;
        const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LBUTTON = 0x01;

        static object mainWindow, mainRoot, snapButton, rotateToolButton;
        static MethodInfo onSnapToggle, getFocused;
        static bool freeRotate;          // aimantation coupee par Maj
        static volatile bool autoAlign;

        static bool Down(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
        static bool HeldNow(int vk) { return (GetKeyState(vk) & 0x8000) != 0; }

        // Appele depuis SelectionTick (thread UI) : memorise les elements utiles de la fenetre principale.
        static void BindMainWindow(object window, object content)
        {
            if (mainWindow != null) return;
            mainWindow = window;
            mainRoot = GetProp(content, "XamlRoot");
            MethodInfo find = content.GetType().GetMethod("FindName", new[] { typeof(string) });
            snapButton = find.Invoke(content, new object[] { "SnapButton" });
            rotateToolButton = find.Invoke(content, new object[] { "RotateTool" });
            foreach (MethodInfo m in window.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                if (m.Name == "OnSnapToggle" && m.GetParameters().Length == 2) onSnapToggle = m;
            Type fm = FindType("Microsoft.UI.Xaml.Input.FocusManager");
            Type xr = FindType("Microsoft.UI.Xaml.XamlRoot");
            if (fm != null && xr != null) getFocused = fm.GetMethod("GetFocusedElement", new[] { xr });
            Log("outils : aimantation " + (snapButton != null && onSnapToggle != null ? "ok" : "INTROUVABLE") + ", outil rotation " + (rotateToolButton != null ? "ok" : "INTROUVABLE"));
        }

        static bool TypingInField()
        {
            try
            {
                if (getFocused == null || mainRoot == null) return false;
                object f = getFocused.Invoke(null, new[] { mainRoot });
                if (f == null) return false;
                string n = f.GetType().Name;
                return n == "TextBox" || n == "PasswordBox" || n == "RichEditBox" || n == "AutoSuggestBox" || n == "NumberBox";
            }
            catch { return false; }
        }

        // Filtre les messages clavier/souris avant que l'app ne les traite (thread UI, dans le hook).
        static void KeyFilter(IntPtr msgPtr)
        {
            if (mainWindow == null) return;
            int off = IntPtr.Size;                    // MSG.message
            int msg = Marshal.ReadInt32(msgPtr, off);
            CleanMouseFilter(msgPtr, msg);
            TraceKeyFilter(msgPtr, msg);
            int wOff = IntPtr.Size * 2, lOff = IntPtr.Size * 3;

            // fin du geste de rotation libre : Maj ou bouton gauche relache
            if (freeRotate && (!Down(VK_SHIFT) || !Down(VK_LBUTTON))) SetFreeRotate(false);

            if (msg != WM_KEYDOWN && msg != WM_KEYUP) return;
            long vk = Marshal.ReadIntPtr(msgPtr, wOff).ToInt64();
            long lp = Marshal.ReadIntPtr(msgPtr, lOff).ToInt64();

            if (vk == VK_SHIFT)
            {
                // Maj tenu (repetition comprise) + bouton gauche + outil Pivoter + aimantation active
                if (msg == WM_KEYDOWN && !freeRotate && Down(VK_LBUTTON) && IsOn(rotateToolButton) && IsOn(snapButton)) SetFreeRotate(true);
                return;
            }
            bool mods = HeldNow(VK_CONTROL) || HeldNow(VK_MENU) || HeldNow(VK_SHIFT);
            if (mods) return;
            if (vk != 'R' && vk != 'T' && vk != 'G') return;
            if (vk != 'G' && NativeShortcuts()) return;     // beta 5+ : R et T sont des raccourcis natifs de NCWE (voir Camera.cs)
            if (TypingInField()) return;

            if (vk == 'G')
            {
                Marshal.WriteInt32(msgPtr, off, 0);   // message neutralise (WM_NULL)
                bool repeat = (lp & (1L << 30)) != 0;
                if (msg == WM_KEYDOWN && !repeat) StartAlign(null);
                return;
            }
            // R -> '3' (Pivoter), T -> '2' (Deplacer) : touche et code de balayage remplaces
            int newVk = vk == 'R' ? '3' : '2';
            int scan = vk == 'R' ? 0x04 : 0x03;
            long newLp = (lp & ~0x00FF0000L) | ((long)scan << 16);
            Marshal.WriteIntPtr(msgPtr, wOff, new IntPtr(newVk));
            Marshal.WriteIntPtr(msgPtr, lOff, new IntPtr(newLp));
        }

        static bool IsOn(object toggle)
        {
            if (toggle == null) return false;
            object v = GetProp(toggle, "IsChecked");
            return v is bool && (bool)v;
        }

        static void SetFreeRotate(bool on)
        {
            if (snapButton == null || onSnapToggle == null) return;
            try
            {
                if (on == freeRotate) return;
                // on passe par la methode de l'app, comme un clic sur le bouton Aimanter
                SetProp(snapButton, "IsChecked", !on);
                onSnapToggle.Invoke(mainWindow, new object[] { snapButton, null });
                freeRotate = on;
            }
            catch (Exception e) { freeRotate = false; LogOnce("rotation libre: " + e.GetBaseException().Message); }
        }

        // ================= alignement sol / mur (thread API) =================
        static void StartAlign(List<Item> only)
        {
            if (opBusy) return;
            RunOp(delegate { AlignItems(only); });
        }

        static string V(double x) { return x.ToString("0.####", CultureInfo.InvariantCulture); }
        static string P(double x, double y, double z) { return "[" + V(x) + "," + V(y) + "," + V(z) + "]"; }

        static Dictionary<string, object> Ray(double ox, double oy, double oz, double dx, double dy, double dz, double max, string ignore)
        {
            string req = "{\"op\":\"api.raycast\",\"client\":" + Q(Client) + ",\"origin\":" + P(ox, oy, oz) + ",\"direction\":" + P(dx, dy, dz)
                + ",\"max_distance\":" + V(max) + ",\"normal\":true,\"show\":false" + (ignore != null ? ",\"ignore\":[" + Q(ignore) + "]" : "") + "}";
            var r = Call(req, 10000);
            object hit; if (!Ok(r) || !r.TryGetValue("hit", out hit) || !(hit is bool) || !(bool)hit) return null;
            return r;
        }

        static double[] Vec(Dictionary<string, object> d, string k)
        {
            object o; if (!d.TryGetValue(k, out o)) return null;
            var l = o as List<object>; if (l == null || l.Count < 3) return null;
            return new[] { Convert.ToDouble(l[0]), Convert.ToDouble(l[1]), Convert.ToDouble(l[2]) };
        }

        static void AlignItems(List<Item> only)
        {
            List<Item> items = only;
            if (items == null) { LoadSelection(); lock (sync) items = new List<Item>(currentItems); }
            if (items.Count == 0) { SetStatus("Sélectionnez d'abord un ou plusieurs objets."); return; }
            if (items.Count > 50) { SetStatus("Alignement limité à 50 objets à la fois."); return; }
            if (!PipeIsMine()) { SetStatus(OtherNcweMessage); return; }
            var sb = new StringBuilder();
            int floors = 0, walls = 0, none = 0;
            foreach (Item it in items)
            {
                if (!it.Editable || !it.HasBounds) { none++; continue; }
                bool wall;
                string part = AlignOne(it, out wall);
                if (part == null) { none++; continue; }
                if (wall) walls++; else floors++;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(part);
            }
            if (sb.Length == 0) { SetStatus("Aucune surface trouvée sous ou à côté de la sélection."); return; }
            var r = Call("{\"op\":\"api.transform\",\"client\":" + Q(Client) + ",\"items\":[" + sb + "]}", 30000);
            if (!Ok(r)) { SetStatus("Alignement refusé : " + Err(r)); return; }
            SetStatus("Aligné : " + floors + " au sol, " + walls + " au mur" + (none > 0 ? ", " + none + " sans surface proche" : "") + " (Ctrl+Z annule).");
            justAligned = true;
        }

        // Retourne l'element JSON de transform_objects pour un objet, ou null.
        static string AlignOne(Item it, out bool wall)
        {
            wall = false;
            double cx = (it.MinX + it.MaxX) / 2, cy = (it.MinY + it.MaxY) / 2, cz = (it.MinZ + it.MaxZ) / 2;
            double hx = (it.MaxX - it.MinX) / 2, hy = (it.MaxY - it.MinY) / 2, hz = (it.MaxZ - it.MinZ) / 2;

            // 1) mur le plus proche : 8 directions horizontales, a mi-hauteur, depuis le bord de la boite
            double best = double.MaxValue; double[] wn = null, wp = null;
            for (int k = 0; k < 8; k++)
            {
                double a = k * Math.PI / 4, dx = Math.Cos(a), dy = Math.Sin(a);
                double edge = Math.Abs(dx) * hx + Math.Abs(dy) * hy;   // distance centre -> bord dans cette direction (approx.)
                var h = Ray(cx, cy, cz, dx, dy, 0, edge + 0.6, it.Id);
                if (h == null) continue;
                double[] n = Vec(h, "normal"), p = Vec(h, "point");
                if (n == null || p == null || Math.Abs(n[2]) > 0.45) continue;   // pas un mur
                double gap = Convert.ToDouble(h["distance"]) - edge;
                if (gap < best) { best = gap; wn = n; wp = p; }
            }
            if (wn != null)
            {
                // dos de l'objet contre le mur : l'avant (+Y local) regarde dans le sens de la normale du mur
                double nl = Math.Sqrt(wn[0] * wn[0] + wn[1] * wn[1]);
                double nx = wn[0] / nl, ny = wn[1] / nl;
                double yaw = Math.Atan2(-nx, ny) * 180 / Math.PI;
                double depth = LocalDepth(it, hx * 2, hy * 2);
                double dist = (cx - wp[0]) * nx + (cy - wp[1]) * ny;        // distance centre -> plan du mur
                double move = depth / 2 - dist;
                wall = true;
                return "{\"id\":" + Q(it.Id) + ",\"rotation\":{\"yaw\":" + V(yaw) + ",\"pitch\":0,\"roll\":0},\"offset\":" + P(nx * move, ny * move, 0) + "}";
            }

            // 2) sol : rayon vers le bas depuis le centre, l'objet epouse la pente
            var g = Ray(cx, cy, cz, 0, 0, -1, hz + 3, it.Id);
            if (g == null) return null;
            double[] gn = Vec(g, "normal"), gp = Vec(g, "point");
            if (gn == null || gp == null || gn[2] < 0.35) return null;
            double psi = it.Yaw * Math.PI / 180, c = Math.Cos(psi), s = Math.Sin(psi);
            // normale dans le repere de l'objet (rotation inverse du lacet)
            double lx = c * gn[0] + s * gn[1], ly = -s * gn[0] + c * gn[1], lz = gn[2];
            double roll = Math.Asin(Math.Max(-1, Math.Min(1, lx))) * 180 / Math.PI;
            double pitch = Math.Atan2(-ly, lz) * 180 / Math.PI;
            double dz = gp[2] - it.MinZ;                                    // pose le dessous sur la surface
            return "{\"id\":" + Q(it.Id) + ",\"rotation\":{\"yaw\":" + V(it.Yaw) + ",\"pitch\":" + V(pitch) + ",\"roll\":" + V(roll) + "},\"offset\":" + P(0, 0, dz) + "}";
        }

        // Profondeur locale (axe Y de l'objet) retrouvee depuis la boite monde et le lacet actuel.
        static double LocalDepth(Item it, double sx, double sy)
        {
            double psi = it.Yaw * Math.PI / 180, c = Math.Abs(Math.Cos(psi)), s = Math.Abs(Math.Sin(psi));
            double det = c * c - s * s;
            if (Math.Abs(det) > 0.2) return Math.Max(0.01, (sy * c - sx * s) / det);
            return Math.Max(0.01, Math.Min(sx, sy) / (c + s));
        }

        // ================= auto-alignement =================
        static readonly Dictionary<string, double[]> lastPositions = new Dictionary<string, double[]>();
        static volatile bool justAligned;
        static DateTime movedAt = DateTime.MinValue;
        static readonly HashSet<string> movedIds = new HashSet<string>();

        // Appele par le poller apres chaque lecture de la selection.
        static void CheckAutoAlign(List<Item> items)
        {
            if (justAligned)
            {
                // nos propres modifications ne relancent pas l'alignement
                justAligned = false; lastPositions.Clear(); movedIds.Clear();
                foreach (Item it in items) lastPositions[it.Id] = new[] { it.X, it.Y, it.Z };
                return;
            }
            foreach (Item it in items)
            {
                double[] p;
                if (lastPositions.TryGetValue(it.Id, out p))
                {
                    double d = Math.Abs(p[0] - it.X) + Math.Abs(p[1] - it.Y) + Math.Abs(p[2] - it.Z);
                    if (d > 0.01) { movedIds.Add(it.Id); movedAt = DateTime.Now; }
                }
                lastPositions[it.Id] = new[] { it.X, it.Y, it.Z };
            }
            // objet lache : bouton gauche relache depuis un instant
            if (autoAlign && movedIds.Count > 0 && !Down(VK_LBUTTON) && (DateTime.Now - movedAt).TotalMilliseconds > 250 && !opBusy)
            {
                var todo = new List<Item>();
                foreach (Item it in items) if (movedIds.Contains(it.Id)) todo.Add(it);
                movedIds.Clear();
                if (todo.Count > 0) StartAlign(todo);
            }
        }
    }
}
