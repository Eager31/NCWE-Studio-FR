// Outil « Tracé » (bouton 📐 sous le balai) : on clique des points au sol dans la vue 3D pour dessiner
// des lignes-guides (murs, portes, vitres, escaliers…) que les outils de construction lisent ensuite.
//   clic : ajoute un point · Maj : angle droit par rapport au segment precedent · Entrée : termine la ligne
//   Échap : annule la ligne en cours · ↶ : retire le dernier point
// Fichier : traces.tsv (dossier du plugin) — nom, type, hauteur (m), points x;y;z separes par |

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
        const string TraceName = "NcweFrTrace";
        const string TraceClient = "Outil trace";
        static readonly string[] TraceTypes = { "mur", "porte", "vitre", "escalier", "salle", "autre" };
        static readonly string[] TraceColors = { "cyan", "orange", "lime", "yellow", "white", "magenta" };

        class TraceLine { public string Name, Type; public double Height; public List<double[]> Points = new List<double[]>(); }
        static readonly List<TraceLine> traceLines = new List<TraceLine>();
        static TraceLine traceCurrent;
        static object traceButton, tracePopup, traceCard, traceNameBox, traceTypeBox, traceHeightBox, traceStatusBlock, traceListBlock;
        static volatile bool traceActive;
        static string traceStatus = ""; static int traceStatusVer, traceShownVer = -1;
        static Thread traceThread;
        static Delegate traceClick;
        static double traceCardX0, traceCardY0, traceCardX1, traceCardY1;

        static string TraceFile { get { return Path.Combine(dir, "traces.tsv"); } }
        static readonly CultureInfo TInv = CultureInfo.InvariantCulture;

        // ---------------- thread UI ----------------
        static void TraceTick(object window, object content)
        {
            try
            {
                if (traceButton == null) { CreateTraceTool(content); return; }
                if (traceStatusVer != traceShownVer)
                {
                    traceShownVer = traceStatusVer;
                    SetProp(traceStatusBlock, "Text", traceStatus);
                    SetProp(traceListBlock, "Text", TraceSummary());
                }
                SetProp(tracePopup, "IsOpen", traceActive);
                if (traceActive)
                {
                    if (toolBox != null)
                    {
                        object pt = TransformToRoot(toolBox, 0, 0);
                        double w = Convert.ToDouble(GetProp(toolBox, "ActualWidth"));
                        SetProp(tracePopup, "HorizontalOffset", Convert.ToDouble(GetProp(pt, "X")) + w + 12);
                        SetProp(tracePopup, "VerticalOffset", Convert.ToDouble(GetProp(pt, "Y")) + 40);
                    }
                    MeasureViewport(window);
                    // zone de la carte, en pixels de la vue (les clics dessus ne sont pas des points)
                    object root = GetProp(cleanViewport, "XamlRoot"); double sc = Convert.ToDouble(GetProp(root, "RasterizationScale"));
                    object pc = TransformToRoot(traceCard, 0, 0), pv = TransformToRoot(cleanViewport, 0, 0);
                    traceCardX0 = (Convert.ToDouble(GetProp(pc, "X")) - Convert.ToDouble(GetProp(pv, "X"))) * sc; traceCardY0 = (Convert.ToDouble(GetProp(pc, "Y")) - Convert.ToDouble(GetProp(pv, "Y"))) * sc;
                    traceCardX1 = traceCardX0 + Convert.ToDouble(GetProp(traceCard, "ActualWidth")) * sc; traceCardY1 = traceCardY0 + Convert.ToDouble(GetProp(traceCard, "ActualHeight")) * sc;
                }
            }
            catch (Exception e) { LogOnce("outil trace: " + e.GetBaseException()); }
        }

        static void CreateTraceTool(object content)
        {
            if (cleanButton == null || cleanViewport == null || !(cleanButton.GetType().Name == "ToggleButton")) return;   // apres le balai
            object panel = tVth.GetMethod("GetParent").Invoke(null, new[] { cleanButton });
            object children = GetProp(panel, "Children");
            if (children == null) { traceButton = new object(); return; }
            skipNames.Add(TraceName);
            traceClick = Delegate.CreateDelegate(FindType("Microsoft.UI.Xaml.RoutedEventHandler"), typeof(Plugin).GetMethod("OnTraceClick", BindingFlags.NonPublic | BindingFlags.Static));
            object btn = Load("<ToggleButton xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Tag='tool' ToolTipService.ToolTip='Tracé : cliquez des points au sol pour dessiner des lignes-guides (murs, portes, vitres…)'><TextBlock Text='📐' FontSize='16' FontFamily='Segoe UI Emoji'/></ToggleButton>");
            object style = GetProp(cleanButton, "Style"); if (style != null) SetProp(btn, "Style", style);
            foreach (string p in new[] { "Width", "Height", "Margin", "Padding", "MinWidth", "MinHeight" }) { object v = GetProp(cleanButton, p); if (v != null) SetProp(btn, p, v); }
            SetProp(btn, "Name", TraceName);
            int idx = (int)children.GetType().GetMethod("IndexOf").Invoke(children, new[] { cleanButton }) + 1;
            children.GetType().GetMethod("Insert").Invoke(children, new object[] { idx, btn });
            btn.GetType().GetEvent("Click").AddEventHandler(btn, traceClick);
            traceButton = btn;

            tracePopup = Load("<Popup xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' IsLightDismissEnabled='False'/>");
            SetProp(tracePopup, "Name", TraceName);
            SetProp(tracePopup, "XamlRoot", GetProp(content, "XamlRoot"));
            var x = new StringBuilder();
            x.Append("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Width='330' Padding='12,10' CornerRadius='8' BorderThickness='1'");
            x.Append(" Background='{ThemeResource AcrylicInAppFillColorDefaultBrush}' BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}'><StackPanel Spacing='8'>");
            x.Append("<TextBlock FontWeight='SemiBold' Text='📐 Tracé'/>");
            x.Append("<TextBox x:Name='name' PlaceholderText='Nom de la ligne (ex. mur hall nord)'/>");
            x.Append("<Grid ColumnSpacing='8'><Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>");
            x.Append("<ComboBox x:Name='type' HorizontalAlignment='Stretch' SelectedIndex='0' ToolTipService.ToolTip='Type de ligne'>");
            foreach (string t in TraceTypes) x.Append("<ComboBoxItem Content='" + t + "'/>");
            x.Append("</ComboBox>");
            x.Append("<NumberBox x:Name='height' Grid.Column='1' Width='120' Minimum='0' Maximum='60' Value='4' SmallChange='0.5' SpinButtonPlacementMode='Compact' ToolTipService.ToolTip='Hauteur (m)'/>");
            x.Append("</Grid>");
            x.Append("<Grid ColumnSpacing='6'><Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='*'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>");
            x.Append("<Button x:Name='undo' HorizontalAlignment='Stretch' Content='↶ Point' Tag='undo' ToolTipService.ToolTip='Retire le dernier point'/>");
            x.Append("<Button x:Name='finish' Grid.Column='1' HorizontalAlignment='Stretch' Content='✓ Terminer' Tag='finish' Style='{ThemeResource AccentButtonStyle}' ToolTipService.ToolTip='Termine la ligne (Entrée)'/>");
            x.Append("<Button x:Name='dellast' Grid.Column='2' HorizontalAlignment='Stretch' Content='✕ Ligne' Tag='dellast' ToolTipService.ToolTip='Efface la dernière ligne terminée'/>");
            x.Append("</Grid>");
            x.Append("<TextBlock x:Name='status' FontSize='12' TextWrapping='Wrap'/>");
            x.Append("<TextBlock x:Name='list' FontSize='12' Opacity='0.75' TextWrapping='Wrap' MaxLines='8'/>");
            x.Append("</StackPanel></Border>");
            traceCard = Load(x.ToString());
            SetProp(traceCard, "Name", TraceName);
            MethodInfo f = traceCard.GetType().GetMethod("FindName", new[] { typeof(string) });
            traceNameBox = f.Invoke(traceCard, new object[] { "name" });
            traceTypeBox = f.Invoke(traceCard, new object[] { "type" });
            traceHeightBox = f.Invoke(traceCard, new object[] { "height" });
            traceStatusBlock = f.Invoke(traceCard, new object[] { "status" });
            traceListBlock = f.Invoke(traceCard, new object[] { "list" });
            foreach (string n in new[] { "undo", "finish", "dellast" }) { object b = f.Invoke(traceCard, new object[] { n }); b.GetType().GetEvent("Click").AddEventHandler(b, traceClick); }
            SetProp(tracePopup, "Child", traceCard);
            LoadTraces();
            traceThread = new Thread(TraceLoop); traceThread.IsBackground = true; traceThread.Name = "ncwe-fr-trace"; traceThread.Start();
            Log("outil trace : bouton ajoute (" + traceLines.Count + " lignes)");
        }

        static void TraceSetStatus(string s) { traceStatus = s; traceStatusVer++; }

        static string TraceSummary()
        {
            var sb = new StringBuilder();
            lock (traceLines)
            {
                sb.Append(traceLines.Count + (traceLines.Count > 1 ? " lignes" : " ligne") + " enregistrée(s)");
                for (int i = Math.Max(0, traceLines.Count - 6); i < traceLines.Count; i++)
                    sb.Append("\n· ").Append(traceLines[i].Name).Append(" (").Append(traceLines[i].Type).Append(", ").Append(traceLines[i].Points.Count).Append(" pts, ").Append(traceLines[i].Height.ToString("0.#", TInv)).Append(" m)");
            }
            return sb.ToString();
        }

        static void OnTraceClick(object sender, object e)
        {
            try
            {
                string tag = GetProp(sender, "Tag") as string ?? "";
                switch (tag)
                {
                    case "tool":
                        traceActive = IsChecked(sender);
                        if (traceActive) { TraceSetStatus("Cliquez des points au sol. Maj = angle droit, Entrée = terminer, Échap = annuler."); ThreadPool.QueueUserWorkItem(delegate { DrawTraces(); }); }
                        else { traceCurrent = null; ThreadPool.QueueUserWorkItem(delegate { Call("{\"op\":\"api.annotate\",\"client\":" + Q(TraceClient) + ",\"clear\":true,\"items\":[]}", 5000); }); }
                        return;
                    case "undo": TraceUndo(); return;
                    case "finish": TraceFinish(); return;
                    case "dellast":
                        lock (traceLines) { if (traceLines.Count > 0) traceLines.RemoveAt(traceLines.Count - 1); }
                        SaveTraces(); TraceSetStatus("Dernière ligne effacée."); ThreadPool.QueueUserWorkItem(delegate { DrawTraces(); });
                        return;
                }
            }
            catch (Exception ex) { LogOnce("outil trace clic: " + ex.GetBaseException().Message); }
        }

        // Appele par le hook clavier (thread UI) : Entrée termine, Échap annule la ligne en cours (touches avalées).
        static void TraceKeyFilter(IntPtr msgPtr, int msg)
        {
            if (!traceActive || msg != WM_KEYDOWN || TypingInField()) return;
            long vk = Marshal.ReadIntPtr(msgPtr, IntPtr.Size * 2).ToInt64();
            if (vk == 0x0D) { TraceFinish(); Marshal.WriteInt32(msgPtr, IntPtr.Size, 0); }
            else if (vk == VK_ESCAPE && traceCurrent != null) { traceCurrent = null; TraceSetStatus("Ligne en cours annulée."); ThreadPool.QueueUserWorkItem(delegate { DrawTraces(); }); Marshal.WriteInt32(msgPtr, IntPtr.Size, 0); }
        }

        static void TraceUndo()
        {
            if (traceCurrent != null && traceCurrent.Points.Count > 0) traceCurrent.Points.RemoveAt(traceCurrent.Points.Count - 1);
            TraceSetStatus(traceCurrent == null ? "Aucune ligne en cours." : traceCurrent.Points.Count + " point(s) dans la ligne en cours.");
            ThreadPool.QueueUserWorkItem(delegate { DrawTraces(); });
        }

        static void TraceFinish()
        {
            TraceLine l = traceCurrent;
            if (l == null || l.Points.Count < 2) { TraceSetStatus("Il faut au moins 2 points pour une ligne."); return; }
            string name = ((GetProp(traceNameBox, "Text") as string) ?? "").Trim().Replace('\t', ' ');
            object ti = GetProp(traceTypeBox, "SelectedIndex"); int t = ti is int && (int)ti >= 0 ? (int)ti : 0;
            object hv = GetProp(traceHeightBox, "Value"); double h = hv is double && !double.IsNaN((double)hv) ? (double)hv : 4;
            l.Type = TraceTypes[t]; l.Height = h;
            lock (traceLines) { l.Name = name.Length > 0 ? name : l.Type + " " + (traceLines.Count + 1); traceLines.Add(l); }
            traceCurrent = null;
            SaveTraces();
            TraceSetStatus("Ligne « " + l.Name + " » enregistrée (" + l.Points.Count + " points). Cliquez pour en commencer une autre.");
            ThreadPool.QueueUserWorkItem(delegate { DrawTraces(); });
        }

        // ---------------- thread de fond : clics au sol ----------------
        static void TraceLoop()
        {
            bool wasDown = false; double dx = 0, dy = 0; bool startIn = false;
            while (true)
            {
                try
                {
                    if (traceActive)
                    {
                        bool down = Down(VK_LBUTTON);
                        double px, py; bool inView = CursorInViewport(out px, out py);
                        if (inView && px >= traceCardX0 && px <= traceCardX1 && py >= traceCardY0 && py <= traceCardY1) inView = false;
                        if (down && !wasDown) { dx = px; dy = py; startIn = inView; }
                        if (wasDown && !down && startIn && inView && Math.Abs(px - dx) < 6 && Math.Abs(py - dy) < 6)
                        {
                            double[] p = PickWorld(px, py);
                            if (p != null) TraceAddPoint(p);
                            Call("{\"op\":\"api.select\",\"client\":" + Q(TraceClient) + ",\"ids\":[]}", 5000);   // le clic ne doit rien sélectionner
                        }
                        wasDown = down;
                    }
                    else wasDown = false;
                }
                catch (Exception e) { LogOnce("outil trace boucle: " + e.Message); }
                Thread.Sleep(traceActive ? 30 : 300);
            }
        }

        static void TraceAddPoint(double[] p)
        {
            if (traceCurrent == null) traceCurrent = new TraceLine();
            var pts = traceCurrent.Points;
            if (Down(VK_SHIFT) && pts.Count >= 1)
            {
                // angle droit : on garde l'axe dominant (par rapport au segment precedent, ou aux axes X/Y)
                double[] a = pts[pts.Count - 1];
                double ux = 1, uy = 0;
                if (pts.Count >= 2) { double[] b = pts[pts.Count - 2]; double lx = a[0] - b[0], ly = a[1] - b[1], ln = Math.Sqrt(lx * lx + ly * ly); if (ln > 0.01) { ux = lx / ln; uy = ly / ln; } }
                double vx = p[0] - a[0], vy = p[1] - a[1];
                double along = vx * ux + vy * uy, across = -vx * uy + vy * ux;
                if (Math.Abs(along) >= Math.Abs(across)) { p = new[] { a[0] + ux * along, a[1] + uy * along, p[2] }; }
                else { p = new[] { a[0] - uy * across, a[1] + ux * across, p[2] }; }
            }
            pts.Add(new[] { Math.Round(p[0], 2), Math.Round(p[1], 2), Math.Round(p[2], 2) });
            double len = 0; for (int i = 1; i < pts.Count; i++) len += Math.Sqrt(Math.Pow(pts[i][0] - pts[i - 1][0], 2) + Math.Pow(pts[i][1] - pts[i - 1][1], 2));
            TraceSetStatus(pts.Count + " point(s) · " + len.ToString("0.0", TInv) + " m. Entrée pour terminer.");
            DrawTraces();
        }

        // ---------------- affichage et fichier ----------------
        static void DrawTraces()
        {
            var sb = new StringBuilder();
            Action<TraceLine, string> add = delegate (TraceLine l, string color)
            {
                if (l.Points.Count == 0) return;
                if (sb.Length > 0) sb.Append(',');
                if (l.Points.Count == 1) { sb.Append("{\"type\":\"point\",\"color\":" + Q(color) + ",\"position\":" + P(l.Points[0][0], l.Points[0][1], l.Points[0][2] + 0.1) + "}"); return; }
                sb.Append("{\"type\":\"line\",\"color\":" + Q(color) + ",\"points\":[");
                for (int i = 0; i < l.Points.Count; i++) { if (i > 0) sb.Append(','); sb.Append(P(l.Points[i][0], l.Points[i][1], l.Points[i][2] + 0.1)); }
                sb.Append("]}");
                // hauteur : trait vertical au premier point
                if (l.Height > 0) sb.Append(",{\"type\":\"line\",\"color\":" + Q(color) + ",\"points\":[" + P(l.Points[0][0], l.Points[0][1], l.Points[0][2]) + "," + P(l.Points[0][0], l.Points[0][1], l.Points[0][2] + l.Height) + "]}");
            };
            lock (traceLines) foreach (TraceLine l in traceLines) add(l, TraceColors[Math.Max(0, Array.IndexOf(TraceTypes, l.Type))]);
            TraceLine cur = traceCurrent; if (cur != null) add(cur, "red");
            Call("{\"op\":\"api.annotate\",\"client\":" + Q(TraceClient) + ",\"clear\":true,\"duration\":0,\"items\":[" + sb + "]}", 5000);
        }

        static void SaveTraces()
        {
            try
            {
                var sb = new StringBuilder("# Tracés (outil 📐) : nom, type, hauteur (m), points x;y;z séparés par |\n");
                lock (traceLines)
                    foreach (TraceLine l in traceLines)
                    {
                        sb.Append(l.Name).Append('\t').Append(l.Type).Append('\t').Append(l.Height.ToString("0.###", TInv)).Append('\t');
                        for (int i = 0; i < l.Points.Count; i++) { if (i > 0) sb.Append('|'); sb.Append(l.Points[i][0].ToString("0.##", TInv)).Append(';').Append(l.Points[i][1].ToString("0.##", TInv)).Append(';').Append(l.Points[i][2].ToString("0.##", TInv)); }
                        sb.Append('\n');
                    }
                File.WriteAllText(TraceFile, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e) { LogOnce("traces: " + e.Message); }
        }

        static void LoadTraces()
        {
            try
            {
                if (!File.Exists(TraceFile)) return;
                lock (traceLines)
                {
                    traceLines.Clear();
                    foreach (string line in File.ReadAllLines(TraceFile, Encoding.UTF8))
                    {
                        if (line.Length == 0 || line[0] == '#') continue;
                        string[] c = line.Split('\t'); if (c.Length < 4) continue;
                        var l = new TraceLine { Name = c[0], Type = c[1], Height = double.Parse(c[2], TInv) };
                        foreach (string p in c[3].Split('|')) { string[] v = p.Split(';'); if (v.Length == 3) l.Points.Add(new[] { double.Parse(v[0], TInv), double.Parse(v[1], TInv), double.Parse(v[2], TInv) }); }
                        traceLines.Add(l);
                    }
                }
            }
            catch (Exception e) { LogOnce("traces lecture: " + e.Message); }
        }
    }
}
