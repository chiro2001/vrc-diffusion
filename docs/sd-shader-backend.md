# SD1.x 动漫 Shader 后端

模型选用 `gsdf/Counterfeit-V3.0` 完整 `Counterfeit-V3.0_fix_fp16.safetensors`，固定 revision `d8ce63b3eb8e9102e54b3fcc07f9cb7a28ef30fc`。checkpoint SHA256：`a54c944e4c04e9d9ca43468ef5b90ea0408bb6264829a4980de79a768df7179f`。本地转换器使用 Diffusers 的 LDM 转换函数，并逐项检查 686 个 U-Net、248 个 VAE 张量名称与形状；模型来源和许可见 `sd-model-source.md`。

旧 f79 Diffusers 组件在原生浮点参考推理中产生噪声；抽样 U-Net 参数与完整 checkpoint 差异显著。因此正式后端使用自行转换的完整 checkpoint，而不混用旧组件的权重。

## 计算路径

| 部分 | 精度与算法 |
| --- | --- |
| 282 个 U-Net 和 40 个 VAE Conv/Linear | W8 per-output-channel；动态对称 A8 per-tensor；真实 INT32 累加；反量化后写 FP16 |
| 激活量化 | GPU 分块 maxabs、归约、ties-away-from-zero、饱和 [-127,127]；RGBA8 编码 q+128 |
| GroupNorm / LayerNorm | FP32 分块 sum/sum-square、归约与仿射，写 FP16 |
| Attention | Q/K/V 投影 W8A8；QK、稳定 softmax 和 P×V 使用 FP32 分块计算；最终写 FP16 |
| SiLU / GELU / GEGLU / 残差 | FP32 计算，FP16 写回 |
| CFG / DDIM / 潜变量 | 浮点公式；不裁剪 x0；latent scale 0.18215；DDIM eta=0 |
| 条件编码 | 离线 CLIP 预设，77×768，四个正向主题和共用负向条件 |

这是 SM5 整数 Shader 路线；没有使用已验证的 DP4A 或 Tensor Core 入口，不能据 W8A8 名称承诺速度提升。动态 maxabs 避免静态范围失配和溢出，但量化仍需要实际生成质量验证。

## 内存与调度

张量采用 NHWC4 平铺布局，纹理行宽为 2 的幂。按最后使用位置回收输出纹理；每种原生尺寸保存独立的池定义，切换尺寸时释放前一组运行内存。U-Net 609 节点，VAE 119 节点；256 尺寸分别使用 23 和 16 个输出槽。

卷积一次只输出 8192 个 texel，再通过独立 scratch/目标 ping-pong 拼合，避免依赖 Blit 保留被 discard 的目标内容，也避免同一 RT 同时读写。attention 每次处理 64 个 key，保存 running max、sum 与未归一化加权 V，完整计算所有 key；没有把整张 N×N×heads 注意力矩阵常驻显存。

PNG 顶行与 Unity 导入后的 Texture2D 底行方向不同：Shader 的 weight Load 显式反转 y，参数与激活由原始数据上传，使用各自的平铺地址。权重导入禁用 sRGB、mip、压缩、NPOT 缩放和透明度处理。FP16 写入显式使用 f32tof16/f16tof32，保证边界舍入可以和参考对照。

## 地图行为

SD 演示站保留现有 MNIST 演示。共享配置包括 preset、seed、原生尺寸、步数、CFG 和运行 epoch；控制者运行时保持所有权，其他人可在停止或暂停时申请接管。每位访客依据共享配置在本地推理，暂停同步，迟加入者可补算同一 run。观察细节和提交速度是本地配置。

默认每约 1/6 个采样过程执行完整 VAE 解码，将真实中间图保存在八格历史中；20 步对应初始噪声、3/6/9/12/15/18 步预览和最终图。VAE 预览开关关闭额外中间解码，改为明确标注的线性 latent 预览，仍保留最后完整 VAE。细节开关独立控制实际中间纹理截图与网络模块高亮；它不会省掉模型必需的计算。两种开关均只影响本机。默认访客本地推理关闭，按本地推理按钮主动开启；生成者自动开启。调度的 frameBudgetMs 是 CPU 提交预算，不等同于纯 GPU 时间。

## 验证入口

- `ml/sd_reference.py`：显式图 / 原 Diffusers U-Net 对照、W8A8 参考生成、多 preset/seed 与可导入地图实际噪声。
- `AgentScripts/ValidateSDModel.cs`：Shader 状态、逐节点 Unity GPU 数值对照、地图噪声读回。
- `AgentScripts/BuildSDModel.cs`：Editor 导入与资产制作；`Prepare` 先创建 Udon program 并请求编译，再导入/构建。
- `AgentScripts/BuildSDPanel.cs`：在现有场景制作平面射线交互演示站。

实际 Udon / VRCGraphics 已完成两个 256×256、20 步样本：seed1234 在关闭细节、仅最终解码时耗时 91.88 秒；seed2345 在细节开启并加入六次中间 VAE 解码后耗时 111.54 秒。两者均输出可辨认的动漫人物；因 seed 和运行环境不同，这不是严格开关性能对照。

609 节点完整 Shader U-Net 与同一量化图参考的 epsilon RMSE 为 0.00999618，reference RMS 为 1.0360947，maxAbs 为 0.0458984，无 NaN/Inf。这个约 1% 的单次前向误差不能视为完整采样轨迹误差。地图 seed2345 同一真实初始噪声的 CPU W8A8 参考与 Shader 最终 latent RMSE 为 0.39419（reference RMS 0.99707）；图像主体和构图一致，局部细节不同。

512×512 CPU 质量批次覆盖四个预设与两个 seed，八张均生成可辨认主题。同初始噪声的浮点混合累加与 W8A8 对照保留主题，但姿态、衣物和取景有可见差异；不能称为无损。CPU 浮点对照使用 F32 卷积累加 / F16 写回，不是原生 CPU FP16。

原生 512×512 / 20 步 / seed3456 已在主地图的真实 Udon PlayMode 完成，耗时 357.74 秒，包含六次中间 VAE 解码，运行中途启用细节；最终 RGB 无 NaN/Inf。测试使用 RTX 5080 / D3D11、每帧至多 32 次提交、8 ms CPU 提交预算，用户其他 GPU 进程保留。以上是实测墙钟时间，不是纯 GPU 时间或默认速度的性能承诺。参见[实际最终图](../results/sd/playmode-512.png)和[去噪序列](../results/sd/trajectory-512.png)。

同一地图初始噪声的 512 CPU W8A8 参考也已完成；完整采样最终 latent RMSE 为 0.68817（reference RMS 0.88979），[并排图](../results/sd/same-noise-reference-512.png)保留相同人物主题，局部细节有差异。
