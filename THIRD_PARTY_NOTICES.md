# 第三方声明

## sing-box 1.14.4

「下载分流引擎.cmd」从 SagerNet 官方发布取得 Windows x64 sing-box 1.14.4，核对原始 ZIP 和可执行文件后，放入 `engine/AstralSplit.Router.exe`。文件字节保持上游原样。AstralSplit 通过命令行启动该独立进程，并为其提供本地配置。

- 上游权利声明和附加条款位于 `engine/LICENSE`。GPLv3 全文位于 `engine/COPYING`。
- 可从上游 `v1.14.4` 标签获取该二进制对应的源码和构建文件。 [对应源码](https://github.com/SagerNet/sing-box/tree/v1.14.4) · [源码归档](https://github.com/SagerNet/sing-box/archive/refs/tags/v1.14.4.tar.gz)
- 可在同一发布页获取原始二进制和上游 ZIP 的 SHA-256。 [官方发布](https://github.com/SagerNet/sing-box/releases/tag/v1.14.4)
- 安装后引擎的 SHA-256 为 `8f30682ee198855440d2a7fe070d5d3653793b2d01bb8c85eea88f93bbe7c8b9`。项目的个人非商业共享许可只适用于本项目有权授权的部分。sing-box 的权利遵循其上游 GPL。

## 平台组件与素材

WPF、.NET Framework、Direct3D、DirectComposition 和 CUDA 驱动由各自供应商提供，便携包仅加载系统或已安装驱动中的相应组件。运行时显示的应用图标从 Windows 中提取。

原生渲染库使用 Zig 0.14.1 的 Windows C++ 工具链构建。随附的 `THIRD_PARTY_LICENSES.txt` 保留 Zig、LLVM C++ 运行库与 MinGW-w64 的许可文本。

`source/StellarArt` 包含本项目使用的星空、天体和托盘素材。吸积盘与体积星云的部分素材可使用同目录下的 `RenderBlackHoleArt.py` 和 `RenderVolume.py` 重建。
