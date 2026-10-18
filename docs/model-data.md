# 模型数据与 Git 发布边界

模型权重和由权重生成的纹理数据体积过大，不进入 GitHub 源码历史。`.meta`、`model.json`、`extensions.json` 和其他可审阅清单仍保留在 Git 中；恢复数据包时需要保持原有相对路径，以保留 Unity GUID 引用。

截至 2026-10-18，以下归档只保存在本地独立发布目录，尚未上传 GitHub Release：

- `sd-model-data-v1.zip`：`Assets/DiffusionLab/Data/SD/` 下的 PNG/bytes，以及 `Assets/DiffusionLab/SD/Parameters/*.asset`
- `tea-legacy-data-v1.zip`：其余 `Assets/DiffusionLab/Data/**/*.png|bytes`、`Assets/DiffusionLab/Textures/Weights.asset` 和 `Assets/DiffusionLab/Textures/Tea/*.asset`

两个归档的文件大小、SHA-256 和逐文件清单记录在仓库根目录的 `model-assets.lock.json`。完整过滤前历史保存在发布目录的 `backup/vrc-diffusion-full-history.bundle`，源 HEAD 为 `6d0fc1b43d31e2fb713554b518e2c227c0311227`。

## 恢复

将两个归档放在同一目录，然后执行：

```sh
python3 tools/model_data.py restore --target . --archives /path/to/model-archives
python3 tools/model_data.py verify --target .
```

恢复脚本先验证归档大小和 SHA-256，再逐个文件校验。目标位置已有内容不同文件时，脚本会停止，不会覆盖本地修改。

## 重新打包

在保留完整模型文件的原始工程或已恢复的工作区执行：

```sh
python3 tools/model_data.py pack \
  --source /path/to/vrc-diffusion \
  --output /path/to/model-archives \
  --lock model-assets.lock.json
```

当前锁文件标明的发布状态是 `local-archives-only`。以后上传 Release 时，应同步更新 `publication` 字段和下载说明，不应把归档重新提交到 Git。
