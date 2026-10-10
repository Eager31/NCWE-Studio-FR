// Camera du NCWE ou tourne le plugin (sans passer par le canal \\.\pipe\ncwe-studio, partage par toutes les
// fenetres NCWE ouvertes : avec deux NCWE, une teleportation partait dans l'autre fenetre).
//   - CameraGame / SetCameraGame : position, cap et inclinaison (memes unites que l'outil camera de NCWE)
//   - PipeIsMine : le NCWE qui repond sur le canal est-il bien le notre ? (garde-fou avant de supprimer)
//   - raccourcis natifs (beta 5+) : Pivoter = R, Deplacer = T, menu radial = X, si l'utilisateur ne les a pas changes

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace NcweFr
{
    static partial class Plugin
    {
        static object Session()
        {
            object w = mainWindow ?? xrayWindow;
            return w == null ? null : XGetProp(w, "Session");
        }

        // { x, y, z, cap, inclinaison } ou null
        static double[] CameraGame()
        {
            try
            {
                object s = Session(); if (s == null) return null;
                object t = s.GetType().GetMethod("CameraGame", AnyInst, null, Type.EmptyTypes, null).Invoke(s, null);
                object pos = t.GetType().GetField("Item1").GetValue(t);
                Type v3 = pos.GetType();
                return new[] {
                    Convert.ToDouble(v3.GetField("X").GetValue(pos)), Convert.ToDouble(v3.GetField("Y").GetValue(pos)), Convert.ToDouble(v3.GetField("Z").GetValue(pos)),
                    Convert.ToDouble(t.GetType().GetField("Item2").GetValue(t)), Convert.ToDouble(t.GetType().GetField("Item3").GetValue(t)) };
            }
            catch (Exception e) { LogOnce("camera: " + e.GetBaseException().Message); return null; }
        }

        // La demande est faite sur le thread UI (file du dispatcher).
        static double[] pendingCamera;
        static Delegate cameraDelegate;

        static bool SetCameraGame(double x, double y, double z, double heading, double pitch)
        {
            pendingCamera = new[] { x, y, z, heading, pitch };
            if (cameraDelegate == null)
                cameraDelegate = Delegate.CreateDelegate(FindType("Microsoft.UI.Dispatching.DispatcherQueueHandler"), typeof(Plugin).GetMethod("ApplyCameraUi", BindingFlags.NonPublic | BindingFlags.Static));
            return UiEnqueue(cameraDelegate);
        }

        static void ApplyCameraUi()
        {
            try
            {
                double[] c = pendingCamera; if (c == null) return;
                object s = Session(); if (s == null) return;
                MethodInfo set = s.GetType().GetMethod("SetCameraGame", AnyInst);
                Type v3 = set.GetParameters()[0].ParameterType;
                object pos = Activator.CreateInstance(v3, new object[] { (float)c[0], (float)c[1], (float)c[2] });
                set.Invoke(s, new object[] { pos, (float)c[3], (float)c[4] });
            }
            catch (Exception e) { LogOnce("camera (pose): " + e.GetBaseException().Message); }
        }

        // Le canal repond-il pour notre fenetre ? On compare la camera vue par le canal a la notre.
        static bool PipeIsMine()
        {
            double[] mine = CameraGame();
            if (mine == null) return true;                     // impossible a verifier : on ne bloque pas
            var st = Call("{\"op\":\"api.status\",\"client\":" + Q(Client) + "}", 5000);
            object cam; if (!Ok(st) || !st.TryGetValue("camera", out cam)) return false;
            double[] p = Vec((Dictionary<string, object>)cam, "position"); if (p == null) return false;
            double dx = p[0] - mine[0], dy = p[1] - mine[1], dz = p[2] - mine[2];
            return dx * dx + dy * dy + dz * dz < 1.0;
        }

        const string OtherNcweMessage = "Plusieurs NCWE sont ouverts : cette action irait dans une autre fenêtre. Fermez les autres NCWE puis réessayez.";

        // ---------------- raccourcis natifs (beta 5+) ----------------
        static bool? nativeShortcuts;

        // NCWE a-t-il ses propres raccourcis configurables ? (StudioSettings.DefaultShortcuts)
        static bool NativeShortcuts()
        {
            if (nativeShortcuts.HasValue) return nativeShortcuts.Value;
            Type st = FindType("NCWE.Studio.StudioSettings");
            nativeShortcuts = st != null && st.GetField("DefaultShortcuts", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) != null;
            return nativeShortcuts.Value;
        }

        static bool shortcutsDone;
        static void SetupNativeShortcuts(object window)
        {
            if (shortcutsDone || !NativeShortcuts()) return;
            shortcutsDone = true;
            try
            {
                FieldInfo sf = window.GetType().GetField("_settings", AnyInst);
                object settings = sf == null ? null : sf.GetValue(window);
                var map = XGetProp(settings, "Shortcuts") as IDictionary;
                if (map == null) { LogOnce("raccourcis : reglages introuvables"); return; }
                var wanted = new[] { new[] { "Rotate", "R" }, new[] { "Move", "T" }, new[] { "Radial", "X" } };
                var set = new List<string>();
                foreach (string[] w in wanted) if (!map.Contains(w[0])) { map[w[0]] = w[1]; set.Add(w[0] + "=" + w[1]); }
                Log("raccourcis natifs : " + (set.Count > 0 ? string.Join(", ", set.ToArray()) : "deja personnalises, rien change"));
            }
            catch (Exception e) { LogOnce("raccourcis: " + e.GetBaseException().Message); }
        }
    }
}
