// Points d'equipe : chaque membre publie ses « Mes points » dans un dossier partage (Google Drive, OneDrive,
// Dropbox, dossier reseau…) sous la forme points-<nom>.tsv, et lit ceux des autres toutes les 10 s.
// Chacun n'ecrit que son propre fichier : pas de conflit de synchronisation.
// Reglage : equipe.txt (ligne 1 = dossier partage, ligne 2 = votre nom), cree par « Configurer l'equipe.cmd ».

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace NcweFr
{
    static partial class Plugin
    {
        const string TeamPrefix = "Équipe – ";
        static string teamDir, teamName;
        static bool teamInited;
        static List<Place> basePlaces;                       // lieux du jeu + points-*.tsv locaux
        static volatile List<Place> teamPlacesNew;           // derniere lecture du dossier d'equipe (thread de fond)
        static volatile int teamMembers;
        static string teamSig = "";

        static string TeamFile { get { return Path.Combine(dir, "equipe.txt"); } }

        static void TeamInit()
        {
            if (teamInited) return;
            teamInited = true;
            try
            {
                if (!File.Exists(TeamFile)) return;
                string[] l = File.ReadAllLines(TeamFile, Encoding.UTF8);
                if (l.Length == 0 || l[0].Trim().Length == 0) return;
                teamDir = l[0].Trim();
                teamName = l.Length > 1 && l[1].Trim().Length > 0 ? l[1].Trim() : Environment.UserName;
                var th = new Thread(TeamLoop); th.IsBackground = true; th.Name = "ncwe-fr-equipe"; th.Start();
                Log("equipe : dossier " + teamDir + ", membre " + teamName);
            }
            catch (Exception e) { LogOnce("equipe: " + e.GetBaseException().Message); }
        }

        static string TeamOwnFile
        {
            get
            {
                var sb = new StringBuilder();
                foreach (char ch in teamName) sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_');
                return Path.Combine(teamDir, "points-" + sb + ".tsv");
            }
        }

        // Publie Mes points dans le dossier d'equipe (fichier temporaire puis remplacement : jamais de fichier a moitie ecrit).
        static void PublishMyPoints()
        {
            if (teamDir == null) return;
            try
            {
                if (!Directory.Exists(teamDir)) return;
                var sb = new StringBuilder("# Points de " + teamName + " (publies par NCWE FR) : categorie, nom, x, y, z, cap, inclinaison\n");
                lock (myPlaces)
                    foreach (Place p in myPlaces)
                        sb.Append(TeamPrefix).Append(teamName).Append('\t').Append(p.Name.Replace('\t', ' ')).Append('\t').Append(F(p.X)).Append('\t').Append(F(p.Y)).Append('\t').Append(F(p.Z))
                          .Append('\t').Append(F(p.Heading)).Append('\t').Append(F(p.Pitch)).Append('\n');
                string target = TeamOwnFile, tmp = target + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);
            }
            catch (Exception e) { LogOnce("equipe publication: " + e.GetBaseException().Message); }
        }

        static void TeamLoop()
        {
            bool published = false;
            while (true)
            {
                try
                {
                    if (Directory.Exists(teamDir))
                    {
                        if (!published && myPlaces.Count > 0) { PublishMyPoints(); published = true; }
                        string own = Path.GetFileName(TeamOwnFile);
                        var files = new List<string>();
                        var sig = new StringBuilder();
                        foreach (string f in Directory.GetFiles(teamDir, "points-*.tsv"))
                        {
                            if (string.Equals(Path.GetFileName(f), own, StringComparison.OrdinalIgnoreCase)) continue;
                            var fi = new FileInfo(f);
                            files.Add(f); sig.Append(fi.Name).Append(fi.Length).Append(fi.LastWriteTimeUtc.Ticks).Append('|');
                        }
                        if (sig.ToString() != teamSig)
                        {
                            var list = new List<Place>();
                            foreach (string f in files) { try { list.AddRange(ReadPlaces(f)); } catch { } }
                            teamMembers = files.Count;
                            teamSig = sig.ToString();
                            teamPlacesNew = list;
                        }
                    }
                }
                catch (Exception e) { LogOnce("equipe lecture: " + e.GetBaseException().Message); }
                Thread.Sleep(10000);
            }
        }

        // Thread UI : integre la derniere lecture du dossier d'equipe (categories + liste).
        static void TeamUiTick()
        {
            List<Place> team = teamPlacesNew;
            if (team == null || placesCat == null || basePlaces == null) return;
            teamPlacesNew = null;
            var all = new List<Place>(basePlaces); all.AddRange(team);
            gamePlaces = all;
            var cats = new List<string>();
            foreach (Place p in gamePlaces) if (!cats.Contains(p.Cat)) cats.Add(p.Cat);
            cats.Add(MyPoints);
            bool same = cats.Count == placesCats.Count;
            for (int i = 0; same && i < cats.Count; i++) same = cats[i] == placesCats[i];
            if (!same)
            {
                string current = placesCurrentCat;
                object items = GetProp(placesCat, "Items");
                placesCats = cats;
                items.GetType().GetMethod("Clear").Invoke(items, null);
                foreach (string c in cats) items.GetType().GetMethod("Add").Invoke(items, new[] { Load("<ComboBoxItem xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Content='" + X(c) + "'/>") });
                int idx = current == null ? 0 : Math.Max(0, cats.IndexOf(current));
                SetProp(placesCat, "SelectedIndex", idx);
            }
            RefreshPlaces();
            SetProp(placesStatus, "Text", "Équipe : " + team.Count + " points de " + teamMembers + " membre" + (teamMembers > 1 ? "s" : "") + " · mis à jour " + DateTime.Now.ToString("HH:mm:ss"));
        }
    }
}
