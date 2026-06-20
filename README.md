# ScenarioTextLocator

这是给 NScripter2 用的日文文本翻译辅助工具。它负责抽取待翻译文本、把原文送进 LM Studio 翻译、再回填成新脚本。

主要能力：

- 定位 NScripter2 脚本里的日文文本。
- 只把真正需要翻译的内容抽出来，控制字段和脚本命令保持原状。
- 直接把原文发送到 LM Studio，输入就是待翻译文本，不额外加 prompt。
- 支持断点续传、SSE 流式输出和末尾复读裁剪。
- 翻译完可以直接回填生成新脚本。

适合这类脚本：

```txt
@scene01
bgm 1
ld 0,"chr\y2_a.png"
[アオイ/00000]「もう朝か。今日は少し早く来すぎたかもしれない」
駅前の風は冷たく、校門のほうまで静かに抜けていった。
```

默认规则：

- `@` 开头：场景标记，不抽取。
- `;` 开头：注释，不抽取。需要时可加 `--include-comments`。
- 英文字母命令行，如 `bgm`、`bg`、`ld`、`se`、`evc`、`if`、`end`：不抽取。
- `[角色/编号]正文`：只抽取右侧正文，保留 `[角色/编号]`。
- 普通日文叙述行：整行抽取。

## 用法

导出待翻译 TSV：

```powershell
ScenarioTextLocator.exe extract "E:\path\02 - 副本.txt" "E:\path\02.todo.tsv"
```

翻译方只需要填写 `Translation` 列。不要改这些列：

```txt
Id, Line, Start, Length, Kind, Speaker, Original, Hash
```

回填生成新脚本：

```powershell
ScenarioTextLocator.exe apply "E:\path\02 - 副本.txt" "E:\path\02.todo.tsv" "E:\path\02.zh.txt"
```

把 TSV 发给 LM Studio 翻译：

```powershell
ScenarioTextLocator.exe translate "E:\path\02.todo.tsv" "E:\path\02.translated.tsv" --model "你的模型名"
```

一条命令直接从输入到输出：

```powershell
ScenarioTextLocator.exe process "E:\path\02 - 副本.txt" "E:\path\02.zh.txt" --model "你的模型名"
```

默认会在输出文件旁边建一个工作目录，例如：

```txt
E:\path\02.zh.ScenarioTextLocator-work\
  extract.tsv
  translated.tsv
  process.log
```

重跑同一个 `process` 命令时，会复用这些 checkpoint。

`translate` 会读取 `Original` 列，把单条原文作为唯一的 user message 发到 LM Studio，不额外添加 prompt。模型返回内容会写进 `Translation` 列。

LM Studio 默认接口：

```txt
http://localhost:1234/v1/chat/completions
```

如果你的端口或地址不一样：

```powershell
ScenarioTextLocator.exe translate "E:\path\02.todo.tsv" "E:\path\02.translated.tsv" --endpoint "http://localhost:1234/v1/chat/completions" --model "你的模型名"
```

查看统计：

```powershell
ScenarioTextLocator.exe inspect "E:\path\02 - 副本.txt"
```

## 编码

源文件默认按 `shift-jis/cp932` 读取。

TSV 默认输出 `utf-8-bom`，方便 Excel 打开。

回填后的脚本默认输出 `utf-8-bom`。中文通常无法写回 CP932，所以除非你确认引擎支持，否则不要强行用 `--output-encoding shift-jis`。

可选参数：

```powershell
--encoding shift-jis
--tsv-encoding utf-8-bom
--output-encoding utf-8-bom
--include-comments
--allow-changed-source
--endpoint http://localhost:1234/v1/chat/completions
--model your-model-name
--temperature 0.2
--timeout 120
--retries 2
--delay-ms 0
--max-rows 10
--overwrite
```

`translate` 默认跳过已有 `Translation` 的行。如果输出 TSV 已存在，会先读取已有译文并继续补未翻译的行，方便中断后续跑。

默认会走 SSE 流式输出，并且 `timeout` 默认为 0，也就是不设 HttpClient 硬超时。你如果遇到某个模型不支持流式，可以加 `--no-stream`。

每翻完一行，工具都会立刻把当前 TSV 原子写回磁盘，所以中断后重新跑同一个命令就会自动从已完成的位置继续。

`process` 会自动在输出文件旁边建一个工作目录，里面放中间产物：

```txt
<输出文件名>.ScenarioTextLocator-work/
  extract.tsv
  translated.tsv
  process.log
```

## 安全校验

TSV 里每条文本都有行号、起始列、长度和原文哈希。回填时工具会检查原脚本对应位置是否还是同一段文本；如果原脚本被改过，会停止并报错，避免错位替换。
