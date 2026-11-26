// fullapp.cs — 7D2D 服务器搜索 完整版单 exe（C#/.NET4，无 Python）
// 编译: csc /nologo /optimize /platform:x64 /target:exe /out:7D2D-ServerSearch.exe ^
//   /resource:api64.gz;api64.gz /resource:swnet.gz;swnet.gz /resource:index.bin;index.bin ^
//   /resource:appid.txt;appid.txt /r:com.rlabrecque.steamworks.net.dll fullapp.cs
// 内嵌资源: steam_api64.dll(GZ) steamworks.net(GZ) index.html appid — 运行时解包到 %LOCALAPPDATA%\7D2DServerSearch
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.IO.Compression;

class FullApp
{
    const int PORT = 20002;
    const uint APPID = 251570;
    static string ExeDir = AppDomain.CurrentDomain.BaseDirectory;
    static string Work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "7D2DServerSearch");
    static readonly object CacheGate = new object();
    static readonly Dictionary<string, object[]> Cache = new Dictionary<string, object[]>();

    static void Main(string[] args)
    {
        Directory.CreateDirectory(Work);
        Unpack("api64.gz", "steam_api64.dll", true);
        Unpack("swnet.gz", "com.rlabrecque.steamworks.net.dll", true);
        Unpack("steamlist.gz", "SteamList.exe", true);
        Unpack("appid.txt", "steam_appid.txt", false);
        Environment.CurrentDirectory = Work;   // steam_api64.dll 经 DllImport 从 cwd 命中

        var listener = new TcpListener(IPAddress.Loopback, PORT);
        try { listener.Start(); }
        catch (Exception e)
        {
            Console.WriteLine("启动失败(端口 " + PORT + " 可能被占用): " + e.Message);
            return;
        }
        Console.WriteLine("7D2D 服务器搜索·完整版已启动: http://127.0.0.1:" + PORT);
        Console.WriteLine("全量搜索需要 Steam 客户端处于登录状态；请保持本窗口开启。");
        try { Process.Start(new ProcessStartInfo("rundll32", "url.dll,FileProtocolHandler http://127.0.0.1:" + PORT)); } catch { }
        while (true)
        {
            try
            {
                var cli = listener.AcceptTcpClient();
                var t = new Thread(() => { try { ServeConn(cli); } catch { try { cli.Close(); } catch { } } });
                t.IsBackground = true;
                t.Start();
            }
            catch { }
        }
    }

    static string ReadLine(BinaryReader rd)
    {
        var sb = new StringBuilder();
        int c;
        while ((c = rd.Read()) >= 0)
        {
            if (c == '\n') break;
            if (c != '\r') sb.Append((char)c);
        }
        if (c < 0 && sb.Length == 0) return null;
        return sb.ToString();
    }
    [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
    [System.Security.SecurityCritical]
    static void ServeConn(TcpClient cli)
    {
        cli.ReceiveTimeout = 30000;
        var st = cli.GetStream();
        var rd = new BinaryReader(st);
        string line = ReadLine(rd);
        if (line == null || !line.StartsWith("GET ")) { cli.Close(); return; }
        string target = line.Split(' ')[1];
        while (true) { var h = ReadLine(rd); if (string.IsNullOrEmpty(h)) break; }
        string path = target, rawQ = "";
        int qi = target.IndexOf('?');
        if (qi >= 0) { path = target.Substring(0, qi); rawQ = target.Substring(qi + 1); }
        int status = 200; string ctype = "application/json; charset=utf-8"; byte[] body;
        string extra = "";
        var q = parseQs(rawQ);
        if (path == "/" || path == "/index.html") { body = ReadRes("index.bin"); ctype = "text/html; charset=utf-8"; }
        else if (path == "/assistant")
        {
            try { body = File.ReadAllBytes(Process.GetCurrentProcess().MainModule.FileName); }
            catch { body = ReadRes("index.bin"); }
            ctype = "application/octet-stream";
            extra = "Content-Disposition: attachment; filename=\"7D2D-ServerSearch.exe\"\r\n";
        }
        else if (path == "/api/ping") body = Encoding.UTF8.GetBytes("{\"pong\":true,\"full\":true}");
        else if (path == "/api/master")
        {
            if (q.ContainsKey("refresh") && q["refresh"] == "1") lock (CacheGate) Cache.Remove("master");
            body = Encoding.UTF8.GetBytes(Js(Cached("master", 300, FetchMaster)));
        }
        else if (path == "/api/query")
        {
            string addr = q.ContainsKey("addr") ? q["addr"] : "";
            if (addr == "") { status = 400; body = Encoding.UTF8.GetBytes("{\"error\":\"need addr\"}"); }
            else body = Encoding.UTF8.GetBytes(Js(DeepQuery(addr)));
        }
        else { status = 404; body = Encoding.UTF8.GetBytes("{\"error\":\"not found\"}"); }
        var hdr = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " OK\r\nContent-Type: " + ctype + "\r\n" + extra + "Access-Control-Allow-Origin: *\r\nCache-Control: no-store\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
        st.Write(hdr, 0, hdr.Length);
        st.Write(body, 0, body.Length);
        st.Flush();
        cli.Close();
    }

    static void Unpack(string resName, string outFile, bool gz)
    {
        var dst = Path.Combine(Work, outFile);
        try
        {
            byte[] data = ReadRes(resName);
            if (gz)
            {
                using (var ms = new MemoryStream(data))
                using (var gzs = new GZipStream(ms, CompressionMode.Decompress))
                using (var fs = File.Create(dst)) gzs.CopyTo(fs);
            }
            else File.WriteAllBytes(dst, data);
        }
        catch (IOException)
        {
            // 文件被另一实例/子进程占用：旧副本内容相同，直接用
            if (File.Exists(dst)) return;
            throw;
        }
    }
    static byte[] ReadRes(string name)
    {
        using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        using (var ms = new MemoryStream()) { st.CopyTo(ms); return ms.ToArray(); }
    }

    // ---------------- 路由辅助 ----------------
    static Dictionary<string, string> parseQs(string qs)
    {
        var d = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(qs)) return d;
        foreach (var kv in qs.Split('&'))
        {
            var p = kv.Split(new[] { '=' }, 2);
            if (p.Length == 2) d[Uri.UnescapeDataString(p[0])] = Uri.UnescapeDataString(p[1].Replace('+', ' '));
        }
        return d;
    }
    static object Cached(string key, int ttlSec, Func<object> fn)
    {
        lock (CacheGate)
        {
            object[] hit;
            if (Cache.TryGetValue(key, out hit) && (DateTime.Now - (DateTime)hit[0]).TotalSeconds < ttlSec) return hit[1];
        }
        object val = fn();
        lock (CacheGate) Cache[key] = new object[] { DateTime.Now, val };
        return val;
    }

    // ---------------- JSON ----------------
    static string Js(object v)
    {
        var sb = new StringBuilder();
        Wr(sb, v);
        return sb.ToString();
    }
    static void Wr(StringBuilder sb, object v)
    {
        if (v == null) { sb.Append("null"); return; }
        if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
        if (v is int || v is long || v is double) { sb.Append(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)); return; }
        if (v is string) { JStr(sb, (string)v); return; }
        var list = v as System.Collections.IEnumerable;
        if (v is Dictionary<string, object>)
        {
            sb.Append('{'); bool f = true;
            foreach (var kv in (Dictionary<string, object>)v) { if (!f) sb.Append(','); f = false; JStr(sb, kv.Key); sb.Append(':'); Wr(sb, kv.Value); }
            sb.Append('}'); return;
        }
        if (list != null)
        {
            sb.Append('['); bool f = true;
            foreach (var it in list) { if (!f) sb.Append(','); f = false; Wr(sb, it); }
            sb.Append(']'); return;
        }
        JStr(sb, v.ToString());
    }
    static void JStr(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        sb.Append('"');
    }

    // ---------------- 基础工具 ----------------
    static readonly Regex IPRE = new Regex(@"^\d{1,3}(\.\d{1,3}){3}$");
    static string[] SplitAddr(string addr)
    {
        var parts = addr.Trim().Split(':');
        string ip = parts[0], port = "26900";
        for (int i = 1; i < parts.Length; i++) if (Regex.IsMatch(parts[i], @"^\d+$")) { port = parts[i]; break; }
        return new[] { ip, port };
    }
    static int PInt(object v, int d) { int x; return v != null && int.TryParse(v.ToString(), out x) ? x : d; }

    // ---------------- A2S ----------------
    static string CStr(byte[] b, ref int p)
    {
        int e = Array.IndexOf(b, (byte)0, p);
        if (e < 0) e = b.Length;
        string s = Encoding.UTF8.GetString(b, p, e - p);
        p = e + 1;
        return s;
    }
    static bool IsM(byte[] d, byte t) { return d.Length >= 5 && d[0] == 0xff && d[1] == 0xff && d[2] == 0xff && d[3] == 0xff && d[4] == t; }
    static Dictionary<string, object> A2sInfo(string ip, int port)
    {
        var u = new UdpClient();
        u.Client.ReceiveTimeout = 3000; u.Client.SendTimeout = 3000;
        try
        {
            var req = new byte[4 + 21];
            req[0] = req[1] = req[2] = req[3] = 0xff;
            byte[] tag = Encoding.ASCII.GetBytes("TSource Engine Query");
            Array.Copy(tag, 0, req, 4, tag.Length);
            req[24] = 0;
            var ep = new IPEndPoint(Dns.GetHostAddresses(ip)[0], port);
            var sw = Stopwatch.StartNew();
            u.Send(req, req.Length, ep);
            byte[] data = u.Receive(ref ep);
            if (IsM(data, (byte)'A') && data.Length >= 9)
            {
                var req2 = new byte[req.Length + 4];
                Array.Copy(req, req2, req.Length);
                Array.Copy(data, 5, req2, req.Length, 4);
                u.Send(req2, req2.Length, ep);
                sw.Restart();
                data = u.Receive(ref ep);
            }
            int rtt = (int)sw.ElapsedMilliseconds;
            if (IsM(data, (byte)'I'))
            {
                int p = 6;
                string name = CStr(data, ref p), map2 = CStr(data, ref p), folder = CStr(data, ref p), game = CStr(data, ref p);
                p += 2;
                int players = data[p], bots = data[p + 1], maxp = data[p + 2], vac = data[p + 6];
                p += 7;
                string ver = "";
                if (p < data.Length) try { ver = CStr(data, ref p); } catch { }
                if (maxp == 0 && bots > 0) maxp = bots;
                return new Dictionary<string, object> {
                    { "ok", true }, { "rtt", rtt }, { "name", name }, { "map", map2 }, { "game", game },
                    { "players", players }, { "max", maxp }, { "version", ver }, { "vac", vac } };
            }
            return new Dictionary<string, object> { { "ok", false } };
        }
        catch { return new Dictionary<string, object> { { "ok", false } }; }
        finally { u.Close(); }
    }
    static Dictionary<string, object> A2sRules(string ip, int port)
    {
        var u = new UdpClient();
        u.Client.ReceiveTimeout = 3000;
        try
        {
            var ep = new IPEndPoint(Dns.GetHostAddresses(ip)[0], port);
            var req = new byte[13];
            req[0] = req[1] = req[2] = req[3] = 0xff; req[4] = (byte)'U';
            u.Send(req, req.Length, ep);
            byte[] data = u.Receive(ref ep);
            if (IsM(data, (byte)'A') && data.Length >= 9)
            {
                var req2 = new byte[17];
                Array.Copy(req, req2, 5); Array.Copy(data, 5, req2, 5, 4); Array.Copy(req, 5, req2, 9, 8);
                u.Send(req2, req2.Length, ep);
                data = u.Receive(ref ep);
            }
            if (IsM(data, (byte)'E')) return ParseRules(data, 5);
            if (data.Length >= 17 && IsM(data, (byte)'D'))
            {
                int total = BitConverter.ToInt32(data, 5), number = BitConverter.ToInt32(data, 9), index = BitConverter.ToInt32(data, 13);
                var parts = new Dictionary<int, byte[]> { { 0, data } };
                for (int i = 0; i < total - 1; i++)
                {
                    byte[] d = u.Receive(ref ep);
                    parts[BitConverter.ToInt32(d, 13)] = d;
                }
                var head = new byte[] { 0xff, 0xff, 0xff, 0xff };
                var body = new List<byte>(head);
                var keys = new List<int>(parts.Keys); keys.Sort();
                foreach (var k in keys) { var dd = parts[k]; for (int i = 17; i < dd.Length; i++) body.Add(dd[i]); }
                return ParseRules(body.ToArray(), 5);
            }
            return new Dictionary<string, object>();
        }
        catch { return new Dictionary<string, object>(); }
        finally { u.Close(); }
    }
    static Dictionary<string, object> ParseRules(byte[] data, int p)
    {
        var outp = new Dictionary<string, object>();
        try
        {
            int count = data[p]; p += 2;
            for (int i = 0; i < count; i++)
            {
                string k = CStr(data, ref p), v = CStr(data, ref p);
                outp[k] = v;
            }
        } catch { }
        return outp;
    }

    // ---------------- TCP 深查（85 字段协议） ----------------
    static Dictionary<string, string> TcpQuery(string ip, int port)
    {
        var cli = new TcpClient();
        try { var ar = cli.BeginConnect(ip, port, null, null); if (!ar.AsyncWaitHandle.WaitOne(5000) || !cli.Connected) { cli.Close(); return null; } cli.EndConnect(ar); }
        catch { return null; }
        try
        {
            cli.ReceiveTimeout = 5000; cli.SendTimeout = 5000;
            var st = cli.GetStream();
            var req = new byte[25];
            req[0] = req[1] = req[2] = req[3] = 0xff;
            byte[] tag = Encoding.ASCII.GetBytes("TSource Engine Query");
            Array.Copy(tag, 0, req, 4, tag.Length);
            var t0 = Stopwatch.StartNew();
            st.Write(req, 0, req.Length);
            var buf = new List<byte>();
            while (buf.Count < 4096)
            {
                int n = st.ReadByte();
                if (n < 0) break;
                buf.Add((byte)n);
                if (n == 10 && buf.Count >= 2 && buf[buf.Count - 2] == 13) break;
            }
            int ei = buf.IndexOf((byte)13);
            int total = 0;
            if (ei > 0)
            {
                string headStr = Encoding.ASCII.GetString(buf.ToArray(), 0, ei);
                int.TryParse(headStr.Trim(), out total);
            }
            var body = new List<byte>();
            for (int k = (ei < 0 ? 0 : ei + 2); k < buf.Count; k++) body.Add(buf[k]);
            var tmp = new byte[65536];
            while ((total > 0 && body.Count < total) || total == 0)
            {
                int n2;
                try { n2 = st.Read(tmp, 0, tmp.Length); } catch { break; }
                if (n2 <= 0) break;
                for (int k = 0; k < n2; k++) body.Add(tmp[k]);
                if (total == 0 && BodyHas(body, "BiomeProgression")) break;
                if (body.Count > 262144) break;
            }
            int rtt = (int)t0.ElapsedMilliseconds;
            string txt = Encoding.UTF8.GetString(body.ToArray());
            if (!txt.Contains("GameType") && !txt.Contains("GameHost")) return null;
            var info = new Dictionary<string, string>();
            foreach (var part in Regex.Split(txt, @"\r?\n"))
            {
                string pp = part.Trim();
                if (pp == "") continue;
                if (pp.EndsWith(";")) pp = pp.Substring(0, pp.Length - 1);
                if (Regex.IsMatch(pp, @"^[A-Za-z][A-Za-z0-9]*:"))
                {
                    int ci = pp.IndexOf(':');
                    info[pp.Substring(0, ci)] = pp.Substring(ci + 1).TrimEnd(';');
                }
            }
            info["_rtt"] = rtt.ToString();
            return info;
        }
        catch { return null; }
        finally { cli.Close(); }
    }
    static bool BodyHas(List<byte> body, string needle)
    {
        var nb = Encoding.UTF8.GetBytes(needle);
        var arr = body.ToArray();
        for (int i = 0; i + nb.Length <= arr.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < nb.Length; j++) if (arr[i + j] != nb[j]) { ok = false; break; }
            if (ok) return true;
        }
        return false;
    }
    static string GetD(Dictionary<string, string> d, string k, string def) { string v; return d != null && d.TryGetValue(k, out v) ? v : def; }

    static string FmtServerVersion(string v)
    {
        var m = Regex.Match((v ?? "").Trim(), @"^V?\.?(\d+)\.(\d{2,3})\.(\d+)$");
        if (m.Success)
        {
            string maj = m.Groups[1].Value, mm = m.Groups[2].Value, build = m.Groups[3].Value;
            if (mm.Length == 2) return "V" + maj + "." + mm[0] + "." + mm[1] + " (b" + build + ")";
            return "V" + maj + "." + mm + " (b" + build + ")";
        }
        return v;
    }
    static string FmtVersion(string v)
    {
        try
        {
            var parts = (v ?? "").Split('.');
            if (parts.Length == 3 && parts[0] == "01")
            {
                int m = int.Parse(parts[1]), n = int.Parse(parts[2]);
                return "V" + m + "." + (n / 10) + "." + (n % 10);
            }
        } catch { }
        return v ?? "";
    }
    static string FmtVerSteam(long v)
    {
        if (v > 10000) return FmtVersion("01." + (v / 100 % 100).ToString("00") + "." + (v % 100).ToString("00"));
        return v > 0 ? v.ToString() : "";
    }

    // ---------------- 地区 ----------------
    static Dictionary<string, string> O2R = BuildO2R();
    static Dictionary<string, string> BuildO2R()
    {
        var d = new Dictionary<string, string>();
        foreach (var o in "36 39 42 43 45 49 58 59 60 61 101 103 106 110 111 112 113 114 115 116 117 118 119 120 121 122 123 124 125 139 144 150 163 171 175 180 182 183 202 203 210 211 218 219 220 221 222 223 1".Split(' ')) d[o] = "as";
        d["27"] = "oc";
        foreach (var o in "21 5 31 44 46 47 48 50 51 62 77 78 79 80 81 82 83 84 85 86 87 88 89 90 91 92 93 94 95 109 134 136 137 138 141 142 145 146 147 148 149 151 152 153 154 155 156 157 158 159 176 178 185 186 187 188 193 194 195 212 213 217".Split(' ')) if (!d.ContainsKey(o)) d[o] = "eu";
        foreach (var o in "3 4 6 8 11 12 13 14 15 16 17 18 19 20 23 24 28 29 30 32 33 40 52 53 54 55 56 63 64 65 66 67 68 69 70 71 72 73 74 75 76 96 97 98 99 100 104 107 108 130 131 132 133 135 140 143 160 161 162 164 165 166 167 168 169 170 172 173 174 184 191 192 198 199 204 205 206 207 208 209 215 216".Split(' ')) if (!d.ContainsKey(o)) d[o] = "na";
        foreach (var o in "37 41 102 105 196 197".Split(' ')) if (!d.ContainsKey(o)) d[o] = "af";
        foreach (var o in "177 179 181 189 200 201".Split(' ')) if (!d.ContainsKey(o)) d[o] = "sa";
        return d;
    }
    static string RegionOf(string ip) { string r; return O2R.TryGetValue((ip ?? "").Split('.')[0], out r) ? r : "?"; }
    static readonly Dictionary<string, string> REGION_ID = new Dictionary<string, string> {
        { "northamericaeast", "na" }, { "northamerica", "na" }, { "centralamerica", "na" }, { "caribbean", "na" },
        { "southamerica", "sa" }, { "brazil", "sa" }, { "europe", "eu" }, { "russia", "ru" }, { "asia", "as" },
        { "middleeast", "me" }, { "africa", "af" }, { "oceania", "oc" }, { "australia", "oc" } };
    static string RegionPref(string reg, string ip) { string r; var k = (reg ?? "").ToLower(); return REGION_ID.TryGetValue(k, out r) ? r : RegionOf(ip); }

    static Dictionary<string, object> DeepQuery(string addr)
    {
        var sp = SplitAddr(addr); string ip = sp[0]; int port = int.Parse(sp[1]);
        if (!IPRE.IsMatch(ip))
            return new Dictionary<string, object> { { "addr", addr }, { "error", "invalid address" }, { "info", (object)new Dictionary<string, object> { { "ok", false } } }, { "rules", new Dictionary<string, object>() }, { "region", "?" } };
        var tcp = TcpQuery(ip, port);
        Dictionary<string, object> info;
        Dictionary<string, object> rules = new Dictionary<string, object>();
        if (tcp != null)
        {
            info = new Dictionary<string, object> {
                { "ok", true }, { "rtt", PInt(GetD(tcp, "_rtt", "0"), 0) }, { "name", GetD(tcp, "GameHost", "") },
                { "map", GetD(tcp, "LevelName", "") }, { "players", 0 }, { "max", 0 },
                { "version", GetD(tcp, "ServerVersion", "") }, { "vac", 0 } };
        }
        else
        {
            info = A2sInfo(ip, port);
            if ((bool)info["ok"]) rules = A2sRules(ip, port);
        }
        string region = tcp != null ? RegionPref(GetD(tcp, "Region", ""), ip) : RegionOf(ip);
        return new Dictionary<string, object> {
            { "addr", ip + ":" + port }, { "info", info }, { "rules", rules },
            { "web", new Dictionary<string, object>() }, { "tcp", tcp ?? new Dictionary<string, string>() }, { "region", region } };
    }

    // ---------------- Steam 主控（子进程隔离） ----------------
    static readonly object SteamGate = new object();   // 同时只允许一个抓取子进程
    [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
    [System.Security.SecurityCritical]
    static Dictionary<string, object> FetchMaster()
    {
        var rows = new List<Dictionary<string, object>>();
        string mode = "steam_off";
        lock (SteamGate)
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Work, "SteamList.exe"),
                Arguments = "--once",
                WorkingDirectory = Work,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            var proc = Process.Start(psi);
            var killer = new Thread(() => { Thread.Sleep(75000); try { if (!proc.HasExited) proc.Kill(); } catch { } });
            killer.IsBackground = true;
            killer.Start();
            string all = proc.StandardOutput.ReadToEnd();
            try { if (!proc.WaitForExit(5000)) proc.Kill(); } catch { }
            var best = new Dictionary<string, int[]>();
            foreach (var ln in all.Split('\n'))
            {
                string line = ln.Trim();
                if (!line.StartsWith("{\"")) continue;
                try
                {
                    string addr = Rx(line, "\"ip\":\"([^\"]+)\"");
                    if (string.IsNullOrEmpty(addr)) continue;
                    int ping = RxI(line, "\"ping\":(-?\\d+)", 9999);
                    if (ping < 0 || ping > 9000) ping = 9999;
                    int players = RxI(line, "\"players\":(-?\\d+)", 0);
                    int maxp = RxI(line, "\"max\":(-?\\d+)", 0);
                    int secure = RxI(line, "\"secure\":(-?\\d+)", 0);
                    long ver = RxL(line, "\"ver\":(-?\\d+)", 0);
                    int[] old;
                    if (best.TryGetValue(addr, out old))
                    {
                        if (ping < old[2])
                        {
                            old[0] = players < 0 ? 0 : players; old[1] = maxp; old[2] = ping;
                            old[3] = secure; old[4] = (int)ver;
                        }
                    }
                    else best[addr] = new int[] { players < 0 ? 0 : players, maxp, ping, secure, (int)ver };
                }
                catch { }
            }
            foreach (var kv in best)
            {
                var sp = SplitAddr(kv.Key);
                string ip = sp[0]; int port = int.Parse(sp[1]);
                var v = kv.Value;
                rows.Add(new Dictionary<string, object> {
                    { "addr", ip + ":" + port }, { "ip", ip }, { "port", port }, { "name", "" }, { "map", "" },
                    { "players", v[0] }, { "max", v[1] }, { "ping", v[2] },
                    { "password", false }, { "secure", v[3] }, { "version", FmtVerSteam(v[4]) },
                    { "region", RegionOf(ip) }, { "online", true }, { "prefs", new Dictionary<string, string>() } });
            }
            if (rows.Count > 0) mode = "steam";
        }
        catch (Exception) { mode = "steam_off"; }
        return new Dictionary<string, object> { { "mode", mode }, { "servers", rows }, { "eos", "skipped" } };
    }
    static string Rx(string s, string pat)
    {
        var m = Regex.Match(s, pat);
        return m.Success ? m.Groups[1].Value : null;
    }
    static int RxI(string s, string pat, int d) { string v = Rx(s, pat); int x; return v != null && int.TryParse(v, out x) ? x : d; }
    static long RxL(string s, string pat, long d) { string v = Rx(s, pat); long x; return v != null && long.TryParse(v, out x) ? x : d; }
}
