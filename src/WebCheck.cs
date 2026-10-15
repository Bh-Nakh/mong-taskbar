// 몽 시리즈 공용: 웹페이지가 "이 앱이 설치·실행 중인가?"를 확인할 수 있게 해 주는 작은 로컬 서버.
//  - 127.0.0.1 에서만 듣는다 (외부 접속 불가). 관리자 권한 불필요.
//  - GET /ping -> {"app":"mongzip","name":"몽ZIP","version":"1.0.0"}
//  - 허용 Origin: 로컬(localhost·127.0.0.1) 페이지 + 실행 파일 옆 webcheck_origins.txt 에 적은 주소(한 줄에 하나)
//  - 웹에서 앱을 깨울 수 있게 사용자 영역(HKCU)에 <id>:// 프로토콜을 등록한다.
// 원본은 브랜딩\앱연동\WebCheck.cs. 각 앱 src 폴더에 같은 파일을 복사해 쓴다 (네임스페이스 MongWebCheck).
// 환경변수(시험용): MONG_WEBCHECK_PORTS=포트,포트  MONG_WEBCHECK_NOREG=1 (프로토콜 등록 안 함)
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace MongWebCheck
{
    public static class WebCheck
    {
        static TcpListener listener;
        static string appId, appName, appVersion;
        // 설치 확인 페이지가 올라간 공식 주소 (이 주소의 페이지만 허용). 정확히 일치해야 한다.
        static List<string> extraOrigins = new List<string> { "https://mongtools.pages.dev" };

        /// <summary>메인 창을 띄우기 직전에 한 번 호출한다. 실패해도 앱 동작에는 영향이 없다.</summary>
        public static void Start(string id, string name, int[] ports, string version = null)
        {
            try
            {
                if (listener != null) return;
                appId = id; appName = name;
                appVersion = version ?? GetVersion();
                LoadOrigins();

                string envPorts = Environment.GetEnvironmentVariable("MONG_WEBCHECK_PORTS");
                if (!string.IsNullOrEmpty(envPorts))
                {
                    List<int> l = new List<int>();
                    foreach (string p in envPorts.Split(','))
                    { int n; if (int.TryParse(p.Trim(), out n)) l.Add(n); }
                    ports = l.ToArray();
                }

                if (Environment.GetEnvironmentVariable("MONG_WEBCHECK_NOREG") != "1")
                    try { RegisterProtocol(); } catch { }

                foreach (int port in ports)
                {
                    try
                    {
                        TcpListener t = new TcpListener(IPAddress.Loopback, port);
                        t.ExclusiveAddressUse = true;   // 다른 프로그램이 같은 포트를 가로채지 못하게
                        t.Start();
                        listener = t;
                        break;
                    }
                    catch { }
                }
                if (listener == null) return;
                Thread th = new Thread(AcceptLoop);
                th.IsBackground = true;
                th.Name = "MongWebCheck";
                th.Start();
            }
            catch { }
        }

        static string GetVersion()
        {
            try
            {
                Assembly a = Assembly.GetEntryAssembly();
                Version v = a.GetName().Version;
                if (v != null && v.ToString() != "0.0.0.0") return v.ToString();
            }
            catch { }
            return "";
        }

        // 실행 중인 exe 경로. 단일 파일 배포(.NET)에서는 Assembly.Location 이 비어 있어 프로세스 정보를 쓴다.
        static string ExePath()
        {
            return System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
        }

        static void LoadOrigins()
        {
            try
            {
                string f = Path.Combine(Path.GetDirectoryName(ExePath()), "webcheck_origins.txt");
                if (!File.Exists(f)) return;
                foreach (string line in File.ReadAllLines(f, Encoding.UTF8))
                {
                    string s = line.Trim().TrimEnd('/');
                    if (s.Length > 0 && !s.StartsWith("#")) extraOrigins.Add(s.ToLowerInvariant());
                }
            }
            catch { }
        }

        static void RegisterProtocol()
        {
            string exe = ExePath();
            if (string.Equals(Path.GetFileName(exe), "dotnet.exe", StringComparison.OrdinalIgnoreCase)) return;   // dotnet run 개발 실행은 등록 안 함
            string low = exe.ToLowerInvariant();
            if (low.Contains(@"\test\") || low.Contains(@"\bin_test\") || low.Contains(@"\bin_dev\")) return;   // 시험 빌드는 등록 안 함
            string cmd = "\"" + exe + "\"";
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + appId))
            {
                string title = "URL:" + appName;
                if (!string.Equals(k.GetValue("") as string, title, StringComparison.Ordinal)) k.SetValue("", title);
                if (k.GetValue("URL Protocol") == null) k.SetValue("URL Protocol", "");
                using (RegistryKey c = k.CreateSubKey(@"shell\open\command"))
                    if (!string.Equals(c.GetValue("") as string, cmd, StringComparison.OrdinalIgnoreCase)) c.SetValue("", cmd);
            }
        }

        static bool OriginAllowed(string origin)
        {
            if (string.IsNullOrEmpty(origin)) return true;      // 브라우저가 아닌 직접 호출
            string o = origin.Trim().TrimEnd('/').ToLowerInvariant();
            if (extraOrigins.Contains(o)) return true;
            Uri u;
            if (!Uri.TryCreate(o, UriKind.Absolute, out u)) return false;
            return u.Scheme == "http" && (u.Host == "localhost" || u.Host == "127.0.0.1" || u.Host == "[::1]");
        }

        static void AcceptLoop()
        {
            while (true)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); }
                catch { return; }
                ThreadPool.QueueUserWorkItem(delegate { Handle(c); });
            }
        }

        static string Json(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        static void Handle(TcpClient c)
        {
            try
            {
                using (c)
                {
                    c.ReceiveTimeout = 2000;
                    c.SendTimeout = 2000;
                    NetworkStream ns = c.GetStream();
                    byte[] buf = new byte[8192];
                    int len = 0;
                    while (len < buf.Length)
                    {
                        int n = ns.Read(buf, len, buf.Length - len);
                        if (n <= 0) break;
                        len += n;
                        if (Encoding.ASCII.GetString(buf, 0, len).Contains("\r\n\r\n")) break;
                    }
                    string req = Encoding.ASCII.GetString(buf, 0, len);
                    string[] lines = req.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                    string[] first = lines[0].Split(' ');
                    string method = first.Length > 0 ? first[0] : "";
                    string path = first.Length > 1 ? first[1] : "";
                    string origin = null;
                    bool pna = false;
                    for (int i = 1; i < lines.Length; i++)
                    {
                        int ix = lines[i].IndexOf(':');
                        if (ix <= 0) continue;
                        string h = lines[i].Substring(0, ix).Trim().ToLowerInvariant();
                        string v = lines[i].Substring(ix + 1).Trim();
                        if (h == "origin") origin = v;
                        else if (h == "access-control-request-private-network") pna = true;
                    }

                    int q = path.IndexOf('?');
                    if (q >= 0) path = path.Substring(0, q);

                    int status = 200;
                    string body = "";
                    if (!OriginAllowed(origin)) { status = 403; body = "{\"error\":\"origin\"}"; }
                    else if (method == "OPTIONS") { status = 204; }
                    else if (method == "GET" && path == "/ping")
                        body = "{\"app\":\"" + Json(appId) + "\",\"name\":\"" + Json(appName) + "\",\"version\":\"" + Json(appVersion) + "\"}";
                    else { status = 404; body = "{\"error\":\"not found\"}"; }

                    byte[] bb = Encoding.UTF8.GetBytes(body);
                    StringBuilder sb = new StringBuilder();
                    sb.Append("HTTP/1.1 ").Append(status).Append(status == 200 ? " OK" : status == 204 ? " No Content" : status == 403 ? " Forbidden" : " Not Found").Append("\r\n");
                    if (status != 403 && !string.IsNullOrEmpty(origin))
                    {
                        sb.Append("Access-Control-Allow-Origin: ").Append(origin).Append("\r\n");
                        sb.Append("Vary: Origin\r\n");
                        sb.Append("Access-Control-Allow-Methods: GET, OPTIONS\r\n");
                        sb.Append("Access-Control-Allow-Headers: *\r\n");
                        if (pna) sb.Append("Access-Control-Allow-Private-Network: true\r\n");
                    }
                    sb.Append("Cache-Control: no-store\r\n");
                    sb.Append("Content-Type: application/json; charset=utf-8\r\n");
                    sb.Append("Content-Length: ").Append(bb.Length).Append("\r\n");
                    sb.Append("Connection: close\r\n\r\n");
                    byte[] hb = Encoding.ASCII.GetBytes(sb.ToString());
                    ns.Write(hb, 0, hb.Length);
                    if (bb.Length > 0) ns.Write(bb, 0, bb.Length);
                }
            }
            catch { }
        }
    }
}
