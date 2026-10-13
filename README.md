# VRChat Diffusion Lab

在 VRChat world 内以片元 Shader 执行小型类别条件 MNIST 扩散模型。Python 负责离线训练；客户端本地 GPU 完成 11 层 U-Net 与 DDIM（eta=0）采样，Udon 调度真实 RenderTexture 计算。主屏幕显示噪声逐步形成数字，观察屏展示预测干净图、预测噪声、网络层通道平铺和采样快照。

开发目标工程：`D:\Projects\VRChatProjects2\vrc-diffusion`（gamepc / 192.168.101.5）。Linux 工作区是该工程的 Git 镜像。Unity 2022.3.22f1 / VRChat Worlds SDK 3.10.5。

## 操作

先按 REQUEST CONTROL；已有控制者时，控制者使用 GRANT 授予排队者。选择 CLASS 0–9、seed 和 16/32/64 steps，再按 GENERATE。INIT / PAUSE / RUN、STEP DENOISE、STEP OPERATOR 和 RESET 用于观察计算过程。待机时按 INIT / PAUSE / RUN 可先初始化噪声并暂停，再逐算子或逐去噪步运行。MEM < / > 切换实际特征图，SLOWER / FASTER 调整本机计算预算。

多人共享控制者、类别、seed、采样步数和已发布进度，每位玩家在本地 GPU 重建图像。新加入者从固定种子追赶；控制者离开时暂停，重新申请控制后继续。界面同时显示 shared 与 local 进度；GPU 浮点差异允许图像有微小差别。详细设计见 [docs/multiplayer-design.md](docs/multiplayer-design.md)。

## 开发

Unity 内容通过 [unity-pipeline 技能](.agents/skills/unity-pipeline/SKILL.md) 制作，使用 `python tools/unity_remote.py ...` 调用指定远程工程。场景、材质、RT、纹理与 Udon program 由 `AgentScripts/BuildDiffusionLab.cs` 在 Editor 中建立并持久化；不要手写 Unity 序列化资产或 meta。

```sh
python tools/deploy_sources.py --no-refresh
python tools/unity_remote.py recompile
python tools/unity_remote.py recompile_status
python tools/unity_remote.py run_script --file AgentScripts/BuildDiffusionLab.cs --entry BuildDiffusionLab.Compile
python tools/unity_remote.py run_script --file AgentScripts/BuildDiffusionLab.cs --entry BuildDiffusionLab.Build
python tools/unity_remote.py run_script --file AgentScripts/ValidateDiffusion.cs --entry ValidateDiffusion.Numeric
```

训练环境需要 PyTorch；RTX 50 系列使用包含 sm_120 支持的 CUDA wheel。Windows 的专用环境位于用户目录 `.venvs\vrc-diffusion`，不修改已有环境。

```sh
python ml/train.py --iterations 18000 --batch 64 --pause-ms 30 --device cuda --output Training
python ml/export.py --checkpoint Training/mnist.pt --output Assets/DiffusionLab/Data
```

采样使用 256 个训练时间步中的离散子序列。初始噪声由 Shader 的整数哈希和 Box–Muller 变换生成；数值对照使用 Python 导出的固定输入，以单独验证每层和完整采样轨迹。状态是 32×32 单通道 RFloat，特征图用水平通道条带存储，显示 Shader 将条带重排为方形通道网格。权重与时间/类别条件 LUT 保存在线性 RFloat 纹理中。

保存工程后用 `snapshot_unity_project.py` 采集真实资产与 GUID，提交本地 Git，再用 `sync_windows_git.py` 核对哈希并同步远端 HEAD/索引。训练缓存、客户端日志和 Library 不纳入 Git。

## 验证状态

最终模型为 146,177 参数、11 层卷积，采用折叠 BatchNorm、类别条件和 classifier-free guidance（3）。模型版本为 1021740377。

RTX 5080 / D3D11 的逐层和32步采样对照通过，最终最大绝对误差为 4.45247e-5。Udon运行测试覆盖类别/九位seed输入、暂停初始化、逐算子/逐去噪步、继续、重置和旧快照拒绝。VRChat SDK Build & Test 已在真实无VR客户端运行32步，GPU回读对照通过（同一误差，约12.95秒）。

[GPU实际采样动画](results/gpu-trajectory.gif)、[数值报告](results/numeric.json)、[运行测试](results/runtime.json)与[训练样本](results/training-samples.png)均已保存。动画使用 class 3 的固定随机验证输入；地图普通生成使用GPU哈希随机种子。

多人控制和追赶协议已实现，真实多客户端网络交接、晚加入及掉线恢复尚未实测。不同GPU不保证逐像素一致，模型也会产生少量变形或错类数字。

训练代码、[最终检查点](ml/checkpoints/mnist-guided.pt)和[模型设计](docs/model-design.md)均可用于复现导出。工程场景默认处于交互待机，SDK自动运行仅用于验证包。
