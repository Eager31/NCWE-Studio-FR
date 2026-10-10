// Outil "Rayon X" (bouton 🩻 a cote de l'outil Selection, cochable / decochable) :
// quand il est actif, la selection rectangle de NCWE (Maj + glisser dans la vue 3D, Ctrl pour ajouter)
// prend aussi les objets caches derriere d'autres (comme le Rayon X de Cinema 4D).
// NCWE fait sa selection rectangle normale (objets visibles) ; juste apres, on y ajoute tous les objets
// dessines dont le centre est dans le rectangle (meme regle que NCWE : moins de 300 m, modifiables).
// Etat memorise dans rayon-x.txt.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace NcweFr
{
    static partial class Plugin
    {
        const string XRayName = "NcweFrRayonX";
        const BindingFlags AnyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        static object xrayButton, xrayController, xrayWindow;
        static int xrayTries;
        static bool xrayOn;
        static Delegate xrayClick, xrayRunDelegate, xrayProbeDelegate;
        static object xrayPress;                 // Vector2 (pixels de la vue) au debut du rectangle
        static volatile bool xrayPending, xrayDragSeen;
        static System.Threading.Thread xrayThread;

        static string XRayFile { get { return Path.Combine(dir, "rayon-x.txt"); } }

        static void XRayTick(object window, object content)
        {
            try
            {
                if (xrayButton == null) CreateXRayTool(window, content);
                SetupNativeShortcuts(window);
            }
            catch (Exception e) { LogOnce("rayon x: " + e.GetBaseException()); xrayButton = new object(); }
        }

        static void CreateXRayTool(object window, object content)
        {
            MethodInfo find = content.GetType().GetMethod("FindName", new[] { typeof(string) });
            object select = find.Invoke(content, new object[] { "SelectTool" });
            xrayWindow = window;
            if (select == null) { if (++xrayTries > 20) { LogOnce("rayon x: outil Selection introuvable"); xrayButton = new object(); } return; }
            object panel = tVth.GetMethod("GetParent").Invoke(null, new[] { select });
            object children = GetProp(panel, "Children");
            if (children == null) { LogOnce("rayon x: parent de SelectTool sans enfants"); xrayButton = new object(); return; }

            try { xrayOn = File.Exists(XRayFile) && File.ReadAllText(XRayFile).Trim() == "oui"; } catch { }
            skipNames.Add(XRayName);
            xrayClick = Delegate.CreateDelegate(FindType("Microsoft.UI.Xaml.RoutedEventHandler"), typeof(Plugin).GetMethod("OnXRayClick", BindingFlags.NonPublic | BindingFlags.Static));
            Type handler = FindType("Microsoft.UI.Dispatching.DispatcherQueueHandler");
            xrayRunDelegate = Delegate.CreateDelegate(handler, typeof(Plugin).GetMethod("XRayRun", BindingFlags.NonPublic | BindingFlags.Static));
            xrayProbeDelegate = Delegate.CreateDelegate(handler, typeof(Plugin).GetMethod("XRayProbe", BindingFlags.NonPublic | BindingFlags.Static));
            xrayThread = new System.Threading.Thread(XRayLoop); xrayThread.IsBackground = true; xrayThread.Name = "ncwe-fr-rayon-x"; xrayThread.Start();

            object btn = Load("<ToggleButton xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' ToolTipService.ToolTip='Rayon X : la sélection rectangle (Maj + glisser) prend aussi les objets cachés derrière les autres. Ctrl ajoute à la sélection.'><TextBlock Text='🩻' FontSize='16' FontFamily='Segoe UI Emoji'/></ToggleButton>");
            object style = GetProp(select, "Style");
            if (style != null && style.GetType().GetProperty("TargetType") != null)
            {
                object tt = GetProp(style, "TargetType");
                if (tt != null && tt.ToString().Contains("ToggleButton")) SetProp(btn, "Style", style);
            }
            foreach (string p in new[] { "Width", "Height", "Margin", "Padding", "MinWidth", "MinHeight" }) { object v = GetProp(select, p); if (v != null) SetProp(btn, p, v); }
            SetProp(btn, "Name", XRayName);
            SetProp(btn, "IsChecked", xrayOn);
            int idx = (int)children.GetType().GetMethod("IndexOf").Invoke(children, new[] { select }) + 1;
            children.GetType().GetMethod("Insert").Invoke(children, new object[] { idx, btn });
            btn.GetType().GetEvent("Click").AddEventHandler(btn, xrayClick);
            xrayButton = btn;
            Log("rayon x : bouton ajoute (" + (xrayOn ? "actif" : "inactif") + ")");
        }

        static void OnXRayClick(object sender, object e)
        {
            try
            {
                object c = GetProp(sender, "IsChecked");
                xrayOn = c is bool && (bool)c;
                try { File.WriteAllText(XRayFile, xrayOn ? "oui" : "non"); } catch { }
                object session = XRayController() ? XGetProp(xrayController, "Session") : null;
                if (session != null) XSetProp(session, "Status", xrayOn
                    ? "Rayon X actif : Maj + glisser sélectionne aussi les objets cachés derrière (Ctrl ajoute)."
                    : "Rayon X désactivé : Maj + glisser ne prend que les objets visibles.");
            }
            catch (Exception ex) { LogOnce("rayon x clic: " + ex.GetBaseException()); }
        }

        // Thread de fond : tant que le bouton gauche est enfonce, on lit (sur le thread UI) si NCWE trace
        // un rectangle de selection ; au relachement, apres la selection normale de NCWE, on ajoute les caches.
        static void XRayLoop()
        {
            bool wasDown = false; int tick = 0;
            while (true)
            {
                try
                {
                    System.Threading.Thread.Sleep(15);
                    bool down = Down(0x01);
                    if (!xrayOn) { wasDown = down; continue; }
                    if (down)
                    {
                        if (!wasDown) { xrayDragSeen = false; tick = 0; }
                        if (tick++ % 2 == 0) UiEnqueue(xrayProbeDelegate);
                    }
                    else if (wasDown && xrayDragSeen && !xrayPending)
                    {
                        xrayPending = true;
                        System.Threading.Thread.Sleep(120);          // laisser NCWE traiter le relachement
                        if (!UiEnqueue(xrayRunDelegate)) xrayPending = false;
                    }
                    wasDown = down;
                }
                catch (Exception e) { LogOnce("rayon x boucle: " + e.GetBaseException()); }
            }
        }

        static bool UiEnqueue(Delegate d)
        {
            if (tryEnqueue == null || dispatcher == null) return false;
            return (bool)tryEnqueue.Invoke(dispatcher, new object[] { d });
        }

        // Thread UI : NCWE est-il en train de tracer un rectangle (Maj + glisser) ?
        static void XRayProbe()
        {
            try
            {
                if (xrayDragSeen || !XRayController()) return;
                Type t = xrayController.GetType();
                object marquee = t.GetField("_marquee", AnyInst).GetValue(xrayController);
                object dragging = t.GetField("_marqueeDragging", AnyInst).GetValue(xrayController);
                if (!(marquee is bool && (bool)marquee && dragging is bool && (bool)dragging)) return;
                xrayPress = t.GetField("_pressPosition", AnyInst).GetValue(xrayController);
                xrayDragSeen = true;
            }
            catch (Exception e) { LogOnce("rayon x sonde: " + e.GetBaseException()); }
        }

        static void XRayRun()
        {
            try
            {
                Type ct = xrayController.GetType();
                object a = xrayPress;
                object b = ct.GetField("_pointer", AnyInst).GetValue(xrayController);
                object viewport = XGetProp(xrayController, "ViewportPixels");
                object session = XGetProp(xrayController, "Session");
                if (a == null || b == null || viewport == null || session == null) return;
                if (Convert.ToInt32(XGetProp(session, "Layer")) != 0) return;   // calques collisions / occluders : selection normale

                Type v2 = a.GetType();
                float ax = (float)v2.GetField("X").GetValue(a), ay = (float)v2.GetField("Y").GetValue(a);
                float bx = (float)v2.GetField("X").GetValue(b), by = (float)v2.GetField("Y").GetValue(b);
                float minX = Math.Min(ax, bx), maxX = Math.Max(ax, bx), minY = Math.Min(ay, by), maxY = Math.Max(ay, by);
                if (maxX - minX < 2 && maxY - minY < 2) return;

                Type st = session.GetType();
                object renderer = st.GetField("_renderer", AnyInst).GetValue(session);
                object cam = XGetProp(renderer, "RenderedWorldCamera");     // Nullable<CameraFrame> : null si pas de rendu
                if (cam == null) return;
                object drawn = XGetProp(renderer, "DrawnPlacements");
                object assets = st.GetField("_assets", AnyInst).GetValue(session);

                Type occT = st.GetNestedType("MarqueeOccluders", BindingFlags.Public | BindingFlags.NonPublic);
                ConstructorInfo occCtor = occT.GetConstructors(AnyInst)[0];
                object min = Activator.CreateInstance(v2, new object[] { minX, minY });
                object max = Activator.CreateInstance(v2, new object[] { maxX, maxY });
                object occ = occCtor.Invoke(new[] { drawn, assets, cam, viewport, min, max });
                int count = (int)XGetProp(occ, "Count");
                MethodInfo item = occT.GetMethod("Item", AnyInst), mesh = occT.GetMethod("Mesh", AnyInst);
                MethodInfo project = st.GetMethod("ProjectRelative", AnyStatic);
                Type v3 = project.GetParameters()[0].ParameterType;
                Type m4 = FindTypeIn(v3, "System.Numerics.Matrix4x4");
                MethodInfo transform = v3.GetMethod("Transform", new[] { v3, m4 });
                MethodInfo distance = v3.GetMethod("Distance", new[] { v3, v3 });
                MethodInfo invert = m4.GetMethod("Invert", new[] { m4, m4.MakeByRefType() });

                // position de la camera (meme calcul que NCWE)
                object view = XGetProp(cam, "View");
                object[] inv = { view, null };
                invert.Invoke(null, inv);
                object eye = XGetProp(inv[1], "Translation");

                var ids = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                PropertyInfo pId = null, pModel = null, pBounds = null, pCenter = null;
                for (int i = 0; i < count; i++)
                {
                    object dp = item.Invoke(occ, new object[] { i });
                    if (pId == null) { pId = dp.GetType().GetProperty("Id"); pModel = dp.GetType().GetProperty("RelativeModel"); }
                    string id = (string)pId.GetValue(dp, null);
                    if (id == null || id.StartsWith("road:", StringComparison.Ordinal) || seen.Contains(id)) continue;
                    object m = mesh.Invoke(occ, new object[] { i });
                    if (m == null) continue;
                    if (pBounds == null) pBounds = m.GetType().GetProperty("Bounds");
                    object bounds = pBounds.GetValue(m, null);
                    if (pCenter == null) pCenter = bounds.GetType().GetProperty("Center");
                    object center = transform.Invoke(null, new[] { pCenter.GetValue(bounds, null), pModel.GetValue(dp, null) });
                    object p = project.Invoke(null, new[] { center, cam, viewport });   // Vector2? -> null ou Vector2
                    if (p == null) continue;
                    float px = (float)v2.GetField("X").GetValue(p), py = (float)v2.GetField("Y").GetValue(p);
                    if (px < minX || px > maxX || py < minY || py > maxY) continue;
                    if ((float)distance.Invoke(null, new[] { center, eye }) > 300f) continue;
                    seen.Add(id);
                    ids.Add(id);
                }

                // seulement les objets modifiables et non supprimes
                object doc = XGetProp(session, "Document");
                MethodInfo tryGet = doc.GetType().GetMethod("TryGetObject", AnyInst, null, new[] { typeof(string) }, null);
                var selection = (IList)st.GetField("_selection", AnyInst).GetValue(session);
                var result = new List<string>();
                var inResult = new HashSet<string>(StringComparer.Ordinal);
                foreach (object o in selection) { string s = o as string; if (s != null && inResult.Add(s)) result.Add(s); }
                int before = result.Count, readOnly = 0;
                foreach (string id in ids)
                {
                    object sp = tryGet.Invoke(doc, new object[] { id });
                    if (sp == null) continue;
                    object canEdit = XGetProp(sp, "CanEdit"), deleted = XGetProp(sp, "Deleted");
                    if (!(canEdit is bool && (bool)canEdit)) { readOnly++; continue; }
                    if (deleted is bool && (bool)deleted) continue;
                    if (inResult.Add(id)) result.Add(id);
                }
                int added = result.Count - before;
                if (added > 0) st.GetMethod("SelectMany", AnyInst).Invoke(session, new object[] { result });
                XSetProp(session, "Status", "Rayon X : " + result.Count + " objet" + (result.Count > 1 ? "s" : "") + " sélectionné" + (result.Count > 1 ? "s" : "")
                    + (added > 0 ? " (dont " + added + " caché" + (added > 1 ? "s" : "") + " derrière)" : "")
                    + (readOnly > 0 ? " · " + readOnly + " en lecture seule ignorés" : "") + " · Déplacer/Pivoter, Suppr, Ctrl+D");
            }
            catch (Exception e) { LogOnce("rayon x: " + e.GetBaseException()); }
            finally { xrayPending = false; }
        }

        // La vue 3D (ViewportController) est creee apres la fenetre : on la cherche quand on en a besoin.
        static bool XRayController()
        {
            if (xrayController != null) return true;
            if (xrayWindow == null) return false;
            FieldInfo vf = xrayWindow.GetType().GetField("_viewport", AnyInst);
            xrayController = vf == null ? null : vf.GetValue(xrayWindow);
            return xrayController != null;
        }

        static Type FindTypeIn(Type near, string fullName)
        {
            Type t = near.Assembly.GetType(fullName, false);
            return t ?? FindType(fullName);
        }

        static object XGetProp(object o, string name)
        {
            if (o == null) return null;
            PropertyInfo p = o.GetType().GetProperty(name, AnyInst);
            return p == null ? null : p.GetValue(o, null);
        }

        static void XSetProp(object o, string name, object v)
        {
            if (o == null) return;
            PropertyInfo p = o.GetType().GetProperty(name, AnyInst);
            if (p != null && p.CanWrite) p.SetValue(o, v, null);
        }
    }
}
