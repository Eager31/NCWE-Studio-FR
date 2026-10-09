// NCWE Studio - traduction francaise de l'interface (plugin, aucun fichier de l'app modifie).
// Charge par .NET via DOTNET_STARTUP_HOOKS (voir "Lancer NCWE en francais.cmd").
// Ne traduit que l'affichage : les donnees (props, meshes, chemins, noms saisis) ne sont jamais modifiees.
// Compile avec le csc de .NET Framework (C# 5), tout passe par reflexion : aucune dependance a WinUI a la compilation.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

public static class StartupHook
{
    public static void Initialize()
    {
        try { NcweFr.Plugin.Start(); }
        catch (Exception e) { NcweFr.Plugin.Log("init: " + e); }
    }
}

namespace NcweFr
{
    static partial class Plugin
    {
        // ---------- configuration ----------
        static string dir;
        static readonly Dictionary<string, string> exact = new Dictionary<string, string>(StringComparer.Ordinal);
        static readonly List<KeyValuePair<string, string>> fragments = new List<KeyValuePair<string, string>>();
        static readonly List<KeyValuePair<Regex, string>> patterns = new List<KeyValuePair<Regex, string>>();
        static readonly HashSet<string> skipNames = new HashSet<string>(StringComparer.Ordinal);
        static readonly Dictionary<string, string> cache = new Dictionary<string, string>(StringComparer.Ordinal);

        // Controles de saisie : leur texte appartient a l'utilisateur.
        static readonly HashSet<string> inputTypes = new HashSet<string>(StringComparer.Ordinal) {
            "TextBox", "PasswordBox", "RichEditBox", "AutoSuggestBox", "NumberBox", "ComboBox"
        };
        static readonly string[] candidateProps = {
            "Text", "Content", "Header", "PlaceholderText", "Title", "Label", "Description",
            "OnContent", "OffContent", "PrimaryButtonText", "SecondaryButtonText", "CloseButtonText",
            "Subtitle", "Message"
        };

        public static void Start()
        {
            dir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
            string exe = "";
            try { exe = Path.GetFileNameWithoutExtension(Process.GetCurrentProcess().MainModule.FileName); } catch { }
            if (!string.Equals(exe, "NCWE.Studio", StringComparison.OrdinalIgnoreCase)) return;
            foreach (string a in Environment.GetCommandLineArgs())
                if (a == "--cache-worker" || a == "convert") return;

            try { File.Delete(Path.Combine(dir, "ncwe-fr.log")); } catch { }
            LoadDictionary(Path.Combine(dir, "fr.txt"));
            Log("dictionnaire : " + exact.Count + " entrees, " + fragments.Count + " fragments, " + patterns.Count + " motifs");

            pump = new Thread(PumpLoop);
            pump.IsBackground = true;
            pump.Name = "ncwe-fr";
            pump.Start();
        }

        // ---------- dictionnaire ----------
        static string Unescape(string s) { return s.Replace("\\n", "\n").Replace("\\t", "\t"); }

        static void LoadDictionary(string path)
        {
            if (!File.Exists(path)) { Log("fr.txt introuvable : " + path); return; }
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (raw.Length == 0 || raw[0] == '#') continue;
                if (raw.StartsWith("@skip "))
                {
                    foreach (string n in raw.Substring(6).Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)) skipNames.Add(n);
                    continue;
                }
                int sep = raw.IndexOf(" => ", StringComparison.Ordinal);
                if (sep <= 0) continue;
                string key = raw.Substring(0, sep);
                string val = Unescape(raw.Substring(sep + 4));
                try
                {
                    if (key[0] == '/' && key.Length > 2 && key[key.Length - 1] == '/')
                        patterns.Add(new KeyValuePair<Regex, string>(new Regex("^" + Unescape(key.Substring(1, key.Length - 2)) + "$", RegexOptions.Singleline), val));
                    else if (key[0] == '~')
                    {
                        string k = Unescape(key.Substring(1)).Trim();
                        // un fragment doit contenir au moins deux mots : il ne peut alors jamais tomber dans un nom de prop
                        if (k.IndexOf(' ') > 0) fragments.Add(new KeyValuePair<string, string>(k, val.Trim()));
                    }
                    else exact[Unescape(key).Trim()] = val.Trim();
                }
                catch (Exception e) { Log("ligne ignoree : " + raw + " (" + e.Message + ")"); }
            }
            fragments.Sort(delegate (KeyValuePair<string, string> a, KeyValuePair<string, string> b) { return b.Key.Length.CompareTo(a.Key.Length); });
        }

        static bool IsWord(char c) { return char.IsLetterOrDigit(c) || c == '_'; }

        // Mode diagnostic (variable NCWE_FR_DUMP=1) : liste les textes affiches restes en anglais.
        static readonly bool dump = Environment.GetEnvironmentVariable("NCWE_FR_DUMP") == "1";
        static readonly HashSet<string> missing = new HashSet<string>(StringComparer.Ordinal);
        static readonly HashSet<string> produced = new HashSet<string>(StringComparer.Ordinal);
        static int applied, lastMissingCount;

        static string Translate(string s, bool exactOnly)
        {
            if (string.IsNullOrEmpty(s)) return s;
            string res = TranslateCached(s, exactOnly);
            if (res == s) { if (dump && !produced.Contains(s) && Regex.IsMatch(s, "[A-Za-z]{2}")) missing.Add(s); }
            else { applied++; if (dump) produced.Add(res); }
            return res;
        }

        static string TranslateCached(string s, bool exactOnly)
        {
            string key = exactOnly ? "\u0001" + s : s;
            string r;
            if (cache.TryGetValue(key, out r)) return r;
            r = TranslateCore(s, exactOnly);
            if (cache.Count > 50000) cache.Clear();
            cache[key] = r;
            return r;
        }

        static string TranslateCore(string s, bool exactOnly)
        {
            string t = s.Trim();
            if (t.Length == 0) return s;
            int lead = s.IndexOf(t, StringComparison.Ordinal);
            string pre = s.Substring(0, lead), post = s.Substring(lead + t.Length);
            string v;
            if (exact.TryGetValue(t, out v)) return pre + v + post;
            if (exactOnly) return s;
            foreach (var p in patterns)
            {
                Match m = p.Key.Match(t);
                if (m.Success) return pre + TranslateInner(m.Result(p.Value)) + post;
            }
            string f = ApplyFragments(t);
            return f == t ? s : pre + f + post;
        }

        // Les captures de motifs ($1...) peuvent elles-memes contenir des morceaux connus.
        static string TranslateInner(string s) { return ApplyFragments(s); }

        static string ApplyFragments(string t)
        {
            if (t.IndexOf(' ') < 0) return t;
            foreach (var f in fragments)
            {
                int from = 0;
                while (true)
                {
                    int i = t.IndexOf(f.Key, from, StringComparison.Ordinal);
                    if (i < 0) break;
                    int end = i + f.Key.Length;
                    bool okL = i == 0 || !IsWord(f.Key[0]) || !IsWord(t[i - 1]);
                    bool okR = end == t.Length || !IsWord(f.Key[f.Key.Length - 1]) || !IsWord(t[end]);
                    if (okL && okR)
                    {
                        t = t.Substring(0, i) + f.Value + t.Substring(end);
                        from = i + f.Value.Length;
                    }
                    else from = i + 1;
                    if (from >= t.Length) break;
                }
            }
            return t;
        }

        // ---------- acces au thread UI ----------
        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc fn, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);

        const int WH_GETMESSAGE = 3;
        static Thread pump;
        static uint wmTick;
        static readonly HookProc hookProc = Hook;            // garde le delegue en vie
        static readonly EnumWindowsProc enumProc = EnumOne;
        static readonly Dictionary<uint, IntPtr> hooks = new Dictionary<uint, IntPtr>();
        static readonly Dictionary<uint, IntPtr> threadWindow = new Dictionary<uint, IntPtr>();
        static uint myPid;
        static volatile int busy;
        static int intervalMs = 600;

        static void PumpLoop()
        {
            myPid = (uint)Process.GetCurrentProcess().Id;
            wmTick = RegisterWindowMessage("NCWE.FR.Tick");
            while (true)
            {
                try
                {
                    EnumWindows(enumProc, IntPtr.Zero);
                    foreach (var kv in new List<KeyValuePair<uint, IntPtr>>(threadWindow))
                    {
                        if (!IsWindow(kv.Value)) { threadWindow.Remove(kv.Key); continue; }
                        if (!hooks.ContainsKey(kv.Key))
                        {
                            IntPtr h = SetWindowsHookEx(WH_GETMESSAGE, hookProc, IntPtr.Zero, kv.Key);
                            hooks[kv.Key] = h;
                            Log("hook thread UI " + kv.Key + (h == IntPtr.Zero ? " : ECHEC " + Marshal.GetLastWin32Error() : " ok"));
                        }
                        if (busy == 0) PostMessage(kv.Value, wmTick, IntPtr.Zero, IntPtr.Zero);
                    }
                }
                catch (Exception e) { LogOnce("pump: " + e); }
                Thread.Sleep(intervalMs);
            }
        }

        static bool EnumOne(IntPtr hwnd, IntPtr l)
        {
            uint pid;
            uint tid = GetWindowThreadProcessId(hwnd, out pid);
            if (pid != myPid || threadWindow.ContainsKey(tid)) return true;
            var sb = new StringBuilder(128);
            GetClassName(hwnd, sb, 128);
            if (sb.ToString() == "WinUIDesktopWin32WindowClass") threadWindow[tid] = hwnd;
            return true;
        }

        static IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (code >= 0 && wParam == (IntPtr)1) KeyFilter(lParam);
            }
            catch (Exception e) { LogOnce("clavier: " + e); }
            try
            {
                // wParam == PM_REMOVE : le message sort vraiment de la file (boucle principale)
                if (code >= 0 && wParam == (IntPtr)1 && (uint)Marshal.ReadInt32(lParam, IntPtr.Size) == wmTick && busy == 0)
                {
                    busy = 1;
                    if (!Enqueue()) { Work(); }
                }
            }
            catch (Exception e) { busy = 0; LogOnce("hook: " + e); }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        static object dispatcher;
        static MethodInfo tryEnqueue;
        static Delegate workDelegate;

        static bool Enqueue()
        {
            try
            {
                if (dispatcher == null)
                {
                    Type dq = FindType("Microsoft.UI.Dispatching.DispatcherQueue");
                    Type handler = FindType("Microsoft.UI.Dispatching.DispatcherQueueHandler");
                    if (dq == null || handler == null) return false;
                    dispatcher = dq.GetMethod("GetForCurrentThread", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
                    tryEnqueue = dq.GetMethod("TryEnqueue", new[] { handler });
                    workDelegate = Delegate.CreateDelegate(handler, typeof(Plugin).GetMethod("Work", BindingFlags.NonPublic | BindingFlags.Static));
                    if (dispatcher == null) return false;
                }
                return (bool)tryEnqueue.Invoke(dispatcher, new object[] { workDelegate });
            }
            catch (Exception e) { LogOnce("dispatcher: " + e); dispatcher = null; return false; }
        }

        // ---------- parcours de l'interface (thread UI) ----------
        static Type tApplication, tWindow, tVth, tToolTipService, tDependencyObject, tUIElement, tFrameworkElement;
        static MethodInfo mChildrenCount, mChild, mPopups, mGetToolTip, mSetToolTip, mReadLocalValue;
        static PropertyInfo pName, pVisibility;
        static readonly Dictionary<Type, PropertyInfo[]> propsByType = new Dictionary<Type, PropertyInfo[]>();
        static readonly Dictionary<string, object> dpCache = new Dictionary<string, object>();
        static readonly List<object> windows = new List<object>();
        static DateTime lastWindowScan = DateTime.MinValue;
        static int scans;

        static Type FindType(string fullName)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = a.GetType(fullName, false);
                if (t != null) return t;
            }
            return null;
        }

        static bool InitTypes()
        {
            if (tVth != null) return true;
            tApplication = FindType("Microsoft.UI.Xaml.Application");
            tWindow = FindType("Microsoft.UI.Xaml.Window");
            tVth = FindType("Microsoft.UI.Xaml.Media.VisualTreeHelper");
            tToolTipService = FindType("Microsoft.UI.Xaml.Controls.ToolTipService");
            tDependencyObject = FindType("Microsoft.UI.Xaml.DependencyObject");
            tUIElement = FindType("Microsoft.UI.Xaml.UIElement");
            tFrameworkElement = FindType("Microsoft.UI.Xaml.FrameworkElement");
            Type tXamlRoot = FindType("Microsoft.UI.Xaml.XamlRoot");
            if (tApplication == null || tWindow == null || tVth == null || tDependencyObject == null || tXamlRoot == null) { tVth = null; return false; }
            mChildrenCount = tVth.GetMethod("GetChildrenCount", new[] { tDependencyObject });
            mChild = tVth.GetMethod("GetChild", new[] { tDependencyObject, typeof(int) });
            mPopups = tVth.GetMethod("GetOpenPopupsForXamlRoot", new[] { tXamlRoot });
            mGetToolTip = tToolTipService.GetMethod("GetToolTip", new[] { tDependencyObject });
            mSetToolTip = tToolTipService.GetMethod("SetToolTip", new[] { tDependencyObject, typeof(object) });
            mReadLocalValue = tDependencyObject.GetMethod("ReadLocalValue");
            pName = tFrameworkElement.GetProperty("Name");
            pVisibility = tUIElement.GetProperty("Visibility");
            return true;
        }

        static void Work()
        {
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                if (!InitTypes()) return;
                object app = tApplication.GetProperty("Current").GetValue(null, null);
                if (app == null) return;
                if ((DateTime.Now - lastWindowScan).TotalSeconds > 2) { FindWindows(app); lastWindowScan = DateTime.Now; }
                ExtractTick(app);
                foreach (object w in windows.ToArray())
                {
                    try
                    {
                        PropertyInfo pt = tWindow.GetProperty("Title");
                        string title = pt.GetValue(w, null) as string;
                        string tt = Translate(title, false);
                        if (tt != title) pt.SetValue(w, tt, null);
                        object content = tWindow.GetProperty("Content").GetValue(w, null);
                        if (content == null) continue;
                        Walk(content);
                        if (w.GetType().Name == "MainWindow") { SelectionTick(w, content); XRayTick(w, content); }
                        object root = tUIElement.GetProperty("XamlRoot").GetValue(content, null);
                        if (root != null)
                            foreach (object popup in (IEnumerable)mPopups.Invoke(null, new[] { root }))
                            {
                                Walk(popup);
                                object child = popup.GetType().GetProperty("Child").GetValue(popup, null);
                                if (child != null) Walk(child);
                            }
                    }
                    catch (Exception e)
                    {
                        // fenetre fermee : on l'oublie
                        windows.Remove(w);
                        LogOnce("fenetre: " + e.GetBaseException().Message);
                    }
                }
            }
            catch (Exception e) { LogOnce("work: " + e); }
            finally
            {
                sw.Stop();
                // intervalle adaptatif : le parcours ne doit jamais peser sur le rendu 3D
                intervalMs = Math.Max(500, Math.Min(3000, (int)sw.ElapsedMilliseconds * 12));
                if (++scans == 5 || scans % 500 == 0) Log("parcours " + sw.ElapsedMilliseconds + " ms, fenetres " + windows.Count + ", intervalle " + intervalMs + " ms");
                if (dump && missing.Count != lastMissingCount)
                {
                    lastMissingCount = missing.Count;
                    var l = new List<string>(missing); l.Sort(StringComparer.Ordinal);
                    try { File.WriteAllLines(Path.Combine(dir, "non-traduits.txt"), l.ConvertAll(x => x.Replace("\n", "\\n")).ToArray(), Encoding.UTF8); } catch { }
                }
                if (scans == 5) Log("traductions appliquees : " + applied);
                busy = 0;
            }
        }

        // Les fenetres WinUI 3 ne sont listees nulle part : on les cherche dans les champs de l'app.
        static void FindWindows(object app)
        {
            var seen = new HashSet<object>(new RefEq());
            var queue = new Queue<KeyValuePair<object, int>>();
            foreach (object w in windows) seen.Add(w);
            queue.Enqueue(new KeyValuePair<object, int>(app, 0));
            Assembly appAsm = app.GetType().Assembly;
            foreach (Type t in SafeTypes(appAsm))
            {
                if (t.ContainsGenericParameters) continue;
                foreach (FieldInfo f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.IsLiteral || !IsInteresting(f.FieldType)) continue;
                    try { object v = f.GetValue(null); if (v != null) queue.Enqueue(new KeyValuePair<object, int>(v, 1)); } catch { }
                }
            }
            while (queue.Count > 0)
            {
                var item = queue.Dequeue();
                object o = item.Key;
                if (o == null || !seen.Add(o)) continue;
                if (tWindow.IsInstanceOfType(o) && !windows.Contains(o)) { windows.Add(o); Log("fenetre trouvee : " + o.GetType().FullName); }
                if (item.Value >= 3) continue;
                IEnumerable list = o as IEnumerable;
                if (list != null && !(o is string) && o.GetType().Namespace != null && o.GetType().Namespace.StartsWith("System.Collections"))
                {
                    int n = 0;
                    foreach (object e in list) { if (++n > 64) break; if (e != null && tWindow.IsInstanceOfType(e)) queue.Enqueue(new KeyValuePair<object, int>(e, item.Value + 1)); }
                    continue;
                }
                if (o.GetType().Assembly != appAsm) continue;
                for (Type t = o.GetType(); t != null && t.Assembly == appAsm; t = t.BaseType)
                    foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (!IsInteresting(f.FieldType)) continue;
                        try { object v = f.GetValue(o); if (v != null) queue.Enqueue(new KeyValuePair<object, int>(v, item.Value + 1)); } catch { }
                    }
            }
        }

        static bool IsInteresting(Type ft)
        {
            if (ft.IsValueType || ft == typeof(string)) return false;
            if (tWindow.IsAssignableFrom(ft)) return true;
            if (ft.IsGenericType && ft.Namespace == "System.Collections.Generic")
                foreach (Type a in ft.GetGenericArguments()) if (tWindow.IsAssignableFrom(a)) return true;
            // objets de l'app (sessions, controleurs...) qui peuvent tenir une fenetre
            return ft.Namespace != null && ft.Namespace.StartsWith("NCWE.Studio");
        }

        static IEnumerable<Type> SafeTypes(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException e) { var l = new List<Type>(); foreach (Type t in e.Types) if (t != null) l.Add(t); return l; }
        }

        class RefEq : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
            public int GetHashCode(object o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }
        }

        // Parcours en profondeur de l'arbre visuel.
        //  - sous-arbres ignores : noms listes par @skip, GridViewItem (cartes de props), champs de saisie
        //  - ListViewItem / TreeViewItem : correspondance exacte seulement (jamais de fragments sur des donnees)
        static void Walk(object rootEl)
        {
            var stack = new Stack<KeyValuePair<object, bool>>();
            stack.Push(new KeyValuePair<object, bool>(rootEl, false));
            while (stack.Count > 0)
            {
                var it = stack.Pop();
                object el = it.Key;
                bool exactOnly = it.Value;
                Type type = el.GetType();
                string tn = type.Name;

                if (tUIElement.IsInstanceOfType(el))
                {
                    object vis = pVisibility.GetValue(el, null);
                    if (Convert.ToInt32(vis) == 1) continue; // Collapsed
                }
                if (tFrameworkElement.IsInstanceOfType(el))
                {
                    string name = pName.GetValue(el, null) as string;
                    if (!string.IsNullOrEmpty(name) && skipNames.Contains(name)) continue;
                    if (name == "GoX" && worldGoTo == null) worldGoTo = el;
                }
                if (tn == "GridViewItem") continue;
                if (tn == "ListViewItem" || tn == "TreeViewItem") exactOnly = true;

                TranslateElement(el, type, tn, exactOnly);
                if (inputTypes.Contains(tn)) continue;

                int n = (int)mChildrenCount.Invoke(null, new[] { el });
                for (int i = n - 1; i >= 0; i--)
                {
                    object c = mChild.Invoke(null, new[] { el, (object)i });
                    if (c != null) stack.Push(new KeyValuePair<object, bool>(c, exactOnly));
                }
            }
        }

        static PropertyInfo[] PropsFor(Type type, string tn)
        {
            PropertyInfo[] arr;
            if (propsByType.TryGetValue(type, out arr)) return arr;
            var list = new List<PropertyInfo>();
            foreach (string p in candidateProps)
            {
                if (p == "Text" && (inputTypes.Contains(tn) || tn == "TextBlock")) continue;
                if (p == "Content" && (tn.EndsWith("ContentPresenter") || tn == "ScrollViewer" || tn == "Frame")) continue;
                PropertyInfo pi = null;
                try { pi = type.GetProperty(p, BindingFlags.Public | BindingFlags.Instance); } catch (AmbiguousMatchException) { }
                if (pi == null || !pi.CanRead || !pi.CanWrite || pi.GetIndexParameters().Length > 0) continue;
                if (pi.PropertyType != typeof(string) && pi.PropertyType != typeof(object)) continue;
                list.Add(pi);
            }
            arr = list.ToArray();
            propsByType[type] = arr;
            return arr;
        }

        static void TranslateElement(object el, Type type, string tn, bool exactOnly)
        {
            if (tn == "TextBlock") TranslateTextBlock(el, type, exactOnly);
            foreach (PropertyInfo pi in PropsFor(type, tn))
            {
                string s = pi.GetValue(el, null) as string;
                if (string.IsNullOrEmpty(s)) continue;
                string t = Translate(s, exactOnly);
                if (t != s && !IsBound(el, type, pi.Name)) pi.SetValue(el, t, null);
            }
            if (mGetToolTip != null && tDependencyObject.IsInstanceOfType(el))
            {
                string tip = mGetToolTip.Invoke(null, new[] { el }) as string;
                if (!string.IsNullOrEmpty(tip))
                {
                    string t = Translate(tip, exactOnly);
                    if (t != tip) mSetToolTip.Invoke(null, new[] { el, (object)t });
                }
            }
        }

        static void TranslateTextBlock(object el, Type type, bool exactOnly)
        {
            IEnumerable inlines = type.GetProperty("Inlines").GetValue(el, null) as IEnumerable;
            int count = 0; object first = null;
            if (inlines != null) foreach (object i in inlines) { if (count == 0) first = i; count++; }
            if (count <= 1 && (first == null || first.GetType().Name == "Run"))
            {
                PropertyInfo pt = type.GetProperty("Text");
                string s = pt.GetValue(el, null) as string;
                if (string.IsNullOrEmpty(s)) return;
                string t = Translate(s, exactOnly);
                if (t != s && !IsBound(el, type, "Text")) pt.SetValue(el, t, null);
                return;
            }
            TranslateInlines(inlines, exactOnly);
        }

        // Texte riche (Run, Span, Hyperlink...) : chaque morceau separement pour garder la mise en forme.
        static void TranslateInlines(IEnumerable inlines, bool exactOnly)
        {
            foreach (object i in inlines)
            {
                Type it = i.GetType();
                if (it.Name == "Run")
                {
                    PropertyInfo pt = it.GetProperty("Text");
                    string s = pt.GetValue(i, null) as string;
                    if (string.IsNullOrEmpty(s)) continue;
                    string t = Translate(s, exactOnly);
                    if (t != s) pt.SetValue(i, t, null);
                }
                else
                {
                    PropertyInfo pc = it.GetProperty("Inlines");
                    IEnumerable sub = pc == null ? null : pc.GetValue(i, null) as IEnumerable;
                    if (sub != null) TranslateInlines(sub, exactOnly);
                }
            }
        }

        // Une propriete liee par {Binding} / TemplateBinding n'est pas ecrasee : on casserait la liaison.
        static bool IsBound(object el, Type type, string prop)
        {
            string k = type.FullName + "." + prop;
            object dp;
            if (!dpCache.TryGetValue(k, out dp))
            {
                dp = null;
                for (Type t = type; t != null && dp == null; t = t.BaseType)
                {
                    PropertyInfo sp = t.GetProperty(prop + "Property", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
                    if (sp != null) dp = sp.GetValue(null, null);
                }
                dpCache[k] = dp;
            }
            if (dp == null || mReadLocalValue == null) return false;
            try
            {
                object local = mReadLocalValue.Invoke(el, new[] { dp });
                return local != null && local.GetType().Name.Contains("Expression");
            }
            catch { return false; }
        }

        // ---------- journal ----------
        static readonly HashSet<string> logged = new HashSet<string>();
        static void LogOnce(string s) { lock (logged) { if (logged.Count > 200 || !logged.Add(s)) return; } Log(s); }
        public static void Log(string s)
        {
            try { File.AppendAllText(Path.Combine(dir ?? ".", "ncwe-fr.log"), DateTime.Now.ToString("HH:mm:ss ") + s + Environment.NewLine); } catch { }
        }
    }
}
