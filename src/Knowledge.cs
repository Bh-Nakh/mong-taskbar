using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Web.Script.Serialization;

namespace TaskbarGroup
{
    public class LinkRec
    {
        public bool Ok { get; set; }
        public string Target { get; set; }
        public string Args { get; set; }
        public string WorkDir { get; set; }
        public LinkRec() { Target = ""; Args = ""; WorkDir = ""; }
    }

    public class ExeRec
    {
        public bool Exists { get; set; }
        public string Description { get; set; }
        public string Product { get; set; }
        public ExeRec() { Description = ""; Product = ""; }
    }

    public class KnowledgeData
    {
        /// <summary>바로가기 경로 → 대상/인수/작업 폴더.</summary>
        public Dictionary<string, LinkRec> Links { get; set; }
        /// <summary>실행 파일 경로 → 존재 여부, 설명, 제품명.</summary>
        public Dictionary<string, ExeRec> Exes { get; set; }
        /// <summary>폴더 경로 → 존재 여부.</summary>
        public Dictionary<string, bool> Dirs { get; set; }
        /// <summary>시작 메뉴(사용자/공용)의 바로가기 목록. null 이면 아직 한 번도 안 읽음.</summary>
        public List<string> StartMenu { get; set; }
        /// <summary>항목 → 그 항목이 실제로 띄운 것으로 확인된 프로세스 ("실행 파일|명령줄").
        /// 예: CapCutAutomator.exe → "pythonw.exe|... capcut_ui.py"</summary>
        public Dictionary<string, List<string>> Launched { get; set; }
        /// <summary>항목 → 그 항목이 띄운 창의 작업표시줄 앱 ID ("1|앱ID" = 본체 창, "0|앱ID" = 하위 창).
        /// 예: 클리버스 스튜디오 → "1|CleverseStudio...", "0|CleverseStudio.SubtitleMaker"</summary>
        public Dictionary<string, List<string>> AppIds { get; set; }

        public KnowledgeData()
        {
            Links = new Dictionary<string, LinkRec>();
            Exes = new Dictionary<string, ExeRec>();
            Dirs = new Dictionary<string, bool>();
            Launched = new Dictionary<string, List<string>>();
            AppIds = new Dictionary<string, List<string>>();
        }
    }

    /// <summary>한 번 읽은 바로가기·실행 파일·폴더·시작 메뉴 정보와, 항목이 실제로 띄운 프로세스를
    /// knowledge.json 에 기록해 두고 다시 읽지 않는다. 시작 메뉴는 폴더 변경 감지로 바뀐 것만 갱신.
    /// "아이콘·정보 다시 읽기" 로 전부 비울 수 있다.</summary>
    public static class Knowledge
    {
        private static readonly object _lock = new object();
        private static KnowledgeData _d;
        private static bool _dirty;
        private static Dictionary<string, List<string>> _startMenuMap;

        /// <summary>파일에 저장할지 (도우미 프로세스만 true — 여러 프로세스가 동시에 쓰지 않게).</summary>
        public static bool Persist;

        public static string FilePath
        {
            get { return Path.Combine(GroupStore.AppDir, "knowledge.json"); }
        }

        private static KnowledgeData D
        {
            get
            {
                if (_d == null)
                {
                    try
                    {
                        if (File.Exists(FilePath))
                        {
                            JavaScriptSerializer ser = new JavaScriptSerializer();
                            ser.MaxJsonLength = int.MaxValue;
                            _d = ser.Deserialize<KnowledgeData>(File.ReadAllText(FilePath, System.Text.Encoding.UTF8));
                        }
                    }
                    catch { _d = null; }
                    if (_d == null) _d = new KnowledgeData();
                    // 대소문자 구분 없이 찾도록 다시 담는다
                    _d.Links = new Dictionary<string, LinkRec>(_d.Links ?? new Dictionary<string, LinkRec>(), StringComparer.OrdinalIgnoreCase);
                    _d.Exes = new Dictionary<string, ExeRec>(_d.Exes ?? new Dictionary<string, ExeRec>(), StringComparer.OrdinalIgnoreCase);
                    _d.Dirs = new Dictionary<string, bool>(_d.Dirs ?? new Dictionary<string, bool>(), StringComparer.OrdinalIgnoreCase);
                    _d.Launched = new Dictionary<string, List<string>>(_d.Launched ?? new Dictionary<string, List<string>>(), StringComparer.OrdinalIgnoreCase);
                    _d.AppIds = new Dictionary<string, List<string>>(_d.AppIds ?? new Dictionary<string, List<string>>(), StringComparer.OrdinalIgnoreCase);
                }
                return _d;
            }
        }

        public static LinkRec Link(string path)
        {
            if (string.IsNullOrEmpty(path)) return new LinkRec();
            lock (_lock)
            {
                LinkRec r;
                if (D.Links.TryGetValue(path, out r)) return r;
            }
            LinkRec rec = new LinkRec();
            try
            {
                ShellLinkInfo i = ShellLink.Read(path);
                rec.Ok = true;
                rec.Target = Environment.ExpandEnvironmentVariables(i.Target ?? "");
                rec.Args = i.Arguments ?? "";
                rec.WorkDir = Environment.ExpandEnvironmentVariables(i.WorkingDirectory ?? "");
            }
            catch { rec.Ok = false; }
            lock (_lock) { D.Links[path] = rec; _dirty = true; }
            return rec;
        }

        public static ExeRec Exe(string path)
        {
            if (string.IsNullOrEmpty(path)) return new ExeRec();
            lock (_lock)
            {
                ExeRec r;
                if (D.Exes.TryGetValue(path, out r)) return r;
            }
            ExeRec rec = new ExeRec();
            try
            {
                rec.Exists = File.Exists(path);
                if (rec.Exists)
                {
                    FileVersionInfo vi = FileVersionInfo.GetVersionInfo(path);
                    rec.Description = vi.FileDescription ?? "";
                    rec.Product = vi.ProductName ?? "";
                }
            }
            catch { }
            lock (_lock) { D.Exes[path] = rec; _dirty = true; }
            return rec;
        }

        public static bool DirExists(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            lock (_lock)
            {
                bool b;
                if (D.Dirs.TryGetValue(path, out b)) return b;
            }
            bool ex = false;
            try { ex = Directory.Exists(path); } catch { }
            lock (_lock) { D.Dirs[path] = ex; _dirty = true; }
            return ex;
        }

        // ---------- 시작 메뉴 ----------

        public static string[] StartMenuRoots
        {
            get
            {
                return new string[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                };
            }
        }

        /// <summary>시작 메뉴 바로가기: 대상 실행 파일 → 바로가기 이름들.
        /// 처음 한 번만 폴더를 훑고, 그 뒤로는 기록된 목록과 기록된 바로가기 정보만 쓴다.</summary>
        public static Dictionary<string, List<string>> StartMenuNames()
        {
            lock (_lock)
            {
                if (_startMenuMap != null) return _startMenuMap;
                if (D.StartMenu == null)
                {
                    List<string> files = new List<string>();
                    foreach (string root in StartMenuRoots)
                    {
                        try { files.AddRange(Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories)); }
                        catch { }
                    }
                    D.StartMenu = files;
                    _dirty = true;
                }
            }
            Dictionary<string, List<string>> map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            List<string> list;
            lock (_lock) { list = new List<string>(D.StartMenu); }
            foreach (string lnk in list)
            {
                LinkRec r = Link(lnk);
                if (!r.Ok || r.Target.Length == 0) continue;
                List<string> names;
                if (!map.TryGetValue(r.Target, out names)) { names = new List<string>(); map[r.Target] = names; }
                names.Add(Path.GetFileNameWithoutExtension(lnk));
            }
            lock (_lock) { _startMenuMap = map; }
            return map;
        }

        /// <summary>폴더 변경 감지에서 호출: 그 바로가기만 다시 읽도록 기록을 지운다.</summary>
        public static void FileChanged(string path, bool deleted)
        {
            if (string.IsNullOrEmpty(path)) return;
            lock (_lock)
            {
                D.Links.Remove(path);
                if (D.StartMenu != null && path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    bool inStartMenu = false;
                    foreach (string root in StartMenuRoots)
                        if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) inStartMenu = true;
                    if (inStartMenu)
                    {
                        D.StartMenu.RemoveAll(delegate (string s) { return string.Equals(s, path, StringComparison.OrdinalIgnoreCase); });
                        if (!deleted) D.StartMenu.Add(path);
                    }
                }
                _startMenuMap = null;
                _dirty = true;
            }
        }

        // ---------- 항목이 띄운 프로세스 ----------

        private static string Sig(RunningProcess p)
        {
            // 작업 폴더까지 넣어야 "cmd.exe" 처럼 흔한 프로그램이 다른 곳에서 떠도 잘못 맞지 않는다
            return (p.ExePath ?? "") + "|" + (p.CommandLine ?? "").Trim() + "|" + (p.CurrentDirectory ?? "").TrimEnd('\\');
        }

        public static void RecordLaunched(string entryKey, RunningProcess p)
        {
            if (string.IsNullOrEmpty(p.ExePath)) return;
            string sig = Sig(p);
            lock (_lock)
            {
                List<string> list;
                if (!D.Launched.TryGetValue(entryKey, out list)) { list = new List<string>(); D.Launched[entryKey] = list; }
                foreach (string s in list) if (string.Equals(s, sig, StringComparison.OrdinalIgnoreCase)) return;
                list.Add(sig);
                _dirty = true;
            }
        }

        /// <summary>이 항목이 띄운 것으로 확인된 프로세스들의 실행 파일 경로.</summary>
        public static List<string> LaunchedExes(string entryKey)
        {
            List<string> res = new List<string>();
            lock (_lock)
            {
                List<string> list;
                if (!D.Launched.TryGetValue(entryKey, out list)) return res;
                foreach (string s in list)
                {
                    int bar = s.IndexOf('|');
                    string exe = bar >= 0 ? s.Substring(0, bar) : s;
                    if (exe.Length > 0 && !res.Contains(exe)) res.Add(exe);
                }
            }
            return res;
        }

        public static bool WasLaunched(string entryKey, RunningProcess p)
        {
            if (string.IsNullOrEmpty(p.ExePath)) return false;
            string sig = Sig(p);
            lock (_lock)
            {
                List<string> list;
                if (!D.Launched.TryGetValue(entryKey, out list)) return false;
                foreach (string s in list) if (string.Equals(s, sig, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // ---------- 항목의 작업표시줄 앱 ID ----------

        public static void RecordAppId(string entryKey, string appId, bool main)
        {
            string rec = (main ? "1|" : "0|") + appId;
            lock (_lock)
            {
                List<string> list;
                if (!D.AppIds.TryGetValue(entryKey, out list)) { list = new List<string>(); D.AppIds[entryKey] = list; }
                for (int i = 0; i < list.Count; i++)
                {
                    if (!string.Equals(list[i].Substring(2), appId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(list[i], rec, StringComparison.OrdinalIgnoreCase)) return;
                    list.RemoveAt(i);     // 본체/하위가 바뀌었으면 새로 기록
                    break;
                }
                list.Add(rec);
                _dirty = true;
            }
        }

        /// <summary>이 항목의 앱 ID 들. main 이 true 면 본체 창의 것만, false 면 하위 창의 것만.</summary>
        public static List<string> AppIdsOf(string entryKey, bool main)
        {
            List<string> res = new List<string>();
            lock (_lock)
            {
                List<string> list;
                if (!D.AppIds.TryGetValue(entryKey, out list)) return res;
                foreach (string s in list)
                    if (s.Length > 2 && (s[0] == '1') == main) res.Add(s.Substring(2));
            }
            return res;
        }

        // ---------- 저장 / 초기화 ----------

        public static void Save()
        {
            if (!Persist) return;
            lock (_lock)
            {
                if (!_dirty || _d == null) return;
                try
                {
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    ser.MaxJsonLength = int.MaxValue;
                    string tmp = FilePath + ".tmp";
                    File.WriteAllText(tmp, ser.Serialize(_d), System.Text.Encoding.UTF8);
                    if (File.Exists(FilePath)) File.Delete(FilePath);
                    File.Move(tmp, FilePath);
                    _dirty = false;
                }
                catch { }
            }
        }

        /// <summary>기록을 전부 비운다 (다음에 필요할 때 다시 한 번씩 읽음).</summary>
        public static void Reset()
        {
            lock (_lock)
            {
                _d = new KnowledgeData();
                _d.Links = new Dictionary<string, LinkRec>(StringComparer.OrdinalIgnoreCase);
                _d.Exes = new Dictionary<string, ExeRec>(StringComparer.OrdinalIgnoreCase);
                _d.Dirs = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                _d.Launched = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                _startMenuMap = null;
                _dirty = true;
                try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
            }
        }
    }
}
