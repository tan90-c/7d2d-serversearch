# 7D2D Server Search（七日杀服务器搜索 · 单文件版）

一个 316 KB 的单文件 Windows 程序：双击运行后在本机启动一个网页服务，用**你自己已登录的 Steam 客户端**搜索全网《7 Days to Die》服务器，并可直查服务器详情。无需安装 Python、.NET 或任何运行库（Windows 10/11 自带 .NET Framework 4）。

## 功能

1. **Steam 全量搜索**：通过 Steam 官方主控服务器接口拉取全部公开 7D2D 服务器（约 2500+ 台），实时显示人数、延迟、地区、版本。
2. **详情直查**：先走 TCP 查询协议读取服务器完整参数（地图、天数、模式、设置项等 80+ 字段），服务器不支持 TCP 时自动回落 UDP A2S。
3. **连接服务器**：点击按钮经 `steam://connect/IP:PORT` 直接加入。
4. **地区筛选**：内置 IP 段 → 大洲归属表，全离线计算，不请求任何第三方地理服务。
5. **完全本地运行**：服务只监听 `127.0.0.1:20002`，除 Steam 官方接口和服务器查询外不与任何第三方通信，不收集任何个人信息。

## 使用

1. 下载 [7D2D-ServerSearch.exe](7D2D-ServerSearch.exe)。
2. 保持 Steam 客户端处于登录状态。
3. 双击运行，浏览器会自动打开 `http://127.0.0.1:20002`。
4. 保持黑色控制台窗口开启即为服务运行中，关闭窗口即退出。

首次运行会在 `%LOCALAPPDATA%\7D2DServerSearch` 解出 Steam 运行组件（steam_api64.dll、Steamworks.NET、SteamList.exe、steam_appid.txt），全部来自内嵌资源，可安全删除后重新生成。

> 安全提示：exe 属第三方二进制，如浏览器或杀软提示，请自行核实来源后再运行。代码全部在本仓库，欢迎自行编译。

## 技术要点

1. 主体为单文件 C#（.NET Framework 4 / `csc` 直接编译），零第三方 NuGet 依赖。
2. `steam_api64.dll`（Valve 官方）与 `com.rlabrecque.steamworks.net.dll`（[Steamworks.NET](https://steamworks.github.io/)，MIT）经 gzip 内嵌进 exe 资源段，运行时解包。
3. Steam 主控抓取由内嵌的 `SteamList.exe --once` 子进程完成（进程隔离：原生崩溃不影响主程序），主程序解析其 JSON 输出并按地址去重取最优延迟。
4. HTTP 服务使用裸 `TcpListener` 手写协议响应，规避 Windows 非管理员下 `HttpListener` 绑定 `127.0.0.1` 返回 400 的问题。
5. 游戏内时间由服务器时钟 tick 直接换算（1 游戏小时 = 1000 tick，1 天 = 24000 tick），不依赖额外状态。

## HTTP API

页面与 API 同源，均挂在 `http://127.0.0.1:20002`：

| 路径 | 说明 |
|---|---|
| `/` | 内置网页 |
| `/api/ping` | 存活探测（完整版返回 `full:true`，页面据此自动隐藏无关入口并直连 Steam 列表） |
| `/api/master?refresh=1` | Steam 全量服务器列表（缓存 5 分钟） |
| `/api/query?addr=IP:PORT` | 单服深度查询（TCP 85 字段协议，回落 A2S） |
| `/assistant` | 下载本 exe 自身 |

## 自行编译

前置：Windows x64、`.NET Framework 4`（系统自带）、`csc.exe`（`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`）、Steamworks.NET 的 `com.rlabrecque.steamworks.net.dll` 与 `steam_api64.dll`（[官方发布页](https://steamworks.github.io/) 下载，放入 `deps\`）。

```powershell
.\build.ps1
```

产物为根目录 `7D2D-ServerSearch.exe`。仓库未附带 UI 页面源码，构建时会自动生成一个最小占位页面；如需完整网页，请按上面的 API 自行编写并放到 `ui\index.html`。

## 目录结构

```
7D2D-ServerSearch.exe   预编译成品（Win10/11 x64）
src/fullapp.cs          主程序：HTTP 服务 + 查询协议 + Steam 结果解析
src/SteamList.cs        Steam 主控抓取组件（serve / --once 双模式）
build.ps1             一键编译脚本
deps/                 （自建）放两个 Steam 运行 DLL
ui/index.html         （自建，可选）自定义前端页面
```

## 许可与声明

1. 本仓库源码以 MIT 协议开源（见 [LICENSE](LICENSE)）。
2. `steam_api64.dll` 版权归 Valve；`com.rlabrecque.steamworks.net.dll` 版权归 Riley Labrecque（MIT）。成品 exe 内嵌上述组件仅作程序运行用途。
3. 本项目是独立的第三方工具，与 Valve、Facepunch Studios 无关联；仅查询公开服务器信息，请勿用于恶意扫描。
