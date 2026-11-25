# AstralSplit 星云分流

AstralSplit 是一款 Windows 按应用分流工具。勾选应用的新建公网连接交给所选 VPN 网卡，其余已识别应用直连。支持手动选择 VPN 虚拟网卡，也可用「星云自动识别」预设。程序可驻留托盘，并提供近期连接、出口检测和限时试用。

本项目由社区独立维护。使用分流功能需要先连接星云加速器或其他提供 Windows 虚拟网卡与公网路由的 VPN。

## 下载与使用

当前正式版为 **2.1.0**，首个正式版本为 2.0.0。请从 [最新正式版](https://github.com/Gryphus-Triune/AstralSplit/releases/latest) 下载 Windows x64 便携包并完整解压。首次使用分流时，双击「下载分流引擎.cmd」，等待脚本取得并核对官方 sing-box，再运行 `AstralSplit.exe`。程序启动时会申请本次运行的管理员授权。「预览界面.cmd」可以先查看界面，预览结束后可从托盘退出。

1. 连接 VPN，并开启其 TUN 或全局路由。星云用户可沿用默认的「星云自动识别」。使用其他 VPN 时，点击状态卡的「VPN 网卡」选择对应网卡。
2. 勾选要经 VPN 的应用，然后开启分流总开关。
3. 已打开的连接可以在原应用中重连。退出前关闭分流，等状态提示恢复完成。

支持范围、兼容绘制入口、旧版迁移和恢复方式见[使用说明](使用说明.md)。

## 硬件与系统兼容性

下载包面向 **Windows x64 电脑**。Intel 和 AMD 的 64 位处理器均可使用这个架构的程序。分流功能对显卡品牌没有要求，核显电脑也可以使用。

| 你的设备 | 2.1.0 的适用情况 |
| --- | --- |
| Windows 11 x64 台式机或笔记本 | 本版实测环境为 Windows 11 24H2、.NET Framework 4.8.1、Core i7-14700KF 和 GeForce RTX 5080。这是一套验证配置，处理器和显卡型号均不构成最低门槛。 |
| Intel、AMD 或 NVIDIA 显卡，包括核显 | 星空、黑洞与液态玻璃使用 Direct3D 11 和 DirectComposition。程序按显示器选择显卡，显卡和驱动支持相应功能时可走原生加速。Intel 和 AMD 显卡的适用性依据源码判断，此次未做真机验证。 |
| 老显卡或仅能软件绘制的环境 | 可用「兼容绘制.cmd」启动 WPF 界面。缺少硬件着色器支持时，部分动态光效会采用静帧或简化绘制。分流本身由独立的网络引擎处理。 |
| Windows 10 x64 | 具备所用图形接口，可在装有 .NET Framework 4.8 系列运行时的系统上尝试。本次发布未在 Windows 10 真机上完成验证。 |
| Windows on Arm、32 位 Windows、macOS 或 Linux | 本版只提供 Windows x64 包。Windows 11 on Arm 可模拟 x64 应用，但 VPN 内核驱动必须支持 ARM64，整套分流尚未验证。其余平台没有对应的原生发行包。 |

**网络条件。** 普通以太网卡或 Wi-Fi 即可。实际分流还需要已连接的 VPN 提供可用的虚拟网卡和公网路由，并且原网络仍能独立直连。启动时需要 Windows 管理员授权。

**CUDA 与高刷。** 支持的 NVIDIA 显卡可用 CUDA 生成银河底图。CUDA 不可用时，程序自动改用 CPU 生成底图，其余支持 Direct3D 的特效仍可用显卡加速。运行便携包无需安装 CUDA Toolkit，也不需要 RTX 光追单元或 AI 加速器。普通显示器即可使用。4K / 240 Hz 是测试场景，实际帧率随分辨率、驱动和特效负载变化。目前没有通过跨机型测试确定最低内存、显存或保证特定帧率的显卡型号。

图形设备的选择与回退可在[原生渲染代码](source/WindowScene.inl)和[银河底图生成代码](source/SkyAtlas.cs)中核对。系统兼容资料见微软的 [WPF 图形分级](https://learn.microsoft.com/dotnet/desktop/wpf/advanced/graphics-rendering-tiers)与 [Windows on Arm 常见问题](https://learn.microsoft.com/windows/arm/faq)。

## 分流范围

- 分流规则按可执行文件路径保存。依赖共享系统服务或自行绑定网络接口的应用，连接的实际出口需要单独核实。
- 通用模式按网卡分别验证 TCP 和 UDP。只通内网的 VPN、没有虚拟网卡的代理，或阻断独立直连的断线保护，可能无法配合分流。
- 星云内部的节点规则仍会决定最终处理路径。连接详情显示本机引擎观测到的出站，已建立连接的出口可能沿用旧规则。
- 连接摘要在本机内存中处理。通用模式启动时，会对各出口发出 TCP 握手和 UDP 探测。UDP 使用 STUN 绑定请求或根域 NS 查询。点击并确认「检测公网出口」才会向 ipify 发起一次 HTTPS 查询。提交问题时请先遮盖应用路径、IP 地址和会话记录。

## 流量里程碑

在普通主窗口按 **Home** 可以预览双尾彗星、脉冲星、电离星云、超新星遗迹和钻石环日冕。预览独立于实际流量计数。累计分流达到 5 GB 时，超新星冲击波会扫过状态卡和应用列表，在全屏星海观测中也会扩散到屏幕边缘。

## 从源码构建

构建需要 Windows x64、Visual Studio 2022 或其 Build Tools 的 Roslyn、.NET Framework 4.8 开发包、Zig 0.14.1、Python 3.11 及以上和 CUDA Toolkit 12.9 的 NVRTC。可用 PowerShell 从仓库根目录运行：

```powershell
.\Build-Release.ps1
```

Zig 若未加入 PATH，可用 `-Zig` 指定其可执行文件。NVRTC 安装在其他目录时，可用 `-CudaBin` 指定其 `bin` 目录。脚本在 `dist` 内构建 WPF 程序、原生渲染库和 CUDA 模块，输出 Windows x64 便携 ZIP。CUDA Toolkit 用于重新构建嵌入的模块，普通用户运行便携包无需安装构建工具。

## 许可

本项目公开源码，允许个人非商业使用和修改。你可以免费分享原版和修改版。分发修改版时，请按同一许可提供完整对应源码。禁止未经授权出售或商业分发。完整条款见 [LICENSE](LICENSE)。sing-box 按其独立的 GPL 条款提供，详见[第三方声明](THIRD_PARTY_NOTICES.md)。
