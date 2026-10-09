// Outil "Nettoyage" (bouton balai dans la barre d'outils de NCWE) :
//  - carte d'options : rayon (menu), familles a nettoyer (menu : familles par defaut + familles perso),
//    creation d'une famille perso a partir des objets selectionnes
//  - « Choisir le point » puis un clic au sol : anneau + objets vises en rouge, collisions invisibles en orange
//  - « Nettoyer » : tout part en une suppression (un Ctrl+Z annule)
// Familles par defaut : nettoyage.tsv. Familles perso : familles-perso.tsv. Meme format :
//   famille <TAB> active (oui/non) <TAB> morceau du chemin du modele (minuscules)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace NcweFr
{
    static partial class Plugin
    {
        const string CleanName = "NcweFrNettoyage";
        const string CleanClient = "Outil nettoyage";
        static readonly int[] CleanRadii = { 5, 10, 20, 30, 50, 100 };

        class CleanRule { public string Cat, Pattern; public bool On, Custom; }
        static readonly List<CleanRule> cleanRules = new List<CleanRule>();
        static readonly List<string> cleanCats = new List<string>();
        static readonly HashSet<string> cleanEnabled = new HashSet<string>();
        static int cleanRadiusIndex = 2;

        // etat partage UI / threads
        static volatile bool cleanActive;
        static volatile bool cleanBusy;
        static volatile string cleanStatusText = "";
        static volatile int cleanStatusVer, cleanShownVer = -1;
        static volatile bool cleanRebuildMenu;
        static double vpLeft, vpTop, vpWidthPx, vpHeightPx;   // vue 3D en pixels ecran
        static volatile bool vpKnown;

        // UI
        static object cleanButton, cleanPopup, cleanCard, cleanRadiusBox, cleanFamiliesButton, cleanFamiliesPanel, cleanStatusBlock, cleanNewName, cleanViewport, toolBox;
        static readonly List<object> cleanBoxes = new List<object>();
        static Delegate cleanClick;
        static Thread cleanThread;

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);

        static string CleanFile { get { return Path.Combine(dir, "nettoyage.tsv"); } }
        static string CustomFile { get { return Path.Combine(dir, "familles-perso.tsv"); } }
        static string CleanSettingsFile { get { return Path.Combine(dir, "nettoyage-reglages.txt"); } }

        // ---------------- familles ----------------
        static void LoadCleanRules()
        {
            lock (cleanRules)
            {
                cleanRules.Clear(); cleanCats.Clear();
                foreach (string file in new[] { CleanFile, CustomFile })
                {
                    if (!File.Exists(file)) continue;
                    bool custom = file == CustomFile;
                    foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
                    {
                        if (line.Length == 0 || line[0] == '#') continue;
                        string[] c = line.Split('\t');
                        if (c.Length < 3 || c[2].Trim().Length == 0) continue;
                        var r = new CleanRule { Cat = c[0].Trim(), On = c[1].Trim().ToLowerInvariant() == "oui", Pattern = c[2].Trim().ToLowerInvariant(), Custom = custom };
                        cleanRules.Add(r);
                        if (!cleanCats.Contains(r.Cat)) cleanCats.Add(r.Cat);
                    }
                }
            }
        }

        static void LoadCleanSettings()
        {
            cleanEnabled.Clear();
            bool loaded = false;
            try
            {
                if (File.Exists(CleanSettingsFile))
                {
                    string[] l = File.ReadAllLines(CleanSettingsFile, Encoding.UTF8);
                    if (l.Length > 0) int.TryParse(l[0], out cleanRadiusIndex);
                    for (int i = 1; i < l.Length; i++) if (l[i].Length > 0) cleanEnabled.Add(l[i]);
                    loaded = true;
                }
            }
            catch { }
            if (!loaded) foreach (CleanRule r in cleanRules) if (r.On) cleanEnabled.Add(r.Cat);
            if (cleanRadiusIndex < 0 || cleanRadiusIndex >= CleanRadii.Length) cleanRadiusIndex = 2;
        }

        static void SaveCleanSettings()
        {
            try
            {
                var l = new List<string> { cleanRadiusIndex.ToString() };
                l.AddRange(cleanEnabled);
                File.WriteAllLines(CleanSettingsFile, l.ToArray(), new UTF8Encoding(false));
            }
            catch { }
        }

        // ---------------- thread UI ----------------
        static void CleanTick(object window, object content)
        {
            try
            {
                if (cleanButton == null) { CreateCleanTool(window, content); return; }
                if (cleanRebuildMenu) { cleanRebuildMenu = false; BuildFamiliesMenu(); }
                if (cleanStatusVer != cleanShownVer) { cleanShownVer = cleanStatusVer; SetProp(cleanStatusBlock, "Text", cleanStatusText); SetProp(cleanGoButton, "IsEnabled", cleanPoint != null && !cleanBusy); }
                SetProp(cleanPopup, "IsOpen", cleanActive);
                if (cleanActive) { PositionCleanCard(content); MeasureViewport(window); }
            }
            catch (Exception e) { LogOnce("outil nettoyage: " + e.GetBaseException()); }
        }

        static void CreateCleanTool(object window, object content)
        {
            MethodInfo find = content.GetType().GetMethod("FindName", new[] { typeof(string) });
            object elevator = find.Invoke(content, new object[] { "ElevatorTool" });
            cleanViewport = find.Invoke(content, new object[] { "Viewport" });
            toolBox = find.Invoke(content, new object[] { "ToolBox" });
            if (elevator == null || cleanViewport == null) { LogOnce("outil nettoyage: barre d'outils introuvable"); cleanButton = new object(); return; }
            object panel = tVth.GetMethod("GetParent").Invoke(null, new[] { elevator });
            object children = GetProp(panel, "Children");
            if (children == null) { LogOnce("outil nettoyage: parent de ElevatorTool sans enfants"); cleanButton = new object(); return; }

            LoadCleanRules(); LoadCleanSettings();
            skipNames.Add(CleanName);
            cleanClick = Delegate.CreateDelegate(FindType("Microsoft.UI.Xaml.RoutedEventHandler"), typeof(Plugin).GetMethod("OnCleanClick", BindingFlags.NonPublic | BindingFlags.Static));

            // bouton de la barre d'outils, meme style que les outils de NCWE
            object btn = Load("<ToggleButton xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Tag='tool' ToolTipService.ToolTip='Nettoyage : cliquez dans le monde pour enlever les ordures autour du point'><TextBlock Text='🧹' FontSize='16' FontFamily='Segoe UI Emoji'/></ToggleButton>");
            object style = GetProp(elevator, "Style");
            if (style != null) SetProp(btn, "Style", style);
            foreach (string p in new[] { "Width", "Height", "Margin", "Padding", "MinWidth", "MinHeight" }) { object v = GetProp(elevator, p); if (v != null) SetProp(btn, p, v); }
            SetProp(btn, "Name", CleanName);
            int idx = (int)children.GetType().GetMethod("IndexOf").Invoke(children, new[] { elevator }) + 1;
            children.GetType().GetMethod("Insert").Invoke(children, new object[] { idx, btn });
            btn.GetType().GetEvent("Click").AddEventHandler(btn, cleanClick);
            cleanButton = btn;

            // carte d'options (popup), affichee quand l'outil est actif
            cleanPopup = Load("<Popup xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' IsLightDismissEnabled='False'/>");
            SetProp(cleanPopup, "Name", CleanName);
            SetProp(cleanPopup, "XamlRoot", GetProp(content, "XamlRoot"));
            var x = new StringBuilder();
            x.Append("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Width='320' Padding='12,10' CornerRadius='8' BorderThickness='1'");
            x.Append(" Background='{ThemeResource AcrylicInAppFillColorDefaultBrush}' BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}'><StackPanel Spacing='8'>");
            x.Append("<TextBlock FontWeight='SemiBold' Text='🧹 Nettoyage'/>");
            x.Append("<Grid ColumnSpacing='8'><Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='2*'/></Grid.ColumnDefinitions>");
            x.Append("<ComboBox x:Name='radius' HorizontalAlignment='Stretch' SelectedIndex='" + cleanRadiusIndex + "' ToolTipService.ToolTip='Rayon du nettoyage'>");
            foreach (int r in CleanRadii) x.Append("<ComboBoxItem Content='" + r + " m'/>");
            x.Append("</ComboBox>");
            x.Append("<DropDownButton x:Name='families' Grid.Column='1' HorizontalAlignment='Stretch' HorizontalContentAlignment='Left' Content='Familles'>");
            x.Append("<DropDownButton.Flyout><Flyout Placement='BottomEdgeAlignedLeft'><StackPanel MinWidth='300' Spacing='6'>");
            x.Append("<StackPanel x:Name='familiesPanel' Spacing='2'/>");
            x.Append("<Rectangle Height='1' Fill='{ThemeResource CardStrokeColorDefaultBrush}'/>");
            x.Append("<TextBlock FontSize='12' Opacity='0.7' TextWrapping='Wrap' Text='Nouvelle famille perso : sélectionnez des objets types dans le monde, nommez la famille, puis Créer.'/>");
            x.Append("<Grid ColumnSpacing='6'><Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>");
            x.Append("<TextBox x:Name='newname' PlaceholderText='Nom de la famille'/><Button Grid.Column='1' Content='Créer' Tag='newfam' x:Name='newfam'/></Grid>");
            x.Append("</StackPanel></Flyout></DropDownButton.Flyout></DropDownButton>");
            x.Append("</Grid>");
            x.Append("<Grid ColumnSpacing='8'><Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>");
            x.Append("<Button x:Name='pick' HorizontalAlignment='Stretch' Content='📍 Choisir le point' Tag='pick' ToolTipService.ToolTip='Puis cliquez une fois au sol dans la vue 3D'/>");
            x.Append("<Button x:Name='cleanbtn' Grid.Column='1' HorizontalAlignment='Stretch' Content='🧹 Nettoyer' Tag='clean' IsEnabled='False' Style='{ThemeResource AccentButtonStyle}' ToolTipService.ToolTip='Enlève les objets encadrés en rouge (Ctrl+Z annule)'/>");
            x.Append("</Grid>");
            x.Append("<TextBlock x:Name='status' FontSize='12' TextWrapping='Wrap'/>");
            x.Append("</StackPanel></Border>");
            cleanCard = Load(x.ToString());
            SetProp(cleanCard, "Name", CleanName);
            MethodInfo f = cleanCard.GetType().GetMethod("FindName", new[] { typeof(string) });
            cleanRadiusBox = f.Invoke(cleanCard, new object[] { "radius" });
            cleanFamiliesButton = f.Invoke(cleanCard, new object[] { "families" });
            cleanFamiliesPanel = f.Invoke(cleanCard, new object[] { "familiesPanel" });
            cleanStatusBlock = f.Invoke(cleanCard, new object[] { "status" });
            cleanNewName = f.Invoke(cleanCard, new object[] { "newname" });
            foreach (string n in new[] { "newfam", "pick", "cleanbtn" })
            {
                object b = f.Invoke(cleanCard, new object[] { n });
                b.GetType().GetEvent("Click").AddEventHandler(b, cleanClick);
                if (n == "cleanbtn") cleanGoButton = b;
            }
            EventInfo sel = cleanRadiusBox.GetType().GetEvent("SelectionChanged");
            sel.AddEventHandler(cleanRadiusBox, Delegate.CreateDelegate(sel.EventHandlerType, typeof(Plugin).GetMethod("OnCleanRadius", BindingFlags.NonPublic | BindingFlags.Static)));
            SetProp(cleanPopup, "Child", cleanCard);
            BuildFamiliesMenu();

            cleanThread = new Thread(CleanLoop); cleanThread.IsBackground = true; cleanThread.Name = "ncwe-fr-nettoyage"; cleanThread.Start();
            Log("outil nettoyage : bouton ajoute (" + cleanCats.Count + " familles)");
        }

        // Menu des familles : cases a cocher ; les familles perso ont un bouton de suppression.
        static void BuildFamiliesMenu()
        {
            object items = GetProp(cleanFamiliesPanel, "Children");
            items.GetType().GetMethod("Clear").Invoke(items, null);
            cleanBoxes.Clear();
            MethodInfo add = items.GetType().GetMethod("Add");
            for (int i = 0; i < cleanCats.Count; i++)
            {
                string cat = cleanCats[i];
                bool custom = false; lock (cleanRules) foreach (CleanRule r in cleanRules) if (r.Cat == cat && r.Custom) custom = true;
                var x = new StringBuilder("<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>");
                x.Append("<CheckBox Content='" + X(cat) + (custom ? " (perso)" : "") + "' Tag='fam|" + i + "' IsChecked='" + (cleanEnabled.Contains(cat) ? "True" : "False") + "'/>");
                if (custom) x.Append("<Button Grid.Column='1' Content='✕' Padding='8,2' Background='Transparent' BorderThickness='0' Tag='delfam|" + i + "' ToolTipService.ToolTip='Supprimer cette famille perso'/>");
                x.Append("</Grid>");
                object row = Load(x.ToString());
                add.Invoke(items, new[] { row });
                foreach (object child in (System.Collections.IEnumerable)GetProp(row, "Children"))
                {
                    child.GetType().GetEvent("Click").AddEventHandler(child, cleanClick);
                    if (child.GetType().Name == "CheckBox") cleanBoxes.Add(child);
                }
            }
            SetProp(cleanFamiliesButton, "Content", "Familles : " + cleanEnabled.Count + " / " + cleanCats.Count);
        }

        static void PositionCleanCard(object content)
        {
            // a droite de la barre d'outils verticale
            if (toolBox == null) return;
            object pt = TransformToRoot(toolBox, 0, 0);
            double w = Convert.ToDouble(GetProp(toolBox, "ActualWidth"));
            SetProp(cleanPopup, "HorizontalOffset", Convert.ToDouble(GetProp(pt, "X")) + w + 12);
            SetProp(cleanPopup, "VerticalOffset", Convert.ToDouble(GetProp(pt, "Y")));
        }

        static object TransformToRoot(object el, double x, double y)
        {
            object tr = el.GetType().GetMethod("TransformToVisual").Invoke(el, new object[] { null });
            Type pointType = FindType("Windows.Foundation.Point");
            object p = Activator.CreateInstance(pointType, new object[] { x, y });
            return tr.GetType().GetMethod("TransformPoint").Invoke(tr, new[] { p });
        }

        // Rectangle de la vue 3D en pixels ecran (pour convertir la souris en pixel de la vue).
        static void MeasureViewport(object window)
        {
            try
            {
                Type wn = FindType("WinRT.Interop.WindowNative");
                IntPtr hwnd = (IntPtr)wn.GetMethod("GetWindowHandle").Invoke(null, new[] { window });
                var o = new POINT(); ClientToScreen(hwnd, ref o);
                object root = GetProp(cleanViewport, "XamlRoot");
                double scale = Convert.ToDouble(GetProp(root, "RasterizationScale"));
                object pt = TransformToRoot(cleanViewport, 0, 0);
                double aw = Convert.ToDouble(GetProp(cleanViewport, "ActualWidth")), ah = Convert.ToDouble(GetProp(cleanViewport, "ActualHeight"));
                vpLeft = o.X + Convert.ToDouble(GetProp(pt, "X")) * scale;
                vpTop = o.Y + Convert.ToDouble(GetProp(pt, "Y")) * scale;
                vpWidthPx = aw * scale; vpHeightPx = ah * scale;
                vpKnown = aw > 0;
            }
            catch (Exception e) { LogOnce("outil nettoyage vue: " + e.GetBaseException().Message); }
        }

        static void OnCleanRadius(object sender, object e)
        {
            object v = GetProp(cleanRadiusBox, "SelectedIndex");
            if (v is int && (int)v >= 0) { cleanRadiusIndex = (int)v; SaveCleanSettings(); RefreshCleanPreview(); }
        }

        static void OnCleanClick(object sender, object e)
        {
            try
            {
                string tag = GetProp(sender, "Tag") as string ?? "";
                string[] t = tag.Split('|');
                switch (t[0])
                {
                    case "tool":
                        cleanActive = IsChecked(sender);
                        cleanArmed = false;
                        if (!cleanActive) { cleanPoint = null; ClearCleanMarks(); }
                        CleanSetStatus(cleanActive ? "« Choisir le point », puis cliquez au sol dans la vue." : "");
                        return;
                    case "pick":
                        cleanArmed = true; cleanPoint = null; ClearCleanMarks();
                        RunOp(delegate { cleanSelBefore = SelectionKey(); });
                        CleanSetStatus("Cliquez une fois au sol dans la vue 3D…");
                        return;
                    case "clean":
                        if (cleanPoint == null || cleanBusy) return;
                        { double[] p = cleanPoint; ThreadPool.QueueUserWorkItem(delegate { CleanDeleteAt(p); }); }
                        return;
                    case "fam":
                    {
                        string cat = cleanCats[int.Parse(t[1])];
                        if (IsChecked(sender)) cleanEnabled.Add(cat); else cleanEnabled.Remove(cat);
                        SaveCleanSettings(); RefreshCleanPreview();
                        SetProp(cleanFamiliesButton, "Content", "Familles : " + cleanEnabled.Count + " / " + cleanCats.Count);
                        return;
                    }
                    case "delfam":
                    {
                        string cat = cleanCats[int.Parse(t[1])];
                        RemoveCustomFamily(cat);
                        CleanSetStatus("Famille « " + cat + " » supprimée.");
                        return;
                    }
                    case "newfam":
                    {
                        string name = ((GetProp(cleanNewName, "Text") as string) ?? "").Trim().Replace('\t', ' ');
                        if (name.Length == 0) { CleanSetStatus("Donnez un nom à la famille."); return; }
                        SetProp(cleanNewName, "Text", "");
                        RunOp(delegate { CreateFamilyFromSelection(name); });
                        return;
                    }
                }
            }
            catch (Exception ex) { LogOnce("outil nettoyage clic: " + ex.GetBaseException().Message); }
        }

        static void CleanSetStatus(string s) { cleanStatusText = s; cleanStatusVer++; }

        // ---------------- familles perso ----------------
        static void CreateFamilyFromSelection(string name)
        {
            var r = Call("{\"op\":\"api.selection\",\"client\":" + Q(CleanClient) + "}", 20000);
            object so; if (!Ok(r) || !r.TryGetValue("selection", out so) || !(so is List<object>) || ((List<object>)so).Count == 0)
            { CleanSetStatus("Sélectionnez d'abord des objets types (canettes, sacs…) dans le monde."); return; }
            var assets = new HashSet<string>();
            foreach (object o in (List<object>)so) { var d = o as Dictionary<string, object>; string a = d == null ? null : Str(d, "asset"); if (!string.IsNullOrEmpty(a)) assets.Add(a.ToLowerInvariant()); }
            if (assets.Count == 0) { CleanSetStatus("Les objets sélectionnés n'ont pas de modèle utilisable."); return; }
            var sb = new StringBuilder();
            if (!File.Exists(CustomFile)) sb.Append("# Familles perso de l'outil Nettoyage : famille, active (oui/non), morceau du chemin du modele\n");
            foreach (string a in assets) sb.Append(name).Append("\toui\t").Append(a).Append('\n');
            File.AppendAllText(CustomFile, sb.ToString(), new UTF8Encoding(false));
            LoadCleanRules(); cleanEnabled.Add(name); SaveCleanSettings();
            cleanRebuildMenu = true;
            CleanSetStatus("Famille perso « " + name + " » créée : " + assets.Count + " modèle" + (assets.Count > 1 ? "s" : "") + ".");
        }

        static void RemoveCustomFamily(string cat)
        {
            if (!File.Exists(CustomFile)) return;
            var keep = new List<string>();
            foreach (string l in File.ReadAllLines(CustomFile, Encoding.UTF8)) if (!l.StartsWith(cat + "\t")) keep.Add(l);
            File.WriteAllLines(CustomFile, keep.ToArray(), new UTF8Encoding(false));
            LoadCleanRules(); cleanEnabled.Remove(cat); SaveCleanSettings();
            cleanRebuildMenu = true;
        }

        // ---------------- choix du point : un clic au sol apres "Choisir le point" ----------------
        const int VK_ESCAPE = 0x1B;
        static volatile bool cleanArmed;
        static volatile string cleanSelBefore = "";
        static volatile double[] cleanPoint;
        static object cleanGoButton;

        // Appele par le hook (thread UI) : Echap annule le choix du point. Aucun clic n'est intercepte.
        static void CleanMouseFilter(IntPtr msgPtr, int msg)
        {
            if (!cleanActive || msg != WM_KEYDOWN) return;
            if ((long)Marshal.ReadIntPtr(msgPtr, IntPtr.Size * 2) != VK_ESCAPE) return;
            if (cleanArmed) { cleanArmed = false; CleanSetStatus("Choix du point annulé."); }
        }

        static string SelectionKey()
        {
            var r = Call("{\"op\":\"api.status\",\"client\":" + Q(CleanClient) + "}", 5000);
            object s; return Ok(r) && r.TryGetValue("selection", out s) ? Json.Write(s) : "";
        }

        static bool CursorInViewport(out double px, out double py)
        {
            px = py = 0;
            if (!vpKnown) return false;
            POINT c; GetCursorPos(out c);
            px = c.X - vpLeft; py = c.Y - vpTop;
            return px >= 0 && py >= 0 && px < vpWidthPx && py < vpHeightPx;
        }

        // Thread de fond : attend le clic au sol (NCWE selectionne l'objet clique), en deduit le point.
        static void CleanLoop()
        {
            bool wasDown = false;
            while (true)
            {
                try
                {
                    if (cleanActive && cleanArmed)
                    {
                        bool down = Down(VK_LBUTTON);
                        double px = 0, py = 0;
                        bool inView = CursorInViewport(out px, out py);
                        if (wasDown && !down)
                        {
                            Thread.Sleep(250);   // NCWE met a jour sa selection
                            string key = SelectionKey();
                            double[] p = null;
                            if (key != cleanSelBefore && key != "[]") p = PointFromClick(inView, px, py);
                            if (p != null)
                            {
                                cleanArmed = false; cleanPoint = p;
                                CleanSetStatus("Point choisi : recherche des objets…");    // l'apercu peut prendre quelques secondes
                                CleanPreview(p);
                            }
                        }
                        wasDown = down;
                    }
                    else wasDown = false;
                }
                catch (Exception e) { LogOnce("outil nettoyage boucle: " + e.Message); }
                Thread.Sleep(cleanArmed ? 40 : 300);
            }
        }

        // Point exact sous la souris si possible, sinon position de l'objet selectionne.
        static double[] PointFromClick(bool inView, double px, double py)
        {
            var sel = Call("{\"op\":\"api.selection\",\"client\":" + Q(CleanClient) + "}", 10000);
            object so; Dictionary<string, object> obj = null;
            if (Ok(sel) && sel.TryGetValue("selection", out so) && so is List<object> && ((List<object>)so).Count > 0) obj = ((List<object>)so)[((List<object>)so).Count - 1] as Dictionary<string, object>;
            if (inView)
            {
                double[] hit = PickWorld(px, py);
                if (hit != null) return hit;
            }
            if (obj == null) return null;
            object b; double[] pos = Vec(obj, "position");
            if (obj.TryGetValue("bounds", out b) && b is Dictionary<string, object>)
            {
                double[] mn = Vec((Dictionary<string, object>)b, "min"), mx = Vec((Dictionary<string, object>)b, "max");
                if (mn != null && mx != null) return new[] { (mn[0] + mx[0]) / 2, (mn[1] + mx[1]) / 2, mx[2] };
            }
            return pos;
        }

        static double[] PickWorld(double px, double py)
        {
            var st = Call("{\"op\":\"api.status\",\"client\":" + Q(CleanClient) + "}", 5000);
            object vp; double sx = 1, sy = 1;
            if (Ok(st) && st.TryGetValue("viewport", out vp) && vp is Dictionary<string, object> && vpWidthPx > 0)
            {
                var v = (Dictionary<string, object>)vp;
                sx = Convert.ToDouble(v["width"]) / vpWidthPx; sy = Convert.ToDouble(v["height"]) / vpHeightPx;
            }
            var r = Call("{\"op\":\"api.pick\",\"client\":" + Q(CleanClient) + ",\"x\":" + ((int)(px * sx)) + ",\"y\":" + ((int)(py * sy)) + "}", 5000);
            object hit; if (!Ok(r) || !r.TryGetValue("hit", out hit) || !(hit is bool) || !(bool)hit) return null;
            return Vec(r, "point");
        }

        static string RingItem(double[] c, double radius, string color)
        {
            var sb = new StringBuilder("{\"type\":\"line\",\"closed\":true,\"color\":" + Q(color) + ",\"points\":[");
            for (int k = 0; k < 48; k++) { double a = k * Math.PI * 2 / 48; if (k > 0) sb.Append(','); sb.Append(P(c[0] + Math.Cos(a) * radius, c[1] + Math.Sin(a) * radius, c[2] + 0.15)); }
            return sb.Append("]}").ToString();
        }

        static double CleanRadius() { return CleanRadii[Math.Max(0, Math.Min(CleanRadii.Length - 1, cleanRadiusIndex))]; }

        static void ClearCleanMarks()
        {
            ThreadPool.QueueUserWorkItem(delegate { try { Call("{\"op\":\"api.annotate\",\"client\":" + Q(CleanClient) + ",\"clear\":true,\"items\":[]}", 5000); } catch { } });
        }

        static void RefreshCleanPreview()
        {
            double[] p = cleanPoint;
            if (p != null && cleanActive) { CleanSetStatus("Recherche des objets…"); ThreadPool.QueueUserWorkItem(delegate { CleanPreview(p); }); }
        }
        // Objets a enlever autour d'un point (familles cochees).
        // cols (facultatif) recoit les collisions du jeu posees sur ces ordures (ou sur des ordures deja enlevees)
        // qui ne sont liees a aucun objet encore present : sinon elles restent en murs invisibles qui bloquent le joueur.
        static List<string> CleanTargets(double[] c, double radius, Dictionary<string, int> perCat, List<string> cols = null)
        {
            var ids = new List<string>();
            var cats = new HashSet<string>(cleanEnabled);
            List<CleanRule> rules; lock (cleanRules) rules = new List<CleanRule>(cleanRules);
            var trashBoxes = new List<double[]>();                 // boites des ordures (a enlever ou deja enlevees)
            var others = new List<KeyValuePair<string, double[]>>(); // objets presents qui restent (id, boite)
            foreach (string kind in new[] { "mesh", "instanced_mesh", "decal" })
            {
                var r = Call("{\"op\":\"api.query\",\"client\":" + Q(CleanClient) + ",\"center\":" + P(c[0], c[1], c[2]) + ",\"radius\":" + V(radius)
                    + ",\"kinds\":[" + Q(kind) + "],\"limit\":2000,\"show\":false" + (cols != null && kind != "decal" ? ",\"include_deleted\":true" : "") + "}", 30000);
                object objs; if (!Ok(r) || !r.TryGetValue("objects", out objs) || !(objs is List<object>)) continue;
                foreach (object o in (List<object>)objs)
                {
                    var d = o as Dictionary<string, object>; if (d == null) continue;
                    object del; bool deleted = d.TryGetValue("deleted", out del) && del is bool && (bool)del;
                    object ed; bool editable = !(d.TryGetValue("editable", out ed) && ed is bool && !(bool)ed);
                    string asset = (Str(d, "asset") ?? "").ToLowerInvariant();
                    bool trash = false;
                    if (asset.Length > 0)
                        foreach (CleanRule rule in rules)
                        {
                            if (!cats.Contains(rule.Cat) || asset.IndexOf(rule.Pattern, StringComparison.Ordinal) < 0) continue;
                            trash = true;
                            if (editable && !deleted)
                            {
                                ids.Add(Str(d, "id"));
                                if (perCat != null) { int k; perCat.TryGetValue(rule.Cat, out k); perCat[rule.Cat] = k + 1; }
                            }
                            break;
                        }
                    if (cols == null || kind == "decal") continue;
                    double[] box = Box(d);
                    if (box == null) continue;
                    if (trash) trashBoxes.Add(box);
                    else if (!deleted) others.Add(new KeyValuePair<string, double[]>(Str(d, "id"), box));
                }
            }
            if (cols != null && trashBoxes.Count > 0) OrphanCollisions(c, radius, trashBoxes, others, cols);
            if (cols != null && ids.Count > 0) LinkedCollisions(ids, cols);
            return ids;
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

        // Collisions liees (selon NCWE) aux objets a enlever.
        static void LinkedCollisions(List<string> ids, List<string> cols)
        {
            var have = new HashSet<string>(cols, StringComparer.Ordinal);
            for (int i = 0; i < ids.Count; i += 300)
            {
                var sb = new StringBuilder("{\"op\":\"api.object\",\"client\":" + Q(CleanClient) + ",\"ids\":[");
                for (int k = i; k < Math.Min(ids.Count, i + 300); k++) { if (k > i) sb.Append(','); sb.Append(Q(ids[k])); }
                var ro = Call(sb.Append("]}").ToString(), 30000);
                object list; if (!Ok(ro) || !ro.TryGetValue("objects", out list) || !(list is List<object>)) return;
                foreach (object o in (List<object>)list)
                {
                    var d = o as Dictionary<string, object>; object lk; if (d == null || !d.TryGetValue("linked", out lk) || !(lk is Dictionary<string, object>)) continue;
                    object lc; if (((Dictionary<string, object>)lk).TryGetValue("collision", out lc) && lc is List<object>)
                        foreach (object id in (List<object>)lc) { string s = id as string; if (s != null && have.Add(s)) cols.Add(s); }
                }
            }
        }

        // Collisions du jeu posees sur une ordure et liees a aucun objet qui reste.
        static void OrphanCollisions(double[] c, double radius, List<double[]> trashBoxes, List<KeyValuePair<string, double[]>> others, List<string> cols)
        {
            var r = Call("{\"op\":\"api.query\",\"client\":" + Q(CleanClient) + ",\"center\":" + P(c[0], c[1], c[2]) + ",\"radius\":" + V(radius + 2)
                + ",\"kinds\":[\"collision\"],\"limit\":2000,\"show\":false}", 30000);
            object objs; if (!Ok(r) || !r.TryGetValue("objects", out objs) || !(objs is List<object>)) return;
            var cand = new List<KeyValuePair<string, double[]>>();
            foreach (object o in (List<object>)objs)
            {
                var d = o as Dictionary<string, object>; if (d == null) continue;
                object ed; if (d.TryGetValue("editable", out ed) && ed is bool && !(bool)ed) continue;
                double[] p = Vec(d, "position"); if (p == null) continue;
                foreach (double[] b in trashBoxes) if (InBox(p, b, 0.1, 0.2)) { cand.Add(new KeyValuePair<string, double[]>(Str(d, "id"), p)); break; }
            }
            if (cand.Count == 0) return;
            // objets qui restent pres des candidates : leurs collisions liees sont gardees
            var near = new List<string>();
            foreach (var ob in others)
                foreach (var cd in cand) if (InBox(cd.Value, ob.Value, 1.0, 1.0)) { near.Add(ob.Key); break; }
            var keep = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < near.Count; i += 300)
            {
                var sb = new StringBuilder("{\"op\":\"api.object\",\"client\":" + Q(CleanClient) + ",\"ids\":[");
                for (int k = i; k < Math.Min(near.Count, i + 300); k++) { if (k > i) sb.Append(','); sb.Append(Q(near[k])); }
                var ro = Call(sb.Append("]}").ToString(), 30000);
                object list; if (!Ok(ro) || !ro.TryGetValue("objects", out list) || !(list is List<object>)) { cand.Clear(); return; }   // prudence : rien si on ne sait pas
                foreach (object o in (List<object>)list)
                {
                    var d = o as Dictionary<string, object>; object lk; if (d == null || !d.TryGetValue("linked", out lk) || !(lk is Dictionary<string, object>)) continue;
                    object lc; if (((Dictionary<string, object>)lk).TryGetValue("collision", out lc) && lc is List<object>) foreach (object id in (List<object>)lc) if (id is string) keep.Add((string)id);
                }
            }
            foreach (var cd in cand) if (!keep.Contains(cd.Key)) cols.Add(cd.Key);
        }

        static string CleanDetail(Dictionary<string, int> perCat)
        {
            var d = new StringBuilder();
            foreach (var kv in perCat) { if (d.Length > 0) d.Append(" · "); d.Append(kv.Value).Append(' ').Append(kv.Key.ToLowerInvariant()); }
            return d.ToString();
        }

        // Point choisi : anneau + objets vises en rouge.
        static void CleanPreview(double[] c)
        {
            double radius = CleanRadius();
            var perCat = new Dictionary<string, int>();
            var cols = new List<string>();
            List<string> ids = CleanTargets(c, radius, perCat, cols);
            var sb = new StringBuilder(RingItem(c, radius, ids.Count + cols.Count > 0 ? "red" : "amber"));
            if (ids.Count > 0)
            {
                sb.Append(",{\"type\":\"objects\",\"color\":\"red\",\"ids\":[");
                for (int i = 0; i < ids.Count && i < 2000; i++) { if (i > 0) sb.Append(','); sb.Append(Q(ids[i])); }
                sb.Append("]}");
            }
            if (cols.Count > 0)
            {
                sb.Append(",{\"type\":\"objects\",\"color\":\"orange\",\"ids\":[");
                for (int i = 0; i < cols.Count && i < 2000; i++) { if (i > 0) sb.Append(','); sb.Append(Q(cols[i])); }
                sb.Append("]}");
            }
            Call("{\"op\":\"api.annotate\",\"client\":" + Q(CleanClient) + ",\"clear\":true,\"duration\":0,\"items\":[" + sb + "]}", 5000);
            string colTxt = cols.Count > 0 ? " + " + cols.Count + " collision" + (cols.Count > 1 ? "s" : "") + " invisible" + (cols.Count > 1 ? "s" : "") + " (en orange)" : "";
            CleanSetStatus(ids.Count + cols.Count == 0 ? "Point choisi : rien à nettoyer dans " + radius + " m."
                : ids.Count + " objet" + (ids.Count > 1 ? "s" : "") + " à enlever" + (ids.Count > 0 ? " (" + CleanDetail(perCat) + ")" : "") + colTxt + ". Cliquez « Nettoyer ».");
        }

        // Bouton Nettoyer : une seule suppression (un Ctrl+Z annule tout).
        static void CleanDeleteAt(double[] c)
        {
            cleanBusy = true;
            try
            {
                double radius = CleanRadius();
                var perCat = new Dictionary<string, int>();
                var cols = new List<string>();
                List<string> ids = CleanTargets(c, radius, perCat, cols);
                if (ids.Count + cols.Count == 0) { CleanSetStatus("Rien à nettoyer ici (" + radius + " m)."); return; }
                var all = new List<string>(ids); all.AddRange(cols);
                var sb = new StringBuilder("{\"op\":\"api.delete\",\"client\":" + Q(CleanClient) + ",\"ids\":[");
                for (int i = 0; i < all.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Q(all[i])); }
                sb.Append("]}");
                var r = Call(sb.ToString(), 120000);
                Call("{\"op\":\"api.annotate\",\"client\":" + Q(CleanClient) + ",\"clear\":true,\"duration\":0,\"items\":[" + RingItem(c, radius, "amber") + "]}", 5000);
                if (Ok(r)) CleanSetStatus(ids.Count + " objets enlevés" + (ids.Count > 0 ? " (" + CleanDetail(perCat) + ")" : "")
                    + (cols.Count > 0 ? " + " + cols.Count + " collisions invisibles" : "") + ". Ctrl+Z annule.");
                else CleanSetStatus("Nettoyage refusé : " + Err(r));
            }
            finally { cleanBusy = false; CleanSetStatus(cleanStatusText); }
        }
    }
}