# 模型、采样与实时显示设计

本文描述当前 `Assets/DiffusionLab` 源码和 Editor 构建器中的实现。Tea 的 GPU 数值对照、seed 批次抽查和单机 Udon 采样已有报告；这些结果不代表真实多客户端网络验证完成。

## 模型注册表

`DiffusionController.models` 持有每个模型各自的算子材质、RFloat 特征图、采样状态、历史纹理和输出纹理。`BuildDiffusionModels.Import` 保留既有 Legacy 模型，并从 `Assets/DiffusionLab/Data/Tea/` 导入 TeaPearce 模型；当前默认选择 Tea。每个模型的属性集中在 `DiffusionModel`，切换模型会重新绑定对应的算子和 RenderTexture。

| 属性 | Legacy MNIST 32 | TeaPearce MNIST 28 |
| --- | --- | --- |
| 来源 | 工程内训练的条件 DDPM；保留用于对比 | [TeaPearce/Conditional_Diffusion_MNIST](https://github.com/TeaPearce/Conditional_Diffusion_MNIST)，MIT，源码 commit `657b49854bbf844caa5580177243ae607c79339f` |
| checkpoint / 参数 | 146,177 个模型参数 | `model_39.pth`：6,585,601 个参数；SHA-256 `1984d5c0940d5f4256d9793cc0f00e07dfaa471c6ffe88046a3579bf2658a496` |
| 原生尺寸与数值域 | 32×32，训练域 `[-1, 1]` | 28×28，训练域 `[0, 1]` |
| shader 图 | 11 个卷积层 | 27 个显式算子节点 |
| 时间表 | 256 项 alpha，默认 32 步，leading spacing | 401 项 alpha；采样时间 `t=400..1`，默认 64 步，线性间隔 |
| CFG / `x₀` 裁剪 | CFG scale 3；裁剪到 `[-1, 1]` | CFG scale 3；不裁剪 `x₀` |
| 导出权重 | 1,019,233 个 packed float32 值；版本 `1021740377` | 9,799,553 个 packed float32 值，39,198,212 字节（约 39 MB）；模型版本 `692248240` |

Tea checkpoint 的训练来源和确切训练轮数说明见[预训练调研](pretrained-research.md)。导出的 `Data/Tea/model.json` 同时记录格式版本 2、来源 commit、许可、checkpoint hash、alpha 表、算子图和权重布局。`modelVersion` 是对 packed `weights.bytes` 做 SHA-256 后按小端整数取首 4 字节并清符号位得到；它与源 checkpoint hash 是不同的校验值。

## Tea 的 27 节点算子图

网络保持 TeaPearce 的 `ContextUnet(in_channels=1, n_feat=128, n_classes=10)` 结构。类别条件和时间嵌入对上采样特征做逐通道调制；解码器在对应分辨率拼接编码器 skip，输出末端再拼接初始特征。空间路径为 28×28 → 14×14 → 7×7 → 1×1，再回到 7×7、14×14 和 28×28。

| 算子类型 | `Operator.shader` 分支 | 用途 |
| --- | --- | --- |
| 卷积 / 转置卷积 | op 0 / 1 | 3×3 特征卷积；上采样使用训练模型的转置卷积 |
| 残差合并 | op 2 | 两个分支按导出比例合并 |
| 最大池化 / 平均池化 | op 3 / 4 | U-Net 降采样和全局向量路径 |
| GroupNorm 统计 / 归一化 | op 5 / 6 | 先输出每组均值、方差，再归一化、仿射变换并 ReLU |

BatchNorm 的运行统计已在导出时折叠进卷积权重和偏置；GroupNorm 保留为显式图节点。GELU 使用 shader 中的 erf 近似。每个节点保存自己的输入/skip 索引、形状、通道数、权重偏移、条件表偏移和激活元数据。`DiffusionTeaching` 根据这些索引绘制真实算子图：当前节点用青色高亮，skip 边用金色显示。

导出权重不仅包含卷积参数，还包含类别/时间条件的 scale 与 bias 查找表，因此 packed float32 数量高于 checkpoint 的可训练参数数。纹理宽度为 2048，Builder 从 `weights.bytes` 加载 RFloat 权重纹理，并按 JSON 偏移为每个算子配置材料。

## 采样与张量状态

两种模型都以整数 seed 初始化噪声。shader 使用整数 hash 与 Box–Muller 变换；CPU 评估脚本 `ml/seed_noise.py` 复现同一套 seed/pixel 输入。PyTorch fixture 的随机数流不同，不能直接把 PyTorch `randn` 和 shader 噪声当作逐像素相等的输入。

Tea 每一步分别计算有类别条件和空类别分支的 epsilon，再做 CFG：

```text
ε_guided = ε_null + guidanceScale × (ε_class − ε_null)
```

`guidanceScale=3`。随后使用确定性 DDIM（η=0）计算预测图像和下一状态：

```text
x₀     = (x_t − √(1−ᾱ_t) × ε_guided) / √ᾱ_t
x_next = √ᾱ_next × x₀ + √(1−ᾱ_next) × ε_guided
```

Tea 从 401 项累计 alpha 表按 `t=400..1` 选取离散采样点，最后一步的 `ᾱ_next` 设为 1；`x₀` 不裁剪。Legacy 使用 256 项 alpha 表和 leading spacing；32 步时从 248 递减到 0，并把预测裁剪到 `[-1,1]`。采样步数按钮当前提供 16、32、64；运行时对输入步数按模型时间范围限幅。

Controller 的一步被拆成五个可观察阶段：

1. **CLASS**：运行条件分支全部算子并保存 `ε_class`。
2. **NULL**：运行空条件分支并保存 `ε_null`。
3. **CFG**：写出组合后的 `ε_guided`。
4. **`x₀`**：计算干净图像预测。
5. **DDIM**：写入 `x_next` 并交换 ping-pong 状态纹理。

CFG 分支中的每个算子由单独的 `VRCGraphics.Blit` pass 执行，因此 Tea 每步包含 27 个 CLASS pass、27 个 NULL pass 和 3 个采样更新 pass。**STEP OPERATOR** 安排一个 pass；**STEP DENOISE** 安排运行到下一完整去噪步；**INIT / PAUSE / RUN** 控制运行阶段。

## 模型原生尺寸与输出尺寸

每个模型有四个显示输出：native、32、64 和 128。Tea 的 native 是 28×28，Legacy 的 native 是 32×32；其他输出由 `Sampling.shader` 的输出模式按最近邻方式读取原生纹理。输出放大服务于面板布局，不会把 28×28 或 32×32 推理变成更高分辨率模型。

每个特征层用 RFloat RenderTexture 保存：宽度为 `层宽 × 输出通道数`，高度为层高；每个通道在纹理中占一条水平区域。教学板读取模型中真实的 `memory` 和 `channels` 数组显示通道网格，`MEM < / >` 也切换这些实时特征图。每个模型另有独立的 state A/B、`x₀`、条件/空条件/引导噪声、8 张历史快照及四种输出纹理。

教学板的像素探针使用 `Probe.shader` 从真实输入状态、`ε_class`、`ε_null`、`ε_guided`、`x₀`、`x_next` 和选中通道采样 7 个值到 7×1 RFloat 纹理，再通过 `VRCAsyncGPUReadback` 显示数字。探针只在完整采样步后请求读回；它不是随时间插值的装饰动画。

## 同步协议与本地 GPU

`schemaVersion=2` 定义当前同步配置结构。`modelIndex`、`outputIndex`、类别、整数 seed、总步数、完成步数、epoch/revision、phase 和控制权等配置由 Udon 同步；`networkModelVersion` 与本地合成 `modelVersion` 用于兼容性检查。版本或 schema 不匹配时 Controller 停止应用运行状态。

同步字段描述所有玩家采用的条件和公开进度。RenderTexture、特征通道、噪声和中间 DDIM 状态不通过网络传送；每个客户端按共享配置在自己的 GPU 上执行。状态标签分别显示 shared completed steps 与 local steps，后加入客户端可按 seed 追赶。不同 GPU 的浮点实现可能带来像素差异，seed 相同不代表跨设备逐像素一致。

## 交互和四页教学

主界面是 world-space UGUI Canvas，使用 interaction layer 17、`GraphicRaycaster`、`VRCUiShape` 和 `BoxCollider` 接收 VRChat 射线。按钮提供模型、输出尺寸、类别、seed、步数、生成/暂停/单步、特征通道和控制权操作。

`DiffusionLocalization` 为每条文本准备 English、简体中文和日文 TMP 展示层。AUTO 读取本地 VRChat 语言并响应语言变化；手动选择只修改该玩家本机，不参与 Udon 同步。Editor 构建器根据 `Localization/catalog.json` 与 `teaching.json` 生成静态 TMP 字体和覆盖文本。

教学板正文由 `Localization/teaching.json` 提供，包含四页：

1. 正向加噪，反向生成。
2. 可观察的 U-Net 内部。
3. 引导与一次 DDIM 更新。
4. 原生像素与多人共享。

每页同时提供英文、简体中文、日文标题和正文。板面实时显示算子图、当前张量形状、五个采样阶段以及像素探针读回值。

## Editor 构建顺序

先停止 Play Mode 并保存要保留的场景编辑。构建器按以下顺序执行：

1. `BuildDiffusionLab.Compile` 编译 UdonSharp 程序。
2. `BuildDiffusionLab.Build` 建立基础模型和主场景，并保存资产/场景。
3. 保存基础场景后运行 `BuildDiffusionModels.Import`，登记 Legacy 与 Tea 两种模型并保存。
4. `BuildDiffusionTeaching.Apply` 建立教学板；`BuildDiffusionLocalization.Apply` 生成三语 TMP 覆盖层。两者都会保存场景。
5. 回到 Unity Editor 确认场景与导入完成后，使用 PlayMode 验证交互和实际 GPU 纹理。

`BuildDiffusionModels.Import` 要求已保存的基础场景且模型数组尚为空。若重新运行 `BuildDiffusionLab.Build`，需要再运行模型导入、教学板和本地化构建。项目级部署命令见 [README](../README.md)。

## 当前验证状态

- [两模型数值报告](../results/models-numeric.json)在 RTX 5080 / D3D11 上通过逐算子与采样轨迹对照，无 non-finite 值。Legacy 32 步最终 `maxAbs=4.45246696472168e-5`；Tea 64 步最终 `maxAbs=4.202127456665039e-6`。Tea 中间算子与轨迹项也逐项记录，不能把最终帧误作所有中间张量的最大误差。
- [CPU 评估 JSON](../results/pretrained-quality.json)包含 32/64/128 步各 100 张图，覆盖 10 类 × 10 个 seed；[64 步网格](../results/pretrained-quality-64.png)整体可辨。另有 10 个类/seed 配对以实际 shader hash 噪声和 D3D11 图计算交叉核对：[GPU 批次报告](../results/gpu-seed-batch.json)标记通过，[样本网格](../results/gpu-seed-batch.png)最大 `maxAbs=2.3066997528076172e-5`，没有 non-finite 值。此抽查不保证所有 seed 都能生成清晰或正确类别的数字。
- 单机 Udon `VRCGraphics` 实际采样以 class 3、seed 1234 完成 Tea 64 步、65 帧：[轨迹报告](../results/play-trajectory.json)、[GPU 轨迹动画](../results/pretrained-gpu-trajectory.gif)、[最终图像](../results/pretrained-gpu-final.png)。[PlayMode 运行报告](../results/runtime.json)标记通过，覆盖同 epoch 输出切换、状态重放/陈旧快照、像素探针和教学公式；公式误差最大为 `2.98023224e-8`。
- UI 的 [EditMode 报告](../results/ui-editmode.json)和[PlayMode 报告](../results/ui-playmode.json)均标记通过，记录 43 个按钮、2 个 Canvas、462 个 TMP 文本覆盖、43 个按钮射线、三语切换，以及 seed 草稿经过 400 ms 后仍保留。中文实时纹理截图：[主面板](../results/world-main.png)、[斜视图](../results/world-oblique.png)、[教学板](../results/world-teaching.png)。
- Legacy 保留作对比，不建议作为默认模型；旧版本样本有较多无效或难辨认输出，旧文档“少量变形”的结论不再代表当前状态。CPU 网格可辨不等于所有 seed 都有质量保证。
- PlayMode 的状态重放和陈旧 revision 检查已有覆盖；这不是实际 VRChat 多客户端所有权交接验证。请求/授予、晚加入、掉线恢复及慢端追赶仍需真实多客户端测试。本轮没有进行 NPU 训练。
