// Section "Lieux de Cyberpunk" de l'onglet Monde : menu deroulant de categories, filtre, clic = teleportation.
// Lieux du jeu : lieux.tsv (extrait une fois des fichiers du jeu). Mes points : mes-points.tsv (modifiable).
// Format des deux fichiers : categorie <TAB> nom <TAB> x <TAB> y <TAB> z [<TAB> cap <TAB> inclinaison]

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace NcweFr
{
    static partial class Plugin
    {
        const string PlacesName = "NcweFrLieux";
        const string MyPoints = "Mes points";

        class Place { public string Cat, Name; public double X, Y, Z; public bool HasView; public double Heading, Pitch; }

        static List<Place> gamePlaces, myPlaces = new List<Place>();
        static object worldGoTo;
        static List<string> placesCats = new List<string>();
        static object placesScroll;
        static object placesSection, placesCat, placesFilter, placesList, placesNewName, placesStatus;
        static Delegate placesClick;
        static string placesCurrentCat;
        static readonly List<Place> placesShown = new List<Place>();

        static string PlacesFile { get { return Path.Combine(dir, "lieux.tsv"); } }
        static string MyPointsFile { get { return Path.Combine(dir, "mes-points.tsv"); } }

        static List<Place> ReadPlaces(string file)
        {
            var list = new List<Place>();
            if (!File.Exists(file)) return list;
            foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                string[] c = line.Split('\t');
                if (c.Length < 5) continue;
                var p = new Place { Cat = c[0], Name = c[1] };
                if (!double.TryParse(c[2], NumberStyles.Float, CultureInfo.InvariantCulture, out p.X)) continue;
                double.TryParse(c[3], NumberStyles.Float, CultureInfo.InvariantCulture, out p.Y);
                double.TryParse(c[4], NumberStyles.Float, CultureInfo.InvariantCulture, out p.Z);
                if (c.Length >= 7 && double.TryParse(c[5], NumberStyles.Float, CultureInfo.InvariantCulture, out p.Heading))
                { double.TryParse(c[6], NumberStyles.Float, CultureInfo.InvariantCulture, out p.Pitch); p.HasView = true; }
                list.Add(p);
            }
            return list;
        }

        static void SaveMyPoints()
        {
            var sb = new StringBuilder("# Mes points : categorie, nom, x, y, z, cap, inclinaison (modifiable a la main)\n");
            foreach (Place p in myPlaces)
                sb.Append(MyPoints).Append('\t').Append(p.Name.Replace('\t', ' ')).Append('\t').Append(F(p.X)).Append('\t').Append(F(p.Y)).Append('\t').Append(F(p.Z))
                  .Append('\t').Append(F(p.Heading)).Append('\t').Append(F(p.Pitch)).Append('\n');
            File.WriteAllText(MyPointsFile, sb.ToString(), new UTF8Encoding(false));
            PublishMyPoints();
        }

        // Thread UI : insere la section sous "Aller a" de l'onglet Monde (une fois).
        static void PlacesTick(object content)
        {
            if (placesSection != null) return;
            try
            {
                if (worldGoTo == null) return;           // champ GoX trouve par le parcours de l'interface (onglet Monde affiche)
                Type sectionType = FindType("NCWE.Studio.PanelSection");
                // remonte de GoX jusqu'a sa section "Aller a"
                MethodInfo getParent = tVth.GetMethod("GetParent");
                object goTo = worldGoTo;
                while (goTo != null && (sectionType == null || !sectionType.IsInstanceOfType(goTo))) goTo = getParent.Invoke(null, new[] { goTo });
                if (goTo == null) { LogOnce("lieux: section Aller a introuvable"); placesSection = new object(); return; }
                if (sectionType == null) { LogOnce("lieux: WorldGoTo " + (goTo == null ? "introuvable" : "ok") + ", PanelSection " + (sectionType == null ? "introuvable" : "ok")); return; }
                object parent = GetProp(goTo, "Parent");
                if (parent == null) { MethodInfo gp = tVth.GetMethod("GetParent"); parent = gp.Invoke(null, new[] { goTo }); }
                object children = GetProp(parent, "Children");
                if (children == null) { LogOnce("lieux: parent de WorldGoTo = " + (parent == null ? "null" : parent.GetType().FullName)); return; }

                TeamInit();
                gamePlaces = ReadPlaces(PlacesFile);
                // points partages par d'autres (points-*.tsv) : une categorie chacun, en lecture seule
                foreach (string shared in Directory.GetFiles(dir, "points-*.tsv")) gamePlaces.AddRange(ReadPlaces(shared));
                basePlaces = new List<Place>(gamePlaces);
                myPlaces = ReadPlaces(MyPointsFile);
                skipNames.Add(PlacesName);
                placesClick = Delegate.CreateDelegate(FindType("Microsoft.UI.Xaml.RoutedEventHandler"), typeof(Plugin).GetMethod("OnPlacesClick", BindingFlags.NonPublic | BindingFlags.Static));

                object section = Activator.CreateInstance(sectionType);
                SetProp(section, "Name", PlacesName);
                SetProp(section, "Header", "Lieux de Cyberpunk");
                PropertyInfo key = sectionType.GetProperty("Key");
                if (key != null && key.PropertyType == typeof(string)) key.SetValue(section, PlacesName, null);

                var cats = new List<string>();
                foreach (Place p in gamePlaces) if (!cats.Contains(p.Cat)) cats.Add(p.Cat);
                cats.Add(MyPoints);

                var x = new StringBuilder();
                x.Append("<StackPanel xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Spacing='8'>");
                placesCats = cats;
                x.Append("<ComboBox x:Name='cat' HorizontalAlignment='Stretch' PlaceholderText='Catégorie' SelectedIndex='0'>");
                foreach (string c in cats) x.Append("<ComboBoxItem Content='" + X(c) + "'/>");
                x.Append("</ComboBox>");
                x.Append("<TextBox x:Name='filter' PlaceholderText='Filtrer (nom, quartier…)'/>");
                x.Append("<ScrollViewer x:Name='scroll' MaxHeight='320'><StackPanel x:Name='list' Spacing='1'/></ScrollViewer>");
                x.Append("<Grid ColumnSpacing='8'><Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>");
                x.Append("<TextBox x:Name='newname' PlaceholderText='Nom du point'/>");
                x.Append("<Button x:Name='add' Grid.Column='1' Content='Ajouter ma position' Tag='add|-1' ToolTipService.ToolTip='Enregistre la position et l&apos;orientation de la caméra dans « Mes points »'/>");
                x.Append("</Grid>");
                x.Append("<TextBlock x:Name='status' FontSize='12' Opacity='0.7' TextWrapping='Wrap' Text='" + (gamePlaces.Count == 0 ? "lieux.tsv absent : seuls « Mes points » sont disponibles." : gamePlaces.Count + " lieux du jeu · cliquez un lieu pour vous y téléporter.") + "'/>");
                x.Append("</StackPanel>");
                object panel = Load(x.ToString());
                MethodInfo f = panel.GetType().GetMethod("FindName", new[] { typeof(string) });
                placesCat = f.Invoke(panel, new object[] { "cat" });
                placesFilter = f.Invoke(panel, new object[] { "filter" });
                placesList = f.Invoke(panel, new object[] { "list" });
                placesScroll = f.Invoke(panel, new object[] { "scroll" });
                placesNewName = f.Invoke(panel, new object[] { "newname" });
                placesStatus = f.Invoke(panel, new object[] { "status" });
                object add = f.Invoke(panel, new object[] { "add" });
                add.GetType().GetEvent("Click").AddEventHandler(add, placesClick);

                // changement de categorie / de filtre : (object, object) se lie a SelectionChanged et TextChanged
                MethodInfo changed = typeof(Plugin).GetMethod("OnPlacesChanged", BindingFlags.NonPublic | BindingFlags.Static);
                EventInfo sel = placesCat.GetType().GetEvent("SelectionChanged");
                sel.AddEventHandler(placesCat, Delegate.CreateDelegate(sel.EventHandlerType, changed));
                EventInfo txt = placesFilter.GetType().GetEvent("TextChanged");
                txt.AddEventHandler(placesFilter, Delegate.CreateDelegate(txt.EventHandlerType, changed));

                if (nativeMargin != null) SetProp(panel, "Margin", nativeMargin);
                GetProp(section, "Children").GetType().GetMethod("Add").Invoke(GetProp(section, "Children"), new[] { panel });
                int index = (int)children.GetType().GetMethod("IndexOf").Invoke(children, new[] { goTo }) + 1;
                children.GetType().GetMethod("Insert").Invoke(children, new object[] { index, section });
                placesSection = section;
                RefreshPlaces();
                Log("lieux : section ajoutee (" + gamePlaces.Count + " lieux, " + myPlaces.Count + " points perso)");
            }
            catch (Exception e) { LogOnce("lieux: " + e.GetBaseException()); placesSection = new object(); }
        }

        static void OnPlacesChanged(object sender, object e) { try { RefreshPlaces(); } catch (Exception ex) { LogOnce("lieux filtre: " + ex.GetBaseException().Message); } }

        static void RefreshPlaces()
        {
            object item = GetProp(placesCat, "SelectedItem");
            placesCurrentCat = item == null ? (placesCats.Count > 0 ? placesCats[0] : null) : GetProp(item, "Content") as string;
            string filter = ((GetProp(placesFilter, "Text") as string) ?? "").Replace(' ', ' ').Trim().ToLowerInvariant();
            List<Place> src = placesCurrentCat == MyPoints ? myPlaces : gamePlaces;
            placesShown.Clear();
            foreach (Place p in src)
            {
                if (placesCurrentCat != null && p.Cat != placesCurrentCat && placesCurrentCat != MyPoints) continue;
                // les noms officiels du jeu contiennent des espaces insecables : on les compare comme des espaces
                if (filter.Length > 0 && p.Name.Replace(' ', ' ').ToLowerInvariant().IndexOf(filter, StringComparison.Ordinal) < 0) continue;
                placesShown.Add(p);
                if (placesShown.Count >= 250) break;
            }
            bool mine = placesCurrentCat == MyPoints;
            var x = new StringBuilder("<StackPanel xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Spacing='1'>");
            for (int i = 0; i < placesShown.Count; i++)
            {
                Place p = placesShown[i];
                x.Append("<Grid><Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>");
                x.Append("<Button x:Name='p_" + i + "' HorizontalAlignment='Stretch' HorizontalContentAlignment='Left' Background='Transparent' BorderThickness='0' Padding='8,4' Tag='tp|" + i + "' ToolTipService.ToolTip='Se téléporter · " + F(p.X) + ", " + F(p.Y) + ", " + F(p.Z) + "'>");
                x.Append("<TextBlock TextTrimming='CharacterEllipsis' Text='" + X(p.Name) + "'/></Button>");
                if (mine) x.Append("<Button x:Name='d_" + i + "' Grid.Column='1' Content='✕' Padding='8,2' Background='Transparent' BorderThickness='0' Tag='del|" + i + "' ToolTipService.ToolTip='Supprimer ce point'/>");
                x.Append("</Grid>");
            }
            if (placesShown.Count == 0) x.Append("<TextBlock Opacity='0.6' Margin='8,4' Text='" + (mine ? "Aucun point : placez la caméra puis « Ajouter ma position »." : "Aucun lieu.") + "'/>");
            x.Append("</StackPanel>");
            object list = Load(x.ToString());
            MethodInfo f = list.GetType().GetMethod("FindName", new[] { typeof(string) });
            for (int i = 0; i < placesShown.Count; i++)
                foreach (string pre in new[] { "p_", "d_" })
                {
                    object b = f.Invoke(list, new object[] { pre + i });
                    if (b != null) b.GetType().GetEvent("Click").AddEventHandler(b, placesClick);
                }
            SetProp(placesScroll, "Content", list);
            placesList = list;
        }

        static void OnPlacesClick(object sender, object e)
        {
            try
            {
                string[] t = ((GetProp(sender, "Tag") as string) ?? "").Split('|');
                int i = int.Parse(t[1]);
                if (t[0] == "add") { AddMyPoint(); return; }
                if (i < 0 || i >= placesShown.Count) return;
                Place p = placesShown[i];
                if (t[0] == "del")
                {
                    myPlaces.Remove(p); SaveMyPoints(); RefreshPlaces();
                    SetProp(placesStatus, "Text", "« " + p.Name + " » supprimé de Mes points.");
                    return;
                }
                SetProp(placesStatus, "Text", "Téléportation : " + p.Name + "…");
                RunPlaceOp(delegate { Teleport(p); });
            }
            catch (Exception ex) { LogOnce("lieux clic: " + ex.GetBaseException().Message); }
        }

        static void RunPlaceOp(System.Threading.ThreadStart op)
        {
            var th = new System.Threading.Thread(delegate () { try { op(); } catch (Exception ex) { LogOnce("lieux op: " + ex.Message); } });
            th.IsBackground = true; th.Start();
        }

        // Dans ce NCWE-ci (pas par le canal partage : avec deux NCWE ouverts, l'autre fenetre se teleportait).
        static void Teleport(Place p)
        {
            if (p.HasView) { SetCameraGame(p.X, p.Y, p.Z, p.Heading, p.Pitch); return; }
            // lieu sans vue enregistree : on se place a 14 m, en regardant le point d'un peu au-dessus, cap actuel garde
            double[] cam = CameraGame();
            double heading = cam != null ? cam[3] : 0, pitch = -25;
            double h = heading * Math.PI / 180, pr = pitch * Math.PI / 180;
            double fx = -Math.Sin(h) * Math.Cos(pr), fy = Math.Cos(h) * Math.Cos(pr), fz = Math.Sin(pr);   // cap 0 = +Y, sens trigo
            SetCameraGame(p.X - fx * 14, p.Y - fy * 14, p.Z + 1 - fz * 14, heading, pitch);
        }

        static void AddMyPoint()
        {
            string name = ((GetProp(placesNewName, "Text") as string) ?? "").Trim();
            if (name.Length == 0) name = "Point " + (myPlaces.Count + 1);
            RunPlaceOp(delegate
            {
                double[] c = CameraGame();                // camera de CE NCWE (pas celle d'une autre fenetre)
                if (c == null) return;
                var p = new Place { Cat = MyPoints, Name = name, X = c[0], Y = c[1], Z = c[2], HasView = true, Heading = c[3], Pitch = c[4] };
                lock (myPlaces) { myPlaces.Add(p); SaveMyPoints(); }
                pendingPlacesRefresh = true;
            });
        }

        static volatile bool pendingPlacesRefresh;

        // Appele a chaque passage UI : rafraichit la liste apres un ajout fait en arriere-plan.
        static void PlacesUiTick()
        {
            try { TeamUiTick(); } catch (Exception e) { LogOnce("equipe ui: " + e.GetBaseException().Message); }
            if (!pendingPlacesRefresh || placesCat == null) return;
            pendingPlacesRefresh = false;
            if (placesCurrentCat != MyPoints)
            {
                int n = (int)GetProp(GetProp(placesCat, "Items"), "Count");
                SetProp(placesCat, "SelectedIndex", n - 1);      // affiche Mes points
            }
            else RefreshPlaces();
            SetProp(placesNewName, "Text", "");
            SetProp(placesStatus, "Text", "Point ajouté à Mes points (" + myPlaces.Count + ").");
        }
    }
}
