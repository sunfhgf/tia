# TIA Automation 项目交接

更新时间：2026-07-16
GitHub：https://github.com/sunfhgf/tia
默认分支：`main`

## 1. 项目目标

本项目通过 Electron 前端、C# API 和 Siemens TIA Portal Openness 自动创建及配置 TIA Portal V20 项目。

主要流程：

1. 从标准工程 `V20250626_V20-多设备` 创建项目副本。
2. 按 `项目代号.线体.V日期` 生成项目名称。
3. 配置 PLC 名称、IP、PROFINET 设备和 GSD 子模块。
4. 按单元数量复制 TIA 中的 `设备1` 程序文件夹。
5. 配置各单元伺服、工位、气缸、感应器和安全条件。
6. 导入 IO 注释并自动生成 `IO处理`、`气缸逻辑` 等 PLC 内容。

## 2. 本机环境

- 工作目录：`C:\Users\Dell\Desktop\TIA`
- Electron：`C:\Users\Dell\Desktop\TIA\electron-app`
- C# 解决方案：`C:\Users\Dell\Desktop\TIA\TiaAutomation\TiaAutomation.sln`
- GSD 目录：`C:\Users\Dell\Desktop\TIA\gsd`
- 标准模板：`C:\Users\Dell\Desktop\TIA\V20250626_V20-多设备\V20250626_V20-多设备.ap20`
- 当前项目 ID：`83ce645e`
- 当前项目：`DM25300.P2.V20260713`
- 当前项目文件：`C:\Users\Dell\Documents\TiaAutomation\83ce645e\network-project\DM25300.P2.V20260713\DM25300.P2.V20260713.ap20`
- API：`http://127.0.0.1:5005`
- TIA Portal / Openness：V20

## 3. 启动方式

```powershell
cd C:\Users\Dell\Desktop\TIA\electron-app
npm start
```

Electron 会自动启动 Release 版 C# API。

构建检查：

```powershell
cd C:\Users\Dell\Desktop\TIA
dotnet build TiaAutomation\TiaAutomation.sln -c Release
```

如果 API 或 Electron 占用生成文件，先执行：

```powershell
Stop-Process -Name electron -Force -ErrorAction SilentlyContinue
Stop-Process -Name TiaAutomation.Api -Force -ErrorAction SilentlyContinue
```

## 4. 已完成功能

### 项目创建

- 创建项目时复制标准工程。
- 项目名称格式：`DM25120.P1.V20250708`。
- 配置名称后即可生成工程，不要求先完成所有后续步骤。
- 同一个项目只维护一个工程副本，不重复创建多个文件。

### PLC 与硬件

- 修改 PLC 名称和 IP。
- 从 `gsd` 目录选择设备。
- 支持设备名称、PROFINET 名称、IP 和 I/Q 起始地址。
- IO 设备支持子模块配置。
- 伺服设备默认使用标准报文 111，不在前端展示报文子级。
- 新设备自动连接 PLC PROFINET IO System。
- 修复模块槽位重复、子模块位置和侧边栏设备分组问题。

### 单元复制与伺服

- 项目配置中有“单元数量”。
- 根据数量复制 TIA 程序文件夹：`设备1`、`设备1_1`、`设备1_2`。
- “单元伺服”页面按单元配置伺服数量和具体伺服设备。
- 自动修改各单元 `伺服逻辑` 初始化代码，包括轴数、HW 和 `DiagnosePN.STATE`。

### 单元工位

- “单元工位”页面按单元维护多个工位。
- 工位名称自定义。
- 工位选择 `工位1I/Q` 到 `工位9I/Q` 数据类型对。
- 同一数据类型对不能分配给多个工位。
- 每个工位最多 16 个气缸和 16 个感应器。
- 自动修改所选工位 UDT 的气缸、气缸感应和普通感应名称。

### IO 注释

- “导入”页面支持粘贴：

```text
I0.0    铁芯插磁钢24V通断电
I0.1    铁芯插磁钢自动模式按钮
Q0.0    铁芯插磁钢自动模式按钮灯
```

- 自动转换为 `%I0.0`、`%Q0.0`。
- 根据地址修改 TIA PLC 标签表中的注释。

### IO处理 FC

- 自动覆盖现有 `IO处理 [FC2]`，不创建另一个替代 FC。
- `工位1 -> "IO".工位I/Q[0]`，`工位2 -> [1]`，依次类推。
- 根据工位中的气缸/感应器名称与导入的 IO 注释自动匹配物理点。
- 自动生成：
  - Q 点到 `工位Q[n].气缸出/回_x`。
  - I 限位到 `工位I[n].气缸感应出/回_x`。
  - 普通感应到 `工位I[n].感应.感应x`。
- 每条 SCL 语句保留原 IO 注释。
- 未匹配到地址时生成警告，不猜测地址。

### 共用电磁阀

- 每个气缸可以选择：
  - 独立电磁阀。
  - 与前面的某个气缸共用电磁阀。
- 共用时：
  - 物理 Q 只映射到主气缸。
  - 从气缸的手动出/回变量映射到主气缸手动变量。
  - 每个气缸的 I 限位仍分别正常映射。

### 气缸逻辑和安全条件

- 自动覆盖现有 `气缸逻辑 [FB36]`。
- 按规划修改：
  - `气缸块` 多实例数组长度。
  - `#工位数量`，如果新版块存在该变量。
  - 旧版固定 `FOR` 循环上限。
  - 每个工位的 `I气缸数量`。
- 兼容参数字段 `安全[]` 和 `互锁安全[]`。
- 每个气缸可配置“出安全条件”和“回安全条件”。
- 条件支持 IO 地址、PLC 标签名或完整 IO 注释。
- 多个条件使用逗号分隔，生成：

```scl
"IO".参数2[0].互锁安全[0] := NOT ("I5_1" AND "I5_2");
```

- 留空生成 `NOT TRUE`。

## 5. 关键代码

- Electron UI：`electron-app/renderer/index.html`
- API 路由：`TiaAutomation/src/TiaAutomation.Api/Program.cs`
- 项目存储：`TiaAutomation/src/TiaAutomation.Api/ProjectStore.cs`
- 项目模型：`TiaAutomation/src/TiaAutomation.Core/Models/ProjectSettings.cs`
- 工位模型：`TiaAutomation/src/TiaAutomation.Core/Models/UnitStationSettings.cs`
- 综合写入顺序：`TiaAutomation/src/TiaAutomation.Openness/TiaProjectWriter.cs`
- 设备/GSD：`TiaAutomation/src/TiaAutomation.Openness/DeviceWriter.cs`
- 单元复制：`TiaAutomation/src/TiaAutomation.Openness/UnitFolderWriter.cs`
- 单元伺服：`TiaAutomation/src/TiaAutomation.Openness/UnitServoWriter.cs`
- 工位 UDT：`TiaAutomation/src/TiaAutomation.Openness/UnitStationTypeWriter.cs`
- IO 注释：`TiaAutomation/src/TiaAutomation.Openness/IoCommentWriter.cs`
- IO处理：`TiaAutomation/src/TiaAutomation.Openness/IoProcessingWriter.cs`
- 气缸逻辑：`TiaAutomation/src/TiaAutomation.Openness/CylinderLogicWriter.cs`

## 6. 当前项目配置摘要

- PLC：`P2`
- PLC IP：`172.30.2.1`
- 单元数量：3
- 伺服分配：3 / 2 / 1
- 已配置设备：2 个 IO，6 个伺服
- 设备1已配置工位：`小插磁钢上料`
- 工位数据类型：`工位1I/Q`
- 气缸：
  - Y轴整形气缸
  - X轴整形气缸1
  - X轴整形气缸2
  - 阻挡气缸
- 感应器：
  - 有料感应1
  - 有料感应2
  - 有料感应3
  - 防翻检测
  - 到位感应

## 7. 已验证内容

- Release 构建：0 警告、0 错误。
- Electron 页面脚本语法通过。
- 使用模板副本完成真实 Openness 写入验证：
  - 工位 UDT 修改后可重新导出。
  - `IO处理` 成功覆盖并重新导出。
  - 共用电磁阀生成 2 条手动变量联动。
  - 4 个气缸的 8 条限位输入独立映射。
  - 5 个普通感应输入正确映射。
  - `气缸逻辑` 实例数组、循环上限、气缸数量和安全条件成功写入。

## 8. 已知限制

- 正式生成前最好关闭 TIA Portal 中正在打开的目标工程，否则 Openness 会报告工程被占用。
- TIA 关闭异常后可能需要等待约 2 分钟才能重新打开工程。
- 模板工程本身可能存在 PLC 编译错误；生成结果会显示编译错误/警告计数，但不会把模板已有错误直接当成本功能写入失败。
- IO 自动匹配依赖气缸/感应器名称与 IO 注释相似。缺少 Q/I 注释时会警告并跳过。
- 工位数据类型目前限定为 `工位1` 到 `工位9`，且全项目不能重复分配。
- PDF 手册 `TIAPortalOpennesszhCN_zh-CHS.pdf` 和 Office 临时文件未提交到 Git。

## 9. 建议下一步

1. 在正式项目中补齐阻挡气缸等缺失的 Q 点注释。
2. 在“单元工位”页面配置实际共用电磁阀关系。
3. 配置每个气缸动作的真实安全条件。
4. 关闭 TIA Portal 中的正式项目并执行一次完整生成。
5. 打开生成后的 `IO处理`、`气缸逻辑`、工位 UDT，逐项核对。
6. 根据实际需求确认多个单元是否各自需要独立的 `气缸逻辑` 副本和独立 IO 数组偏移。
7. 增加自动化测试，覆盖 IO 注释匹配、主从气缸、安全条件解析和 XML 生成。

## 10. 新账号继续任务提示词

在新的 Codex 账号中打开仓库后发送：

```text
请先读取 TiaAutomation/docs/project-handoff.md，了解当前 TIA Automation 项目。
继续前先检查 git status、当前 main 分支和现有 Electron/C# 实现，不要覆盖未提交改动。
然后根据交接文档的“建议下一步”继续，并在模板副本中验证 Openness 写入，不能直接用正式工程做破坏性测试。
```
