// 몽 시리즈 공용 자동 업데이트 모듈 — 핵심(화면 없음). 원본은 브랜딩\앱연동\MongUpdate.cs, 각 앱 src 폴더에 복사해 쓴다.
//
// 동작: 공개 배포 저장소(Bh-Nakh/mong-downloads)의 Release 에서 <앱ID>-latest.json 을 읽어 새 버전이 있는지 확인한다.
//       토큰이 필요 없다(공개 주소). 받은 파일은 SHA-256 으로 확인한 뒤에만 적용한다.
// latest.json 예:  {"version":"1.10","kind":"exe","file":"MongExplorer.exe","sha256":"...","note":"변경 내용"}
//   kind = "exe"   : 받은 파일이 앱 본체. 실행 중인 exe 를 .old 로 바꾸고 새 파일을 그 자리에 둔 뒤 다시 시작한다.
//   kind = "setup" : 받은 파일이 설치 프로그램(Inno Setup). 조용히 실행(/SILENT)해 설치한 뒤 앱을 다시 시작한다.
// 화면(안내창·진행 창)은 MongUpdateUi.WinForms.cs / MongUpdateUi.Wpf.cs 를 쓴다. C# 5 문법만 사용(.NET Framework 4 컴파일러 호환).
// 시험용 환경변수: MONG_UPDATE_BASE=http://127.0.0.1:포트/  (기본 주소 대신 쓴다)
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MongUpdate
{
    public sealed class UpdateInfo
    {
        public string AppId;
        public string Version;
        public string Kind;      // "exe" | "setup"
        public string File;      // 받을 파일 이름 (공개 Release 안)
        public string Sha256;
        public string Note;
        public string Url;
        public long Size;
    }

    public static class UpdateService
    {
        public const string DefaultBase = "https://github.com/Bh-Nakh/mong-taskbar/releases/latest/download/";

        /// <summary>마지막 확인·받기 오류 문구 (성공하면 null)</summary>
        public static string LastError { get; private set; }

        static UpdateService()
        {
            try { ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol | (SecurityProtocolType)3072; } catch { }   // TLS 1.2
        }

        static string BaseUrl()
        {
            string e = Environment.GetEnvironmentVariable("MONG_UPDATE_BASE");
            if (string.IsNullOrEmpty(e)) return DefaultBase;
            return e.EndsWith("/") ? e : e + "/";
        }

        /// <summary>지금 실행 중인 exe 경로 (단일 파일 배포에서도 동작)</summary>
        public static string CurrentExe()
        {
            return Process.GetCurrentProcess().MainModule.FileName;
        }

        /// <summary>이전 업데이트가 남긴 .old 파일 정리. 앱 시작 때 한 번 호출한다.</summary>
        public static void CleanupOld()
        {
            try
            {
                string old = CurrentExe() + ".old";
                if (File.Exists(old)) File.Delete(old);
            }
            catch { }
        }

        /// <summary>
        /// 새 버전을 확인한다. 확인에 성공하면 true (새 버전이 없으면 info 는 null), 인터넷·형식 오류면 false (LastError 에 이유).
        /// 화면을 막지 않도록 UI 스레드가 아닌 곳에서 부르거나 UpdateUi 의 도우미를 쓴다.
        /// </summary>
        public static bool TryCheck(string appId, string currentVersion, out UpdateInfo info)
        {
            info = null;
            LastError = null;
            try
            {
                string json = GetText(BaseUrl() + appId + "-latest.json");
                string ver = Str(json, "version");
                string file = Str(json, "file");
                if (string.IsNullOrEmpty(ver) || string.IsNullOrEmpty(file))
                {
                    LastError = "업데이트 정보 형식이 올바르지 않습니다.";
                    return false;
                }
                if (Compare(ver, currentVersion) <= 0) return true;   // 이미 최신

                UpdateInfo i = new UpdateInfo();
                i.AppId = appId;
                i.Version = ver;
                i.File = Path.GetFileName(file);                     // 경로 문자는 허용하지 않는다
                i.Kind = (Str(json, "kind") ?? "exe").ToLowerInvariant();
                i.Sha256 = (Str(json, "sha256") ?? "").ToLowerInvariant();
                i.Note = Str(json, "note") ?? "";
                i.Url = BaseUrl() + i.File;
                long size;
                if (long.TryParse(Num(json, "size"), out size)) i.Size = size;
                if (i.Sha256.Length != 64) { LastError = "업데이트 정보에 올바른 해시가 없습니다."; return false; }
                if (i.Kind != "exe" && i.Kind != "setup") { LastError = "알 수 없는 업데이트 종류입니다: " + i.Kind; return false; }
                info = i;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        // 받을 때 쓰는 이름표(User-Agent). 일부 보안 프로그램·프록시는 낯선 프로그램의 exe 받기를 다른 내용으로 바꾸거나 막으므로,
        // 해시가 틀리면 이름표를 바꿔 다시 받는다 (일반 프로그램처럼 보이는 것 → 파워셸 → 브라우저 순).
        static readonly string[] UserAgents =
        {
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) MongUpdate/1.1",
            "Mozilla/5.0 (Windows NT; Windows NT 10.0; ko-KR) WindowsPowerShell/5.1.19041.1",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
        };

        /// <summary>
        /// 새 파일을 임시 폴더에 받고 SHA-256 을 확인한다. 성공하면 받은 파일 경로, 실패하면 예외.
        /// 해시가 틀리거나 받다가 끊기면 이름표를 바꿔 최대 3번 다시 받고, 끝내 실패하면 받은 크기·해시·응답 주소·앞부분을 오류 문구와
        /// 임시 폴더의 mong-update\last_error.txt 에 남긴다 (원인 파악용).
        /// </summary>
        public static string Download(UpdateInfo info, Action<int> progress)
        {
            string dir = Path.Combine(Path.GetTempPath(), "mong-update");
            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, info.File);
            StringBuilder log = new StringBuilder();
            string problem = null;
            for (int attempt = 0; attempt < UserAgents.Length; attempt++)
            {
                try { if (File.Exists(dest)) File.Delete(dest); } catch { }
                problem = TryDownload(info, dest, attempt, progress);
                if (problem == null) return dest;
                log.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  시도 " + (attempt + 1) + ": " + problem);
                try { if (File.Exists(dest)) File.Delete(dest); } catch { }
                if (attempt < UserAgents.Length - 1) System.Threading.Thread.Sleep(1500);
            }
            try { File.WriteAllText(Path.Combine(dir, "last_error.txt"), "받을 주소: " + info.Url + "\r\n예상 해시: " + info.Sha256 + "\r\n예상 크기: " + info.Size + "\r\n\r\n" + log); } catch { }
            throw new InvalidDataException("받은 파일의 해시가 맞지 않아 업데이트를 취소했습니다. (3번 시도) " + problem);
        }

        /// <summary>한 번 받아 보고, 문제가 없으면 null, 있으면 문제 설명을 돌려준다</summary>
        static string TryDownload(UpdateInfo info, string dest, int attempt, Action<int> progress)
        {
            // 주소 뒤에 매번 다른 값을 붙여 중간 캐시가 옛 파일을 주지 않게 한다 (GitHub 는 무시함)
            string url = info.Url + (info.Url.IndexOf('?') >= 0 ? "&" : "?") + "mong=" + Guid.NewGuid().ToString("N");
            string via = "";
            long done = 0;
            byte[] head = new byte[64];
            int headLen = 0;
            try
            {
                HttpWebRequest req = Create(url, 30000);
                req.UserAgent = UserAgents[attempt];
                req.Accept = "application/octet-stream, */*";
                req.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.BypassCache);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream src = resp.GetResponseStream())
                using (FileStream dst = new FileStream(dest, FileMode.Create, FileAccess.Write))
                using (SHA256 sha = SHA256.Create())
                {
                    via = "응답 " + (int)resp.StatusCode + " " + resp.ResponseUri.Host + " " + resp.ContentType + " " + resp.ContentLength + "바이트";
                    long total = resp.ContentLength > 0 ? resp.ContentLength : info.Size;
                    int last = -1;
                    byte[] buf = new byte[81920];
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        dst.Write(buf, 0, n);
                        sha.TransformBlock(buf, 0, n, null, 0);
                        if (headLen < head.Length) { int c = Math.Min(n, head.Length - headLen); Array.Copy(buf, 0, head, headLen, c); headLen += c; }
                        done += n;
                        if (progress != null && total > 0)
                        {
                            int pct = (int)(done * 100 / total);
                            if (pct != last) { last = pct; progress(pct > 100 ? 100 : pct); }
                        }
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    string got = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                    dst.Flush();
                    if (got == info.Sha256) return null;
                    // 앞부분이 실행 파일(MZ)이 아니고 글자면 가로챈 웹페이지일 수 있어 보여 준다
                    string peek = "";
                    if (!(headLen >= 2 && head[0] == 0x4D && head[1] == 0x5A))
                    {
                        StringBuilder sb = new StringBuilder();
                        for (int k = 0; k < headLen; k++) sb.Append(head[k] >= 32 && head[k] < 127 ? (char)head[k] : '.');
                        peek = ", 앞부분=\"" + sb + "\"";
                    }
                    return "받은 크기 " + done + "바이트(예상 " + info.Size + "), 해시 " + got.Substring(0, 12) + "…(예상 " + info.Sha256.Substring(0, 12) + "…), " + via + peek;
                }
            }
            catch (Exception ex)
            {
                return "받는 중 오류: " + ex.Message + " (받은 " + done + "바이트" + (via.Length > 0 ? ", " + via : "") + ")";
            }
        }

        /// <summary>
        /// 받은 파일을 적용하고 앱을 다시 시작하도록 예약한다. 호출한 쪽은 바로 앱을 종료해야 한다(Application.Exit 등).
        /// restartArgs: 다시 시작할 때 붙일 인자(없으면 null).
        /// </summary>
        public static void Apply(UpdateInfo info, string downloadedFile, string restartArgs)
        {
            string exe = CurrentExe();
            string args = string.IsNullOrEmpty(restartArgs) ? "" : " " + restartArgs;
            if (info.Kind == "setup")
            {
                // 이 앱이 끝날 시간을 잠깐 준 뒤, 설치 프로그램을 지금 설치된 폴더로 조용히 실행(남은 앱은 설치 프로그램이 닫는다) → 끝나면 앱 시작
                string dir = Path.GetDirectoryName(exe);
                RunDetached("ping -n 3 127.0.0.1 >nul & \"" + downloadedFile + "\" /SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /DIR=\"" + dir + "\" & start \"\" \"" + exe + "\"" + args);
                return;
            }

            // exe 교체: 실행 중인 exe 는 지울 수 없지만 이름은 바꿀 수 있다.
            string old = exe + ".old";
            try { if (File.Exists(old)) File.Delete(old); } catch { }
            File.Move(exe, old);
            try
            {
                File.Copy(downloadedFile, exe, true);
            }
            catch
            {
                try { File.Move(old, exe); } catch { }       // 실패하면 원래대로
                throw;
            }
            // 이 프로세스가 끝난 뒤 새 exe 를 시작 (잠깐 기다림)
            RunDetached("ping -n 3 127.0.0.1 >nul & start \"\" \"" + exe + "\"" + args);
        }

        // ── 내부 도우미 ─────────────────────────────────────────────

        static void RunDetached(string cmdLine)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.Arguments = "/c \"" + cmdLine + "\"";
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process.Start(psi);
        }

        static HttpWebRequest Create(string url, int timeoutMs)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "MongUpdate/1.0";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.AllowAutoRedirect = true;
            return req;
        }

        static string GetText(string url)
        {
            HttpWebRequest req = Create(url, 10000);
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        // latest.json 은 평평한 구조라 간단한 검색으로 읽는다 (JSON 라이브러리 참조가 필요 없게)
        static string Str(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return null;
            string v = m.Groups[1].Value;
            v = Regex.Replace(v, "\\\\u([0-9a-fA-F]{4})", delegate (Match x) { return ((char)Convert.ToInt32(x.Groups[1].Value, 16)).ToString(); });
            return v.Replace("\\n", "\n").Replace("\\r", "").Replace("\\t", "\t").Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\/", "/");
        }

        static string Num(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(\\d+)");
            return m.Success ? m.Groups[1].Value : "";
        }

        /// <summary>버전 비교 (1.10 > 1.9, 2.0.7 > 2.0.6). a 가 크면 1, 같으면 0, 작으면 -1</summary>
        public static int Compare(string a, string b)
        {
            int[] x = Parts(a), y = Parts(b);
            int n = Math.Max(x.Length, y.Length);
            for (int i = 0; i < n; i++)
            {
                int p = i < x.Length ? x[i] : 0, q = i < y.Length ? y[i] : 0;
                if (p != q) return p < q ? -1 : 1;
            }
            return 0;
        }

        static int[] Parts(string s)
        {
            if (string.IsNullOrEmpty(s)) return new int[0];
            string[] t = s.Trim().TrimStart('v', 'V').Split('.');
            int[] r = new int[t.Length];
            for (int i = 0; i < t.Length; i++)
            {
                int n;
                int.TryParse(Regex.Match(t[i], "^\\d+").Value, out n);
                r[i] = n;
            }
            return r;
        }
    }
}
