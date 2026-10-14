# 公开预训练扩散模型与 VRChat Shader 参考

调研日期：2026-10-14。权重与源码副本保存在 `~/tmp/vrc-diffusion/pretrained/`，不在 Unity 工程内。

## 推荐的 MNIST 质量基准

目前最适合作为原生 28×28、类别可控质量基准的是 [TeaPearce/Conditional_Diffusion_MNIST](https://github.com/TeaPearce/Conditional_Diffusion_MNIST)，源码固定在 commit [`657b49854bbf844caa5580177243ae607c79339f`](https://github.com/TeaPearce/Conditional_Diffusion_MNIST/tree/657b49854bbf844caa5580177243ae607c79339f)。仓库声明 MIT，许可证文件署名 `Copyright (c) 2022 Tim Pearce`。它带有按数字类别生成的 checkpoint、采样代码和 guidance 对照图。作者样图在 `w=0、0.5、2.0` 下显示了可辨认的 0–9（[样图](https://raw.githubusercontent.com/TeaPearce/Conditional_Diffusion_MNIST/657b49854bbf844caa5580177243ae607c79339f/guided_mnist.png)）；这是定性证据，没有报告 FID。样图没有 seed，也没有证明是从下方这个 checkpoint hash 生成，因此需要先用下载的 checkpoint 固定 seed 生成本项目自己的参考输出。

我下载并检查了 `pretrained_model.zip`。归档大小为 24,375,189 字节，SHA-256 为 `968f651891ec5f0ae2c9fa90c100d4c55b177aeeefed0fc0ad9df890c1be01d6`，ZIP CRC 校验通过。解出的 [model_39.pth](https://github.com/TeaPearce/Conditional_Diffusion_MNIST/blob/657b49854bbf844caa5580177243ae607c79339f/pretrained_model.zip) 为 26,418,492 字节，SHA-256 为 `1984d5c0940d5f4256d9793cc0f00e07dfaa471c6ffe88046a3579bf2658a496`。隔离副本位于 `~/tmp/vrc-diffusion/pretrained/Conditional_Diffusion_MNIST/`。

checkpoint 是 PyTorch `OrderedDict`，含 135 个 tensor。网络键均以 `nn_model.*` 开头；另有 `alpha_t`、`oneover_sqrta`、`sqrt_beta_t`、`alphabar_t`、`sqrtab`、`sqrtmab`、`mab_over_sqrtmab` 七个扩散表，每个长度为 401，因此能从权重确认扩散时间步数 `T=400`。按网络权重和偏置计，模型有 6,585,601 个可训练参数。权重含 `cuda:0` 位置标记，CPU 检查时应使用 `map_location="cpu"`。checkpoint 没有自描述配置；从仓库脚本和 tensor shape 可确认构造参数是 `ContextUnet(in_channels=1, n_feat=128, n_classes=10)`，DDPM 的 beta 从 `1e-4` 线性增长到 `0.02`，训练时 class dropout 为 `0.1`。

这个模型不是当前 146k 参数小 U-Net 的可替换权重：需要按原结构实现 28×28 输入、两级降采样、残差卷积、time 与 one-hot class MLP、GroupNorm、skip concat、转置卷积和 CFG。卷积后用了 BatchNorm+GELU，瓶颈/输出路径用了 GroupNorm+ReLU；有 MaxPool、AvgPool 和 ConvTranspose2D，没有 self-attention。训练输入来自 torchvision `ToTensor()`，数值范围是 `[0,1]`；仓库为展示黑字白底样图使用 `1-x` 反相。移植时须保持模型输入范围和呈现反相的区别。

采样复现设置是 `n_T=400`、DDPM ancestral sampler、标签 0–9、固定 PyTorch CPU seed，并为每一步保留随机噪声。源 `sample()` 没有显式 generator 参数，内部用 `torch.randn` 生成初始噪声及各步 `z`；同一 seed 和 CPU 环境可重建 PyTorch 样本。Unity 的 hash/Box–Muller 和 Torch RNG 不同，不能期待同 seed 的逐像素相等，应先导出固定 seed 的初始噪声、每步噪声或中间轨迹作为 shader fixture。Tea 代码的 guidance 写作 `(1+w) * eps_class - w * eps_null`，等价于项目公式 `eps_null + scale * (eps_class - eps_null)`，其中 `scale=1+w`；作者 `w=2` 对应项目 `guidanceScale=3`。

有一项来源元数据需要保留：作者 README 称训练约 20 epochs（约 20 分钟），但随仓库 checkpoint 命名为 `model_39.pth`，当前脚本设 `n_epoch=20` 且 `save_model=False`。因此权重文件来源与 MIT 许可明确，但训练该确切文件的 epoch/更新数无法由当前提交核实；不要把 README 的约 20 epochs 当成该 checkpoint 的已验证步数。

## 下载并检查过的备选模型

| 模型与来源 | 架构、尺寸与权重 | 训练和采样 | 质量证据与适配判断 |
|---|---|---|---|
| [TeaPearce/Conditional_Diffusion_MNIST](https://github.com/TeaPearce/Conditional_Diffusion_MNIST)；MIT；源码 commit `657b49854bbf844caa5580177243ae607c79339f` | 28×28 单通道，类别条件 + CFG；6.586M 参数；`.pth` 26.42 MB | checkpoint 表确认 400 步；仓库作者称约 20 epochs，但确切 checkpoint epoch 不明；线性 beta 的随机 DDPM，`guide_w=2` | 作者图按 0–9 展示了可辨认数字和 guidance 影响；无 FID、无样图 seed。适合作为原生 MNIST 质量基准；shader 移植工作量高且每步较重。 |
| [DD-65/diffusionnumbers](https://huggingface.co/DD-65/diffusionnumbers)；权重 MIT；训练源码 [diffusionnumbers-code](https://github.com/DD-65/diffusionnumbers-code)；模型 commit `9012c9d7bc43f3a2cf968b207f11fd4022541642`，源码 commit `98e059c9a82a38ab7ec6ac5b5ad6bc1f2f351db9` | 64×64 单通道；3.941M 参数；Safetensors 15,790,724 字节，SHA-256 `5e54c11a27dd40c56a8a9dcd94dbf9ac189e1d05ccbe3d76102cdc3705cd9197` | 60k MNIST，作者报告 40 epochs、batch 128、AdamW 2e-4，约 18,800 更新（按 469 batches/epoch 推算）；1000 步、linear beta、epsilon prediction；每一步传类别 embedding。训练源码 MIT。 | Hugging Face 卡片提供 [2、4、8 的生成图](https://huggingface.co/DD-65/diffusionnumbers/tree/9012c9d7bc43f3a2cf968b207f11fd4022541642/samples)，也明确写出输出可能畸形或含糊；无 FID。Diffusers U-Net 使用 residual、GroupNorm、SiLU、attention 和 nearest+conv 上采样，算子较接近 shader U-Net，但分辨率是当前两倍；模型无 CFG。副本位于 `~/tmp/vrc-diffusion/pretrained/huggingface/DD-65-diffusionnumbers/`。 |
| [1aurent/ddpm-mnist](https://huggingface.co/1aurent/ddpm-mnist)；模型卡 MIT；Hub commit `e6ee37a720cd5c9b48fa5be809e5926f49f38246` | 28×28 单通道；1.065M 参数；Safetensors 4,288,156 字节，SHA-256 `5af709d4b2efeda2a3c1f5015bbaf2f8bfad5b82d6ea63efbf4f6b7188a5c0b4` | 作者称在 L4 上训练约 40 分钟；Diffusers DDPM 1000 步、linear beta；无条件生成。 | 是下载体积最小者，架构为 `[32,64,32]` 通道、每 block 两个 ResNet、GroupNorm、SiLU、attention 和 nearest+conv 上采样。但不能指定数字，也没有 CFG。模型卡引用的图是原始 MNIST 示例，不是生成结果；目前没有足够的公开定性证据把它选作质量基准。副本位于 `~/tmp/vrc-diffusion/pretrained/huggingface/1aurent-ddpm-mnist/`。 |
| [mlvar/ddpm-cifar10](https://huggingface.co/mlvar/ddpm-cifar10)；模型卡 MIT；Hub commit `a00ba41801e081968b969d9e1f7b2b1d77004539` | CIFAR 原生 32×32 RGB；26.820M 参数；Safetensors 107,307,420 字节，SHA-256 `00c1845055b66d57b831ef9dab3d7cee562dfe2980195364f38d82fafcd4fff2` | 50k CIFAR-10，作者报告 300 epochs / 58,500 updates、batch 256；1000 timestep。 | 作者提供相同 seed 的训练进度图；[最终 32×32 样图](https://huggingface.co/mlvar/ddpm-cifar10/resolve/a00ba41801e081968b969d9e1f7b2b1d77004539/sample_step_58500.png) 能看出物体类别，但作者说明未评估 FID。它使用 GroupNorm、SiLU、残差块、attention 和 nearest+conv，可供 32×32 RGB 扩展参考；体积和参数远超当前 VRC MNIST 预算，不建议作为当前目标权重。副本位于 `~/tmp/vrc-diffusion/pretrained/huggingface/mlvar-ddpm-cifar10/`。 |

HF 文件通过模型 revision SHA 固定下载，`Safetensors` 的 tensor 个数与总元素数和 Hub 元数据吻合。Tea checkpoint 另行验证了 ZIP CRC，并只读解析了 tensor key/shape；本次没有训练模型，也没有启动 Unity 或 VRChat 客户端。

## VRChat 中运行 shader 模型的参考

- [SCRN-VRC/SimpNet-Deep-Learning-in-a-Shader](https://github.com/SCRN-VRC/SimpNet-Deep-Learning-in-a-Shader)，MIT，仓库 commit `0b472f754abd8b78191eabd80ced80ff929606a0`。README 明确称 HLSL 版本在 VRChat 内运行，并提供[可启动的 VRChat world 链接](https://vrchat.com/home/launch?worldId=wrld_4cbc4ccb-3c0d-419b-bc8b-e370c951edd2)。项目含 Python/Keras、C++/OpenCV 和 HLSL 三套实现，以及四套分类器权重；shader 用纹理保存权重/中间激活并按层推进。README 仍要求旧 VRC SDK 2，并注明尚未转换为 Udon，因此它是 fragment shader + VRChat 的直接先例，不能直接复用为当前 SDK/Udon 扩散实现。
- [lox9973/ShaderGPT](https://gitlab.com/lox9973/ShaderGPT)，MIT，源码 commit `99bf46ed6ccdb4ada05526ea115a315b56e00f88`。仓库有 `Example/Udon.unity`、烘焙模型资产和 UdonSharp 组件；README 说明把 Hugging Face 权重转成纹理并用 pixel shader / `Graphics.Blit` 推理，示例场景与 VRChat/Udon 兼容。它实际支持的是 Transformer/VITS，不是 U-Net 或 diffusion；参考价值在于模型权重纹理化、预烘焙和 Udon 调度。
- [VRChat VRCGraphics 文档](https://creators.vrchat.com/worlds/udon/vrc-graphics/)说明 Udon 可通过 `VRCGraphics.Blit` 执行材质 shader 并写入目标 RenderTexture；这是当前项目采用的运行时接口文档。

## 隔离下载清单

- Tea 模型、源码、样图和解出的 checkpoint：`~/tmp/vrc-diffusion/pretrained/Conditional_Diffusion_MNIST/`
- 1aurent、DD-65、mlvar 的模型配置、Safetensors 和样图：`~/tmp/vrc-diffusion/pretrained/huggingface/`
- DD-65 训练/推理源码：`~/tmp/vrc-diffusion/pretrained/diffusionnumbers-code/`
- VRChat shader 参考源码：`~/tmp/vrc-diffusion/pretrained/SimpNet-Deep-Learning-in-a-Shader/`、`~/tmp/vrc-diffusion/pretrained/ShaderGPT/`
