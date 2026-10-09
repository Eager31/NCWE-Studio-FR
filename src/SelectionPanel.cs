// Panneau "Selection" : des que 2 objets ou plus sont selectionnes, liste les props selectionnes
// regroupes par mesh, avec des actions rapides (isoler, retirer, selectionner / supprimer les similaires).
// Passe par l'API officielle du Studio (pipe "ncwe-studio", le meme que les agents IA) :
// chaque suppression est une etape d'annulation normale (Ctrl+Z).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security;
using System.Text;
using System.Threading;

namespace NcweFr
{
    static partial class Plugin
    {
        const string PanelName = "NcweFrSelection";
        const string Client = "Panneau selection";

        class Item
        {
            public string Id, Name, Asset, Node; public bool Editable, HasBounds;
            public double X, Y, Z, Yaw, MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        }
        static List<Item> currentItems = new List<Item>();
        static volatile float radiusValue = 50;
        static object autoAlignBox;
        class Group { public string Key, Label, Asset; public List<Item> Items = new List<Item>(); }

        // --- etat partage entre le thread de l'API et le thread UI ---
        static readonly object sync = new object();
        static List<Group> groups = new List<Group>();
        static int selCount;
        static string selSignature = "";
        static int dataVersion, shownVersion = -1;
        static string status = "";
        static int statusVersion, shownStatusVersion = -1;
        static volatile bool opBusy;
        static Thread poller;

        // --- etat UI ---
        static object popup, panelRoot, statusText, radiusBox, listPanel;
        static readonly Dictionary<string, object> buttons = new Dictionary<string, object>();
        static Delegate clickDelegate;
        static string armedButton; static DateTime armedAt;
        static int corner;            // 0 bas-centre, 1 bas-gauche, 2 haut-droite, 3 haut-gauche
        static bool collapsed;
        static List<Group> shownGroups = new List<Group>();

        // ================= thread UI =================
        // Le panneau est une section repliable de l'Inspecteur (comme Transformation / Disposer),
        // inseree juste sous "Disposer". Si l'Inspecteur est introuvable : carte flottante de secours.
        static object hostSection, hostChildren, panelContent, confirmBar, confirmText, summaryText, nativeMargin;
        static string confirmMsg = ""; static int confirmVer, shownConfirmVer = -1;

        class Pending { public List<Group> Groups; public bool Similar; public double Radius; public DateTime At; }
        static Pending pending;

        static void SelectionTick(object window, object content)
        {
            try
            {
                BindMainWindow(window, content);
                if (scans > 20) DumpTree(content);
                if (radiusBox != null) radiusValue = (float)ReadRadius();
                if (autoAlignBox != null) autoAlign = IsChecked(autoAlignBox);
                if (poller == null)
                {
                    skipNames.Add(PanelName);
                    LoadPanelSettings();
                    Type handler = FindType("Microsoft.UI.Xaml.RoutedEventHandler");
                    clickDelegate = Delegate.CreateDelegate(handler, typeof(Plugin).GetMethod("OnPanelClick", BindingFlags.NonPublic | BindingFlags.Static));
                    poller = new Thread(PollLoop); poller.IsBackground = true; poller.Name = "ncwe-fr-selection"; poller.Start();
                }
                if (hostSection == null && popup == null) CreateHost(content);
                PlacesTick(content);
                PlacesUiTick();
                CleanTick(window, content);
                if (hostSection == null && popup == null) return;

                int ver, sv; int count;
                lock (sync) { ver = dataVersion; sv = statusVersion; count = selCount; }
                if (ver != shownVersion) { shownVersion = ver; Rebuild(); }
                if (sv != shownStatusVersion) { shownStatusVersion = sv; string s; lock (sync) s = status; SetProp(statusText, "Text", s); }
                if (confirmVer != shownConfirmVer) { shownConfirmVer = confirmVer; SetProp(confirmText, "Text", confirmMsg); }
                if (pending != null && (DateTime.Now - pending.At).TotalSeconds > 30) CancelPending();

                bool show = count >= 1 || opBusy;
                if (hostSection != null) SetVisible(hostSection, show);
                else { SetProp(popup, "IsOpen", show); if (show) Position(content); }
            }
            catch (Exception e) { LogOnce("selection ui: " + e); }
        }

        static void SetVisible(object el, bool v)
        {
            object cur = GetProp(el, "Visibility");
            if (cur == null) return;
            int want = v ? 0 : 1;
            if (Convert.ToInt32(cur) != want) SetProp(el, "Visibility", Enum.ToObject(cur.GetType(), want));
        }

        static void CreateHost(object content)
        {
            MethodInfo find = content.GetType().GetMethod("FindName", new[] { typeof(string) });
            object inspector = find.Invoke(content, new object[] { "InspectorContent" });
            object arrange = find.Invoke(content, new object[] { "ArrangeSection" });
            Type sectionType = FindType("NCWE.Studio.PanelSection");
            if (inspector != null && sectionType != null)
            {
                try
                {
                    object section = Activator.CreateInstance(sectionType);
                    SetProp(section, "Name", PanelName);
                    SetProp(section, "Header", "Sélection");
                    PropertyInfo key = sectionType.GetProperty("Key");
                    if (key != null && key.PropertyType == typeof(string)) key.SetValue(section, PanelName, null);
                    object children = GetProp(inspector, "Children");
                    int index = arrange != null ? (int)children.GetType().GetMethod("IndexOf").Invoke(children, new[] { arrange }) + 1 : 1;
                    children.GetType().GetMethod("Insert").Invoke(children, new object[] { index, section });
                    hostSection = section;
                    hostChildren = GetProp(section, "Children");      // PanelSection = panneau a en-tete
                    // meme marge que le contenu des sections natives (ex. Disposer)
                    if (arrange != null)
                    {
                        int k = 0;
                        foreach (object child in (IEnumerable)GetProp(arrange, "Children"))
                            if (++k == 2) { nativeMargin = GetProp(child, "Margin"); break; }
                    }
                    Log("panneau selection : section de l'Inspecteur (" + sectionType.BaseType.Name + ")");
                    return;
                }
                catch (Exception e) { LogOnce("section inspecteur: " + e.GetBaseException().Message); hostSection = null; }
            }
            // secours : carte flottante
            object root = GetProp(content, "XamlRoot");
            if (root == null) return;
            popup = Load("<Popup xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' IsLightDismissEnabled='False'/>");
            SetProp(popup, "Name", PanelName);
            SetProp(popup, "XamlRoot", root);
            Log("panneau selection : carte flottante (Inspecteur introuvable)");
        }

        static object Load(string xaml)
        {
            Type reader = FindType("Microsoft.UI.Xaml.Markup.XamlReader");
            return reader.GetMethod("Load", new[] { typeof(string) }).Invoke(null, new object[] { xaml });
        }

        static DateTime lastStatusAt = DateTime.MinValue;
        static bool treeDumped;

        // Diagnostic (NCWE_FR_TREE=1) : structure de l'interface dans arbre-ui.txt
        static void DumpTree(object content)
        {
            // declenche par le fichier dump-now.txt dans le dossier du plugin (efface ensuite)
            string flag = Path.Combine(dir, "dump-now.txt");
            if (!File.Exists(flag)) return;
            try { File.Delete(flag); } catch { return; }
            var sb = new StringBuilder();
            var st = new Stack<KeyValuePair<object, int>>();
            st.Push(new KeyValuePair<object, int>(content, 0));
            while (st.Count > 0)
            {
                var e = st.Pop();
                string name = tFrameworkElement.IsInstanceOfType(e.Key) ? pName.GetValue(e.Key, null) as string : "";
                if (e.Value < 22) sb.Append(new string(' ', e.Value * 2)).Append(e.Key.GetType().FullName).Append(string.IsNullOrEmpty(name) ? "" : " #" + name).Append('\n');
                int n = (int)mChildrenCount.Invoke(null, new[] { e.Key });
                for (int i = n - 1; i >= 0; i--) st.Push(new KeyValuePair<object, int>(mChild.Invoke(null, new[] { e.Key, (object)i }), e.Value + 1));
            }
            File.WriteAllText(Path.Combine(dir, "arbre-ui.txt"), sb.ToString());
        }

        static string X(string s) { return SecurityElement.Escape(s ?? ""); }

        static void Rebuild()
        {
            List<Group> gs; int count;
            lock (sync) { gs = new List<Group>(groups); count = selCount; }
            SaveChecks();
            shownGroups = gs;
            CancelPending();
            buttons.Clear();
            double radius = radiusBox != null ? ReadRadius() : 50;
            bool floating = hostSection == null;

            var x = new StringBuilder();
            x.Append("<StackPanel xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Spacing='8'");
            if (floating) x.Append(" Width='380'");
            x.Append(">");
            x.Append("<TextBlock x:Name='summary' Opacity='0.75' Text='" + count + (count > 1 ? " objets" : " objet") + " · " + gs.Count + (gs.Count > 1 ? " props différents" : " prop") + "'/>");

            // alignement + aide clavier
            x.Append("<Grid ColumnSpacing='8'><Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>");
            x.Append("<Button x:Name='b_align' HorizontalAlignment='Stretch' Content='Aligner sur la surface (G)' Tag='align|-1' ToolTipService.ToolTip='Sol : l&apos;objet épouse la pente et se pose dessus. Mur à moins de 60 cm : son dos se colle au mur, droit. Ctrl+Z annule.'/>");
            x.Append("<CheckBox x:Name='autoalign' Grid.Column='1' MinWidth='0' Content='Auto' IsChecked='" + (autoAlign ? "True" : "False") + "' ToolTipService.ToolTip='Auto-aligner : aligne l&apos;objet sur le sol ou le mur quand vous le lâchez après un déplacement'/>");
            x.Append("</Grid>");
            x.Append("<TextBlock FontSize='12' Opacity='0.6' TextWrapping='Wrap' Text='R pivoter · T déplacer · Maj pendant une rotation : libre'/>");

            // rayon
            x.Append("<Grid ColumnSpacing='8'><Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>");
            x.Append("<TextBlock VerticalAlignment='Center' Text='Rayon des similaires (m)' ToolTipService.ToolTip='Cercles orange dans la vue 3D : zone où « similaires » cherche et supprime (objets chargés uniquement)'/>");
            x.Append("<NumberBox x:Name='radius' Grid.Column='1' Width='112' Minimum='0' Maximum='2000' SmallChange='5' LargeChange='25' SpinButtonPlacementMode='Compact' Value='" + V(radius) + "'/>");
            x.Append("</Grid>");

            // liste des props
            x.Append("<StackPanel Spacing='2'>");
            int shown = Math.Min(gs.Count, 40);
            for (int i = 0; i < shown; i++)
            {
                Group g = gs[i];
                int n = g.Items.Count;
                x.Append("<Grid ColumnSpacing='6'><Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>");
                x.Append("<CheckBox x:Name='c_" + i + "' MinWidth='0' Padding='0' IsChecked='" + (checkedKeys.Contains(g.Key) ? "True" : "False") + "' ToolTipService.ToolTip='Cocher pour agir sur plusieurs props ensemble'/>");
                x.Append("<TextBlock Grid.Column='1' VerticalAlignment='Center' TextTrimming='CharacterEllipsis' Text='" + X(g.Label) + "' ToolTipService.ToolTip='" + X(g.Asset ?? g.Key) + "'/>");
                x.Append("<TextBlock Grid.Column='2' VerticalAlignment='Center' Opacity='0.7' Text='×" + n + "'/>");
                x.Append("<Button Grid.Column='3' Padding='8,2' Content='⋯' ToolTipService.ToolTip='Actions'><Button.Flyout><MenuFlyout Placement='BottomEdgeAlignedRight'>");
                MenuItem(x, "iso", i, "Garder seulement ces " + n);
                MenuItem(x, "rem", i, "Retirer de la sélection");
                MenuItem(x, "sim", i, "Sélectionner les similaires (rayon)");
                x.Append("<MenuFlyoutSeparator/>");
                MenuItem(x, "del", i, "Supprimer ces " + n + " objets…");
                MenuItem(x, "delsim", i, "Supprimer tous les similaires (rayon)…");
                x.Append("</MenuFlyout></Button.Flyout></Button>");
                x.Append("</Grid>");
            }
            if (gs.Count > shown) x.Append("<TextBlock Opacity='0.7' Text='+ " + (gs.Count - shown) + " autres props…'/>");
            x.Append("</StackPanel>");

            // actions groupees sur les lignes cochees
            if (gs.Count > 1)
            {
                x.Append("<Grid ColumnSpacing='8'><Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>");
                x.Append("<Button x:Name='b_chkall' HorizontalAlignment='Stretch' Content='Tout cocher' Tag='chkall|-1'/>");
                x.Append("<DropDownButton Grid.Column='1' HorizontalAlignment='Stretch' Content='Supprimer…' ToolTipService.ToolTip='Supprimer les props cochés'><DropDownButton.Flyout><MenuFlyout Placement='BottomEdgeAlignedRight'>");
                x.Append("<MenuFlyoutItem x:Name='m_delchk' Text='Leurs objets sélectionnés…' Tag='delchk|-1'/>");
                x.Append("<MenuFlyoutItem x:Name='m_delchksim' Text='Avec tous les similaires (rayon)…' Tag='delchksim|-1'/>");
                x.Append("</MenuFlyout></DropDownButton.Flyout></DropDownButton>");
                x.Append("</Grid>");
            }

            // barre de confirmation (avant toute suppression)
            x.Append("<Border x:Name='confirmBar' Visibility='Collapsed' CornerRadius='4' Padding='10,8' Background='{ThemeResource SystemFillColorCriticalBackgroundBrush}'>");
            x.Append("<StackPanel Spacing='8'><TextBlock x:Name='confirmText' TextWrapping='Wrap'/>");
            x.Append("<StackPanel Orientation='Horizontal' Spacing='8'>");
            x.Append("<Button x:Name='b_confirm' Content='Supprimer' Tag='confirm|-1' Style='{ThemeResource AccentButtonStyle}'/>");
            x.Append("<Button x:Name='b_cancel' Content='Annuler' Tag='cancel|-1'/>");
            x.Append("</StackPanel></StackPanel></Border>");

            x.Append("<TextBlock x:Name='status' FontSize='12' Opacity='0.8' TextWrapping='Wrap'/>");
            x.Append("</StackPanel>");

            object panel = Load(x.ToString());
            MethodInfo find = panel.GetType().GetMethod("FindName", new[] { typeof(string) });
            Func<string, object> F = delegate (string n) { return find.Invoke(panel, new object[] { n }); };
            statusText = F("status"); radiusBox = F("radius"); summaryText = F("summary");
            confirmBar = F("confirmBar"); confirmText = F("confirmText");
            autoAlignBox = F("autoalign");
            checkBoxes.Clear();
            for (int i = 0; i < shown; i++) checkBoxes.Add(F("c_" + i));
            foreach (string n in new[] { "b_align", "b_chkall", "m_delchk", "m_delchksim", "b_confirm", "b_cancel" }) AttachClick(F(n), n);
            for (int i = 0; i < shown; i++)
                foreach (string a in new[] { "iso", "rem", "sim", "del", "delsim" }) AttachClick(F("m_" + a + "_" + i), "m_" + a + "_" + i);
            string s; lock (sync) s = status;
            SetProp(statusText, "Text", s);

            if (hostSection != null)
            {
                MethodInfo remove = hostChildren.GetType().GetMethod("Remove");
                if (panelContent != null) remove.Invoke(hostChildren, new[] { panelContent });
                if (nativeMargin != null) SetProp(panel, "Margin", nativeMargin);
                hostChildren.GetType().GetMethod("Add").Invoke(hostChildren, new[] { panel });
            }
            else
            {
                object card = Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Padding='12,10' CornerRadius='8' BorderThickness='1' Background='{ThemeResource AcrylicInAppFillColorDefaultBrush}' BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}'/>");
                SetProp(card, "Child", panel);
                panelRoot = card;
                SetProp(popup, "Child", card);
            }
            panelContent = panel;
        }

        static void MenuItem(StringBuilder x, string act, int i, string text)
        {
            x.Append("<MenuFlyoutItem x:Name='m_" + act + "_" + i + "' Text='" + X(text) + "' Tag='" + act + "|" + i + "'/>");
        }

        static void AttachClick(object b, string name)
        {
            if (b == null) return;
            buttons[name] = b;
            b.GetType().GetEvent("Click").AddEventHandler(b, clickDelegate);
        }

        static void Position(object content)
        {
            if (panelRoot == null) return;
            object root = GetProp(content, "XamlRoot");
            object size = GetProp(root, "Size");
            double w = Convert.ToDouble(GetProp(size, "Width")), h = Convert.ToDouble(GetProp(size, "Height"));
            double ph = Convert.ToDouble(GetProp(panelRoot, "ActualHeight"));
            SetProp(popup, "HorizontalOffset", Math.Max(0, w - 420 - 420));
            SetProp(popup, "VerticalOffset", Math.Max(0, h - ph - 60));
        }

        // Clic sur un bouton ou une entree de menu du panneau (thread UI). Signature liee au RoutedEventHandler.
        static void OnPanelClick(object sender, object e)
        {
            try
            {
                string tag = GetProp(sender, "Tag") as string;
                if (tag == null) return;
                string[] p = tag.Split('|');
                string act = p[0]; int gi = int.Parse(p[1]);
                double radius = ReadRadius();

                switch (act)
                {
                    case "align": StartAlign(null); return;
                    case "cancel": CancelPending(); SetStatus("Suppression annulée."); return;
                    case "confirm":
                    {
                        Pending pd = pending;
                        if (pd == null || opBusy) return;
                        CancelPending();
                        RunOp(delegate { DeleteChecked(pd.Groups, pd.Similar, pd.Radius); });
                        return;
                    }
                    case "chkall":
                    {
                        bool any = false;
                        foreach (object cb in checkBoxes) if (IsChecked(cb)) any = true;
                        foreach (object cb in checkBoxes) SetProp(cb, "IsChecked", !any);
                        return;
                    }
                    case "delchk":
                    case "delchksim":
                    {
                        var chosen = new List<Group>();
                        for (int i = 0; i < checkBoxes.Count && i < shownGroups.Count; i++) if (IsChecked(checkBoxes[i])) chosen.Add(shownGroups[i]);
                        if (chosen.Count == 0) { SetStatus("Cochez d'abord une ou plusieurs lignes."); return; }
                        AskDelete(chosen, act == "delchksim", radius);
                        return;
                    }
                }
                if (opBusy || gi < 0 || gi >= shownGroups.Count) return;
                Group g = shownGroups[gi];
                if (act == "del" || act == "delsim") { AskDelete(new List<Group> { g }, act == "delsim", radius); return; }
                List<Group> all = shownGroups;
                RunOp(delegate { DoAction(act, g, all, radius); });
            }
            catch (Exception ex) { LogOnce("clic: " + ex); }
        }

        // Avant de supprimer : objets vises encadres en rouge dans la vue, barre Supprimer / Annuler.
        static void AskDelete(List<Group> chosen, bool similar, double radius)
        {
            if (opBusy) return;
            pending = new Pending { Groups = chosen, Similar = similar, Radius = radius, At = DateTime.Now };
            SetConfirm("Calcul des objets visés…");
            SetVisible(confirmBar, true);
            StartPreview(chosen, similar, radius);
        }

        static void SetConfirm(string s) { confirmMsg = s; confirmVer++; }

        static void CancelPending()
        {
            if (pending == null) return;
            pending = null;
            SetVisible(confirmBar, false);
            previewGen++;
            ringsDirty = true;      // l'apercu rouge disparait, les cercles normaux reviennent
        }

        static readonly HashSet<string> checkedKeys = new HashSet<string>();
        static readonly List<object> checkBoxes = new List<object>();

        static bool IsChecked(object cb)
        {
            object v = GetProp(cb, "IsChecked");
            return v is bool && (bool)v;
        }

        // garde les coches quand la liste est reconstruite (selection modifiee)
        static void SaveChecks()
        {
            if (checkBoxes.Count == 0) return;
            checkedKeys.Clear();
            for (int i = 0; i < checkBoxes.Count && i < shownGroups.Count; i++) if (IsChecked(checkBoxes[i])) checkedKeys.Add(shownGroups[i].Key);
        }

        static double ReadRadius()
        {
            object v = GetProp(radiusBox, "Value");
            double r = v is double ? (double)v : double.NaN;
            return double.IsNaN(r) || r <= 0 ? 50 : r;
        }

        // Une seule commande de suppression pour toutes les lignes choisies : un seul Ctrl+Z annule tout.
        static void DeleteChecked(List<Group> chosen, bool similar, double radius)
        {
            var ids = new List<string>(); var set = new HashSet<string>(); int readOnly = 0;
            foreach (Group g in chosen)
            {
                foreach (Item it in g.Items) { if (!it.Editable) { readOnly++; continue; } if (set.Add(it.Id)) ids.Add(it.Id); }
                if (similar) foreach (string id in FindSimilar(g, radius)) if (set.Add(id)) ids.Add(id);
            }
            Delete(ids, chosen.Count == 1 ? chosen[0].Label : chosen.Count + " props", readOnly);
        }

        // ================= reperes dans la vue 3D =================
        static volatile bool ringsDirty = true;
        static volatile int previewGen;
        static string lastRingsKey = "";

        // Cercle au sol (ligne fermee) autour du centre d'un groupe.
        static string Ring(Group g, double radius, string color)
        {
            double cx = 0, cy = 0, z = double.MaxValue;
            foreach (Item it in g.Items) { cx += it.X; cy += it.Y; z = Math.Min(z, it.HasBounds ? it.MinZ : it.Z); }
            cx /= g.Items.Count; cy /= g.Items.Count; z += 0.15;
            var sb = new StringBuilder("{\"type\":\"line\",\"closed\":true,\"color\":" + Q(color) + ",\"points\":[");
            for (int k = 0; k < 48; k++)
            {
                double a = k * Math.PI * 2 / 48;
                if (k > 0) sb.Append(',');
                sb.Append(P(cx + Math.Cos(a) * radius, cy + Math.Sin(a) * radius, z));
            }
            return sb.Append("]}").ToString();
        }

        static void Annotate(string items)
        {
            Call("{\"op\":\"api.annotate\",\"client\":" + Q(Client) + ",\"clear\":true,\"duration\":0,\"items\":[" + items + "]}", 10000);
        }

        // Appele par le poller : cercles orange de rayon autour de chaque prop selectionne (15 au plus).
        static void UpdateRings()
        {
            if (pending != null) return;              // l'apercu de suppression est affiche
            List<Group> gs; lock (sync) gs = new List<Group>(groups);
            double r = radiusValue;
            var key = new StringBuilder(r.ToString(CultureInfo.InvariantCulture));
            foreach (Group g in gs)
            {
                double sx = 0; foreach (Item it in g.Items) sx += Math.Round(it.X) + Math.Round(it.Y) * 7 + Math.Round(it.Z) * 13;
                key.Append('|').Append(g.Key).Append(g.Items.Count).Append('@').Append(sx);
            }
            if (!ringsDirty && key.ToString() == lastRingsKey) return;
            ringsDirty = false; lastRingsKey = key.ToString();
            var sb = new StringBuilder();
            for (int i = 0; i < gs.Count && i < 15; i++)
            {
                if (string.IsNullOrEmpty(gs[i].Asset)) continue;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(Ring(gs[i], r, "amber"));
            }
            Annotate(sb.ToString());   // vide = efface les reperes
        }

        // Premier clic sur une suppression : objets vises encadres en rouge, avec leur nombre.
        static void StartPreview(List<Group> chosen, bool similar, double radius)
        {
            int gen = ++previewGen;
            var t = new Thread(delegate ()
            {
                try
                {
                    var ids = new List<string>(); var set = new HashSet<string>();
                    var rings = new StringBuilder();
                    foreach (Group g in chosen)
                    {
                        foreach (Item it in g.Items) if (it.Editable && set.Add(it.Id)) ids.Add(it.Id);
                        if (similar)
                        {
                            foreach (string id in FindSimilar(g, radius)) if (set.Add(id)) ids.Add(id);
                            if (!string.IsNullOrEmpty(g.Asset)) { if (rings.Length > 0) rings.Append(','); rings.Append(Ring(g, radius, "red")); }
                        }
                    }
                    if (gen != previewGen) return;
                    var sb = new StringBuilder("{\"type\":\"objects\",\"color\":\"red\",\"ids\":[");
                    for (int i = 0; i < ids.Count && i < 2000; i++) { if (i > 0) sb.Append(','); sb.Append(Q(ids[i])); }
                    sb.Append("]}");
                    if (rings.Length > 0) sb.Append(',').Append(rings);
                    Annotate(sb.ToString());
                    lastRingsKey = "";
                    SetConfirm(ids.Count + (ids.Count > 1 ? " objets seront supprimés" : " objet sera supprimé") + " (encadrés en rouge dans la vue). Ctrl+Z annulera.");
                }
                catch (Exception e) { LogOnce("apercu: " + e.Message); }
            });
            t.IsBackground = true; t.Start();
        }

        // ================= thread API =================
        static void RunOp(ThreadStart op)
        {
            opBusy = true;
            var t = new Thread(delegate ()
            {
                try { op(); }
                catch (Exception e) { SetStatus("Erreur : " + e.Message); LogOnce("op: " + e); }
                finally { opBusy = false; forcePoll = true; }
            });
            t.IsBackground = true; t.Start();
        }

        static void DoAction(string act, Group g, List<Group> all, double radius)
        {
            var ids = new List<string>();
            switch (act)
            {
                case "iso":
                    foreach (Item it in g.Items) ids.Add(it.Id);
                    Select(ids);
                    SetStatus(g.Items.Count + " objets « " + g.Label + " » gardés dans la sélection.");
                    break;
                case "rem":
                    foreach (Group o in all) if (o != g) foreach (Item it in o.Items) ids.Add(it.Id);
                    Select(ids);
                    SetStatus("« " + g.Label + " » retiré de la sélection.");
                    break;
                case "sim":
                {
                    List<string> found = FindSimilar(g, radius);
                    var set = new HashSet<string>();
                    foreach (Group o in all) foreach (Item it in o.Items) if (set.Add(it.Id)) ids.Add(it.Id);
                    int added = 0;
                    foreach (string id in found) if (set.Add(id)) { ids.Add(id); added++; }
                    Select(ids);
                    SetStatus(added + " objets « " + g.Label + " » ajoutés à la sélection (rayon " + radius + " m).");
                    break;
                }
                case "del":
                    foreach (Item it in g.Items) if (it.Editable) ids.Add(it.Id);
                    Delete(ids, g.Label, g.Items.Count - ids.Count);
                    break;
                case "delsim":
                {
                    var set = new HashSet<string>();
                    foreach (Item it in g.Items) if (it.Editable && set.Add(it.Id)) ids.Add(it.Id);
                    foreach (string id in FindSimilar(g, radius)) if (set.Add(id)) ids.Add(id);
                    Delete(ids, g.Label, 0);
                    break;
                }
            }
        }

        static void Select(List<string> ids)
        {
            var sb = new StringBuilder("{\"op\":\"api.select\",\"client\":" + Q(Client) + ",\"ids\":[");
            for (int i = 0; i < ids.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Q(ids[i])); }
            sb.Append("]}");
            var r = Call(sb.ToString(), 30000);
            if (!Ok(r)) SetStatus("La sélection a échoué : " + Err(r));
        }

        static void Delete(List<string> ids, string label, int readOnly)
        {
            if (ids.Count == 0) { SetStatus("Rien de supprimable (objets en lecture seule)."); return; }
            SetStatus("Suppression de " + ids.Count + " objets « " + label + " »…");
            var sb = new StringBuilder("{\"op\":\"api.delete\",\"client\":" + Q(Client) + ",\"ids\":[");
            for (int i = 0; i < ids.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Q(ids[i])); }
            sb.Append("]}");
            var r = Call(sb.ToString(), 120000);
            if (Ok(r))
            {
                object d; r.TryGetValue("deleted", out d);
                SetStatus(Convert.ToInt32(d ?? ids.Count) + " objets « " + label + " » supprimés (Ctrl+Z pour annuler)."
                    + (readOnly > 0 ? " " + readOnly + " en lecture seule ignorés." : ""));
            }
            else SetStatus("Suppression refusée : " + Err(r));
        }

        // Objets charges avec exactement le meme mesh, dans le rayon autour du centre du groupe.
        static List<string> FindSimilar(Group g, double radius)
        {
            var res = new List<string>();
            if (string.IsNullOrEmpty(g.Asset)) { SetStatus("Ce groupe n'a pas de mesh : pas de recherche de similaires."); return res; }
            double cx = 0, cy = 0, cz = 0;
            foreach (Item it in g.Items) { cx += it.X; cy += it.Y; cz += it.Z; }
            int n = g.Items.Count; cx /= n; cy /= n; cz /= n;
            string stem = Path.GetFileNameWithoutExtension(g.Asset.Replace('\\', '/'));
            string req = "{\"op\":\"api.query\",\"client\":" + Q(Client) + ",\"text\":" + Q(stem)
                + ",\"radius\":" + radius.ToString(CultureInfo.InvariantCulture)
                + ",\"center\":[" + cx.ToString(CultureInfo.InvariantCulture) + "," + cy.ToString(CultureInfo.InvariantCulture) + "," + cz.ToString(CultureInfo.InvariantCulture) + "]"
                + ",\"limit\":5000}";
            var r = Call(req, 60000);
            if (!Ok(r)) { SetStatus("Recherche impossible : " + Err(r)); return res; }
            object objs; r.TryGetValue("objects", out objs);
            var list = objs as List<object>;
            if (list == null) return res;
            foreach (object o in list)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                if (!string.Equals(Str(d, "asset"), g.Asset, StringComparison.OrdinalIgnoreCase)) continue;
                object ed; if (d.TryGetValue("editable", out ed) && ed is bool && !(bool)ed) continue;
                res.Add(Str(d, "id"));
            }
            return res;
        }

        static volatile bool forcePoll;

        static void PollLoop()
        {
            string lastIds = null;
            while (true)
            {
                try
                {
                    if (!opBusy)
                    {
                        var st = Call("{\"op\":\"api.status\",\"client\":" + Q(Client) + "}", 10000);
                        if (Ok(st))
                        {
                            object sel; st.TryGetValue("selection", out sel);
                            string ids = Json.Write(sel);
                            var list = sel as List<object>;
                            int n = list == null ? 0 : list.Count;
                            bool changed = ids != lastIds || forcePoll;
                            // auto-alignement : positions relues a chaque passage (30 objets au plus)
                            bool track = autoAlign && n >= 1 && n <= 30;
                            if (changed || track)
                            {
                                forcePoll = false;
                                if (changed) { lastIds = ids; lastPositions.Clear(); movedIds.Clear(); }
                                if (n >= 1) LoadSelection();
                                else lock (sync) { selCount = 0; groups = new List<Group>(); currentItems = new List<Item>(); selSignature = ""; dataVersion++; }
                            }
                            if (track) { List<Item> cur; lock (sync) cur = currentItems; CheckAutoAlign(cur); }
                            UpdateRings();
                        }
                    }
                }
                catch (Exception e) { LogOnce("poll: " + e.Message); }
                Thread.Sleep(forcePoll ? 100 : 700);
            }
        }

        static void LoadSelection()
        {
            var r = Call("{\"op\":\"api.selection\",\"client\":" + Q(Client) + "}", 30000);
            if (!Ok(r)) return;
            object sel; r.TryGetValue("selection", out sel);
            var list = sel as List<object> ?? new List<object>();
            var byKey = new Dictionary<string, Group>();
            var order = new List<Group>();
            foreach (object o in list)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                var it = new Item { Id = Str(d, "id"), Name = Str(d, "name"), Asset = Str(d, "asset"), Node = Str(d, "node") };
                object ed; it.Editable = !(d.TryGetValue("editable", out ed) && ed is bool && !(bool)ed);
                object pos; if (d.TryGetValue("position", out pos) && pos is List<object> && ((List<object>)pos).Count >= 3)
                {
                    var pl = (List<object>)pos; it.X = Convert.ToDouble(pl[0]); it.Y = Convert.ToDouble(pl[1]); it.Z = Convert.ToDouble(pl[2]);
                }
                object rot; if (d.TryGetValue("rotation", out rot) && rot is Dictionary<string, object>)
                {
                    object y; if (((Dictionary<string, object>)rot).TryGetValue("yaw", out y) && y != null) it.Yaw = Convert.ToDouble(y);
                }
                object bo; if (d.TryGetValue("bounds", out bo) && bo is Dictionary<string, object>)
                {
                    double[] mn = Vec((Dictionary<string, object>)bo, "min"), mx = Vec((Dictionary<string, object>)bo, "max");
                    if (mn != null && mx != null)
                    {
                        it.HasBounds = true;
                        it.MinX = mn[0]; it.MinY = mn[1]; it.MinZ = mn[2]; it.MaxX = mx[0]; it.MaxY = mx[1]; it.MaxZ = mx[2];
                    }
                }
                string key = !string.IsNullOrEmpty(it.Asset) ? it.Asset.ToLowerInvariant() : "node:" + (it.Node ?? it.Name);
                Group g;
                if (!byKey.TryGetValue(key, out g))
                {
                    g = new Group { Key = key, Asset = it.Asset };
                    // le nom affiche est le nom du mesh tel quel (jamais traduit)
                    g.Label = !string.IsNullOrEmpty(it.Asset) ? Path.GetFileName(it.Asset.Replace('\\', '/')) : (it.Node ?? it.Name);
                    byKey[key] = g; order.Add(g);
                }
                g.Items.Add(it);
            }
            order.Sort(delegate (Group a, Group b) { return b.Items.Count.CompareTo(a.Items.Count); });
            var all = new List<Item>(); var sig = new StringBuilder();
            foreach (Group g in order) { all.AddRange(g.Items); sig.Append(g.Key).Append(':').Append(g.Items.Count).Append('|'); }
            foreach (Item it in all) sig.Append(it.Id).Append(',');
            lock (sync)
            {
                groups = order; selCount = list.Count; currentItems = all;
                // le panneau n'est reconstruit que si la composition change (pas a chaque deplacement)
                if (sig.ToString() != selSignature) { selSignature = sig.ToString(); dataVersion++; }
            }
        }

        static void SetStatus(string s) { lock (sync) { status = s; statusVersion++; } lastStatusAt = DateTime.Now; }

        // ---------- pipe ----------
        static Dictionary<string, object> Call(string json, int timeoutMs)
        {
            using (var p = new NamedPipeClientStream(".", "ncwe-studio", PipeDirection.InOut))
            {
                p.Connect(3000);
                var w = new StreamWriter(p, new UTF8Encoding(false)); w.AutoFlush = true;
                var rd = new StreamReader(p, Encoding.UTF8);
                w.WriteLine(json);
                var task = rd.ReadLineAsync();
                if (!task.Wait(timeoutMs)) return new Dictionary<string, object> { { "ok", false }, { "error", "pas de réponse du Studio" } };
                return Json.Parse(task.Result) as Dictionary<string, object> ?? new Dictionary<string, object>();
            }
        }

        static bool Ok(Dictionary<string, object> r) { object o; return r.TryGetValue("ok", out o) && o is bool && (bool)o; }
        static string Err(Dictionary<string, object> r) { string e = Str(r, "error"); return string.IsNullOrEmpty(e) ? "erreur inconnue" : Translate(e, false); }
        static string Str(Dictionary<string, object> d, string k) { object o; return d.TryGetValue(k, out o) && o != null ? o.ToString() : null; }
        static string Q(string s) { return Json.Write(s); }

        // ---------- reflexion ----------
        static object GetProp(object o, string name)
        {
            if (o == null) return null;
            PropertyInfo p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            return p == null ? null : p.GetValue(o, null);
        }
        static void SetProp(object o, string name, object v)
        {
            if (o == null) return;
            PropertyInfo p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null) p.SetValue(o, v, null);
        }

        // ---------- reglages ----------
        static string SettingsPath { get { return Path.Combine(dir, "panneau-selection.txt"); } }
        static void LoadPanelSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                string[] l = File.ReadAllText(SettingsPath).Trim().Split(';');
                corner = int.Parse(l[0]) % 4; collapsed = l.Length > 1 && l[1] == "1";
            }
            catch { }
        }
        static void SavePanelSettings() { try { File.WriteAllText(SettingsPath, corner + ";" + (collapsed ? "1" : "0")); } catch { } }
    }

    // JSON minimal (objets -> Dictionary, tableaux -> List, nombres -> double).
    static class Json
    {
        public static object Parse(string s) { int i = 0; return Value(s, ref i); }

        static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) return null;
            char c = s[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>(); i++;
                Ws(s, ref i); if (s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(s, ref i); string k = (string)Value(s, ref i); Ws(s, ref i); i++; // ':'
                    d[k] = Value(s, ref i); Ws(s, ref i);
                    if (s[i++] == '}') return d;
                }
            }
            if (c == '[')
            {
                var l = new List<object>(); i++;
                Ws(s, ref i); if (s[i] == ']') { i++; return l; }
                while (true) { l.Add(Value(s, ref i)); Ws(s, ref i); if (s[i++] == ']') return l; }
            }
            if (c == '"')
            {
                var sb = new StringBuilder(); i++;
                while (s[i] != '"')
                {
                    if (s[i] == '\\')
                    {
                        i++;
                        switch (s[i])
                        {
                            case 'n': sb.Append('\n'); break;
                            case 't': sb.Append('\t'); break;
                            case 'r': sb.Append('\r'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16)); i += 4; break;
                            default: sb.Append(s[i]); break;
                        }
                        i++;
                    }
                    else sb.Append(s[i++]);
                }
                i++;
                return sb.ToString();
            }
            if (s.Substring(i).StartsWith("true")) { i += 4; return true; }
            if (s.Substring(i, Math.Min(5, s.Length - i)) == "false") { i += 5; return false; }
            if (s.Substring(i, Math.Min(4, s.Length - i)) == "null") { i += 4; return null; }
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            return double.Parse(s.Substring(st, i - st), CultureInfo.InvariantCulture);
        }

        public static string Write(object o)
        {
            if (o == null) return "null";
            string s = o as string;
            if (s != null)
            {
                var sb = new StringBuilder("\"");
                foreach (char c in s)
                {
                    if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                    else if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                }
                return sb.Append('"').ToString();
            }
            if (o is bool) return (bool)o ? "true" : "false";
            if (o is double) return ((double)o).ToString(CultureInfo.InvariantCulture);
            var list = o as List<object>;
            if (list != null) { var sb = new StringBuilder("["); for (int i = 0; i < list.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Write(list[i])); } return sb.Append(']').ToString(); }
            var d = o as Dictionary<string, object>;
            if (d != null) { var sb = new StringBuilder("{"); bool f = true; foreach (var kv in d) { if (!f) sb.Append(','); f = false; sb.Append(Write(kv.Key)).Append(':').Append(Write(kv.Value)); } return sb.Append('}').ToString(); }
            return o.ToString();
        }
    }
}
