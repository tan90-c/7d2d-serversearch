// SteamList.cs - 7 Days to Die Steam master server list fetcher.
//  serve (default): keeps a local HTTP endpoint http://localhost:25157 alive and
//    returns the full internet server list as JSON (GET /api/master, /api/ping).
//  --once: fetches once, prints one JSON line per server to stdout, then exits.
// Requires steam_api64.dll + com.rlabrecque.steamworks.net.dll + steam_appid.txt
// (251570) next to the exe, and a logged-in Steam client.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Steamworks;

class SteamList
{
    const uint APPID = 251570;
    const int PORT = 25157;
    static readonly object Lk = new object();
    static readonly Dictionary<string, gameserveritem_t> Best = new Dictionary<string, gameserveritem_t>();
    static volatile bool Done = false;
    // serve 模式共享状态
    static string CachedJson = null;      // 最近一次完整列表 JSON 数组
    static volatile bool Loading = false;

    static void Main(string[] args)
    {
        bool once = args.Length > 0 && args[0] == "--once";
        if (!once)
        {
            var t = new Thread(RefreshLoop);
            t.IsBackground = true;
            t.Start();
            Serve();
        }
        else
        {
            FetchOnce();
            foreach (var kv in Snap()) Console.WriteLine(Line(kv.Value, kv.Key));
        }
    }

    // ---------------- Steam 主控抓取 ----------------
    static Dictionary<string, gameserveritem_t> Snap()
    {
        lock (Lk) return new Dictionary<string, gameserveritem_t>(Best);
    }

    static void FetchOnce()
    {
        if (!SteamAPI.Init()) { Console.WriteLine("{\"error\":\"steam_off\"}"); return; }
        lock (Lk) Best.Clear();
        Done = false;
        var resp = new ISteamMatchmakingServerListResponse(
            (HServerListRequest req, int i) =>
            {
                try
                {
                    var g = SteamMatchmakingServers.GetServerDetails(req, i);
                    if (g.m_nAppID != APPID) return;
                    string addr = g.m_NetAdr.GetConnectionAddressString();
                    if (string.IsNullOrEmpty(addr)) return;
                    lock (Lk)
                    {
                        gameserveritem_t old;
                        if (!Best.TryGetValue(addr, out old) || Ping(g) < Ping(old))
                            Best[addr] = g;
                    }
                }
                catch { }
            },
            (HServerListRequest req, int i) => { },
            (HServerListRequest req, EMatchMakingServerResponse r) => { Done = true; }
        );
        HServerListRequest h = SteamMatchmakingServers.RequestInternetServerList(
            new AppId_t(APPID), new MatchMakingKeyValuePair_t[0], 0, resp);
        long endMs = Environment.TickCount + 50000;
        int last = -1, stableSince = Environment.TickCount;
        while (!Done && Environment.TickCount < endMs)
        {
            SteamAPI.RunCallbacks();
            Thread.Sleep(5);
            int c; lock (Lk) c = Best.Count;
            if (c == last && c > 0 && Environment.TickCount - stableSince > 4000) break;
            if (c != last) { last = c; stableSince = Environment.TickCount; }
        }
        long t2 = Environment.TickCount + 1200;
        while (Environment.TickCount < t2) { SteamAPI.RunCallbacks(); Thread.Sleep(5); }
        try { SteamMatchmakingServers.ReleaseRequest(h); } catch { }
        SteamAPI.Shutdown();
    }

    static string Line(gameserveritem_t g, string addr)
    {
        string q = "";
        try { q = g.m_NetAdr.GetQueryAddressString(); } catch { }
        return string.Format(
            "{{\"ip\":\"{0}\",\"ping\":{1},\"players\":{2},\"max\":{3},\"secure\":{4},\"ver\":{5},\"qip\":\"{6}\"}}",
            Esc(addr), Ping(g), g.m_nPlayers < 0 ? 0 : g.m_nPlayers, g.m_nMaxPlayers,
            g.m_bSecure ? 1 : 0, g.m_nServerVersion, Esc(q));
    }

    static string Esc(string s)
    {
        return s == null ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    static int Ping(gameserveritem_t g)
    {
        int p = g.m_nPing;
        if (p < 0 || p > 9000) return 9999;
        return p;
    }

    // ---------------- 常驻服务 ----------------
    static void RefreshLoop()
    {
        while (true)
        {
            try
            {
                Loading = true;
                FetchOnce();
                var sb = new StringBuilder("[");
                bool first = true;
                foreach (var kv in Snap())
                {
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append(Line(kv.Value, kv.Key));
                }
                sb.Append("]");
                lock (Lk) CachedJson = sb.ToString();
                Loading = false;
            }
            catch (Exception e)
            {
                Console.WriteLine("refresh error: " + e.Message);
                Loading = false;
            }
            Thread.Sleep(300000);   // 5 分钟一轮
        }
    }

    static void Serve()
    {
        var listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:" + PORT + "/");
        try { listener.Start(); }
        catch (Exception e)
        {
            Console.WriteLine("端口占用或权限不足: " + e.Message);
            return;
        }
        Console.WriteLine("7D2D Steam 搜索助手已启动: http://localhost:" + PORT);
        Console.WriteLine("请保持本窗口开启, 回到网页点「用我的 Steam 搜索」即可。首次列表约需 1 分钟。");
        while (true)
        {
            try
            {
                var ctx = listener.GetContext();
                var req = ctx.Request;
                var res = ctx.Response;
                res.AddHeader("Access-Control-Allow-Origin", "*");
                res.AddHeader("Access-Control-Allow-Methods", "GET, OPTIONS");
                res.AddHeader("Access-Control-Allow-Headers", "*");
                res.AddHeader("Access-Control-Allow-Private-Network", "true");
                res.AddHeader("Access-Control-Max-Age", "86400");
                res.ContentType = "application/json; charset=utf-8";
                string path = req.Url.AbsolutePath;
                string body;
                if (req.HttpMethod == "OPTIONS") { res.StatusCode = 204; res.Close(); continue; }
                if (path == "/api/ping")
                {
                    int n; lock (Lk) n = CachedJson == null ? 0 : Best.Count;
                    body = "{\"pong\":1,\"servers\":" + n + ",\"loading\":" + (Loading ? 1 : 0) + "}";
                }
                else if (path == "/api/master")
                {
                    string cj; lock (Lk) cj = CachedJson;
                    body = cj == null
                        ? "{\"mode\":\"loading\",\"servers\":[]}"
                        : "{\"mode\":\"steam\",\"servers\":" + cj + "}";
                }
                else { res.StatusCode = 404; res.Close(); continue; }
                byte[] b = Encoding.UTF8.GetBytes(body);
                res.ContentLength64 = b.Length;
                res.OutputStream.Write(b, 0, b.Length);
                res.Close();
            }
            catch (Exception e) { Console.WriteLine("serve: " + e.Message); }
        }
    }
}
