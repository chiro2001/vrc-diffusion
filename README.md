# VRChat Diffusion Lab

在 VRChat world 中观察并运行 MNIST 与 SD1.x 动漫扩散模型。Udon 调度 fragment shader，通过 `VRCGraphics.Blit` 把算子结果写入本机 GPU 的 RenderTexture（MNIST 用 RFloat，SD 用 RGBA8 打包整数与 ARGBHalf）；各玩家同步生成配置和进度，各自在本机计算图像。

MNIST 面板有两种可选模型：保留的 32×32 Legacy 小型模型，以及默认选中的 MIT TeaPearce 28×28 预训练模型。模型原生推理尺寸和输出显示尺寸分开标注；选择 32、64 或 128 输出只会最近邻放大，不会增加原生细节。完整架构、数据格式和同步边界见[模型设计](docs/model-design.md)。

Unity 工程位于 Windows 主机 `192.168.101.5` 的 `D:\Projects\VRChatProjects2\vrc-diffusion`，Linux 工作区是 Git 镜像。项目使用 Unity 2022.3.22f1 与 VRChat Worlds SDK 3.10.5。

模型权重和生成纹理约 1.27 GiB，不进入 Git 历史。当前 GitHub 私有仓库只包含源码、Unity `.meta` 与模型清单；模型归档尚未发布 Release。获取、恢复和重新打包说明见[模型数据](docs/model-data.md)。

## World 操作

1. 按 **REQUEST CONTROL** 申请控制权。没有控制者时会直接取得控制；已有控制者时会进入请求队列，由当前控制者按 **GRANT** 移交。
2. 选择 **MODEL**、数字类别、seed、16/32/64 步和 **OUTPUT** 尺寸，再按 **GENERATE**。Tea 默认 64 步，Legacy 默认 32 步。
3. **INIT / PAUSE / RUN** 初始化、暂停或继续。暂停后，**STEP OPERATOR** 前进一个 shader pass，**STEP DENOISE** 完成一个完整采样步。**RESET** 清空当前运行。
4. **MEM < / >** 切换当前算子层的实际特征图（显示该层各通道）；**SLOWER / FASTER** 调整每帧 shader pass 数量。
5. 教学板的左右箭头翻阅四页说明。**PROBE X/Y** 与 **CHANNEL +** 选择像素和特征通道，像素探针在采样步完成后读回真实 GPU 纹理数值。

主面板可用 **AUTO / English / 简体中文 / 日本語** 切换语言。AUTO 读取本机 VRChat 语言；手动语言覆盖只作用于本机。

MNIST 与 SD 分别使用独立控制锁，初始 owner 自动取得控制。共享操作在无权限时变灰并禁用，本地预览、性能和语言开关仍可使用。场景保留用户放大的 100×100 floor；本地玩家初始化和重生时设置跳跃冲量 3.5。

## 模型概览

| 模型 | 原生尺寸 | 参数量 | shader 图 | 默认设置 |
| --- | ---: | ---: | ---: | --- |
| Legacy MNIST 32 | 32×32 | 146,177 | 11 个卷积层 | 32 步、[-1, 1]、预测裁剪 |
| TeaPearce MNIST 28 | 28×28 | 6,585,601 | 27 个算子 | 64 步、CFG=3、[0, 1]、不裁剪预测 |
| Counterfeit-V3.0（独立 SD 面板） | 256 / 384 / 512 原生 | 主干 908,529,552，另含 VAE encoder / CLIP | U-Net 609 / VAE decoder 119 / encoder 87 / CLIP 146 节点 | 20 步、CFG=7.5、DDIM；主干 W8A8，CLIP W8A16 |

Tea 权重来自 [TeaPearce/Conditional_Diffusion_MNIST](https://github.com/TeaPearce/Conditional_Diffusion_MNIST)，许可为 MIT；checkpoint commit、SHA-256 和导出图版本见[模型设计](docs/model-design.md)与[预训练调研](docs/pretrained-research.md)。本项目没有在这轮工作中重新训练该模型。

## 构建与部署

先结束 Play Mode，保存需要保留的场景编辑，再按顺序部署源码、编译 UdonSharp、建立基础场景、导入两种模型、生成教学板和本地化覆盖层。`BuildDiffusionLab.Build`、`BuildDiffusionModels.Import`、`BuildDiffusionTeaching.Apply` 和 `BuildDiffusionLocalization.Apply` 会保存它们构建的资产和场景；模型导入前需要干净且已保存的基础场景。不要在模型导入后再次运行基础 `Build`，因为它会重建场景。

```sh
python tools/deploy_sources.py --no-refresh
python tools/unity_remote.py recompile
python tools/unity_remote.py recompile_status
python tools/unity_remote.py run_script --file AgentScripts/BuildDiffusionLab.cs --entry BuildDiffusionLab.Compile
python tools/unity_remote.py run_script --file AgentScripts/BuildDiffusionLab.cs --entry BuildDiffusionLab.Build
python tools/unity_remote.py run_script --file AgentScripts/BuildDiffusionModels.cs --entry BuildDiffusionModels.Import
python tools/unity_remote.py run_script --file AgentScripts/BuildDiffusionTeaching.cs --entry BuildDiffusionTeaching.Apply
python tools/unity_remote.py run_script --file AgentScripts/BuildDiffusionLocalization.cs --entry BuildDiffusionLocalization.Apply
```

源码和场景由 [Unity Pipeline 技能](.agents/skills/unity-pipeline/SKILL.md)及 `tools/unity_remote.py` 管理。本轮已在 Unity Editor PlayMode 检查 UI 射线、三语切换、模型/输出控制、状态重放和实际 Udon 采样；未运行 SDK Build & Test，也未验证真实多客户端网络所有权交接。

## 验证状态

Tea 模型的 32、64、128 步 CPU 评估各包含 10 类 × 10 个 seed，共 100 张图。[64 步 CPU 样本网格](results/pretrained-quality-64.png)整体可辨，但不表示每个 seed 都清晰或类别正确。其中 10 个类别/seed 配对已用实际 shader hash 噪声和 D3D11 图计算交叉核对，[GPU 批次报告](results/gpu-seed-batch.json)标记通过；[图像](results/gpu-seed-batch.png)的最大误差为 `2.30669975e-5`，无 non-finite 值。

[两模型数值报告](results/models-numeric.json)标记通过，包含逐算子和完整采样轨迹对照：RTX 5080 / D3D11 下，Legacy 32 步最终 `maxAbs=4.45246696e-5`，Tea 64 步最终 `maxAbs=4.20212746e-6`。Tea 各中间节点也逐项核对。真实 Udon `VRCGraphics` 还以 class 3、seed 1234 完成了 Tea 64 步、65 帧运行，见[PlayMode 轨迹报告](results/play-trajectory.json)、[采样动画](results/pretrained-gpu-trajectory.gif)和[最终图像](results/pretrained-gpu-final.png)。运行期模型/输出/单步、陈旧快照、像素探针和教学公式结果记录在[运行报告](results/runtime.json)，报告标记通过。

UI 构建与 PlayMode 射线结果见[EditMode 报告](results/ui-editmode.json)和[PlayMode 报告](results/ui-playmode.json)，两份报告均标记通过：43 个按钮、2 个 world-space Canvas、462 个 TMP 文本覆盖层；三语切换、43 个按钮射线和 seed 草稿 400 ms 不丢失均有记录。中文 UI 和实时 RT 截图：

![主面板](results/world-main.png)

![斜视图](results/world-oblique.png)

![教学板](results/world-teaching.png)

Legacy 保留作对比，不建议作为默认模型；旧版本样本曾有大量无效或难辨认输出，不能把少量变形的旧结论沿用到当前模型。PlayMode 已覆盖本地状态重放、陈旧快照和运行控制；真实多客户端的请求、所有权移交、晚加入和掉线恢复尚未实测。本轮没有进行 NPU 训练。

更多资料：[多人控制与追赶](docs/multiplayer-design.md)、[教学板设计](docs/teaching-design.md)、[射线 UI 参考](docs/ui-reference.md)、[预训练模型调研](docs/pretrained-research.md)。

W8A8 算子原型与复现步骤见 [低精度计算验证](docs/w8a8.md)。独立验证场景已在 PlayMode 通过真实 Udon/VRCGraphics 的卷积、重量化、线性层和精确整数读回。SD 演示站现已接入 Counterfeit-V3.0：全部 U-Net / VAE 卷积和线性层走 W8A8 / INT32，其他算子混合浮点。256×256、20 步真实 Udon 生成约 92–112 秒，后者包含中间 VAE 解码与内部纹理展示；尚未证明相对浮点 Shader 的统一提速。

SD 面板位于原面板左侧，旁边有性能与输入板。可选择 24 组预设，或输入普通英文正、负提示词后按“应用自定义提示词”，由本机 CLIP 编码；最多 75 个内容 token，不支持 A1111 权重和调度语法。配置原生 256 / 384 / 512、步数和 seed 后生成。共享配置和暂停状态，各访客按“本地推理”主动启用自己 GPU 的计算。

可拾取 Polaroid 和台座位于 SD 面板与输入板之间，靠近 SD 面板。取得 SD 控制后用 Polaroid 拍照，照片会替换中心正方形输入；开启“照片初始化”并选择 0.35 / 0.60 / 0.85 重绘强度，再生成。玩家出生点后方约 3 米处的青色框是保存区域，说明牌朝向出生点：把 VRChat 原生拍照相机移入后，生成结果会填入相机画面，玩家按原生快门保存。宽高比不同时保留黑边，普通玩家视角和区域外相机不显示该覆盖层。原生客户端保存和真正多人同步仍待实测，见[相机说明](docs/camera-integration.md)。

性能板显示真实执行阶段、进度条、有效步数、运行秒数、平均 steps/s 和 Blit 提交次数。细节开关控制真实 INT8 / FP16 内存与网络高亮；VAE 预览开关控制额外的中间 RGB 解码，默认均关闭并标明性能成本。CLIP 的激活保留 A16 以降低量化误差，U-Net 与 VAE 的 Conv/Linear 保持 W8A8。模型许可为 CreativeML Open RAIL-M，详见[模型来源](docs/sd-model-source.md)、[后端说明](docs/sd-shader-backend.md)。

本轮已通过实际 Udon 的相机图生图：256×256，20 步日程中执行 17 步、重绘强度 0.85、自定义提示词，约 75 秒（每帧最多 32 次提交、8ms CPU 预算）。见[新功能验证](results/sd-experience/validation.json)、[结果](results/sd-experience/img2img-final.png)、[相机保存画面](results/sd-experience/camera-export-square.png)与[场景截图](results/sd-experience/experience-world.png)。C 盘临时文件检查见[空间审计](docs/storage-audit.md)。

SD 实测与图像：[256 去噪序列](results/sd/trajectory-256.png)、[512 最终图](results/sd/playmode-512.png)、[512 去噪序列](results/sd/trajectory-512.png)、[地图截图](results/sd/world-512.png)、[八组质量样本](results/sd/quality-w8a8-512.png)、[浮点/W8A8 对照](results/sd/quality-float-vs-w8a8-512.png)。原生 512×512 / 20 步实测 357.74 秒，包含中间 VAE 预览与部分细节展示；测试使用每帧至多 32 次提交和 8 ms CPU 提交预算，默认设置更保守。真实多客户端网络、SD Android 版与 SDK 构建/上传尚未验证。
