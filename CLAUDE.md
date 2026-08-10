# CLAUDE.md — YuSwitch（禹枢）

面向后续 Claude Code 会话与开发者的项目指南。阅读本文件前建议先看 `README.md`（用户文档）与 `docs/architecture.png`、`docs/flow.png`（架构/流程图）。

## 项目概述

YuSwitch（禹枢）是一个 .NET 8 的 AI 模型网关：统一接入、切换与治理多种 AI 模型。
- **入站双协议**：OpenAI（`/v1/*`）、Anthropic（`/v1/messages`）、Responses API
- **出站多上游**：OpenAI 兼容 + Anthropic/Claude，支持负载均衡、按优先级 failover、Sticky Session、模型别名映射
- **管理界面**：Blazor Server（嵌入单文件 exe），含服务管理、API Key、用量统计、调用日志、路由追踪、实时订阅
- **Windows 桌面壳**：WinForms + WebView2（仅 `net8.0-windows`），Linux/macOS 为 headless 服务

仓库：`github.com/geekwind/YuSwitch`（曾用名 easy-ai-gateway，重命名后由 GitHub 301 跳转，代码里不要再用旧名）。

## 技术栈与关键事实

- **多目标框架**：`net8.0`（headless）+ `net8.0-windows`（桌面 GUI）。`csproj` 是双目标，**publish 必须 `-f` 指定 TFM**；`net8.0-windows` 下 `OutputType` 为 WinExe（双击无黑框）。
- **单文件发布**：`PublishSingleFile=true` 自包含。wwwroot + 编译生成的 scoped CSS（`YuSwitch.styles.css`）都通过 build manifest 内嵌；运行时若无磁盘 wwwroot，`Program.cs` 回退到 `ManifestEmbeddedFileProvider`。因此**单文件 exe 可以脱离目录单独拷贝运行**。
- **数据存储**：SQLite（`simpleone.db`，默认 `Database:Path`，已 gitignore）。配置（服务/模型/Key/设置）存在 DB 而非 JSON，由 `ConfigService` 持有内存快照，写入后 `ReloadAsync()` 刷新，热路径不查库。
- **CWD 锚定**：`Program.cs` 开头把工作目录 pin 到 exe 所在目录，确保 `logs/`、`simpleone.db` 在单文件临时解压目录下也能持久化。
- **HTTP 客户端**：`"openai"` 命名客户端**无超时、无透明重试**。超时/熔断/failover 完全由 `GatewayService` 决定（每服务超时）；不要在 `AddHttpClient` 层加 Polly 重试（会吞掉非幂等 POST 且延迟 failover）。
- **Provider 注册**：`Program.cs` 里 `ProviderRegistry` 按 key 注册工厂：`openai/deepseek/zhipu/groq/upstream → OpenAIProvider`，`claude/anthropic → ClaudeProvider`。
- **鉴权中间件**：`AdminAuthMiddleware`（回环免认证 / 设置 AdminToken 后保护 `/admin`）→ `ApiKeyAuthMiddleware`（网关 API 用 `sk-` Key）。**CORS 必须在鉴权之前**，否则跨域预检 OPTIONS 被 401 挡住（预检无凭据永远过不了 Key 校验）。
- **桌面模式判定**：`Program.cs` 在 `CreateBuilder` 前从原始 args 剥离 `--headless`/`--no-gui` 与 `--restart-of <pid>`（裸开关会被 ASP.NET 命令行配置源吞掉下一个参数导致 FormatException）。GUI 仅当 `IsWindows && UserInteractive && !forceHeadless`。`--restart-of` 是新进程等待旧进程退出再启动，用于设置页「一键重启」。

## 目录结构

| 路径 | 职责 |
|---|---|
| `Program.cs` | 启动引导、DI、中间件管线、监听地址解析、桌面/headless 分叉 |
| `Endpoints/` | HTTP 端点：`OpenAiEndpoints`、`AnthropicEndpoints`、`ResponsesEndpoints`、`AdminEndpoints`（Blazor UI 调用的管理 API）、`UpstreamResults` |
| `Gateway/` | `GatewayService`（路由/failover/熔断/Sticky）、`SseWriter`（SSE 转发）、`ServiceRuntimeState` |
| `Providers/` | `OpenAI/OpenAIProvider`、`Claude/ClaudeProvider`、`ProviderRegistry`、`IProvider` |
| `Middleware/` | `AdminAuthMiddleware`、`ApiKeyAuthMiddleware` |
| `Services/` | `ConfigService`（配置快照）、`AppSettingsService`（运行设置/Key）、`AppVersionService`（版本）、`UsageService`、`ApiKeyLimiter`、`HealthProbeService`（后台健康探测）、`RealtimeNotificationService`、`ToastService`、`UiFormat` |
| `Data/` | EF Core `AppDbContext` + `Entities/`（Service/Model/ApiKey/Setting/UsageLog） |
| `Components/` | Blazor Server 管理界面（`Layout/`、`Pages/`、`Shared/`、scoped CSS `*.razor.css`） |
| `Gui/` | `MainForm.cs`（仅 net8.0-windows 编译；WinForms + WebView2 窗口、托盘、单实例逻辑） |
| `wwwroot/` | 静态资源：`app.css`（全局主题/暗色模式变量）、`js/eg.js`（标题/favicon/主题/logo 注入） |
| `tools/` | `gen_icon.py`：logo.png → icon.ico（16–256 多尺寸）+ favicon.png |
| `docs/` | 架构图 `architecture.png`、流程图 `flow.png` |
| `.github/workflows/release.yml` | 打 tag 自动构建 6 平台单文件并创建 GitHub Release |

## 版本管理规范

- **版本来源**：`YuSwitch.csproj` 的 `<Version>`（当前 `0.0.1`）是本地构建的版本号；CI 发布时用 tag 覆盖：`-p:Version=$BINVERSION`。改版本先改 csproj，保持与 tag 一致。
- **展示位置**：`/health`（`Program.cs` 读程序集版本）+ 侧边栏底部 `NavMenu.razor` 的 `YuSwitch v@(AppInfo.Version)`（由 `AppVersionService` 提供，取 Major.Minor.Build）。
- **Tag 格式**：语义化版本 `vX.Y.Z`，如 `v0.0.1`。只有 `v*` tag 推送会触发完整发布；`workflow_dispatch` 只构建上传 artifact 不建 Release。
- **切版本流程**：更新 csproj `<Version>` → 提交 → `git tag vX.Y.Z` → `git push origin vX.Y.Z` → CI 自动构建 + 发布。多平台产物见 release.yml matrix（win/linux/osx × x64/arm64；Windows 带 GUI，其余 headless）。
- **注意**：`Program.cs` 单文件模式下 `Assembly.Location` 为空（IL3000 警告）是**预期行为**——重启逻辑已用 `isDotnetHost` 判断绕过，不要用 `Assembly.Location` 解析路径，用 `AppContext.BaseDirectory`。

## Git 规范

- **分支**：单分支 `main`，无 PR 流程。直接 commit + push。
- **提交信息**：Conventional Commits 格式，`type(scope): 英文描述`，首行 ≤ 72 字符。Claude 辅助的提交追加 `Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>` 结尾。
  - `feat(ui):` / `feat(gateway):` / `fix(gateway):` / `style(ui):` / `docs:` / `refactor:` / `chore:`
- **历史已经重置过一次**（初始提交 `4aca4c6` + 文档修正 `bb37c77`），不要再无谓强推；日常更新用普通 push。只有在用户明确要求时才 `git push --force`。
- **`simpleone.db`、`bin/`、`obj/`、`publish*/`、`dist/`、`logs/`、`*.db`** 均已被 .gitignore 排除，属运行时产物，不入库。

## 构建与运行

```bash
# 本地开发（Windows 桌面 GUI）
dotnet run                                    # 等效 -f net8.0-windows，含 WinForms/WebView2 窗口
dotnet run -- --headless                      # Windows 上强制 headless 服务

# 纯 headless 构建 / 运行
dotnet build -f net8.0
dotnet run -f net8.0 --urls http://localhost:5078

# 单文件发布（与 release.yml 参数一致；-f 必填）
dotnet publish YuSwitch.csproj -c Release -f net8.0-windows -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=embedded \
  -p:Version=0.0.1 -o ./publish
```

- 默认监听 `localhost:5078`（`AppSettingsService` 常量；可在「设置 → 监听/网络」改，改后需重启）。
- 无测试项目。改代码后至少跑 `dotnet build` 全 TFM 编译 + 启动冒烟（`/health` 应返回 `app=YuSwitch` + 正确版本）。
- UI 改动：启动后浏览器实际点一遍再交付（组件交互、暗色模式、`wwwroot/app.css` 主题变量 `--eg-*`）。

## 图标 / 品牌

- 源图 `logo.png`（用户提供的真实 logo；缺省时 `tools/gen_icon.py` 生成 "YS" 占位）。
- `tools/gen_icon.py` 把 `logo.png` → `icon.ico`（16–256 多尺寸，保持宽高比）+ `wwwroot/favicon.png`。改 logo 后：覆盖 `logo.png` → 跑脚本 → 重新构建即可，**无需改 C#/csproj**。
- `icon.ico` 同时被 `<ApplicationIcon>`（exe 图标）与 `<EmbeddedResource>`（运行时窗口/托盘图标）引用；`Gui/MainForm.cs` 的 `LoadAppIcon()` 从内嵌资源加载。

## 容易踩的坑

1. **Razor 里写版本号**：`v@expr` 会被当作邮箱地址字面量，必须写 `v@(expr)`。
2. **publish 不指定 `-f`**：双目标 csproj 直接 `dotnet publish` 会报错，必须 `-f net8.0` 或 `-f net8.0-windows`。
3. **命令行为参数**：`--headless`、`--restart-of <pid>` 必须在 `CreateBuilder` 前从 args 剥离（见 Program.cs 开头注释）。
4. **DB 迁移**：`EnsureCreated` 只建全新库的 schema；对已存在库，新增列用 `AddColumnIfMissingAsync` 幂等迁移（Program.cs 尾部）。
5. **不要给 "openai" HttpClient 加超时/重试**：超时与 failover 由 GatewayService 统一决策，见上文。
