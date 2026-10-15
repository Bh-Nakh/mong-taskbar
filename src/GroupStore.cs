using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TaskbarGroup
{
    public static class GroupStore
    {
        /// <summary>실행 파일이 있는 폴더. 설정·아이콘·바로가기가 모두 이 아래에 있어서
        /// 폴더째 복사하면 다른 PC 에서도 그대로 쓸 수 있다.</summary>
        public static string AppDir
        {
            get { return Path.GetDirectoryName(Application.ExecutablePath); }
        }

        public static string ConfigPath
        {
            get { return Path.Combine(AppDir, "groups.json"); }
        }

        /// <summary>예전 버전이 쓰던 설정 위치 (처음 실행 시 한 번 옮겨 온다).</summary>
        private static string LegacyConfigPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "TaskbarGroup", "groups.json");
            }
        }

        public static string IconsDir
        {
            get
            {
                string d = Path.Combine(AppDir, "icons");
                if (!Directory.Exists(d)) Directory.CreateDirectory(d);
                return d;
            }
        }

        /// <summary>바로가기 폴더가 지정되지 않았을 때 쓰는 기본값.</summary>
        public static string DefaultShortcutFolder
        {
            get { return Path.Combine(AppDir, "바로가기"); }
        }

        /// <summary>설정된 바로가기 폴더. 비어 있거나 없어진 폴더면 기본값으로 대체.</summary>
        public static string ResolveShortcutFolder(Config c)
        {
            if (c != null && !string.IsNullOrEmpty(c.ShortcutFolder))
            {
                string full = Path.IsPathRooted(c.ShortcutFolder)
                    ? c.ShortcutFolder
                    : Path.GetFullPath(Path.Combine(AppDir, c.ShortcutFolder));
                if (Directory.Exists(full)) return full;
            }
            return DefaultShortcutFolder;
        }

        /// <summary>실행 파일 폴더 아래 경로면 상대 경로로 바꿔서 저장 (복사 이동 대비).</summary>
        public static string ToStoredFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return "";
            string app = AppDir.TrimEnd('\\') + "\\";
            string full = Path.GetFullPath(folder);
            if (full.StartsWith(app, StringComparison.OrdinalIgnoreCase))
                return full.Substring(app.Length);
            if (string.Equals(full.TrimEnd('\\'), AppDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return ".";
            return full;
        }

        /// <summary>이 프로세스가 마지막으로 읽거나 쓴 설정 내용 (외부 변경 감지용).</summary>
        public static string LastJson { get; private set; }

        public static Config Load()
        {
            return Load(true);
        }

        /// <param name="track">false 면 LastJson(외부 변경 감지 기준)을 건드리지 않는다 — 읽기만 할 때.</param>
        public static Config Load(bool track)
        {
            try
            {
                if (!File.Exists(ConfigPath) && File.Exists(LegacyConfigPath))
                    File.Copy(LegacyConfigPath, ConfigPath);
            }
            catch { }

            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath, System.Text.Encoding.UTF8);
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    ser.MaxJsonLength = int.MaxValue;
                    Config c = ser.Deserialize<Config>(json);
                    if (c != null)
                    {
                        if (track) LastJson = json;
                        if (c.Groups == null) c.Groups = new List<Group>();
                        if (c.ShortcutFolder == null) c.ShortcutFolder = "";
                        foreach (Group g in c.Groups)
                        {
                            if (g.Entries == null) g.Entries = new List<Entry>();
                            if (g.Id == null || g.Id.Length == 0) g.Id = Guid.NewGuid().ToString("N");
                            if (g.Name == null) g.Name = "";
                            if (g.IconLocation == null) g.IconLocation = "";
                        }
                        return c;
                    }
                }
            }
            catch { }
            return new Config();
        }

        public static void Save(Config c)
        {
            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            string json = ser.Serialize(c);
            LastJson = json;
            File.WriteAllText(ConfigPath, json, System.Text.Encoding.UTF8);
        }

        public static Group FindGroup(Config c, string id)
        {
            if (c == null || id == null) return null;
            foreach (Group g in c.Groups)
                if (g.Id == id) return g;
            return null;
        }

        /// <summary>드롭/선택된 경로들을 Entry 로 변환 (폴더는 건너뜀).</summary>
        public static List<Entry> EntriesFromPaths(IEnumerable<string> paths)
        {
            List<Entry> list = new List<Entry>();
            if (paths == null) return list;
            foreach (string f in paths)
            {
                if (string.IsNullOrEmpty(f)) continue;
                if (Directory.Exists(f)) continue;   // 폴더는 실행 대상이 아니므로 제외
                if (!File.Exists(f)) continue;
                Entry en = new Entry();
                en.Target = f;
                en.Name = Path.GetFileNameWithoutExtension(f);
                en.IconLocation = IconHelper.GetIconLocationForPath(f);
                list.Add(en);
            }
            return list;
        }

        /// <summary>설정 파일을 새로 읽어서 그룹에 항목을 끼워 넣고 저장 + 아이콘/바로가기 갱신.
        /// index 가 범위 밖이면 맨 끝. 추가된 게 없으면 null.</summary>
        public static Group InsertEntries(string groupId, IEnumerable<string> paths, int index, out int added)
        {
            added = 0;
            Config cfg = Load();
            Group g = FindGroup(cfg, groupId);
            if (g == null) return null;
            List<Entry> list = EntriesFromPaths(paths);
            if (list.Count == 0) return null;
            if (index < 0 || index > g.Entries.Count) index = g.Entries.Count;
            g.Entries.InsertRange(index, list);
            Save(cfg);
            added = list.Count;
            try { ShortcutSync.SyncGroup(cfg, g); } catch { }
            return g;
        }
    }
}
