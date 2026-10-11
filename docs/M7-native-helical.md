# M7：实体螺纹和螺旋齿槽

版本0.3.0新增两个受限建模入口。使用类型明确的NXOpen C#调用；不接受任意Journal、Python代码、NX对象tag或自动目标选择。

## 流程与会话

按已有流程调用 `nx_ensure_ready`，获得明确接管授权后 `nx_takeover`。`nx_get_capabilities` 的 `model_helical_adapter_available` 和 `model_helical_write_enabled` 必须为true；旧桥、未接管及未知结果停写状态不具备该写能力。

准备工具返回操作记录；查询 `nx_get_operation`，取得成功的 `result.plan.plan_id` 后调用 `nx_execute_plan`。执行成功后以 `execute_operation_id` 调用 `nx_verify_part`。`operation_id`用于幂等查询；同ID不同参数拒绝，计划只允许执行一次。目标或修订变化后必须重新准备，不自动重试未知结果。

`saved_independent_copy` 可通过 `nx_save_copy` 保存到新的、不存在的.prt路径并重开，成功后所有权与binding epoch更新。`current_work_part`仍禁止该SaveAs/重开。新增功能没有改变这两种保存权限。

## 公共输入

两个工具的全部字段都必须提供，不接受额外字段。

| 字段 | 语义 |
| --- | --- |
| operation_id | 本次准备请求的唯一ID，最多128字符 |
| ownership_id / binding_epoch / revision_id | 当前接管和修订凭据 |
| target_body_id | 当前零件中唯一实体的journal ID；不能省略 |
| axis | X、Y或Z主轴 |
| axis_origin_mm | 全局XYZ轴线原点，3个有限数值 |
| start_mm | 相对axis_origin_mm沿正axis的有符号轴向坐标 |
| length_mm | 正长度；螺纹由direction控制，齿槽沿正轴推进 |
| handedness | right或left |

毫米坐标、长度、直径和导程等绝对值不超过1,000,000；拒绝布尔值、NaN、Infinity及非法类型。所有建模仅限毫米制、完全加载且无受禁外部依赖的独立原生零件。

## nx_prepare_thread

只支持外部、单头、60°、实体螺纹，不能把符号螺纹当实体齿形验收。

额外字段：`direction`为positive/negative；`major_diameter_mm`为目标圆柱名义大径；`minor_diameter_mm`为明确的小径，必须0<小径<大径；`pitch_mm`为正螺距。长度/螺距不超过500。

准备过程要求唯一匹配的同轴圆柱面和起始平面，并校验有效螺纹区间。不能仅根据整个阶梯轴的包围盒认定螺纹长度满足要求。跨不同直径或不明确的起始几何应拒绝，不能自动调整长度或把部分螺纹报告为完整。

示例：Ø40、长100的Z轴圆柱，在Z=100端面向负Z建长30的外螺纹。小径37.546262是本示例的明确输入，不是API隐含默认值。

```json
{
  "operation_id": "thread-prepare-1",
  "ownership_id": "<current owner>", "binding_epoch": 1, "revision_id": "<current revision>",
  "target_body_id": "<solid journal id>",
  "axis": "Z", "axis_origin_mm": [0, 0, 0],
  "start_mm": 100, "length_mm": 30, "direction": "negative", "handedness": "right",
  "major_diameter_mm": 40, "minor_diameter_mm": 37.546262, "pitch_mm": 2
}
```

## nx_prepare_helical_groove

额外字段：`lead_mm`为每转轴向推进量；`root_diameter_mm`、`reference_diameter_mm`、`outer_diameter_mm`必须依次增加；`space_width_mm`为参考直径处的轴向槽宽；`flank_angle_deg`大于0小于45°；`phase_deg`为-360～360°。

半槽宽定义为：

`w(r) = space_width_mm/2 + (r-reference_diameter_mm/2) × tan(flank_angle_deg)`

要求根部半槽宽>0、外部全槽宽<导程，拒绝截面退化和相邻圈相交。轴向长度/导程不超过50。四条直线截面、三条三次插值螺旋导引曲线；每转96个区间，距离/交线容差0.001 mm、扫掠角度容差0.05°。这是明确参数驱动的几何近似，不能据此认证制造齿形精度或自动认定标准、齿根和侧隙。

例如对Ø96轴体进行单头右旋齿槽切除时，可以显式提供导程25.132741、齿根Ø60.8、参考Ø80、刀具体外径Ø110、参考槽宽12.566371、20°齿形角。缺少这些参数时API拒绝，不推断“标准蜗杆”。

旋向通过螺旋角度正负控制；相位参与导引曲线真实几何，不能只写入属性。生成独立扫掠刀具体之后，再对 `target_body_id` 指定的实体求差，保留非目标实体。

## 原生证据与错误处置

螺纹读回原生ThreadBuilder的Detailed/Manual、外部标志、头数、角度、大/小径、轴径、螺距、长度、旋向、方向和面关联。齿槽读回扫掠的截面/导引曲线、真实样条插值定义及节点/极点、容差和显式布尔目标。没有创建草图约束的操作，在constraints检查中明确说明其证据是原生参数与关联。

特征定义和辅助曲线参与语义修订、执行前置检查、撤销恢复证明以及保存重开比对，不能只凭体积/包围盒检测相位或旋向改变。实体数量、几何一致性、目标体积减少、非目标实体和错误列表均要检查。

全部辅助曲线、扫掠及布尔操作在同一UndoMark范围内。失败后只有证明确实恢复原模型才返回ROLLED_BACK；无法确认时返回UNKNOWN并冻结写入。超时晚完成即使没有中途轮询，也不能改报SUCCEEDED或自动解冻。

## 验证层次与部署

- `tests/unit/test_helical_tools.py`：Python输入及派发、旧桥能力不误报。
- `tests/m2/test_m7_bridge.py`：真实CLR桥/helper/MCP协议和计划生命周期，以NX-free fixture验证幂等、单次执行、失效、回滚、UNKNOWN与超时。
- `tests/nx/M7Acceptance.cs`：独立测试零件中调用生产C#适配器，验证实体螺纹、不同主轴及左右旋螺旋槽、非目标实体与保存重开。通过 `tests/nx/build-m7-acceptance.ps1` 仅编译，不自动启动NX；实际运行需要独立NX验证会话授权。

更新安装目录的Python源码和DLL后，重新连接MCP以发现20个工具。已经加载旧DLL的NX会话须正常保存退出后从MCP专用入口重新启动；不做热替换、进程强杀或未知状态自动解冻。

本次实现的已有离线结果：84项桥接/协议回归和26项单元测试全部通过；NX2506生产DLL及独立实机验证DLL已编译；已安装文件与源码产物逐一SHA256一致，新启动的安装版MCP确认版本0.3.0和20个工具。用户已要求本次跳过测试，仅更新全局安装与本地原项目；因此不再等待实机测试授权，也未启动NX验证。之前蜗杆Python Journal建模成功不替代新C#功能的实机验证。记录位于 `workspace/build/current/M2/m7-validation.json` 和 `m7-installed-discovery.json`。
