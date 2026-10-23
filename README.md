# OpenSeat 0.2 — 三工位技术验证版

目标：Windows 10 及以上 x64，RTX 3050 及以上显卡，3 台显示器、3 个鼠标，键盘按 3 套设计但允许不绑定。

**本包仍未实现完整 ASTER 多座位功能。它是原生 Windows 应用的源码及本机编译入口：真实识别物理键鼠，在三个测试窗口内分流输入。系统桌面、系统光标、音频和其他软件仍共享，不能让三人独立运行任意 Windows 软件。**

📖 **[中文使用说明书](docs/使用说明书.md)**：安装、绑定、测试、排错与 0.2.1 改进说明。

显卡型号只是目标范围，不是兼容保证。本程序不检查 GPU 性能，也不实现 GPU 隔离。图形输出以 Windows 当前实际启用的三个独立显示区域为准。

## 下载已编译版本

[下载 Windows x64 便携包](https://github.com/Ren-ZH-007/OpenSeat/releases/download/v0.2.0-preview.1/OpenSeat-0.2.0-preview.1-win-x64.zip) · [直接下载 EXE](https://github.com/Ren-ZH-007/OpenSeat/releases/download/v0.2.0-preview.1/OpenSeat.exe) · [发布说明](https://github.com/Ren-ZH-007/OpenSeat/releases/tag/v0.2.0-preview.1)

私有仓库需要登录有权限的 GitHub 账号。便携包解压后运行 `OpenSeat.exe`，无需 Python 或本机编译；需 .NET Framework 4.x。程序未进行代码签名。这是技术验证预发布版，模拟效果图尚未应用到实际界面。

便携包内的 README 为发布前源码说明快照，编译状态请以包内 `START-HERE.txt`、`native-test-results.txt` 及本页为准。

## 从源码编译（无需 Python）

1. 把整个 ZIP 解压到一个可写目录。
2. 在 Windows x64 上双击 `start.cmd`。
3. 脚本调用本机 .NET Framework 4.x 的 C# 编译器生成 `dist\OpenSeat.exe`，随后启动。
4. 若找不到编译器，按脚本提示安装/修复 .NET Framework 4.x。若编译报错，窗口会保留错误；本包未附预编译 EXE。
5. 构建成功后，可直接运行或复制 `dist\OpenSeat.exe`，不再依赖源码目录。目标电脑仍需 .NET Framework 4.x。

程序不要求管理员身份，不安装驱动，不修改安全策略、不联网激活。无应用登录、角色、付费授权、HWID 绑定。

## 三工位操作顺序

1. 连接三个显示器，在 Windows 显示设置中设为“扩展这些显示器”，不要使用复制模式。
2. 点“刷新设备 / 屏幕”，查看 Windows 屏幕名和 Raw Input 设备清单。
3. 点“显示屏编号”，每块屏幕会显示自己的名称，约 2.5 秒后关闭。
4. 为三个工位各选一个不同的屏幕。
5. 点工位 1 的“绑定鼠标”，等 1 秒后只移动工位 1 的鼠标。20 秒未操作则超时。依次绑定三个鼠标。
6. 如有三套键盘，分别点击“绑定键盘”，等 1 秒后只在对应键盘按一下 A。未绑定键盘也可以测试鼠标。
7. 点“保存配置”，再点“启动三屏输入测试”。缺少显示器、鼠标离线或重复分配会阻止启动。
8. 三个窗口会显示各自的蓝色测试指针、鼠标事件/点击计数和按键事件计数。分别操作三套设备，观察是否只有对应窗口变化。
9. 关闭任一测试窗口或在测试窗口按 Esc，会停止全部测试。控制台也有停止按钮。

蓝点是程序绘制的测试指针，不是系统鼠标光标。系统光标仍会受到所有鼠标影响；本程序不阻止原有 Windows 输入。长按键盘可能产生多个重复按键事件，计数不是唯一物理按压次数。

测试只处理本程序或其测试窗口处于前台时的输入。切换到其他应用后不增加测试计数。程序不会记录按键字符、输入文本或密码；诊断报告也不含输入内容。

输入设备连接变化或显示拓扑变化时停止测试，请手动刷新后重新核对。复合无线接收器可能把多个设备合并成一个 Raw Input 设备，这种情形不能凭本程序拆分。拔插到新端口可能改变路径，应重新绑定。绝对坐标输入映射到测试画布，不能代表触摸设备在桌面上的精确路由。

## 配置和诊断

- 默认配置：`%LOCALAPPDATA%\OpenSeat\three-seat.xml`。
- 固定三个工位；不同工位禁止绑定同一输入设备或显示器。
- 更改只在点击保存时落盘，关闭时提醒未保存修改。
- 导入先验证再替换内存配置；保存采用临时文件与原子替换。无效配置不会覆盖原文件。
- “导出诊断报告”记录操作系统信息、屏幕区域、设备路径与工位绑定。包含设备标识，不包含按键内容。
- 0.1 Python 配置器的 PnP ID 与本版 Raw Input ID 不同，不能混用。两个版本使用独立配置文件。

## 本版的实际实现与缺口

| 项目 | 实现状态 |
|---|---|
| 固定三工位原生 Windows Forms 界面 | Windows x64 编译通过，界面和真实硬件待验收 |
| 显示器枚举、屏幕编号 | Windows API 路径完成，待硬件验证 |
| 按实际设备输入绑定鼠标/键盘 | Raw Input 路径完成，待硬件验证 |
| 三屏应用内指针与计数分流 | 实现，不等于 Windows 桌面输入隔离 |
| 热插拔/显示变动时停止测试 | 实现，待实机验证 |
| 配置校验、保存、导入导出 | 实现，附 Windows 自检 |
| 完整独立桌面会话 | 未实现 |
| 对任意 Windows 应用的输入隔离 | 未实现 |
| GPU/音频/USB Hub/磁盘隔离 | 未实现 |
| 安装器、驱动、签名 | 未实现 |
| RTX 3050 及以上全型号兼容 | 未验证，不承诺 |

## 测试状态

已在 GitHub Windows runner 完成 x64 编译，14 项原生自检通过。[查看构建记录](https://github.com/Ren-ZH-007/OpenSeat/actions/runs/39891042142)。自检不等于三套硬件验收，也不代表已实现 Windows 桌面隔离。

附 `self-test.cmd`：在 Windows 本机构建后执行 20 项断言（0.2.1 起；0.2.0 预发布版为 14 项），覆盖三工位数量、设备/屏幕重复、空字段与名称长度、配置往返/原子替换、拒绝无效覆盖、Raw Input 鼠标/键盘包结构与截断处理、E0 按键识别、长按重复过滤、多键点击计数，以及 USB/蓝牙设备 VID/PID 解析。非 main 分支和 PR 会由 `.github/workflows/ci.yml` 在 Windows 上自动编译并自检。自检不依赖三屏硬件；通过后仍须按上面的实际操作步骤测试。

原 0.1 Python 配置模型的 6 项测试在制作环境通过；这些测试不验证 C# 程序，更不验证硬件隔离。原代码保留用于 PnP 扫描参考，入口为 `start-python-config.cmd`，说明为 `README-python-legacy.md`。

## 技术结构

- `native/OpenSeat.cs`：Windows Forms 界面、三工位配置、Raw Input 读取/绑定/分流、显示屏测试。
- `native/SelfTest.cs`：配置与数据包解析自检。
- `build-native.cmd`：本机构建；`start.cmd`：构建并启动；`self-test.cmd`：构建并自检。
- 当前输入仅路由给本程序绘制的测试画布，没有向其他应用注入按键或移动系统指针。
- 没有通过修改终端服务或复用 ASTER 驱动伪装成完整独立会话。

后续关键工作仍是实现经过验证的 Windows 多会话与输入/显示隔离引擎，而不是继续扩充配置界面。此技术验证版只能提供设备可区分性及显示拓扑的第一步证据。

参考接口：
- https://learn.microsoft.com/en-us/windows/win32/inputdev/raw-input
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-rawmouse
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-rawkeyboard
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdisplaymonitors
