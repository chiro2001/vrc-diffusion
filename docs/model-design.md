# 模型、采样与实时纹理设计

## 当前模型产物

模型面向 MNIST 的 32×32 单通道图像。训练输入是 28×28 MNIST，先映射到 `[-1, 1]`，再四边各补 2 像素，补充值为 `-1`。模型预测每个扩散 timestep 的噪声 `epsilon`。

网络是固定图的 11 层卷积 U-Net 变体，空间分辨率依次为 32、16、8、16、32。上采样使用 nearest，解码段在两处拼接早期特征：第 7 层接第 3 层，第 9 层接第 1 层。前 10 层接收时间/类别条件，最后一层输出 1 通道噪声预测。

| 层 | 分辨率 | 输入 → 输出通道 | 操作 |
|---:|---:|---:|---|
| 0 | 32×32 | 1 → 16 | 3×3 卷积 |
| 1 | 32×32 | 16 → 16 | 3×3 卷积 |
| 2 | 16×16 | 16 → 32 | stride 2 卷积 |
| 3 | 16×16 | 32 → 32 | 3×3 卷积 |
| 4 | 8×8 | 32 → 64 | stride 2 卷积 |
| 5 | 8×8 | 64 → 64 | 3×3 卷积 |
| 6 | 16×16 | 64 → 32 | nearest ×2 后卷积 |
| 7 | 16×16 | 64 → 32 | 与第 3 层拼接后卷积 |
| 8 | 32×32 | 32 → 16 | nearest ×2 后卷积 |
| 9 | 32×32 | 32 → 16 | 与第 1 层拼接后卷积 |
| 10 | 32×32 | 16 → 1 | 输出卷积，无激活 |

当前最终检查点来自两阶段训练：先用归一化模型训练到 10,000 iter，再从该 checkpoint warm-start，增加 null 类和 classifier-free dropout，继续训练 12,000 iter。guided 阶段以 0.1 概率把类别替换为 null 类 `10`。导出的 `trainingIteration: 12000` 记录 guided 阶段的轮数；模型来源是 10k normalized checkpoint 加 12k guided 更新。最终 checkpoint 有 146,177 个模型参数。

每层卷积后（最终输出层除外）训练时使用 BatchNorm。导出时将每层的 running mean、running variance、scale 和 bias 折叠进卷积权重与偏置；Unity shader 不运行 BatchNorm。EMA 权重用于生成 checkpoint 和样本。

## 类别条件与采样

可选类别是 `0..9`；类别 `10` 专作无条件分支，不显示为用户可选数字。时间正弦编码与 32 维类别 embedding 相加，再经 MLP 生成各卷积层的逐通道条件偏置。导出程序把 11 个类别、256 个训练 timestep 的条件偏置表写进权重数据，shader 按 `(class, timestep, channel)` 索引。

最终导出启用 classifier-free guidance，默认 guidance scale 为 `3.0`。每个反向采样步分别得到条件预测和 null 类预测，组合方式为：

```text
epsilon = epsilon_null + guidanceScale * (epsilon_class - epsilon_null)
```

噪声日程有 256 个 cosine `alphaCumprod` 值。Unity 默认运行 32 步的 DDIM eta=0；`timestepSpacing` 为 `leading`，因此 32 步的 timestep 是 `248, 240, …, 8, 0`。Controller 可设置 4 到 128 步，具体索引由总步数和已完成步数算出。每步先由预测噪声计算并裁剪 `x0` 到 `[-1,1]`，再按 eta=0 更新到下一个 alpha；中间步不再抽取随机噪声。

共享 seed 是整数。Controller 把低 16 位和高 16 位分别写入 `_SeedLo` / `_SeedHi`，shader 再恢复为 32 位整数，避免把大 seed 先转成单精度 float 后丢失低位。初始噪声按 `(seed, pixelX, pixelY)` 经过整数 hash，再用 Box–Muller 变换成高斯值。Unity 每台客户端都用相同整数输入重建本地噪声；GPU 浮点实现仍可能产生小幅像素差异。

训练脚本里的 PyTorch 随机数流与 Unity shader 的逐像素 hash 不是同一个随机序列。因此数值 fixture 使用固定的 `reference-input.bytes` 作为初态，再比较卷积层和 DDIM 各步结果；它不会把两个不同的 RNG 当成应逐像素一致。

## 导出格式与版本门

当前 [model.json](../Assets/DiffusionLab/Data/model.json) 记录 11 层结构、权重偏移、条件表偏移、256 个 alpha 值、尺寸、采样设置和版本。`weights.bytes` 是小端 float32 原始数据，共 1,019,233 个值、4,076,932 字节；纹理宽度为 1024，Builder 将数据加载为 point-filtered RFloat 权重纹理。

`modelVersion` 是对完整 packed weight bytes 求 SHA-256，再取前 4 字节小端整数并清除符号位得到的版本号。当前值为 `1021740377`。`BuildDiffusionLab` 会重新计算并校验该值，然后把它同时写入 Controller 的本地 `modelVersion` 和同步字段 `networkModelVersion`。Controller 在应用共享状态和运行时比较两者；版本或 `schemaVersion` 不匹配时会停止计算并提示不兼容。

当前导出元数据为：`trainingIteration=12000`、`normalizationFolded=true`、`classifierFreeGuidance=true`、`guidanceDefault=3.0`、`timestepSpacing=leading`、`timesteps=256`、`size=32`、`weightCount=1019233`。改权重或模型格式时应重新导出并构建，让版本门随数据更新。

## 实时 RenderTexture 显示

Unity 运行时以 `VRCGraphics.Blit` 执行每层卷积和采样更新，显示的是本机真实 RenderTexture，不是训练时写入的 PNG：

- `stateA` / `stateB` 以 ping-pong 方式保存当前 32×32 状态 `x_t`。
- `cleanPrediction` 保存预测的 `x0`；`conditionalNoise` 和 `guidedNoise` 分别保存条件噪声与 CFG 组合后的噪声。
- 每层 feature memory 是 RFloat RenderTexture，宽度为 `分辨率 × 输出通道数`、高度为该层分辨率。卷积 shader 把每个通道的空间图横向排成一条带。
- `MemoryDisplay.shader` 将横向通道带重新排成接近方形的通道网格。例如 16 通道显示为 4×4，32 通道显示为 6×6（空余格留暗），64 通道显示为 8×8。带符号的激活以橙色表示正值、蓝色表示负值。
- `historyMemory` 保存最多 8 个采样阶段的本地快照，供历史条带查看；它也不是网络同步纹理。

Builder 将主图、`x0`、噪声、当前层激活和 8 个历史 RT 绑定到对应显示材质。选择不同层只改变展示的 RT 与通道数，不会改动采样状态。多人的同步状态只包含配置和离散步数，每台客户端独立重建这些 RT；RT 本身不会因 `UdonSynced` 自动传到其他玩家。

## Fixture 与生成质量边界

导出目录包含固定输入、每层参考值和 32 步 DDIM 轨迹；`reference-step-00..32.bytes` 共 33 个状态，含初始噪声与最终结果。Controller 的 `fixtureRun` / `validationRunOnStart` 用于该已知输入的 shader 数值校验，比较 1024 个输出值并以最大绝对误差 `< 0.003` 判定通过。`fixtureRun` 是测试夹具标记，不是游戏生成结果或 RenderTexture 的持久快照格式。

当前一次实际 GPU fixture 报告在 [numeric.json](../results/numeric.json)：RTX 5080、Direct3D11，`modelVersion=1021740377`，CFG 已启用，报告 `passed=true`，没有 non-finite 值；第 32 步与参考数值的 `maxAbs` 为 `4.45247e-5`。 [gpu-trajectory.gif](../results/gpu-trajectory.gif) 和 [gpu-final.png](../results/gpu-final.png) 直接由 `Reports/DiffusionLab/GpuTrajectory/step-00..32.bytes` 中的 33 个实际 GPU float32 RT 状态生成，使用 class 3 固定 fixture；它们不是 PyTorch 参考图。`training-samples.png` 则是最终 12k guided 训练样本网格，与该 GPU fixture 轨迹分开。

训练样本网格用于观察趋势，不代表每个随机样本都会成为标准手写数字；一些类别或样本仍会模糊、变形或难以辨认。类别条件表示生成目标，不提供逐张图正确分类的保证。
