# ⬡ WebInterceptor

一个基于 **WebView2 + HTTP 本地服务** 的网络请求拦截工具，支持多实例并发、深色主题 GUI 以及灵活的拦截策略。

---

## ✨ 功能特性

- **HTTP 本地服务**：监听本地端口（默认 `8888`），对外暴露 `POST /intercept` 接口，任何语言均可调用
- **多 WebView2 实例池**：可配置 1–20 个并发实例，每个实例独立展示在 Tab 中，支持实时调整
- **三种拦截模式**：
  - **无 Filter 模式**：导航后返回完整页面 HTML
  - **立即返回模式**：监听指定 URL 关键词，第一个匹配即返回
  - **完整加载模式**（`wait_for_complete`）：等待页面加载完毕，收集所有匹配响应后统一返回
- **请求/响应数据采集**：可选返回 Body、Headers、Cookies
- **FIFO 实例锁定**：通过 `instances` 参数指定实例 ID，同一实例的请求严格按到达顺序处理
- **实时统计面板**：总请求数、成功、失败、超时、平均响应时间及各实例状态
- **系统托盘**：关闭窗口后最小化到托盘，不终止服务
- **深色主题 GUI**：VS Code Dark+ 风格界面
- **CORS 支持**：允许跨域调用

---

## 🖥️ 运行环境

| 依赖 | 要求 |
|------|------|
| 操作系统 | Windows 10 / 11（64-bit） |
| .NET | .NET 8 或更高 |
| WebView2 Runtime | [Microsoft Edge WebView2](https://developer.microsoft.com/microsoft-edge/webview2/)（大部分 Win10/11 已内置） |
| NuGet 包 | `Microsoft.Web.WebView2`、`System.Threading.Tasks.Dataflow` |

---

## 🚀 快速开始

### 1. 编译

```bash
dotnet build -c Release
```

### 2. 运行

直接双击生成的 `.exe`，或：

```bash
dotnet run
```

程序启动后自动监听 `http://0.0.0.0:8888/intercept`。

### 3. 配置（可选）

程序目录下的 `config.ini` 会在首次启动时自动生成：

```ini
; WebInterceptor 配置文件
; 修改后重启程序生效

[server]
port = 8888

[webview2]
instance_count = 5
```

也可以在 GUI 中直接修改端口和实例数，点击 **应用** 后立即生效并保存。

---

## 📡 API 文档

### 端点

```
POST http://localhost:8888/intercept
Content-Type: application/json
```

### 请求参数

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `url` | `string` | ✅ | 要导航的目标 URL |
| `filter` | `string` | ❌ | 拦截的 URL 关键词（为空时返回页面 HTML） |
| `timeout_seconds` | `int` | ❌ | 超时秒数，默认 `30`（无 Filter 模式默认 `60`） |
| `keep_page` | `bool` | ❌ | `true` 表示拦截完成后保留当前页面，默认 `false`（跳回空白页） |
| `wait_for_complete` | `bool` | ❌ | `true` 开启完整加载模式，收集所有匹配后返回，默认 `false` |
| `include_body` | `bool` | ❌ | 是否返回响应 Body，默认 `true`（完整加载模式）/ `false`（无 Filter 模式） |
| `include_headers` | `bool` | ❌ | 是否返回响应 Headers，默认 `false` |
| `instances` | `string` | ❌ | 指定实例 ID（逗号分隔，如 `"1,2,!3"`），不填则自动分配 |
| `collect_delay_seconds` | `int` | ❌ | 完整加载模式下导航完成后的额外等待时间，默认 `2`，最大 `30` |

### 响应格式

**立即返回模式（有 Filter）**

```json
{
  "requestUrl": "https://api.example.com/data?token=xxx",
  "body": "{ \"key\": \"value\" }",
  "headers": { "Content-Type": "application/json" },
  "cookies": { "session": "abc123" }
}
```

**完整加载模式（`wait_for_complete: true`）**

```json
{
  "items": [
    {
      "requestUrl": "https://api.example.com/list",
      "body": "...",
      "headers": { "Content-Type": "application/json" }
    }
  ]
}
```

**无 Filter 模式（返回 HTML）**

```
HTTP 200  text/html; charset=utf-8
<html>...</html>
```

若请求了 `include_headers` 或 `include_body`，返回 JSON 格式：

```json
{
  "html": "<html>...</html>",
  "headers": { "Content-Type": "text/html" }
}
```

### 错误码

| 状态码 | 含义 |
|--------|------|
| `400` | 请求参数错误 |
| `404` | 页面加载完成但未匹配到内容 |
| `429` | 并发超限（最多 10 个并发请求） |
| `500` | 内部错误 |
| `503` | WebView2 实例池不可用 |
| `504` | 超时 |

---

## 💡 调用示例

### Python

```python
import requests

resp = requests.post("http://localhost:8888/intercept", json={
    "url": "https://example.com",
    "filter": "api/data",
    "timeout_seconds": 30,
    "include_body": True,
    "include_headers": False
})

print(resp.json())
```

### curl

```bash
curl -X POST http://localhost:8888/intercept \
  -H "Content-Type: application/json" \
  -d '{"url":"https://example.com","filter":"api/data","timeout_seconds":30}'
```

### JavaScript (fetch)

```js
const res = await fetch("http://localhost:8888/intercept", {
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify({
    url: "https://example.com",
    filter: "api/data",
    wait_for_complete: true,
    collect_delay_seconds: 3
  })
});
const data = await res.json();
console.log(data.items);
```

---

## 🗂️ 项目结构

```
WebInterceptor/
├── Program.cs          # 全部源码（单文件）
├── config.ini          # 运行时配置（自动生成）
├── icon.ico            # 可选：自定义托盘图标
└── README.md
```

---

## ⚙️ 高级说明

### 实例调度

- 默认情况下从公共池中取空闲实例（先进先出）
- 通过 `instances` 字段可指定固定实例，多个请求指定同一实例时自动排队（FIFO），保证顺序
- 临时超出配置实例数时可动态扩充，超出部分使用后销毁

### 并发限制

- HTTP 请求并发上限：**10**（超出返回 `429`）
- WebView2 实例并发上限：由 `instance_count` 决定（最多 20）

### 内存管理

- 每次请求完成后默认导航回 `about:blank` 释放页面资源
- 设置 `keep_page: true` 可跳过此步骤（适合连续操作同一页面）

---

## 📄 License

MIT
