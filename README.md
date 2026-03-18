# ⬡ WebInterceptor

> 基于 WebView2 的本地 HTTP 网络请求拦截工具，通过内嵌真实浏览器引擎抓取动态加载的 XHR / Fetch 数据，并以 REST API 的形式对外暴露，适合爬虫、自动化测试等场景。

![Platform](https://img.shields.io/badge/platform-Windows-blue?logo=windows)
![Runtime](https://img.shields.io/badge/.NET-8%2B-purple?logo=dotnet)
![WebView2](https://img.shields.io/badge/WebView2-Required-orange)
![License](https://img.shields.io/badge/license-MIT-green)

---

## ✨ 功能特性

- **真实浏览器引擎**：基于 Microsoft WebView2（Chromium），完整执行 JavaScript，无需模拟或逆向分析接口
- **多实例并发**：可配置 1–20 个 WebView2 实例组成资源池，支持并发请求，内置公平调度队列
- **灵活拦截模式**：
  - **即时模式**（默认）：一旦拦截到匹配响应立即返回
  - **完整加载模式**（`wait_for_complete`）：等待页面 `NavigationCompleted` 后再收集所有匹配响应
- **URL 过滤**：通过 `filter` 字符串精确匹配目标 XHR/Fetch 请求 URL
- **可选载荷**：按需返回响应 Body、Headers、Cookies
- **图片直传**：自动识别图片 URL（`.webp/.jpg/.jpeg/.png/.gif`），代理下载并以正确 Content-Type 返回
- **页面快照**：可获取当前页面完整 HTML（`readyState` 感知）
- **统计面板**：实时显示总请求数、成功 / 失败 / 超时计数及平均耗时
- **深色 UI**：VS Code Dark+ 风格的 WinForms 界面，实例状态实时可视化
- **系统托盘**：关闭窗口后最小化到托盘，后台持续运行
- **热配置**：界面直接修改端口 / 实例数，无需重启程序；配置自动持久化到 `config.ini`

---

## 🖼️ 界面预览

```
┌─────────────────────────────────────────────────────────────┐
│ ⬡ WebInterceptor    实例数 [5▾]   端口 [8888]  [应用]       │
├─────────────────────────────────────────────────────────────┤
│ STATS  总计 42  成功 38  失败 2  超时 2  均耗 1240ms │ 实例状态 … │
├─────────────────────────────────────────────────────────────┤
│  实例 #1 │ 实例 #2 │ 实例 #3 │ 实例 #4 │ 实例 #5            │
│  ┌──────────────────────────────────────────────────────┐   │
│  │  <WebView2 内嵌浏览器预览>                           │   │
│  └──────────────────────────────────────────────────────┘   │
├─────────────────────────────────────────────────────────────┤
│ OUTPUT                               [完整▾]  [清空]        │
│  12:34:01  实例 #1  →  https://example.com/api/data         │
│  12:34:02  实例 #1  拦截成功  3821 字节                      │
└─────────────────────────────────────────────────────────────┘
```

---

## 🔧 环境要求

| 依赖 | 版本 |
|------|------|
| Windows | 10 / 11（x64） |
| .NET | 8.0 或更高 |
| Microsoft WebView2 Runtime | 最新版（随 Edge 自动安装） |

> 若系统尚未安装 WebView2 Runtime，可从 [Microsoft 官网](https://developer.microsoft.com/microsoft-edge/webview2/) 下载安装。

---

## 🚀 快速开始

### 1. 克隆 & 编译

```bash
git clone https://github.com/yourname/WebInterceptor.git
cd WebInterceptor
dotnet build -c Release
```

### 2. 运行

```bash
dotnet run
# 或直接双击 Release 目录下的 WebInterceptor.exe
```

程序启动后将在 `http://localhost:8888` 监听请求（端口可在界面或 `config.ini` 中修改）。

---

## 📡 API 使用

所有请求均以 `POST /` 方式发送，Body 为 JSON。

### 请求参数

| 字段 | 类型 | 必须 | 默认值 | 说明 |
|------|------|:----:|--------|------|
| `url` | `string` | ✅ | — | 要导航到的目标页面 URL |
| `filter` | `string` | | `""` | 过滤拦截的 XHR/Fetch URL（子字符串匹配） |
| `timeout_seconds` | `int` | | `30` | 超时秒数 |
| `keep_page` | `bool` | | `false` | `true` 保留当前页面不重新导航 |
| `wait_for_complete` | `bool` | | `false` | `true` 等待页面完全加载后再返回 |
| `include_body` | `bool` | | `true` | 是否在响应中包含 Body |
| `include_headers` | `bool` | | `false` | 是否在响应中包含 Headers |
| `instances` | `string` | | — | 指定使用的实例 ID，如 `"1"` 或 `"1,3"` |
| `collect_delay_seconds` | `int` | | `2` | 完整加载模式下导航完成后额外等待秒数（0–30） |

### 示例：即时拦截

```bash
curl -X POST http://localhost:8888 \
  -H "Content-Type: application/json" \
  -d '{
    "url": "https://example.com/page",
    "filter": "/api/data",
    "timeout_seconds": 15,
    "include_body": true,
    "include_headers": false
  }'
```

### 示例：完整加载模式

```bash
curl -X POST http://localhost:8888 \
  -H "Content-Type: application/json" \
  -d '{
    "url": "https://example.com/page",
    "filter": "/api/list",
    "wait_for_complete": true,
    "collect_delay_seconds": 3
  }'
```

### 成功响应（`200 OK`）

即时模式返回单条数据：

```json
{
  "requestUrl": "https://example.com/api/data?page=1",
  "body": "{\"items\":[...]}",
  "headers": null,
  "cookies": { "session": "abc123" }
}
```

完整加载模式返回数组：

```json
{
  "items": [
    {
      "requestUrl": "https://example.com/api/list?page=1",
      "body": "...",
      "headers": null,
      "cookies": {}
    },
    { "..." }
  ]
}
```

### 错误响应

| HTTP 状态码 | 含义 |
|-------------|------|
| `400` | 请求参数缺失或格式错误 |
| `404` | 页面加载完成但未找到匹配内容 |
| `504` | 超时未拦截到数据 |
| `500` | 内部异常 |

---

## ⚙️ 配置文件

程序首次运行时自动生成 `config.ini`（与 `.exe` 同目录）：

```ini
; WebInterceptor 配置文件
; 修改后重启程序生效

[server]
port = 8888

[webview2]
instance_count = 5

[log]
display_mode = full   ; full | simple
```

也可直接在程序界面修改实例数和端口后点击「应用」，配置将自动写入 `config.ini`。

---

## 🗂️ 项目结构

```
WebInterceptor/
├── Program.cs          # 全部源码（单文件）
├── config.ini          # 运行时自动生成的配置文件
├── icon.ico            # 可选自定义图标
└── README.md
```

---

## 🔍 工作原理

```
外部调用方
    │  POST http://localhost:8888  { url, filter, ... }
    ▼
HttpListener（C# 内置）
    │
    ▼
实例调度器（SemaphoreSlim + FIFO 队列）
    │  从 WebView2 池中获取空闲实例
    ▼
WebView2 实例
    ├─ Navigate(url)
    ├─ WebResourceResponseReceived 事件
    │      └─ 匹配 filter → 读取 Body / Headers / Cookies
    └─ NavigationCompleted（完整加载模式）
    │
    ▼
序列化 → 返回 JSON 给调用方
```

---

## 🛠️ 常见问题

**Q: 端口被占用怎么办？**  
A: 在界面顶部修改端口号后点击「应用」，或修改 `config.ini` 中的 `port` 后重启程序。

**Q: 拦截不到数据，一直超时？**  
A: 检查 `filter` 是否匹配目标请求 URL 的子字符串；可先将 `filter` 置空拦截所有请求进行调试。

**Q: 是否支持需要登录的页面？**  
A: 支持。WebView2 实例共享本机的 Edge 用户数据目录，可在实例内手动完成登录后再发起拦截请求。

**Q: 如何在 Python 中调用？**  
```python
import requests, json

resp = requests.post("http://localhost:8888", json={
    "url": "https://example.com/page",
    "filter": "/api/data",
    "timeout_seconds": 20
})
data = resp.json()
print(data["body"])
```

---

## 📄 License

[MIT](LICENSE)
