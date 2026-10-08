# CodexReset for Windows

这是 [boyso/codex-reset](https://github.com/boyso/codex-reset) 的 Windows 版本。它是一个 Codex Desktop 辅助工具，用于在用量限制导致任务暂停后，让用户指定的对话在额度恢复时自动继续执行。

CodexReset 会监控本机 Codex 的用量状态。当某个已选中的对话因为 `usageLimitExceeded` 停止，而账号随后重新恢复可用时，它会恢复同一个 thread，并启动一个新的 turn，发送可配置的继续指令（默认：`继续`）。

> 本项目为非官方工具。它依赖 Codex 本地状态和本地 app-server 协议，这些内部实现可能随着 Codex 版本更新而变化。

## 当前状态

Windows 工程已经在 GitHub Actions 的 `windows-latest` 环境中完成编译和测试，并成功执行托盘程序与 CLI 的 `win-x64` self-contained 发布。

当前最主要的发布前验证项，是在真实 Windows Codex 环境中完成运行时兼容性验证。尤其需要在实际安装 Codex 的 Windows 机器上验证本地 app-server 的启动方式，以及以下 RPC 调用：

```text
initialize
initialized
account/rateLimits/read
thread/resume
turn/start
thread/turns/list
thread/unsubscribe
```

在完成上述本机验证前，不应把自动续作功能视为已经完成生产环境验证。

## 功能

- 自动解析 `CODEX_HOME` 并读取现有 Codex 本地状态。
- 通过本地 app-server 读取 5 小时用量窗口和重置时间。
- 找出最近一次失败 turn 中包含 `usageLimitExceeded` 的对话。
- 列出正常 Codex 对话，并过滤已归档线程和 sub-agent 线程。
- 由用户明确选择哪些对话允许自动继续。
- 检测账号从“用量受限”变为“重新可用”的状态变化。
- 使用 `thread/resume` 恢复指定线程，再通过 `turn/start` 启动新的继续 turn。
- 等待新 turn 执行结束后调用 `thread/unsubscribe`，释放线程占用。
- 提供 Windows 系统托盘程序，每 30 秒轮询一次状态。
- 提供诊断 CLI，用于验证本机协议兼容性。
- 支持 `codex://threads/<thread-id>` 深链打开指定对话。
- 包含 Windows 当前用户登录时自动启动的注册能力。
- 新发现的对话永远不会被自动勾选。

## 自动继续的工作原理

CodexReset **不会恢复被中断的那个原始 turn 本身**。它的行为与源项目的核心机制一致：

```text
原始任务
    |
    v
Codex turn 因 usageLimitExceeded 停止
    |
    v
CodexReset 检测到账户处于限额状态
    |
    v
额度恢复
    |
    v
thread/resume(原 thread id)
    |
    v
turn/start("继续")
    |
    v
Codex 读取原对话上下文并继续原任务
    |
    v
等待 turn 不再处于 inProgress / queued / pending
    |
    v
thread/unsubscribe
```

只有用户明确选中的对话才允许自动继续。

## 仓库结构

```text
src/
  CodexReset.Core/       Codex 状态、SQLite、RPC、额度恢复和自动续作逻辑
  CodexReset.Cli/        Windows 诊断工具和手动续作命令
  CodexReset.App/        Windows 系统托盘程序
tests/
  CodexReset.Core.Tests/ Core 行为与协议结构测试
docs/
  PARITY.md              功能对齐 / 发布验证清单
  superpowers/specs/     设计规范
  superpowers/plans/     实施计划
.github/workflows/
  ci.yml                 Windows 编译、测试和发布流程
```

## 环境要求

开发环境：

- Windows 10 或 Windows 11 x64
- .NET 8 SDK
- 如需验证真实运行协议，需要安装当前版本的 Codex Desktop / CLI

GitHub Actions 生成的是 self-contained 发布包，因此发布后的可执行文件通常不需要用户额外安装 .NET Runtime。但仍然必须安装 Codex，因为 CodexReset 使用现有 Codex 安装、本地账号状态和 `CODEX_HOME`。

## 编译与测试

在仓库根目录执行：

```powershell
dotnet restore CodexReset.sln
dotnet test CodexReset.sln -c Release
dotnet build CodexReset.sln -c Release
```

发布托盘程序：

```powershell
dotnet publish src/CodexReset.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/app
```

发布 CLI：

```powershell
dotnet publish src/CodexReset.Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/cli
```

GitHub Actions 会在 `windows-latest` 上运行测试，并执行上述两个 publish 操作，最终上传 `codex-reset-windows-x64` artifact。

## Codex Home

CodexReset 按以下顺序确定 Codex Home：

1. 如果显式设置了 `CODEX_HOME`，优先使用它。
2. 否则使用 `%USERPROFILE%\.codex`。

当前预期的本地文件包括：

```text
%CODEX_HOME%\state_5.sqlite
%CODEX_HOME%\thread_history_1.sqlite
%CODEX_HOME%\config.toml
```

这些数据库结构属于 Codex 内部实现，未来可能发生变化。

## CLI 使用说明

从源码运行：

```powershell
dotnet run --project src/CodexReset.Cli -- doctor
dotnet run --project src/CodexReset.Cli -- status
dotnet run --project src/CodexReset.Cli -- threads
dotnet run --project src/CodexReset.Cli -- paused
dotnet run --project src/CodexReset.Cli -- continue <thread-id> "继续"
```

也可以直接使用发布后的 `CodexReset.Cli.exe`。

### `doctor`

只读环境诊断命令，会输出：

- 解析后的 `CODEX_HOME`
- `state_5.sqlite` 是否存在
- `thread_history_1.sqlite` 是否存在
- `config.toml` 中是否启用了 `remote_control`
- 如果能够检测到，则显示 Codex CLI 路径

Windows 本机验证时建议先运行这个命令。

### `status`

启动一个独立的本地 Codex app-server，完成 Codex app-server 握手，然后调用：

```text
account/rateLimits/read
```

命令会打印返回的 JSON。这是 Windows 运行时兼容性验证中最重要的第一步。

### `threads`

只读命令。从本地 Codex 状态数据库中列出正常、未归档、非 sub-agent 的对话。

### `paused`

只读命令。列出最近一次失败 turn 中包含 `usageLimitExceeded` 的对话；如果能够解析到恢复提示，也会一并显示。

### `continue`

**该命令会向指定 Codex 对话写入新的 turn。**

```powershell
CodexReset.Cli.exe continue <thread-id> "继续"
```

它大致执行：

```text
thread/resume
turn/start
thread/turns/list
thread/unsubscribe
```

第一次验证 Windows 写入链路时，建议使用可随时丢弃的测试对话。

## 托盘程序

运行 `CodexReset.App.exe`。

托盘程序会：

- 连接本地 Codex app-server
- 每 30 秒轮询一次
- 显示当前 5 小时用量百分比与重置时间
- 列出本地对话
- 标记因用量限制暂停的对话
- 持久化用户明确选择的对话
- 检测“受限 → 恢复可用”的状态变化
- 自动继续符合条件且已选中的对话
- 通过 Codex 深链打开指定对话

新发现的对话 **不会** 自动加入自动续作列表。

## Windows 本机验证流程

将仓库拉取到实际运行 Codex 的 Windows 电脑后，建议按以下顺序验证。

### 1. 验证仓库本身

```powershell
git pull
dotnet test CodexReset.sln -c Release
```

预期结果：所有测试通过。

### 2. 验证本机 Codex 环境发现

```powershell
dotnet run --project src/CodexReset.Cli -- doctor
```

检查解析出来的 Codex Home 是否与当前安装的 Codex 实际使用目录一致，并确认预期数据库存在。

如果 Codex Home 位于其他位置：

```powershell
$env:CODEX_HOME = "D:\path\to\codex-home"
dotnet run --project src/CodexReset.Cli -- doctor
```

### 3. 验证只读 SQLite 读取

```powershell
dotnet run --project src/CodexReset.Cli -- threads
dotnet run --project src/CodexReset.Cli -- paused
```

确认输出的 thread ID 和标题与 Codex Desktop 中实际存在的对话一致。

### 4. 验证 Windows app-server

```powershell
dotnet run --project src/CodexReset.Cli -- status
```

成功标准：

- Codex app-server 进程能够正常启动
- WebSocket 连接成功
- `initialize` 成功
- `initialized` 被服务器接受
- `account/rateLimits/read` 返回有效的 rate-limit 对象

如果失败，请保留完整异常与输出。最可能需要适配的位置包括：

- Codex 可执行文件发现
- app-server 命令行参数
- WebSocket transport
- JSON-RPC schema

### 5. 在测试对话中验证续作链路

创建或选择一个发送 `继续` 不会产生风险的测试对话。通过 `threads` 获取 thread ID，然后执行：

```powershell
dotnet run --project src/CodexReset.Cli -- continue <thread-id> "继续"
```

成功标准：

- 恢复的是同一个 thread
- 对话中出现新的用户 turn：`继续`
- Codex 能够读取此前上下文并继续原任务
- 新 turn 执行结束后，Codex Desktop 仍然可以正常重新打开该对话

第一次写入验证不要使用重要对话。

### 6. 验证自动恢复行为

确认协议链路没有问题后：

1. 运行托盘程序。
2. 选择一个可丢弃的测试对话。
3. 保持 Auto continue 开启。
4. 等待一次真实的用量限制事件。
5. 在额度恢复期间保持 CodexReset 运行。
6. 确认账号从受限变为可用之后，已选中的 thread **只收到一次**继续 turn。

同时确认未选中的对话完全不受影响。

## 给本机 Codex 的验证任务

如果准备直接使用本机 Codex 来验证和修复这个 Windows 版本，可以让它读取本仓库并执行下面的任务：

```text
请在这台 Windows 机器上，基于实际安装的 Codex 环境完整验证 CodexReset for Windows。

1. 阅读 README.md、docs/PARITY.md、设计文档和实施计划。
2. 运行完整 Release 测试。
3. 运行 CodexReset.Cli doctor，验证 CODEX_HOME、数据库和 Codex CLI 发现逻辑。
4. 运行 threads 和 paused，并与本机真实 Codex 对话状态进行比对。
5. 运行 status，持续诊断并修复 Windows 下 app-server 发现、进程启动、
   WebSocket 握手或 JSON-RPC 协议兼容性问题，直到只读链路完全工作。
6. 只读链路成功后，使用一个可丢弃测试对话验证：
   continue <thread-id> "继续"。
7. 验证完整链路：thread/resume -> turn/start -> turn 完成轮询 -> thread/unsubscribe。
8. 编译并运行托盘程序，验证 30 秒轮询、对话选择持久化、limited -> recovered
   状态检测和重复续作抑制。
9. 每次修复后重新运行全部测试，并为发现的兼容性问题补充回归测试。
10. 只有在这台 Windows 机器上实际验证成功的功能，才允许更新 docs/PARITY.md 为完成。
11. 只要还有 release gate 未验证，就不要宣称项目已经完成。
```

这段任务说明的目的，是让本地 Codex 直接利用当前机器上真实安装环境中的事实，而不是猜测 Windows 平台内部实现。

## 安全与数据处理

CodexReset 按本地工具设计：

- 不要求用户输入或保存 OpenAI 密码。
- 不维护独立的 OpenAI 身份验证 token。
- 直接使用现有 Codex 安装及其 `CODEX_HOME`。
- thread / 数据库检查原则上为只读操作。
- 只有用户明确运行 `continue`，或者开启自动续作并选中对话后，才会启动新的 turn。
- 新发现的对话不会自动加入自动续作范围。

本项目会与 Codex 的内部本地状态和协议交互。如果对此有顾虑，请在运行前审查源码。

## 故障排查

### `doctor` 找不到数据库

确认当前安装的 Codex 实际使用哪个 `CODEX_HOME`。如果不是默认目录，请在运行 CodexReset 前显式设置 `CODEX_HOME`。

### 找不到 Codex CLI

将 `CODEX_CLI_PATH` 设置为 Windows 上实际 Codex 可执行文件或脚本路径，然后重新运行 `doctor` / `status`。

### `status` 无法启动 app-server

手动运行当前安装的 Codex 可执行文件并查看 app-server 的 help / 参数，再与 `AppServerManager` 中当前启动方式进行对比。

当前实现预期 Codex app-server 能够监听一个本地 WebSocket 地址。

### `initialize` 或其他 RPC 调用失败

Codex 可能修改了内部 app-server schema。应记录完整请求、响应和错误，然后修改隔离的 RPC 适配层，并增加回归测试，而不是在 UI 层添加临时绕过逻辑。

### thread 已经 resume，但 Codex Desktop 无法重新打开

确认 turn 完成监控能够进入最终状态，并且 `thread/unsubscribe` 调用成功。继续 turn 结束后必须释放 writer ownership。

### 自动继续触发了多次

这属于正确性问题。在重要对话上继续使用前，应检查：

- recovery transition 状态
- handled thread 状态
- 持久化逻辑
- 重复续作抑制

## 兼容性设计原则

源 macOS 项目依赖的是 Codex 本地内部行为，而不是稳定公开 API。因此 Windows 版本将以下模块分离：

- 本地状态发现
- app-server transport
- RPC 消息构造
- 额度恢复检测
- 自动续作编排
- Windows UI

当 Codex 内部实现发生变化时，应优先修复尽可能窄的兼容层，并增加对应回归测试。

## 功能对齐状态

详细功能和发布验证清单见 [docs/PARITY.md](docs/PARITY.md)。

目前 GitHub Actions 的 Windows 编译 / 测试 / 发布流程已经通过。当前最主要的剩余 release gate，是在真实 Windows Codex 环境中验证运行时协议兼容性。

## License

上游项目采用 MIT License。分发衍生版本时，请保留适用的上游署名和许可证要求。
