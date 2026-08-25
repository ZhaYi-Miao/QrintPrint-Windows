# 热印 ThermoPrint - 错题小印 Windows 版

基于 Qring 私有蓝牙协议的热敏打印机桌面客户端，支持文本、图片、条码、Word 文档、LaTeX 公式、函数图像及自定义画布打印。

## 功能

| 模块 | 说明 |
|------|------|
| **文本打印** | 支持字体大小/样式/行距/边距调节，LaTeX 公式识别与渲染 |
| **图片打印** | 支持多种抖动算法（Floyd-Steinberg / Bayer / 阈值），实时预览 |
| **条码打印** | 支持 QR Code、Code128、EAN-13、DataMatrix 等 20+ 种码制 |
| **Word 打印** | 解析 .docx 文档，支持段落文本与 LaTeX 公式混合渲染 |
| **自定义画布** | 自由添加文字/图片/公式/条码元素，拖拽定位，所见即所得 |
| **历史记录** | 自动保存打印记录，支持缩略图预览与一键重打 |
| **模版管理** | 保存/加载常用打印模版，快速复用 |
| **蓝牙管理** | 自动扫描 小印 打印机，支持自动重连、状态轮询、电量/纸张监测 |

## 系统要求

- Windows 10 / 11 (64-bit)
- 蓝牙适配器（经典蓝牙 SPP 协议）
- 小印系列热敏打印机

## 快速开始

### 方式一：自包含单文件版（推荐）

从 [Releases](../../releases) 下载 **自包含单文件版**（单个 EXE），双击即可运行，无需安装任何环境。

### 方式二：自包含多文件版

下载 **自包含多文件版**，解压到任意目录，运行 `QrintPrint.exe`。

### 方式三：框架依赖单文件版（最小体积）

下载 **框架依赖单文件版**（仅一个 EXE），需先安装 [.NET 8.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)。

## 编译与发布

### 开发环境

```bash
# 需要 .NET 8.0 SDK
dotnet --version

# 还原依赖
cd src/QrintPrint
dotnet restore

# 调试运行
dotnet run
```

### 一键发布四种版本

```powershell
# 在项目根目录执行
.\publish-all.ps1
```

脚本会生成以下四个版本并打包为 zip，输出到 `release/` 目录：

| 版本 | 大小 | 说明 |
|------|------|------|
| 框架依赖单文件版 | ~3 MB | 需安装 .NET 8，仅一个 EXE |
| 框架依赖多文件版 | ~3 MB | 需安装 .NET 8，多个 DLL |
| 自包含多文件版 | ~70 MB | 解压即用，完整多语言 |
| 自包含单文件版 | ~66 MB | 单 EXE，无需安装 |

## 项目结构

```
print/
├── src/QrintPrint/          # 主项目
│   ├── Bluetooth/           # 蓝牙通信层
│   │   ├── PrinterConnection.cs   # 连接管理（单例）
│   │   ├── QringProtocol.cs       # 协议封装
│   │   ├── RasterEncoder.cs       # 图像/文本 → 光栅字节
│   │   ├── Compositor.cs          # 画布合成
│   │   ├── Dither.cs              # 抖动算法
│   │   └── PrinterDiscovery.cs    # 设备扫描
│   ├── Models/              # 数据模型
│   │   ├── FormulaRenderer.cs     # LaTeX 公式渲染
│   │   ├── PrinterStatus.cs       # 打印机状态
│   │   ├── CanvasModel.cs         # 自定义画布文档
│   │   ├── HistoryRecord.cs       # 历史记录
│   │   └── Template.cs            # 模版
│   ├── Views/               # UI 层
│   │   ├── MainWindow.xaml        # 主窗口
│   │   ├── DevicePickerDialog.xaml # 设备选择
│   │   └── Pages/                 # 各功能页面
│   └── QrintPrint.csproj
├── publish-all.ps1          # 一键发布脚本
── README.md
```

## 技术栈

- **框架**: .NET 8.0 + WPF
- **蓝牙**: 32feet.NET (InTheHand.Net.Bluetooth)
- **公式渲染**: WpfMath (XamlMath)
- **条码生成**: ZXing.Net
- **图像处理**: SixLabors.ImageSharp
- **Word 解析**: DocumentFormat.OpenXml
- **本地存储**: System.Text.Json

## 作者

ZhaYi & DeepSeek

## 致谢

本项目站在众多开源前辈的肩膀上 —— **没有这些项目，就没有热印 ThermoPrint**。在此向它们致以最诚挚的敬意：

- **[Thisko/QrintPrint](https://github.com/Thisko/QrintPrint)** —— **错题小印开源的先行者与开创者**
- [lztttt/QrintPrint-Android](https://github.com/lztttt/QrintPrint-Android) —— Android 原生版
- [snowboys/QrintPrint-Windows](https://github.com/snowboys/QrintPrint-Windows) —— Windows 端参考实现
- [yiran168/suda-win-web](https://github.com/yiran168/suda-win-web) —— 桌面/网页版「素打」

感谢所有为错题小印生态做出贡献的开源开发者 ❤️

## 相关仓库

- 网页打印控制台（浏览器远程打印面板）：[QrintPrint-Web-Console](https://github.com/ZhaYi-Miao/QrintPrint-Web-Console)
- HTTP API 接口文档见本仓库 `API.md`

## License

GPL-v3.0
