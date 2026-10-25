# TinyCAD Viewer

**简单、快速、支持 GPU 加速的开源二维 CAD 查看与测量工具。**

如今，工业设计越来越多地使用 3D 建模软件，但大量工程图纸仍以 DWG / DXF 格式保存和流转。工程人员很多时候只需要查看图纸、核对尺寸，并不需要完整的 CAD 编辑功能，也不希望为此承担 AutoCAD 等专业软件的授权成本。

TinyCAD Viewer 为这样的日常工作而开发：专注于看图和简单尺寸测量，通过 GPU 加速改善复杂图纸的缩放与平移体验，让普通电脑、包括配备集成显卡的设备，也能更轻松地处理 CAD 图纸。实际表现取决于图纸内容、显卡和驱动；项目会持续优化兼容性与流畅度。

[下载 Windows 版本](https://github.com/itsjinsheng/TinyCAD-Viewer/releases/latest) · [更新说明](RELEASE-NOTES.md) · [第三方许可](THIRD-PARTY-NOTICES.md)

## 功能

- 只读打开 DWG、ASCII DXF 和二进制 DXF，支持拖放和最近文件。
- GPU 加速显示、缩放、平移、全图和框选放大；不可用时自动回退。
- 两点距离、累计长度、角度、面积、半径、直径与实体长度测量。
- 端点、中点、圆心、象限点、交点、最近点和垂足捕捉，支持正交取点。
- 右侧统一提供对象属性、测量记录、文字查找与图层管理。
- 标题栏提供中文、English、한국어 切换，首次使用默认英语，语言选择自动保存。
- mm / in（英寸）显示单位切换并保存，已有测量记录同步换算。
- 加载阶段提示、动态进度条和已处理对象数量，支持取消读取。
- 可拖动的测量标签、PNG 导出、打印区域和系统打印 / PDF。

## 使用

系统要求：Windows x64；GPU 加速需要支持 Direct3D 11 的显卡与驱动。发布包自带 .NET 运行时，无需单独安装。

从 Releases 下载并解压 Windows 包，运行 `TinyCAD.Viewer.exe`。打开随附示例或拖入自己的 DWG / DXF 即可使用。软件离线运行，不上传图纸、不修改原始文件。单 EXE 首次启动时，运行组件会释放到本地缓存。

| 操作 | 方法 |
| --- | --- |
| 打开图纸 | Ctrl+O 或拖放文件 |
| 缩放 / 平移 | 滚轮 / 中键拖动，或空格＋左键 |
| 全图 / 查找文字 | F / Ctrl+F |
| 捕捉 / 正交 | F3 / 按住 Shift |
| 完成连续测量 | Enter 或最后一点双击 |
| 面积闭合 | 点击起点、双击最后一点或 Enter |
| 撤回 / 取消 | Backspace / 右键或 Esc |
| 测量记录 | 单击高亮，双击定位；支持单条删除 |
| 语言 / 单位 | 标题栏语言选择 / 底部右侧 mm、in 选择 |

单位根据图纸头信息换算：**1 in = 25.4 mm**，面积按平方比例换算，角度与原始几何不变。没有指定单位或单位无法识别时，保留原始数值并禁用换算，不猜测毫米。语言与单位保存在 `%LOCALAPPDATA%/TinyCAD/settings.json`，最近文件保存在同目录的 `recent.json`。

示例 `samples/二维测量示例.dwg` 外框为 120 × 80 mm，面积 9600 mm²，四个圆孔半径为 6 mm，可用于熟悉操作。

## 兼容范围

支持常见二维线段、圆弧、多段线、椭圆、样条近似、嵌套块、文字、保存的标注显示块及填充边界。模型空间可测量，布局和普通俯视视口用于预览。

- 图纸字体未捆绑，替代字体可能造成文字外观和排版差异。
- 样条和部分曲线的长度为近似值；自交、复杂嵌套轮廓不保证有效面积。
- 图案填充主要显示边界，不提供填充面积测量。
- 动态块显示已保存几何；不加载外部引用，不支持全部代理对象、AEC 对象、图像与 PDF 底图。
- 不支持完整三维显示、透视视口或非矩形裁剪视口。
- 测量记录在换图或切换空间时清空，可先导出 PNG 或打印保存；打印适应纸张，不提供工程比例出图。
- “读取报告”会提示解析或显示限制。正式尺寸核验应结合原图；标注文字可能经过人工修改。

GPU 采用 Direct3D 11 缓存几何绘制，并保留传统渲染回退。技术细节与性能测试方法见 [GPU 说明](GPU-EXPERIMENT.md)。

## 构建

需要 Windows 和 .NET 8 SDK：

```powershell
./build.ps1 -Test -Publish
```

输出：`dist/1.0.0/TinyCAD.Viewer.exe`。源码打包：`./tools/Pack-Source.ps1`。

- `src/CadView`：C# / WPF 应用、图纸解析、GPU 渲染、交互与本地化。
- `tests/CadView.Tests`：几何、读取、界面、单位、多语言与 GPU 回归测试。
- `samples`：项目自制示例；`licenses`：随软件分发的第三方声明。
- [测试说明](TESTING.md)：验证范围和可复现命令。

欢迎提交 Issue 和 Pull Request，帮助改进兼容性、翻译和性能。提交问题时请尽量提供可公开的最小示例。

## 开源许可

项目源码和自制示例采用 [MIT License](LICENSE)。任何人都可以下载、使用、修改源码，以及分发原版或修改后的版本（包括商业使用），请保留相应版权声明和许可证。

**第三方库保留各自的许可证要求。** 分发源码或软件时，必须同时遵守所引用库的相关协议并保留所需声明，详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 和 [licenses/](licenses/)。依赖许可证也内嵌于软件的“帮助 / 开源许可”中。

## English

TinyCAD Viewer is an open-source, GPU-accelerated 2D DWG/DXF viewer for Windows. It focuses on quick drawing review and basic measurements for engineers who do not need a full CAD editor. Chinese, English and Korean interfaces and persistent millimeter/inch display settings are included. GPU acceleration aims to make complex drawings more responsive on everyday computers, including integrated graphics; results depend on the drawing, hardware and driver.

The project is MIT licensed. You may use, modify and redistribute it while retaining the required notices. Third-party libraries remain subject to their own licenses.

## 한국어

TinyCAD Viewer는 Windows용 오픈 소스 2D DWG/DXF 뷰어입니다. CAD 편집 기능 없이 도면을 빠르게 확인하고 기본 치수를 측정할 수 있도록 개발되었습니다. GPU 가속, 한국어·영어·중국어 전환, 저장되는 mm/인치 표시 설정을 지원합니다. 내장 그래픽을 포함한 일반 PC에서 복잡한 도면을 더 원활하게 다루는 것을 목표로 하며, 성능은 도면과 하드웨어·드라이버에 따라 달라집니다.

프로젝트는 MIT 라이선스로 배포됩니다. 필요한 저작권 및 라이선스 고지를 유지하면 사용·수정·재배포할 수 있습니다. 포함된 타사 라이브러리에는 각 라이선스가 적용됩니다.
