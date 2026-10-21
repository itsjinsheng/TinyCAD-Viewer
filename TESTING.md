# 测试

在 Windows 桌面会话和 .NET 8 SDK 下运行：

```powershell
./build.ps1 -Test -Publish
```

脚本先运行功能回归，再在独立进程运行 GPU 回归，避免软件渲染测试影响 GPU 检查。

## 覆盖范围

- 几何数值、DWG / DXF 往返、布局、块变换、损坏文件和取消读取。
- 同名空间连续换图、并发打开仅采用最后请求、测量与打印区域清理。
- 距离、面积、角度、半径、实体测量及捕捉，测量记录选择、定位和删除。
- 三种语言资源完整性与格式占位符、即时界面翻译、正常及窄窗口布局。
- 语言和单位设置保存、损坏配置恢复；毫米 / 英寸长度和面积换算、反复切换无累计舍入误差、角度不变、无单位图纸禁用换算。
- 加载阶段和对象数报告，以及转换阶段取消。
- PNG 像素尺寸、打印区域裁剪、测量叠加、PNG 设置输入校验。
- GPU 场景缓存、曲线细化、绘制顺序、缩放平移及旧任务失效。

无法取得有效桌面像素的环境会明确报告 SKIP，请勿将其视为图像验证通过。系统打印对话框和实体打印机不在自动测试范围内。测试生成的日志、图片和临时文件位于 artifacts/，不提交到仓库。

## 可选性能检查

```powershell
dotnet run --project tests/CadView.Tests -c Release -- . --perf-screen '图纸路径.dwg' gpu input-stream
dotnet run --project tests/CadView.Tests -c Release -- . --perf-screen '图纸路径.dwg' gpu input-stream-long-4k
```

使用自己有权测试的图纸；无需向仓库提交图纸。该输入队列测试不等于物理鼠标到屏幕的端到端延迟。

单文件解析诊断：

```powershell
./dist/1.0.0/TinyCAD.Viewer.exe --check input.dwg report.json
```
