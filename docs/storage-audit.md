# 存储审计记录

审计日期为 2026-10-15（北京时间）。Windows 主机 `192.168.101.5` 上的统计通过只读 PowerShell 查询完成，范围限定为工程临时目录、Temp 根层及明确的 Unity、Unity Hub、pip 缓存目录；没有扫描整盘或整个 AppData。所有统计均为采样值，磁盘余量会随运行中的进程变化。

## 磁盘与工程临时目录

| 盘符 | 总量 | 已用 | 空闲 | 采样说明 |
| --- | ---: | ---: | ---: | --- |
| C: | 255.07 GiB | 252.95 GiB | 2.123 GiB（2,280,001,536 B） | 最后一次采样；此前同日采样约 2.35、2.20 GiB，余量在变化 |
| E: | 734.12 GiB | 约 655.88 GiB | 78.24 GiB | 模型目录与本次备份位于此盘 |

`C:\Users\chiro\tmp\vrc-diffusion` 中实际存放在 C 盘的文件合计 **175,315,227 B（0.163 GiB）**。其中 `sd-ref-libs` 依赖副本占 151,420,077 B（0.141 GiB），`scene-checkpoints` 占约 2.60 MB；场景备份 `main-after-w8a8.unity` 和 `sd-before-panel.unity` 分别约 3.20 MB、4.50 MB。

路径 `C:\Users\chiro\tmp\vrc-diffusion\sd-anime` 是 junction，目标为 `E:\Temp\vrc-diffusion\sd-anime`。目标模型目录占 6,495,793,743 B（6.05 GiB），已计在 E 盘，不占上述 C 盘工程临时目录容量。

## Temp 与缓存

在 `C:\Users\chiro\AppData\Local\Temp` 仅统计了根层文件 Top 20 和一级子目录名称、更新时间。根层最大文件约 5.90 MB；按 `unity-snapshot*.tgz`、SD、W8A8、source-deploy、bundle、pip 等名称检查，没有发现 vrc-diffusion 的大型快照或部署归档。仅有两个旧的 `prep_*_bundle_*_perf.cache`，合计约 1.39 MB。一级子目录共 133 个；`Deployment` 和 `UnityCBMCrashes` 目录为空，未发现对应项目的大型快照目录。该结论只适用于上述限定范围。

`C:\Windows\Temp` 根层 Top 10 最大项是 `wctF636.tmp`（147,194,768 B，10 月 10 日更新）；其余主要是 GAMEPC 日志和小型临时文件。未能从名称将这些文件归因于 vrc-diffusion，因此保留未动。

明确统计到的缓存和更新文件如下。**本次审计只读，以下项目均未清理、移动或改写。**

| 路径 | 占用 | 备注 |
| --- | ---: | --- |
| `C:\Users\chiro\AppData\Local\Unity\cache` | 1,554,389,443 B（1.448 GiB） | `npm` 561,831,929 B，`packages` 992,557,514 B；Unity 包管理缓存 |
| `C:\Users\chiro\AppData\Roaming\Unity\cache\bee` | 268,413,789 B（0.250 GiB） | Unity Bee 缓存，更新时间为 10 月 15 日 07:06 |
| `C:\Users\chiro\AppData\Local\UnityHub\pending` | 193,468,942 B（0.180 GiB） | 含 `UnityHubSetup-3.22.3-x64.exe`（193,266,120 B）、blockmap 和更新信息 |
| `C:\Users\chiro\AppData\Local\pip\Cache` | 296,180,082 B（0.276 GiB） | 通用 pip 下载缓存 |
| `C:\Users\chiro\AppData\Roaming\UnityHub\Cache` | 18,651,860 B（0.017 GiB） | Unity Hub 缓存；GPU 与着色器缓存另有约 7 MB |

默认位置的 HuggingFace、Transformers 和 Torch 模型缓存目录不存在。UnityHub 安装包处于 `pending` 目录，审计未判断更新是否已完成，所以保留。

## tracked dirty 备份

备份时远端 HEAD 为 `fdceb7836d97036f6a54b6911a79678b52ab45f8`，tracked dirty 列表为 **1510 个文件**，均为未暂存修改；没有把 untracked 文件或 `Library` 纳入归档。文件合计 10,264,801 B。归档使用 NUL 分隔的安全文件列表及 GNU tar `--files-from` 创建，并通过 `tar --compare` 与当前工程逐项比对。

- 远端归档：`E:\tmp\vrc-diffusion\ux-baseline\tracked-dirty.tgz`，874,273 B；1510 个成员；SHA-256：`d1d2cb4861bd84c104bf9efeecf49045232e4248796a9ccb6c23938e7ad91eb4`。
- 远端清单：`E:\tmp\vrc-diffusion\ux-baseline\manifest.json`，包含每个路径的字节数和 SHA-256。
- 本地清单副本：`/home/chiro/tmp/vrc-diffusion/ux-baseline/manifest.json`，SHA-256：`5cd476c51f9c039dab2944c8827e8b54353c7efbea3fe0aa0f3b03f1f0454a39`。

归档和缓存审计均未清理或移动原文件。
