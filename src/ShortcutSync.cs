using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace TaskbarGroup
{
    /// <summary>그룹 바로가기(.lnk)들을 현재 설정·실행 파일 위치에 맞춰 갱신한다.
    /// 바로가기 폴더, 작업표시줄 고정 폴더, 실행 파일 폴더에 있는 우리 바로가기가 대상.</summary>
    public static class ShortcutSync
    {
        public const string GroupAppIdPrefix = "TaskbarGroup.Group.";
        public const string ManagerAppId = "TaskbarGroup.Manager";
        private const string StartupLnkName = "몽작업표시줄 도우미.lnk";
        // 앱 이름이 몽작업표시줄로 바뀌기 전(TaskbarGroup.exe)에 만든 바로가기도 우리 것으로 알아본다 (이름이 바뀌어도 기존 그룹 바로가기가 이어지게)
        private const string LegacyExeName = "TaskbarGroup.exe";
        private const string LegacyStartupLnkName = "작업표시줄 그룹 도우미.lnk";

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(int wEventId, uint uFlags, string dwItem1, IntPtr dwItem2);
        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        private const int SHCNE_UPDATEITEM = 0x00002000;
        private const int SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_PATHW = 0x0005;
        private const uint SHCNF_IDLIST = 0x0000;

        private static readonly Regex GroupArg = new Regex("--group\\s+\"?([0-9A-Za-z_-]+)\"?");

        public static string PinnedFolder
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
            }
        }

        private static string StartupLnkPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupLnkName); }
        }

        public static string GroupIdFromArgs(string args)
        {
            if (string.IsNullOrEmpty(args)) return null;
            Match m = GroupArg.Match(args);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static bool IsOurExe(string target)
        {
            if (string.IsNullOrEmpty(target)) return false;
            string own = Path.GetFileName(Application.ExecutablePath);
            string name = Path.GetFileName(target);
            return string.Equals(name, own, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, LegacyExeName, StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> ScanFolders(Config cfg)
        {
            List<string> list = new List<string>();
            list.Add(GroupStore.ResolveShortcutFolder(cfg));
            list.Add(PinnedFolder);
            list.Add(GroupStore.AppDir);
            List<string> uniq = new List<string>();
            foreach (string f in list)
            {
                bool dup = false;
                foreach (string u in uniq)
                    if (string.Equals(u.TrimEnd('\\'), f.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) dup = true;
                if (!dup && Directory.Exists(f)) uniq.Add(f);
            }
            return uniq;
        }

        private class LinkFile
        {
            public string Path;
            public ShellLinkInfo Info;
            public string GroupId;
        }

        private static List<LinkFile> ScanOurLinks(Config cfg)
        {
            List<LinkFile> result = new List<LinkFile>();
            foreach (string dir in ScanFolders(cfg))
            {
                string[] files;
                try { files = Directory.GetFiles(dir, "*.lnk"); }
                catch { continue; }
                foreach (string f in files)
                {
                    ShellLinkInfo info;
                    try { info = ShellLink.Read(f); }
                    catch { continue; }
                    if (!IsOurExe(info.Target)) continue;
                    LinkFile lf = new LinkFile();
                    lf.Path = f;
                    lf.Info = info;
                    lf.GroupId = GroupIdFromArgs(info.Arguments);
                    result.Add(lf);
                }
            }
            return result;
        }

        private static ShellLinkInfo GroupLinkSpec(Group g, string iconPath)
        {
            ShellLinkInfo s = new ShellLinkInfo();
            s.Target = Application.ExecutablePath;
            s.Arguments = "--group \"" + g.Id + "\"";
            s.WorkingDirectory = GroupStore.AppDir;
            s.IconPath = iconPath;
            s.IconIndex = 0;
            s.Description = "몽작업표시줄: " + g.Name;
            s.AppId = GroupAppIdPrefix + g.Id;
            return s;
        }

        private static bool Same(string a, string b)
        {
            return string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private static bool NeedsUpdate(ShellLinkInfo cur, ShellLinkInfo want)
        {
            return !Same(cur.Target, want.Target) || !Same(cur.Arguments, want.Arguments)
                || !Same(cur.IconPath, want.IconPath) || cur.IconIndex != want.IconIndex
                || !Same(cur.AppId, want.AppId) || !Same(cur.WorkingDirectory, want.WorkingDirectory)
                || !Same(cur.Description, want.Description);
        }

        public static string SafeFileName(string name)
        {
            if (name == null) return "";
            string r = name;
            foreach (char c in Path.GetInvalidFileNameChars()) r = r.Replace(c, '_');
            return r.Trim();
        }

        /// <summary>바로가기 폴더에 그룹 바로가기를 만들거나 갱신하고 경로를 돌려준다.</summary>
        public static string CreateGroupShortcut(Config cfg, Group g)
        {
            string folder = GroupStore.ResolveShortcutFolder(cfg);
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            string safe = SafeFileName(g.Name);
            if (safe.Length == 0) safe = "그룹";
            string lnk = Path.Combine(folder, safe + ".lnk");
            ShellLink.Write(lnk, GroupLinkSpec(g, GroupIcon.EnsureGroupIcon(g)));
            SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, lnk, IntPtr.Zero);
            return lnk;
        }

        public static void SyncGroup(Config cfg, Group g)
        {
            List<LinkFile> links = ScanOurLinks(cfg);
            bool changed = EnsureLink(cfg, g, links);
            if (SyncGroupCore(cfg, g, links)) changed = true;
            if (changed) SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>그룹 바로가기가 어디에도 없으면 바로가기 폴더에 새로 만든다.</summary>
        private static bool EnsureLink(Config cfg, Group g, List<LinkFile> links)
        {
            foreach (LinkFile lf in links)
                if (lf.GroupId == g.Id) return false;
            string lnk = CreateGroupShortcut(cfg, g);
            LinkFile n = new LinkFile();
            n.Path = lnk;
            n.Info = ShellLink.Read(lnk);
            n.GroupId = g.Id;
            links.Add(n);
            return true;
        }

        private static bool SyncGroupCore(Config cfg, Group g, List<LinkFile> links)
        {
            string icon = GroupIcon.EnsureGroupIcon(g);
            ShellLinkInfo want = GroupLinkSpec(g, icon);
            string shortcutFolder = GroupStore.ResolveShortcutFolder(cfg).TrimEnd('\\');
            bool changed = false;

            foreach (LinkFile lf in links)
            {
                if (lf.GroupId != g.Id) continue;
                string path = lf.Path;

                // 바로가기 폴더 안의 파일은 그룹 이름이 바뀌면 파일 이름도 맞춰 준다
                // (작업표시줄에 고정된 사본은 이름을 바꾸면 고정이 풀리므로 건드리지 않음)
                if (Same(Path.GetDirectoryName(path).TrimEnd('\\'), shortcutFolder))
                {
                    string safe = SafeFileName(g.Name);
                    if (safe.Length > 0)
                    {
                        string wantPath = Path.Combine(shortcutFolder, safe + ".lnk");
                        if (!Same(wantPath, path) && !File.Exists(wantPath))
                        {
                            try { File.Move(path, wantPath); path = wantPath; changed = true; } catch { }
                        }
                    }
                }

                if (NeedsUpdate(lf.Info, want))
                {
                    try
                    {
                        ShellLink.Write(path, want);
                        SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, path, IntPtr.Zero);
                        changed = true;
                    }
                    catch { }
                }
            }
            return changed;
        }

        /// <summary>그룹을 삭제할 때: 작업표시줄 고정을 풀고, 바로가기 폴더의 바로가기 파일을 지운다.
        /// 고정 해제가 안 되는 환경이면 false (사용자가 직접 제거해야 함).</summary>
        public static bool RemoveGroup(Config cfg, string groupId)
        {
            bool unpinned = true;
            foreach (KeyValuePair<string, string> kv in PinnedGroups())
            {
                if (kv.Value != groupId) continue;
                string copy = Path.Combine(PinnedFolder, kv.Key + ".lnk");
                if (!IconHelper.TryUnpinFromTaskbar(copy)) unpinned = false;
            }
            string folder = GroupStore.ResolveShortcutFolder(cfg).TrimEnd('\\');
            foreach (LinkFile lf in ScanOurLinks(cfg))
            {
                if (lf.GroupId != groupId) continue;
                if (!Same(Path.GetDirectoryName(lf.Path).TrimEnd('\\'), folder)) continue;
                try { File.Delete(lf.Path); } catch { }
            }
            InvalidatePinned();
            return unpinned;
        }

        /// <summary>모든 그룹 바로가기 갱신 + 관리 화면 바로가기 경로 수정 + 시작 프로그램 등록.</summary>
        public static void SyncAll(Config cfg)
        {
            List<LinkFile> links = ScanOurLinks(cfg);
            bool changed = false;
            foreach (Group g in cfg.Groups)
            {
                try { if (EnsureLink(cfg, g, links)) changed = true; } catch { }
                try { if (SyncGroupCore(cfg, g, links)) changed = true; } catch { }
            }

            // 그룹이 아닌 우리 바로가기(관리 화면 등): 실행 파일 위치만 맞춰 준다
            foreach (LinkFile lf in links)
            {
                if (lf.GroupId != null) continue;
                // 도우미 실행용(--agent) 등 인수가 붙은 바로가기는 관리 화면이 아니므로 앱 ID 를 넣지 않는다
                bool manager = string.IsNullOrEmpty((lf.Info.Arguments ?? "").Trim());
                if (Same(lf.Info.Target, Application.ExecutablePath) && (!manager || Same(lf.Info.AppId, ManagerAppId))) continue;
                try
                {
                    ShellLinkInfo w = lf.Info;
                    if (manager) w.AppId = ManagerAppId;
                    w.Target = Application.ExecutablePath;
                    w.WorkingDirectory = GroupStore.AppDir;
                    if (string.IsNullOrEmpty(w.IconPath) || !File.Exists(Environment.ExpandEnvironmentVariables(w.IconPath)))
                    {
                        w.IconPath = Application.ExecutablePath;
                        w.IconIndex = 0;
                    }
                    ShellLink.Write(lf.Path, w);
                    SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, lf.Path, IntPtr.Zero);
                    changed = true;
                }
                catch { }
            }

            try { UpdateStartup(cfg.AutoStartAgent); } catch { }
            GroupIcon.CleanupOrphans(cfg);
            if (changed) SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }

        public static void UpdateStartup(bool enabled)
        {
            try
            {
                string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), LegacyStartupLnkName);
                if (File.Exists(legacy)) File.Delete(legacy);
            }
            catch { }
            string p = StartupLnkPath;
            if (!enabled)
            {
                if (File.Exists(p)) File.Delete(p);
                return;
            }
            ShellLinkInfo want = new ShellLinkInfo();
            want.Target = Application.ExecutablePath;
            want.Arguments = "--agent";
            want.WorkingDirectory = GroupStore.AppDir;
            want.IconPath = Application.ExecutablePath;
            want.Description = "몽작업표시줄 도우미 (드래그 앤 드롭·자동 정렬)";
            if (File.Exists(p))
            {
                try
                {
                    ShellLinkInfo cur = ShellLink.Read(p);
                    if (Same(cur.Target, want.Target) && Same(cur.Arguments, want.Arguments)) return;
                }
                catch { }
            }
            ShellLink.Write(p, want);
        }

        public static bool IsGroupPinned(string groupId)
        {
            foreach (KeyValuePair<string, string> kv in PinnedGroups())
                if (kv.Value == groupId) return true;
            return false;
        }

        /// <summary>작업표시줄에 고정된 그룹 바로가기: (버튼에 보이는 이름, 그룹 ID).</summary>
        private static List<KeyValuePair<string, string>> _pinnedCache;

        /// <summary>도우미처럼 고정 폴더 변경을 감시하는 프로세스는 true — 바뀌기 전까지 기록된 목록을 그대로 쓴다.</summary>
        public static bool CachePinned;

        /// <summary>고정 폴더가 바뀌었을 때 호출 (다음에 다시 읽음).</summary>
        public static void InvalidatePinned()
        {
            lock (typeof(ShortcutSync)) { _pinnedCache = null; }
        }

        public static List<KeyValuePair<string, string>> PinnedGroups()
        {
            lock (typeof(ShortcutSync))
            {
                if (CachePinned && _pinnedCache != null)
                    return new List<KeyValuePair<string, string>>(_pinnedCache);
            }
            List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>();
            string dir = PinnedFolder;
            if (!Directory.Exists(dir)) return list;
            string[] files;
            try { files = Directory.GetFiles(dir, "*.lnk"); }
            catch { return list; }
            foreach (string f in files)
            {
                // 바로가기 내용은 기록해 둔 것을 쓴다 (새 파일만 실제로 읽음)
                LinkRec info = Knowledge.Link(f);
                if (!info.Ok || !IsOurExe(info.Target)) continue;
                string id = GroupIdFromArgs(info.Args);
                if (id == null) continue;
                list.Add(new KeyValuePair<string, string>(Path.GetFileNameWithoutExtension(f), id));
            }
            lock (typeof(ShortcutSync)) { if (CachePinned) _pinnedCache = list; }
            return new List<KeyValuePair<string, string>>(list);
        }

        /// <summary>탐색기가 다시 시작됐을 때: 고정된 그룹 바로가기의 아이콘을 다시 읽으라고 알린다.</summary>
        public static void NotifyPinnedIcons()
        {
            try
            {
                foreach (string f in Directory.GetFiles(PinnedFolder, "*.lnk"))
                    SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, f, IntPtr.Zero);
                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        /// <summary>"아이콘·정보 다시 읽기": 기록·아이콘 저장본을 비우고, 그룹 아이콘을 새 파일로 다시 만들어
        /// 작업표시줄이 새로 읽게 한다. (cfg 는 저장까지 한다)</summary>
        public static void RefreshEverything(Config cfg)
        {
            cfg.IconRevision++;
            GroupStore.Save(cfg);
            Knowledge.Reset();
            try
            {
                string cache = Path.Combine(GroupStore.IconsDir, "cache");
                if (Directory.Exists(cache))
                    foreach (string f in Directory.GetFiles(cache)) { try { File.Delete(f); } catch { } }
            }
            catch { }
            SyncAll(cfg);
            NotifyPinnedIcons();
            try { TaskbarUia.CleanupOrphanShadows(); } catch { }
            // Windows 셸 아이콘 캐시도 새로 고침
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ie4uinit.exe"), "-show");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                System.Diagnostics.Process.Start(psi);
            }
            catch { }
        }
    }
}
