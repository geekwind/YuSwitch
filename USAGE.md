# YuSwitch 使用说明

## 快速开始

### 1. 下载

从 [Releases](https://github.com/geekwind/easy-ai-gateway/releases) 页面下载对应平台的版本：

| 平台 | 文件 |
|---|---|
| Windows x64 | `simpleone-api-win-x64-*.zip` |
| Windows ARM64 | `simpleone-api-win-arm64-*.zip` |
| Linux x64 | `simpleone-api-linux-x64-*.tar.gz` |
| Linux ARM64 | `simpleone-api-linux-arm64-*.tar.gz` |
| macOS x64 | `simpleone-api-osx-x64-*.tar.gz` |

> **无需安装 .NET 运行时**，程序已自包含所有依赖，单文件即可运行。

### 2. 运行

**Windows：**
```bash
# 解压后双击 YuSwitch.exe，或命令行运行：
YuSwitch.exe
```

**Linux / macOS：**
```bash
# 解压
tar xzf simpleone-api-linux-x64-*.tar.gz

# 赋予执行权限
chmod +x YuSwitch

# 运行
./YuSwitch
```

### 3. 配置

启动后访问 **http://localhost:5078** 进入管理界面：

1. 首页点击「**初始化示例配置**」—— 一键配置 Your Provider 上游 + 10 个模型 + 本地 API Key
2. 或手动添加：进入「服务管理」配置你的上游服务

### 4. 调用

**OpenAI 规范：**
```bash
curl -X POST http://localhost:5078/v1/chat/completions \
  -H "Authorization: Bearer sk-yuswitch-local" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "your-model-alias",
    "messages": [{"role": "user", "content": "你好"}],
    "max_tokens": 100
  }'
```

**Anthropic 规范（Claude 客户端）：**
```bash
curl -X POST http://localhost:5078/v1/messages \
  -H "x-api-key: sk-yuswitch-local" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "your-model-alias",
    "max_tokens": 100,
    "messages": [{"role": "user", "content": "你好"}]
  }'
```

**在客户端中使用（NextChat / LobeChat / 沉浸式翻译等）：**
- API Base URL: `http://localhost:5078/v1`
- API Key: `sk-yuswitch-local`
- 模型: `your-model-alias` / `your-model` / `DeepSeek-R1` 等

---

## 配置指南

### 添加上游服务

管理界面 → 服务管理 → 新建服务：

| 字段 | 说明 | 示例 |
|---|---|---|
| 名称 | 服务显示名 | 我的 DeepSeek |
| Provider 类型 | 上游协议 | `openai`（OpenAI兼容）/ `claude`（Anthropic原生） |
| 上游地址 | API base URL | `https://api.deepseek.com/v1` |
| 凭据 (JSON) | API Key | `{"api_key":"sk-xxx"}` |
| 权重 | 负载均衡权重 | 1（越大分配越多请求） |
| 优先级 | failover 优先级 | 0（越小越优先） |

### 模型映射

为服务添加模型时支持别名映射：

| 字段 | 说明 | 示例 |
|---|---|---|
| 模型名称 | 客户端请求的名字 | `your-model-alias` |
| 上游真实名 | 发给上游的真实名 | `your-reasoning-model`（留空=同模型名） |
| 别名 | 额外匹配名 | `your-model-alias,glm5.2`（逗号分隔） |

**场景**：同一模型不同服务商命名不同
- 服务 A: 模型名=`your-model-alias`，上游真实名=`your-reasoning-model`
- 服务 B: 模型名=`your-model-alias`，上游真实名=`glm-5.2-air`
- 客户端请求 `your-model-alias` → 两个服务都成候选，负载均衡，各自映射

### 负载均衡与 failover

- **加权轮询**：同优先级服务按 `weight` 严格轮询（等权 ABAB）
- **优先级**：`priority` 低值优先，失败后自动切换到下一个
- **Sticky Session**：仅在显式 `X-Session-Id` 请求头时做会话亲和（`user` 字段不再触发）

### API Key 管理

管理界面 → API Key → 新建：
- 自动生成或自定义 key
- 配置允许模型（`*` 或逗号分隔列表）
- 无任何 key 时为开放模式（不鉴权）

---

## 运行配置

### 命令行参数

```bash
# 指定端口
YuSwitch --urls "http://localhost:9090"

# Windows 上强制无窗口运行（服务器/计划任务场景）
YuSwitch --headless --urls "http://localhost:9090"

# 指定配置文件
YuSwitch --urls "http://localhost:9090" --Configuration:Path="config.json"
```

### appsettings.json（可选）

程序无需配置文件即可运行（使用默认值）。如需自定义，创建 `appsettings.json`：

```json
{
  "Urls": "http://localhost:5078",
  "Database": {
    "Path": "simpleone.db"
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information"
    }
  }
}
```

### 数据存储

- 默认使用 SQLite（`simpleone.db`，程序同目录自动创建）
- 生产环境可改 PostgreSQL（需修改源码连接串重新编译）
- 配置通过管理界面或 Admin API 操作，持久化到数据库

---

## 部署

### Docker

```dockerfile
FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY publish/YuSwitch .
EXPOSE 5078
ENTRYPOINT ["./YuSwitch", "--urls", "http://0.0.0.0:5078"]
```

```bash
docker build -t simpleone-api .
docker run -d -p 5078:5078 -v $(pwd)/data:/app/data simpleone-api
```

### systemd (Linux)

```ini
[Unit]
Description=YuSwitch AI Gateway
After=network.target

[Service]
Type=simple
WorkingDirectory=/opt/simpleone-api
ExecStart=/opt/simpleone-api/YuSwitch --urls http://0.0.0.0:5078
Restart=always
RestartSec=10

[Install]
WantedBy=multi-user.target
```

---

## 管理 API

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/admin/services` | 获取服务列表 |
| `POST` | `/admin/services` | 创建服务 |
| `PUT` | `/admin/services/{id}` | 更新服务 |
| `DELETE` | `/admin/services/{id}` | 删除服务 |
| `POST` | `/admin/services/{id}/test` | 测试连通性 |
| `GET` | `/admin/services/{id}/models` | 获取模型列表 |
| `POST` | `/admin/services/{id}/models` | 添加模型 |
| `DELETE` | `/admin/models/{id}` | 删除模型 |
| `GET` | `/admin/apikeys` | API Key 列表 |
| `POST` | `/admin/apikeys` | 创建 Key |
| `PUT` | `/admin/apikeys/{id}` | 编辑 Key（名称/白名单/启停） |
| `DELETE` | `/admin/apikeys/{id}` | 删除 Key |
| `GET` | `/admin/usage` | 用量统计 |
| `GET` | `/admin/call-logs?limit=100&offset=0` | 调用日志（分页，返回 `{total, items}`） |
| `GET` | `/admin/export` | 导出全部配置（JSON，含密钥明文） |
| `POST` | `/admin/import` | 导入配置（增量合并） |
| `GET` | `/admin/dispatch-trace` | 调度追踪 |
| `POST` | `/admin/seed` | 初始化示例配置 |

> 管理端点默认仅本机 (localhost) 可访问。在「设置」中配置管理令牌 (Admin Token) 后，
> 远程访问需携带 `X-Admin-Token: <令牌>` 请求头（或首次访问 URL 附加 `?admin_token=<令牌>`）。

---

## 开发者

### 从源码构建

```bash
# 安装 .NET 8 SDK
git clone https://github.com/geekwind/easy-ai-gateway.git
cd easy-ai-gateway

# 开发运行（多目标项目需指定框架：net8.0-windows 带桌面 GUI，net8.0 为 headless）
dotnet run -f net8.0-windows
dotnet run -f net8.0 -- --urls "http://localhost:5078"

# 发布单文件（win-* 用 -f net8.0-windows，linux/osx 用 -f net8.0）
dotnet publish -c Release -f net8.0-windows -r win-x64 --self-contained \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -o ./publish
```

### 发布版本（GitHub Release）

```bash
# 打 tag 触发自动构建
git tag v1.0.0
git push origin v1.0.0
```

GitHub Action 会自动构建 5 个平台版本并发布到 Release。
