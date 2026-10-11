# NX Sketch MCP

全局安装版本 **0.3.0**，共 **20个工具**。原项目及完整功能说明：[项目 README](C:/0sf/1PROJECT/AI/design/nx-sketch-mcp/README.md)。

新增 `nx_prepare_thread`（单头60°外部实体螺纹）和 `nx_prepare_helical_groove`（左右旋梯形螺旋齿槽）。两者通过准备计划、执行、独立验证和保存重开流程工作，明确指定目标实体及全部几何输入，不默认推断图纸缺失尺寸。接口见 [M7说明](app/docs/M7-native-helical.md)。

更新后重新连接MCP，使服务进程加载新代码并发现20个工具。正常使用先调用 `nx_ensure_ready`，自动准备环境、启动专用NX并加载桥接，连接可用时复用。桌面“NX（MCP试用）”使用同一自动启动profile。准备连接后，获得用户明确授权再接管真实路径/PID；准备计划、执行、轮询、独立verify，结束后 `nx_return`。

STARTING时再次调用复用启动中的进程；一次启动/探测预算最多30秒，加有界helper清理开销。已有NX会话和许可窗口不会自动关闭；普通启动且未加载桥接的NX返回NEEDS_MANUAL_ACTION。连接准备保持原所有权，不读写模型、不重放写操作、不解除UNKNOWN冻结。

原草图拉伸能力仍只支持一个整圆或四直线轴对齐闭合矩形；新螺旋齿槽通过专用NXOpen实现。C#与Python NXOpen接口名称、结构及重载不同，不能直接互换。

`mcp-config.json` 的launch指向ugraf.exe和 `app/nx-profile/startup`，后者仅含M2Startup.dll。`sessions/` 保留运行必需私有token、启动状态及锁；`app/src` 与 `app/tools` 包含运行入口，四个生产产物在 `app/workspace/build/current/M2`。仅为新NX子进程设置环境，不改变全局环境或热替换运行中DLL。

本次按用户要求仅更新全局安装与本地原项目，跳过测试及NX实机验证。
