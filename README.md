# YuSwitch

> 基于 .NET 8 的 AI 模型网关 —— 统一接入多家大模型，对外提供 OpenAI / Anthropic 双规范兼容接口，内置 Blazor Server 管理界面，单文件无依赖运行。

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-win%7Clinux%7Cosx-lightgrey)](#)
[![Release](https://img.shields.io/badge/release-single--file-success)](https://github.com/geekwind/YuSwitch/releases)
[![License](https://img.shields.io/badge/license-MIT-green)](#)

📚 [使用说明](USAGE.md) · 📦 [下载](https://github.com/geekwind/YuSwitch/releases) · 🏗️ [架构图](#-架构)

---

## ✨ 功能特性

### 🔄 双协议入站
一个网关地址，同时服务两类客户端，**无需客户端改造**：

| 入站协议 | 端点 | 适用客户端 |
|---|---|---|
| **OpenAI Chat Completions** | `POST /v1/chat/completions` | OpenAI SDK、NextChat、LobeChat、沉浸式翻译等 |
| **Anthropic Messages** | `POST /v1/messages` | Claude Code、Anthropic SDK、Claude 客户端 |
| OpenAI Models | `GET /v1/models` | 模型列表查询 |
| Anthropic Count Tokens | `POST /v1/messages/count_tokens` | Token 预估 |

### 🔌 多上游 Provider
统一 `IProvider` 抽象，内置两类 provider 实现：

| Provider | 上游协议 | 适用 |
|---|---|---|
| `OpenAIProvider` | OpenAI `/v1/chat/completions` | OpenAI、DeepSeek、智谱、Groq、Azure 等所有 OpenAI 兼容服务 |
| `ClaudeProvider` | Anthropic `/v1/messages` | Anthropic 原生 API 等 Claude 兼容服务 |

**全协议转换矩阵**（客户端↔上游任意组合）：

```
客户端          上游            转换路径
─────────────────────────────────────────────
OpenAI    →    OpenAI     →   直通
OpenAI    →    Claude     →   出站转换 (ChatRequest→Anthropic)
Claude    →    OpenAI     →   入站转换 (Anthropic→ChatRequest)
Claude    →    Claude     →   全链路转换
```

### ⚖️ 智能调度
- **加权轮询负载均衡** —— 同优先级服务按 `Weight` 严格轮询（等权 ABAB，不等权 AAAB），流量在服务间均匀交替
- **优先级 failover** —— `Priority` 低值优先，失败自动切换到下一个候选服务
- **Sticky Session** —— 仅在显式 `X-Session-Id` 请求头时做会话亲和，同一会话固定路由到同一服务，保留多轮推理上下文（客户端的 `user` / `metadata.user_id` 不再触发粘性，避免误把全部流量钉到单服务）
- 同一模型别名可挂载到多个服务商，网关自动在它们之间负载均衡

### 🏷️ 模型映射
解决不同服务商对同一模型命名不一致的问题：

```
客户端请求: my-model-alias
     │
     ├── 服务 A (OpenAI 规范):  my-model-alias → real-model-name-A (upstream_model)
     ├── 服务 B (Claude 规范):  my-model-alias → real-model-name-B
     └── 服务 C (另一家):       my-model-alias → real-model-name-C
```

- `ModelName`：客户端看到的统一别名
- `UpstreamModel`：发给该服务商上游的真实名（留空=同名）
- `Aliases`：额外匹配名（逗号分隔），让多个别名都命中同一行

### 🛠️ 协议字段全支持
| 字段 | 流式/非流式 | 说明 |
|---|---|---|
| `tools` / `tool_choice` | ✅ | function calling，多轮 tool 链完整闭环 |
| `parallel_tool_calls` | ✅ | 并行工具调用控制 |
| `response_format` | ✅ | `json_object` / `json_schema` 结构化输出 |
| `reasoning_content` | ✅ | 推理模型思考过程 |
| `stream_options.include_usage` | ✅ | 流式末尾返回 usage |
| `temperature` / `top_p` / `max_tokens` / `stop` / `seed` | ✅ | 采样参数透传 |

### 📊 可观测性
- **调用日志** —— 每次请求记录：模型、服务、token 消耗、缓存命中、延迟、TTFT（首 token 时间）、prompt/response 预览、session id
- **调度追踪** —— `/admin/dispatch-trace` 查看最近 64 次调度决策
- **用量统计** —— 按模型/服务/Provider 分组，缓存命中次数、平均延迟、平均 TTFT
- **结构化日志** —— Serilog，文件按天滚动

### 🖥️ Blazor Server 管理界面
内嵌单页管理 UI（C# 全栈，无需前端构建）：
- **仪表盘** —— 请求/Token/缓存命中概览
- **服务管理** —— 增删改查 Provider 服务、配置模型映射、测试连通性、一键发现上游模型
- **API Key 管理** —— 生成/吊销密钥、模型权限白名单
- **测试** —— 在线对话测试，支持 OpenAI/Anthropic 协议切换、流式/非流式
- **用量统计** —— 详细指标与分组视图
- **调用日志** —— 每次调用详情，可展开看 prompt/response/缓存命中
- **检查更新** —— 自动更新单文件版本（下载 → 校验 → 替换 → 重启）

### 🔐 鉴权
- 多 API Key 管理，每 key 可配 `allowed_models`（支持 `*` 通配）
- 开放模式（无 key 配置时不鉴权，便于本地开发）

### 🔄 自动更新
发布版支持设置页一键升级，无需重新下载安装包：
- **检查更新** —— 查询 GitHub Releases 最新版，显示版本号、更新说明与下载大小
- **立即更新** —— 下载平台匹配的单文件 → `.sha256` 校验 → 原子替换 exe → 自动重启（数据与配置保留，headless 模式不变）
- **升级安全** —— 校验不符自动中止；更新前自动备份数据库，库损坏自动回滚最新备份
- **限 macOS `.app`** —— 桌面 `.app` 包装形态只读不可替换，请手动下载 `.dmg`；headless 二进制可正常自动更新

---

## 🏗️ 架构

```mermaid
graph TB
    subgraph Clients["客户端"]
        OC["OpenAI 客户端<br/>SDK / NextChat / LobeChat"]
        CC["Claude 客户端<br/>Claude Code / Anthropic SDK"]
    end

    subgraph Gateway["YuSwitch 网关"]
        direction TB
        AUTH["鉴权中间件<br/>ApiKeyAuth"]

        subgraph Inbound["入站协议层"]
            OAI["OpenAI 端点<br/>/v1/chat/completions"]
            ANT["Anthropic 端点<br/>/v1/messages"]
        end

        subgraph Core["网关核心"]
            GW["GatewayService<br/>调度编排"]
            LB["加权轮询 + 优先级<br/>Sticky Session"]
            FO["Failover 容错"]
        end

        subgraph Providers["Provider 抽象层"]
            OP["OpenAIProvider<br/>→ /v1/chat/completions"]
            CP["ClaudeProvider<br/>→ /v1/messages"]
            REG["ProviderRegistry<br/>按类型注册/查找"]
        end

        SSE["SseWriter<br/>统一流式输出"]
        AUTH --> Inbound
        Inbound --> Core
        Core --> Providers
        Providers --> SSE
    end

    subgraph Upstreams["上游模型服务"]
        U1["OpenAI 兼容<br/>OpenAI / DeepSeek / 智谱<br/>Groq / Azure"]
        U2["Claude 原生<br/>Anthropic"]
    end

    subgraph Store["持久化"]
        DB[("SQLite / PostgreSQL<br/>EF Core")]
        UI["Blazor Server UI<br/>管理界面"]
    end

    OC -->|"OpenAI 规范"| AUTH
    CC -->|"Anthropic 规范"| AUTH
    OP --> U1
    CP --> U2
    Core -.->|"读写配置/日志"| DB
    UI -.->|"CRUD"| DB
    UI -->|"管理"| Gateway
```

### 请求处理流程

```mermaid
sequenceDiagram
    participant C as 客户端
    participant A as 鉴权中间件
    participant E as 入站端点
    participant G as GatewayService
    participant P as Provider
    participant U as 上游

    C->>A: POST /v1/chat/completions (或 /v1/messages)
    A->>A: 校验 API Key
    A->>E: 转发请求
    E->>E: 协议转换 (Anthropic→ChatRequest 如需)
    E->>G: ChatAsync(ChatRequest)
    G->>G: ResolveCandidates (别名匹配 + 加权随机)
    G->>G: ApplyRedirect (模型名映射)
    G->>P: ChatAsync / StreamAsync
    P->>U: 调用上游 (按 provider 协议)
    U-->>P: 响应 (OpenAI 或 Anthropic 格式)
    P-->>G: 统一 ChatResponse / StreamChunk
    G->>G: 记录调用日志 (token/缓存/延迟)
    G-->>E: 统一响应
    E->>E: 协议转换 (ChatResponse→Anthropic 如需)
    E-->>C: 客户端协议响应
```

### 项目结构

```
YuSwitch/
├── Program.cs                    # 主机配置 + DI + 路由注册
├── Models/                       # 统一领域模型
│   ├── ChatRequest.cs            #   请求 (含 tools/reasoning/response_format 全字段)
│   ├── ChatResponse.cs           #   响应 + 流式 chunk + 错误格式
│   └── Embedding.cs              #   向量模型
├── Providers/                    # ★ 上游 Provider 抽象
│   ├── IProvider.cs              #   统一接口 + 能力标记
│   ├── ProviderRegistry.cs       #   注册表 (DI)
│   ├── OpenAI/OpenAIProvider.cs  #   OpenAI 兼容上游
│   └── Claude/ClaudeProvider.cs  #   Anthropic 原生上游
├── Gateway/                      # ★ 网关核心
│   ├── GatewayService.cs         #   调度编排 + failover + sticky session
│   └── SseWriter.cs              #   统一 SSE 输出
├── Endpoints/                    # 入站端点
│   ├── OpenAiEndpoints.cs        #   /v1/chat/completions, /v1/models
│   ├── AnthropicEndpoints.cs     #   /v1/messages, count_tokens
│   └── AdminEndpoints.cs         #   /admin/* 管理 API
├── Middleware/
│   └── ApiKeyAuthMiddleware.cs   #   鉴权 (按路径返回对应规范错误)
├── Services/
│   ├── ConfigService.cs          #   配置快照 + 模型匹配
│   └── UsageService.cs           #   用量记录 + 统计
├── Data/
│   ├── AppDbContext.cs           #   EF Core DbContext
│   └── Entities/                 #   Service / Model / ApiKey / UsageLog
└── Components/                   # Blazor Server UI
    ├── Pages/                    #   仪表盘/服务管理/Key/测试/用量/日志
    └── Layout/                   #   布局 + 导航
```

---

## 🚀 快速开始

### 下载运行（无需 .NET 环境）

从 [Releases](https://github.com/geekwind/YuSwitch/releases) 下载对应平台的单文件版本：

```bash
# Windows: 解压后运行
YuSwitch.exe

# Linux / macOS: 解压后运行
chmod +x YuSwitch && ./YuSwitch
```

访问 **http://localhost:5078** 进入管理界面，配置你的上游服务。

> 💡 升级：设置页 →「检查更新」→「立即更新」，自动下载新版本并重启（数据保留）。macOS `.app` 请手动下载 `.dmg`。

### 从源码运行

项目为多目标（`net8.0` headless / `net8.0-windows` 桌面 GUI），`dotnet run` 需指定框架：

```bash
# Windows 桌面模式（WebView2 窗口 + 托盘）
dotnet run -f net8.0-windows

# headless 模式（任意平台；Windows 上也可用 --headless 强制无窗口）
dotnet run -f net8.0 -- --urls "http://localhost:5078"
```

### 初始配置

1. 启动后访问 **http://localhost:5078**
2. 首页点击「初始化示例配置」创建占位服务
3. 进入「服务管理」编辑服务，填入你的真实上游地址和 API Key
4. 点击「发现模型」自动拉取上游可用模型
5. 启用服务后即可调用

### 调用

**OpenAI 规范：**
```bash
curl -X POST http://localhost:5078/v1/chat/completions \
  -H "Authorization: Bearer sk-yuswitch-local" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "your-model",
    "messages": [{"role": "user", "content": "你好"}],
    "max_tokens": 100
  }'
```

**Anthropic 规范：**
```bash
curl -X POST http://localhost:5078/v1/messages \
  -H "x-api-key: sk-yuswitch-local" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "your-model",
    "max_tokens": 100,
    "messages": [{"role": "user", "content": "你好"}]
  }'
```

**流式（任一协议加 `"stream": true`）：**
```bash
curl -N -X POST http://localhost:5078/v1/chat/completions \
  -H "Authorization: Bearer sk-yuswitch-local" \
  -H "Content-Type: application/json" \
  -d '{"model":"your-model","stream":true,"messages":[{"role":"user","content":"count 1 to 5"}],"max_tokens":30}'
```

**Function Calling：**
```bash
curl -X POST http://localhost:5078/v1/chat/completions \
  -H "Authorization: Bearer sk-yuswitch-local" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "your-model",
    "messages": [{"role": "user", "content": "北京天气如何？"}],
    "tools": [{"type": "function", "function": {
      "name": "get_weather",
      "description": "获取天气",
      "parameters": {"type": "object", "properties": {"city": {"type": "string"}}, "required": ["city"]}
    }}],
    "tool_choice": "auto"
  }'
```

---

## ⚙️ 配置

所有配置通过 Blazor 管理界面或 Admin API 完成，持久化到 SQLite（生产可切 PostgreSQL/MySQL，改连接串即可）。

### 配置一个上游服务

**界面操作**：服务管理 → 新建服务

**API 操作**：
```bash
# 创建服务
curl -X POST http://localhost:5078/admin/services \
  -H "Content-Type: application/json" \
  -d '{
    "providerType": "openai",
    "name": "我的 DeepSeek",
    "enabled": true,
    "serverUrl": "https://api.deepseek.com/v1",
    "credentialsJson": "{\"api_key\":\"sk-your-key\"}",
    "weight": 1,
    "priority": 0
  }'

# 为服务添加模型（含别名映射）
curl -X POST http://localhost:5078/admin/services/1/models \
  -H "Content-Type: application/json" \
  -d '{
    "modelName": "deepseek-chat",
    "upstreamModel": "deepseek-chat",
    "aliases": "deepseek,ds-chat",
    "enabled": true
  }'
```

### 数据库切换

默认 SQLite（零配置），生产环境切换 PostgreSQL：

```csharp
// Program.cs
builder.Services.AddDbContextFactory<AppDbContext>(opt =>
    opt.UseNpgsql(Configuration.GetConnectionString("Default")));
```

---

## 📡 API 参考

### 网关 API
| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/v1/chat/completions` | OpenAI Chat Completions（流式/非流式）|
| GET | `/v1/models` | 模型列表 |
| GET | `/v1/models/{model}` | 模型详情 |
| POST | `/v1/messages` | Anthropic Messages（流式/非流式）|
| POST | `/v1/messages/count_tokens` | Token 预估 |

### 管理 API
| 方法 | 路径 | 说明 |
|---|---|---|
| GET/POST | `/admin/services` | 服务 CRUD |
| GET/POST | `/admin/services/{id}/models` | 模型管理 |
| POST | `/admin/services/{id}/test` | 测试连通性 |
| POST | `/admin/services/{id}/discover-models` | 一键发现上游模型 |
| GET/POST | `/admin/apikeys` | API Key 管理 |
| GET | `/admin/usage` | 用量统计 |
| GET | `/admin/call-logs?limit=50` | 调用日志 |
| GET | `/admin/dispatch-trace` | 调度追踪 |
| POST | `/admin/seed` | 初始化示例配置 |
| POST | `/admin/system/restart` | 重启服务（新进程重新加载配置）|
| GET | `/admin/system/update/check` | 检查新版本 |
| POST | `/admin/system/update/apply` | 应用更新（下载校验替换重启）|

---

## 🔧 技术栈

| 组件 | 技术 |
|---|---|
| 运行时 | .NET 8 (LTS) |
| Web 框架 | ASP.NET Core 8 Minimal API |
| UI | Blazor Server (SignalR 实时) |
| ORM | EF Core 8 (SQLite / PostgreSQL) |
| 日志 | Serilog (文件滚动) |
| HTTP 容错 | 网关层自适应故障转移 + 熔断（无隐藏重试） |
| JSON | System.Text.Json (snake_case) |

---

## 📄 License

MIT

---

## 🔨 构建与发布

### 本地构建单文件

```bash
dotnet publish YuSwitch.csproj \
  -c Release -f net8.0-windows -r win-x64 --self-contained \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -o ./publish
```

产物为单个 `YuSwitch.exe`（约 100MB，含 .NET 运行时 + Blazor 静态资源嵌入），无需安装 .NET 即可运行。

支持的目标平台（RID 与目标框架 `-f` 对应）：
- `win-x64` / `win-arm64` → `-f net8.0-windows`（桌面 GUI + 网关）
- `linux-x64` / `linux-arm64` / `osx-x64` / `osx-arm64` → `-f net8.0`（headless 网关）

### GitHub Actions 自动发布

打 tag 触发自动构建并发布到 Release：

```bash
git tag v1.0.0
git push origin v1.0.0
```

Action 自动构建 6 个平台版本（Windows x64/ARM64、Linux x64/ARM64、macOS x64/ARM64）并发布到 [Releases](https://github.com/geekwind/YuSwitch/releases)，每个资产附带 `.sha256` 校验文件，设置页「检查更新」可直接一键升级。

详细使用说明见 [USAGE.md](USAGE.md)。
