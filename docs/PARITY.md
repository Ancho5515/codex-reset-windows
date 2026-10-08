# Parity checklist

Reference: boyso/codex-reset

- [x] Resolve CODEX_HOME and local Codex databases
- [x] Detect usageLimitExceeded conversations
- [x] Browse non-archived, non-subagent conversations
- [x] Preserve meaningful original title after automatic Continue turns
- [x] Query account/rateLimits/read
- [x] Detect limited -> recovered transition
- [x] Explicit selected-conversation persistence
- [x] thread/resume + turn/start continuation
- [x] turn completion polling + thread/unsubscribe
- [x] CLI doctor/status/threads/paused/continue
- [x] Windows tray app
- [x] Windows 主窗口：启动显示、关闭收起、托盘重新打开
- [x] 30-second polling
- [x] 5-hour usage/reset display
- [x] Deep-link to Codex thread
- [x] Windows sign-in registration primitive
- [x] 已在本机安装的 Codex 中验证 Windows app-server 启动和用量读取
- [x] Confirm CI build/test artifact on GitHub Actions
- [x] 主窗口显示每周用量和恢复倒计时
- [ ] Reset-history UI
- [x] 主窗口显示账户计划和点数余额
- [ ] Fully verified UI Automation text-entry fallback

2026-10-08 本机验证通过：12 项核心测试、发布程序可见窗口检查、对话筛选与选择保存、窗口收起与重新显示、真实 Codex 连接、30 秒刷新，以及退出时释放本程序启动的 app-server。

真实对话续作链路和额度恢复后的自动续作仍需单独验证。未勾选的界面功能也需要继续对齐。
