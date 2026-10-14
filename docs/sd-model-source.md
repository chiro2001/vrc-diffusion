# Counterfeit-V3.0 模型来源与参考推理配置

当前推理权重以 Hugging Face 仓库 [`gsdf/Counterfeit-V3.0`](https://huggingface.co/gsdf/Counterfeit-V3.0) 的完整 checkpoint `Counterfeit-V3.0_fix_fp16.safetensors` 为准，固定在 revision [`d8ce63b3eb8e9102e54b3fcc07f9cb7a28ef30fc`](https://huggingface.co/gsdf/Counterfeit-V3.0/commit/d8ce63b3eb8e9102e54b3fcc07f9cb7a28ef30fc)。文件大小为 2,132,651,162 bytes，SHA-256 为 `a54c944e4c04e9d9ca43468ef5b90ea0408bb6264829a4980de79a768df7179f`。转换记录保存在 `/home/chiro/tmp/vrc-diffusion/sd-anime/fixed-diffusers/conversion.json`；当前完整 checkpoint 位于同级的 `Counterfeit-V3.0_fix_fp16.safetensors`。

本地使用 Diffusers 0.35.2 将完整 checkpoint 转换到 `/home/chiro/tmp/vrc-diffusion/sd-anime/fixed-diffusers/`，转换包含 UNet、VAE 和文本编码器，不取 EMA 权重，并执行键名与形状检查。转换配置和 tokenizer 沿用仓库 revision [`f79f307806896050ac376e46d8081d221b8aa93a`](https://huggingface.co/gsdf/Counterfeit-V3.0/tree/f79f307806896050ac376e46d8081d221b8aa93a)，其用途仅为提供模型结构、scheduler 和 tokenizer 配置；当前权重来自完整 fix checkpoint。导出的固定文本条件位于 `/home/chiro/tmp/vrc-diffusion/sd-anime/fixed-conditions/`。

## 旧 Diffusers 参考的核查结果

此前的参考流程直接使用固定 `f79f3078…` revision 中的 Diffusers 组件。当前测试生成的参考图出现明显噪声，因此改为从完整 fix checkpoint 自行转换。抽样检查也发现了关键权重差异：old checkpoint 与 fix checkpoint 的 UNet `conv_in`、`conv_out`、`conv_norm_out` 和时间嵌入权重逐元素一致；固定 f79 Diffusers UNet 对应层的数值则明显不同。例如 `conv_in.weight` 的标准差为 old/fix `0.065001`、f79 `0.029455`，二者 RMSE 为 `0.03555`；`time_embedding.linear_2.weight` 的标准差为 old/fix `0.002875`、f79 `0.001336`，RMSE 为 `0.001564`。

抽样的 VAE encoder 权重在 old checkpoint 与 f79 Diffusers 组件之间逐元素一致；fix checkpoint 的 VAE decoder 数值不同。例如 `decoder.conv_in.weight` 的 old/f79 标准差为 `0.10626`，fix 为 `0.10689`，RMSE 为 `0.02531`；`decoder.conv_norm_out.weight` 的 old/f79 标准差为 `0.13972`，fix 为 `0.21784`，RMSE 为 `0.13435`。这些比较是对指定张量的局部核查，不能单独证明噪声的因果来源。

旧 f79 文件的 SHA-256 已与 Hugging Face LFS 元数据核对一致；上述结果说明的是旧参考组件与完整 checkpoint 的关键权重不一致，不能据此断言旧 Diffusers 文件必定损坏。旧 revision 的模型卡与当前卡片一致，没有说明 `fix_fp16` 的具体改动范围。当前实现选择完整 fix checkpoint 并自行转换，以统一 UNet、VAE 和文本编码器的权重来源。

模型卡 front matter 声明 `creativeml-openrail-m`；模型仓库没有单独的 `LICENSE` 文件。完整 CreativeML Open RAIL-M 文本随项目保存在 [ModelLicense.txt](../Assets/DiffusionLab/SD/ModelLicense.txt)，场景控制器持有该 TextAsset。导出的 W8 参数、量化比例和预设条件属于对原权重格式的修改，来源、固定版本及修改方式记录在导出 model.json 与本文中。模型卡说明训练使用了 BLIP-2，并指出自然语言提示可能更有效；原始模型卡可在 [`f79f3078` revision](https://huggingface.co/gsdf/Counterfeit-V3.0/blob/f79f307806896050ac376e46d8081d221b8aa93a/README.md) 查看。

## 结构与采样配置

固定转换使用 SD 1.x 尺寸：512×512 输出对应 64×64×4 latent，VAE 每个空间维度下采样 8 倍。图像编码得到的 VAE latent 需乘以 `scaling_factor=0.18215` 进入扩散空间；本项目文生图直接从该空间的单位高斯噪声开始，不重复乘此系数。解码时将最终 latent 除以同一系数。`sample_size=64` 是 UNet 的 latent 尺寸，不是 64×64 像素输出。

| 组件 | 配置 |
| --- | --- |
| Scheduler | DDIM；训练步数 1000；`prediction_type=epsilon`；`beta_schedule=scaled_linear`；`beta_start=0.00085`、`beta_end=0.012`；`steps_offset=1`；`clip_sample=false`、`thresholding=false` |
| UNet | 4 个 latent 输入、4 个输出；通道 `[320,640,1280,1280]`；每个 block 2 层；cross-attention 维度 768；sample size 64；输入/输出卷积核 3×3 |
| VAE | RGB 3 通道 ↔ latent 4 通道；通道 `[128,256,512,512]`；sample size 512；`scaling_factor=0.18215` |
| CLIP text encoder | `CLIPTextModel`；hidden size 768；12 层、12 个 attention heads；词表 49,408；最多 77 tokens；FP16 源权重 |

## 离线文本条件

`ml/export_sd_conditions.py` 在 CPU 上从所选转换目录加载 tokenizer 和 CLIP，将 `last_hidden_state` 导出为 77×768 little-endian FP32 条件数据。Unity 运行时只选用预设，不接受任意文本输入，也不运行 tokenizer 或 CLIP。

四个正向预设使用统一前缀 `masterpiece, best quality, anime illustration, `，导出器会在编码前检查全部提示词不超过 CLIP 的 77-token 上限：

1. **动漫头像** — `masterpiece, best quality, anime illustration, A calm portrait of an adult anime woman with short dark hair, wearing a blue cardigan, gentle expression, soft window light, simple background.`
2. **春日海岸** — `masterpiece, best quality, anime illustration, A peaceful coastal village at sunrise, pastel houses beside a quiet sea, distant mountains, soft clouds, detailed anime landscape.`
3. **秋日街角** — `masterpiece, best quality, anime illustration, A small street cafe in autumn, falling leaves, warm light in the windows, wet pavement, a cozy anime illustration.`
4. **花园里的猫** — `masterpiece, best quality, anime illustration, A white cat resting on a windowsill and looking into a green garden in spring, soft morning light, a peaceful illustrated scene.`

负向预设为 `low quality, worst quality, low resolution, blurry, jpeg artifacts, bad anatomy, deformed anatomy, malformed hands, extra fingers, missing fingers, fused fingers, extra limbs, text, watermark, logo, nsfw, nude, nudity, explicit, pornographic, sexual content`，四个正向条件共用该负向条件。提示预览见[模型卡示例 01](https://huggingface.co/gsdf/Counterfeit-V3.0/resolve/f79f307806896050ac376e46d8081d221b8aa93a/images/01.png)、[示例 02](https://huggingface.co/gsdf/Counterfeit-V3.0/resolve/f79f307806896050ac376e46d8081d221b8aa93a/images/02.png)；原始设置见 [Civitai 页面](https://civitai.com/models/4468/counterfeit-v30)。
