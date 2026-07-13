# TiaAutomation 使用说明

这是 TIA Portal V20 Openness 自动化工具 MVP。

## 命令

```bash
TiaAutomation.Cli.exe inspect --project "..\V20250626_V20-多设备\V20250626_V20-多设备.ap20" --gsd-dir "..\V20250626_V20-多设备\AdditionalFiles\GSD" --out "output\inspect-report.json"

TiaAutomation.Cli.exe plan --job "config\sample-job.json" --catalog "config\device-catalog.json" --gsd-dir "..\V20250626_V20-多设备\AdditionalFiles\GSD" --out "output\plan-report.json"

TiaAutomation.Cli.exe apply --job "config\sample-job.json" --catalog "config\device-catalog.json" --gsd-dir "..\V20250626_V20-多设备\AdditionalFiles\GSD" --source-project "..\V20250626_V20-多设备\V20250626_V20-多设备.ap20" --target-project-dir "output\projects" --out "output\apply"

# 显式启用 TIA Openness 写入 PLC 标签；只写复制后的工程，不写原始标准工程
TiaAutomation.Cli.exe apply --job "config\sample-job.json" --catalog "config\device-catalog.json" --gsd-dir "..\V20250626_V20-多设备\AdditionalFiles\GSD" --source-project "..\V20250626_V20-多设备\V20250626_V20-多设备.ap20" --target-project-dir "output\projects" --out "output\apply-write" --enable-tia-write --tag-table "TIA_AUTO_IO"
```

## 配置内容

`config/sample-job.json` 现在按《PLC手动配置说明》扩展为标准程序转项目程序的配置样例，包含：

- `project`：项目编号、线体/工位、版本日期，生成类似 `DM25120.P1.V20250708` 的项目名。
- `stations`：工位名称、逻辑块、安全条件、关联设备。
- `devices`：远程 IO、伺服、变频器、机器人等硬件计划。
- `ioPoints`：输入/输出地址、标签名、注释。
- `cylinders`：气缸 IO 绑定、动作延时、报警时间、自动/单步/初始化/屏蔽模式。
- `servos`：伺服轴名、通讯报文、硬件标识、报文地址。
- `motors`：电机运行/故障 IO 和逻辑块。
- `alarms`：工位报警来源、文本、等级。

规则整理文档：

```text
docs/standard-conversion-rules.md
```

## 安全规则

- 首版不会原地修改标准工程。
- 默认 `apply` 只复制标准工程目录，并输出标签 CSV、气缸/伺服/电机/工位/报警映射 JSON、人工确认清单和计划报告。
- 只有显式添加 `--enable-tia-write` 时，才会尝试通过 TIA Openness 写入复制后的工程。
- 当前 TIA 写入范围仅限 PLC Bool 标签表，硬件新增、PLC 块写入、HMI 画面生成仍暂时禁用。
- 扫描时忽略 `*.baiduyun.uploading.cfg` 文件。

## TIA Openness

如果要读取或写入 `.ap20` 工程，需要本机安装 TIA Portal V20，并启用 Openness。默认查找：

```text
C:\Program Files\Siemens\Automation\Portal V20\PublicAPI\V20\Siemens.Engineering.dll
```

如果路径不同，可用 `--openness-dll` 指定。

启用写入后，工具会生成：

```text
tia-write-result.json
```

如果未安装 Openness 或权限不足，文件里会写明诊断信息，原始标准工程不会被修改。
