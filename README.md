# 小插件V2，直接让AI安装即可。

# NX Sketch MCP V2

NX Sketch MCP 是 Windows 本机 MCP 服务，通过 STDIO 连接 MCP 客户端，并通过 NXOpen 桥接 Siemens NX。当前软件版本为 **0.22.0**，提供 **42 个工具**；V2 是产品阶段名称，并非软件版本号。模型操作的支持范围受几何类型、所有权、零件状态和当前会话限制，调用前请查阅 [V2 功能与边界](docs/V2.md) 和相应功能文档。

V2 扩展了空间基准面与草图、草图编辑、拉伸和旋转、孔和螺纹孔、布尔、倒圆和倒斜角、特征阵列和镜像、空间曲线与扫掠，并提供独立 Python Journal。部分操作只支持限定几何，Journal 在独立 NX batch 会话执行，不附着桥接模型；C# Journal 在本版本被拒绝。

## 环境与构建

需要 Windows、已安装且获授权的 Siemens NX 2506、NXOpen 程序集、.NET Framework 4.x 和可用的 C# 编译器，以及 Python 和 PowerShell 7。服务使用 Python 标准库；`tools/run-mcp.ps1` 使用 Python 3.14，安装脚本的发现步骤使用 `python.exe`。其他 NX 版本和换机部署尚未经验证。

在仓库根目录，以本机 NX 根目录替换示例路径：

```powershell
$env:UGII_BASE_DIR = 'C:\Path\To\NX2506'
pwsh -NoProfile -File tools/build-m2-bridge.ps1
```

构建脚本只编译，产物位于 `workspace/build/current/M2`；本机生成产物不随源码分发。

## 连接与现有安装更新

`config/bridge.local.example.json` 和 `config/mcp-client.local.example.json` 是本地配置模板。填写本机绝对路径及私有令牌文件路径后，可将客户端指向 `tools/mcp_stdio.py`；示例中的路径仅作占位。真实配置、令牌和模型应保存在私有位置，不要提交或输出令牌。

```powershell
$ProjectRoot = (Resolve-Path .).Path
python -B "$ProjectRoot\tools\mcp_stdio.py" --config "$ProjectRoot\config\bridge.local.json"
```

自动启动另需本机 NX 可执行文件、启动 profile 和对应的绝对路径配置。`launch.nx_executable` 指向 `ugraf.exe`，`launch.startup_directory` 指向专用 profile 的 `startup` 目录，其中的启动 DLL 只能是 `M2Startup.dll`；需要正确部署桥接程序集及私有会话配置。启动器从本机非空 Process、User、Machine 值按序继承 `SPLM_LICENSE_SERVER` 和 `UGII_LANG`。不会自动发现 NX 路径。

`tools/install-local.ps1` **只更新已经配置好的安装**，要求安装根已有 `installation.json`，且其 `source_root` 匹配当前源码树；它不创建首次安装、客户端注册、配置或令牌。默认安装根为 `$env:CODEX_HOME\nx-sketch-mcp`，未设置时为当前用户目录下 `.codex\nx-sketch-mcp`。请先正常保存并关闭 NX、Journal 和 helper，再构建并运行：

```powershell
pwsh -NoProfile -File tools/install-local.ps1 -Version 0.22.0
```

完成后重新连接 MCP 客户端以加载版本与工具 schema。首次安装和跨机部署尚无经验证的一键流程；源码构建成功不等于自动启动配置已完成。

## 基本使用

1. 调用 `nx_ensure_ready` 准备或复用连接；`nx_bridge_status` 和 `nx_bridge_ping` 用于诊断。未加载桥接的普通 NX 会话可能要求人工处理。
2. 经用户明确授权后，`nx_takeover` 指定目标零件路径、进程和作用域；使用当前返回的所有权、绑定和修订凭据。
3. 对支持的操作准备计划，执行并通过 `nx_get_operation` 查询，再用 `nx_verify_part` 独立回读。保存副本需满足相应作用域和重新打开条件。
4. 使用 `nx_return` 安全交回人工。结果为 UNKNOWN 或停写时停止写入，不重放、不自动解冻；先核查实际状态和副作用。不要跨会话复用 Tag 或操作凭据。

真实 NX 操作需串行，禁止热替换已加载的 DLL 或强制终止 NX。详细接口、限制和验证说明见 [功能索引](docs/V2.md) 与 [测试说明](docs/testing.md)。
