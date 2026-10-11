# NX Sketch MCP

Windows本机MCP服务，使用Python标准库及NX2506/.NET Framework桥接。STDIO为UTF-8 JSONL，协议2025-11-25，stdout仅输出协议。

公共源码仓库不包含 `workspace/` 下的本机构建产物、安装副本或实机证据。构建和使用需要 Windows、本机已安装且获授权的 Siemens NX 2506、.NET Framework 4.x 与可用的 C# 编译器；仓库不能替代 NX 安装或授权。

## 能力与限制

20个工具支持连接自动准备、诊断、接管/交回、模型与草图检查、选面、尺寸编辑计划、草图/拉伸/实体螺纹/螺旋齿槽计划、执行、操作查询/取消、独立后验及保存副本重开。当前服务版本0.3.0。

- 草图：XY/XZ/YZ或唯一平面支撑面；毫米坐标的线段、矩形、整圆。
- 拉伸：每张草图仅一个整圆，或四条直线组成的轴对齐闭合矩形；开放轮廓、任意多边形和多轮廓返回UNSUPPORTED_PROFILE。
- create/unite/subtract；unite/subtract必须指定target_body_id；through_all只用于unite/subtract。
- Driving尺寸编辑：圆直径、圆心与点的水平偏移、单线端点的水平/垂直投影；必须有唯一毫米表达式关系。
- `nx_prepare_thread`：既有实体上的单头、60°外部实体螺纹；明确输入大径、小径、螺距、轴线、起点、长度、方向和旋向，唯一解析圆柱及起始平面。
- `nx_prepare_helical_groove`：按明确的齿根径、参考直径、槽宽、齿形角、导程及相位生成三导引曲线梯形螺旋槽，先创建独立刀具体再对显式目标求差。X/Y/Z主轴、左右旋，最多50转、固定每转96点三次插值；不默认补齐图纸缺失尺寸。
- 两类新增操作复用现有所有权、准备/执行/验证、撤销回滚、未知结果停写及保存重开；原生参数、导引曲线和布尔关联参与修订与重开检查。详见 [M7接口和验证说明](docs/M7-native-helical.md)。
- 不提供通用Journal执行、圆弧段输入、任意轮廓拉伸、倒圆角、装配、Teamcenter、WAVE或自动解冻。
- 实际能力以nx_get_capabilities和零件检查结果为准。

## 已安装服务

默认安装根为 `$env:CODEX_HOME\nx-sketch-mcp`；未设置 `CODEX_HOME` 时使用当前 Windows 用户主目录下的 `.codex\nx-sketch-mcp`。`tools/install-local.ps1` 只更新已经配置好的安装，要求安装目录内已有 `installation.json`；它不创建首次安装、不注册 MCP 客户端，也不生成本机配置或令牌。更新后重新连接 MCP 以发现新增工具。正常使用先调用 `nx_ensure_ready`：连接可用时立即复用；无 NX 时自动准备子进程环境、启动专用 NX 并通过官方 Startup 插件加载桥接。每次使用不重新安装、编译或制作临时加载脚本。桌面“NX（MCP试用）”也使用同一已安装启动配置。

工具返回READY、STARTING、NEEDS_MANUAL_ACTION或FAILED。STARTING时再次调用会复用已启动的进程；一次调用的启动/探测预算最多30秒，加有界的helper清理开销。许可或启动窗口未处理时不会强制关闭或反复新开NX。普通方式启动且未加载桥接的既有NX会返回NEEDS_MANUAL_ACTION；用户可正常保存退出后使用MCP专用入口启动。

1. nx_ensure_ready自动准备或复用连接；nx_bridge_status/nx_bridge_ping仍只诊断，不启动NX。
2. 用户明确授权后，nx_takeover指定真实expected_part_path、expected_pid、authorize=true、artifact_scope。
3. 使用返回的ownership_id/binding_epoch/revision_id准备计划，再执行返回的plan_id。
4. nx_get_operation查询完成后，用nx_verify_part独立回读。
5. saved_independent_copy可nx_save_copy到不存在的新.prt，必须reopen=true；成功后使用新凭据。
6. nx_return确认HUMAN_OWNED/safe_for_human后继续人工操作。

current_work_part保留内存件，支持允许的读写，不能nx_save_copy。UNKNOWN/停写时检查write_halt_reason及active_operations，不重放或自动解冻。

## 构建与配置

先从本机获授权的 NX 2506 安装提供 NXOpen 程序集，并设置 .NET Framework 4.x 编译环境。源码构建示例（将路径替换为本机 NX 安装根）：

```powershell
$env:UGII_BASE_DIR = 'C:\Path\To\NX2506'
.\tools\build-m2-bridge.ps1
```

构建脚本只编译，不启动 NX；产物写入 `workspace/build/current/M2`。该目录属于本机生成内容，不随公共源码仓库分发。启动 profile 的 `startup` 文件夹只部署 `M2Startup.dll`；它按子进程环境指定的绝对路径加载生产 Bridge/Entry。运行中的 DLL 不自动热替换；修改桥接源码后，正常保存退出 NX 再加载新构建。`tools/install-local.ps1` 可在构建后更新前述既有安装，不能引导首次安装。

客户端命令示例（将 `config/bridge.local.example.json` 复制为本地 `config/bridge.local.json`，填入本机绝对路径和私有令牌路径）：

```powershell
$ProjectRoot = (Resolve-Path .).Path
python -B "$ProjectRoot\tools\mcp_stdio.py" --config "$ProjectRoot\config\bridge.local.json"
```

`config/` 中的 `.local.example.json` 是模板；真实 `.local.json` 配置不应发布。字段为 `pipe_name`、`token_file`、`helper_path`、`timeout_seconds`（0.1—120，默认5）。不要输出令牌内容。`tools/prepare-m2-session.ps1` 准备独立会话；`start-nx-global.ps1` 使用已安装连接配置；`verify-m2-local-network.ps1` 核对本地网络条件。

旧连接配置仍兼容。自动启动增加可选launch对象：nx_executable（ugraf.exe绝对路径）、startup_directory（专用profile/startup绝对目录，仅含M2Startup.dll）、ready_timeout_seconds（1—30）。已安装配置已设置该对象。工具仅为新NX子进程设置UGII_USER_DIR、PIPE、TOKEN、ENTRY_DLL，不修改系统或用户全局环境；同用户Global mutex覆盖不同配置和直接启动入口，私有会话byte锁协调同一端点的MCP客户端。私有目录中的ready状态/锁与startup.last_error.txt为可替换的运行状态，不是逐次开发报告。

## NXOpen接口

C#桥使用UFSession.GetUFSession().Modl.AskBoundingBox(tag, double[6])，CreateArc的半径和角度均为double；这是NX2506 .NET有效接口。

经授权另用NXOpen Python Journal时，包围盒接口为uf.ModlGeneral.AskBoundingBox(tag)；CreateArc半径、起角、终角应显式传float，不能依赖整数隐式转换。Python命名不能直接替换C#调用。运行外部Journal前必须交回MCP所有权。

螺纹使用 `ThreadBuilder` 的 `Manual` / `Detailed` 模式。`IsInternalThread` 只读，起始对象须为面；`SelectDisplayableObject.SetValue` 传对象、视图和点；手动指定大/小径时须同步 `ShaftPreference` / `ShaftDiameterExp`。UF样条的 `slope_type` / `crvatr_type` 未指定值为 `-1`，不是 `0`。这些接口用法固化于 `M7NxHelicalFactory.cs`。
