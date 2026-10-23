# OpenSeat 0.4 — 原生会话游戏启动器开发预览版

目标环境：Windows 10 及以上 x64，.NET Framework 4.x。

**当前不提供独立桌面或物理键鼠隔离。** 三人分别操作游戏需要另行安装、授权并配置外部多工位引擎。OpenSeat 仅在已经存在的 Windows 用户会话内启动和管理游戏；打开外部控制台不等于完成引擎接入。

## 下载

[完整 Windows ZIP](https://github.com/Ren-ZH-007/OpenSeat/releases/download/v0.4.0-preview.1/OpenSeat-0.4.0-preview.1-win-x64.zip) · [发布页面](https://github.com/Ren-ZH-007/OpenSeat/releases/tag/v0.4.0-preview.1)

解压后保留 OpenSeat.exe 和 OpenSeat.Gaming.exe 在同一目录，运行 OpenSeat.exe，点击“原生工位游戏…”。也可以在每个已经建立的独立用户会话中直接运行 OpenSeat.Gaming.exe。

## 已实现

- 原有三工位设备识别、应用内输入测试、屏幕只读监控。
- 原有 VMware 虚拟机配置助手；USB 直通仍需实机确认。
- 游戏启动器包含 LOL、三角洲、CF 三个可配置 EXE 与参数的启动项。
- 当前会话启动、启动进程重复保护、退出状态追踪、手动窗口定位。
- Windows 会话枚举、按用户保存配置、错误日志。
- 不完整 Windows 路径校验，损坏配置保留，XML 外部实体禁用。
- 无 OpenSeat 登录、激活或角色权限系统。

## 尚未实现或验证

- 原生多工位引擎、独立桌面创建、物理键鼠独占和 GPU 资源分配。
- ASTER SDK 集成、跨工位统一启动、自动恢复。
- 三套实机设备验收、三款游戏对局及反作弊兼容性。
- 启动器交接给其他进程后的游戏状态：显示未知，不误报游戏已经结束。
- RTX 3050 及以上不能作为通用兼容或帧率保证；需要具体配置实测。

## 构建与测试

运行 `build-native.cmd`，生成两个 EXE。运行 `build-gaming.cmd` 仅构建游戏启动器。
测试源码为 native/SelfTest.cs 和 native/GamingTests.cs；Windows CI 执行 28 项原有自检及 31 项启动器测试，包含进程与窗口生命周期测试。
发布流水线重新编译、执行上述测试，并在 ZIP 中附测试日志及 BUILD-INFO.json。

[启动器技术说明](native/GAMING.md)。EXE 未签名；Windows 系统权限仍然适用。
