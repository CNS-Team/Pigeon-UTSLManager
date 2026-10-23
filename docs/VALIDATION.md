# 本地验证

所有证据写入工作区新目录，测试包使用 runtime/ 或 test-packages/。桌面正式目录不导入测试服务器、地图或日志。

后台测试：构建后调用 out/tests/Manager.Tests.exe，三个参数依次为新证据目录、out/host/Pigeon.UTSLManager.Host.exe 的绝对路径、out/probe 绝对路径。可传入第 4 参数（完整包根 EXE 路径）以验证实际 UI 关闭、重新打开时同两个服务器 PID 和控制台存活。

真实 GUI 测试：scripts/Test-DesktopPackage.ps1 使用 PackageRoot、ProbeOutput、EvidenceDirectory 参数；PackageRoot 必须是新建的隔离运行包。可传 Panorama 指向已有地图长图。测试会导入本地控制台探针，不加载 UTSL 或插件，不连接游戏。界面截图、逐项结果和原始日志保留在工作区；结尾停止探针及测试后台。

控制台探针真实调用 Windows Console API，通过 ConPTY 接收标准输入；它不是 UTSL 服务端。通过上述测试只能接受多进程控制、中文色彩、同窗地图 UI 和文件隔离范围。真实 UTSL 世界启动、插件组合兼容、玩家登录/切服/边界渲染仍需另行验收，不以进程状态替代。
