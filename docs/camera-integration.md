# 可拾取相机与原生拍照相机集成

## 原始相机包

本功能基于 Fairplex 的付费 BOOTH 商品 [Polaroid Camera for VRChat World](https://fairplex.booth.pm/items/3864886)（商品编号 **3864886**），版本 **v1.6**。

用户指定的 UnityPackage 路径：

```text
E:\BaiduNetdiskDownload\地图基本功能合集-初音的VRC小店\3864886相机\Polaroid.unitypackage
```

同目录 `Polaroid_1.6\Polaroid.unitypackage` 是相同副本。SHA-256：

```text
ec337265d68e0853fa2396d53192b9eb81b427fdbfffded8c9d9911eed671f01
```

包内资源位于 `Assets/Polaroid/`。GiantSleepWorld 中已导入到 `Assets/相机/` 的 `PolaroidManager.cs` 和 `PolaroidPickup.cs` 与 v1.6 包内源码逐字节相同。集成使用旁路桥接组件读取相机状态和 RenderTexture，保留付费包原脚本不变。

BOOTH 许可允许个人用途修改，也允许在一次性付费项目中使用；不允许转售或直接分享其任何部分（修改版也一样），也不允许用于以付费墙限制访问的项目。商品页保留更改许可条款的权利。该包可留在项目的本地 Git 双端同步中；目前没有公共仓库，不能把 UnityPackage 或其内容放进公开仓库或公开分发。

安装依赖 VRCSDK 和 UdonSharp。项目通过 VCC 提供相应的 VRChat Worlds SDK / UdonSharp。

## 将拍立得照片接入 img2img

原包的入口关系如下：`PolaroidPickup.OnPickupUseDown()` 调用 `PolaroidManager.SetPhotoIndex()`；相机管理器递增同步的 `photoIndex`，随后对索引 `photoIndex - 1` 调用 `TakePhoto()`。拍摄时 `cameraPhoto.targetTexture` 指向 `renderTexture[idx]`，相纸 RT 为 **512×512**。约 0.25 秒后 `_SnapDone()` 会将目标切换到取景镜头 RT（256×256），所以桥接器应依据 `photoIndex - 1` 记录槽位，并在该槽位完成相机渲染后读取 `renderTexture[idx]`；不能把相机当前的 `targetTexture` 当作永久照片引用。

照片先按模型尺寸做居中正方形裁剪，再进入真正的 VAE 编码。使用 VAE 后验均值作为图像 latent，乘以 SD 1.x 的 `0.18215` scaling factor；根据 img2img strength 选择起始去噪 timestep，再按该 timestep 的累计 alpha 与 seed 噪声混合得到 `x_t`。强度越高，起始噪声越多、允许的变化越大。

本工程使用 Linear 色彩空间。照片预览 RT 保留线性颜色，送入 VAE 前转换到训练使用的 sRGB 值；VAE 解码后将 sRGB 转回线性显示 RT。导出的 PNG 再做 sRGB 编码，使它与世界显示和原生相机保存的颜色一致。

控制者将编码后的 latent 连同尺寸和量化 scale 打包成 16-bit 数值并同步。其他玩家解码这份 latent，再从相同的起始 timestep 按共享配置在本机 GPU 上继续采样。`photoIndex` 只代表拍照槽位，RenderTexture 内容本身不会通过网络发送；跨客户端共用输入依赖同步压缩后的 latent。

## 原生 VRChat 拍照相机输出

实现使用 clip-space 全屏 Quad。`_VRChatCameraMode` 为相机/截图模式、镜面模式为零，且当前渲染相机位于半径 0.65 米的标记区内时，才覆盖其画面。区域中心为 `(-16.6, 1.5, 0.4)`，位于输入板左侧，以青色框标记。按相机宽高比保留完整方形图及黑边，用投影方向修正 RenderTexture 的上下翻转；普通眼睛视角、镜面和区域外相机不会被覆盖。

Udon 可通过 `VRCCameraSettings.PhotoCamera` 查询相机是否活动、位置、朝向、FOV 和宽高比，但 VRChat 不向世界脚本暴露原生相机的 `Camera` 组件或其 RenderTexture。`PhotoCamera` 属性指向预览相机；它不能作为原生照片的像素输入。世界也没有 Udon 自动把截图写入玩家磁盘的接口。玩家看到铺满相机画面的生成图后，仍需按 VRChat 原生快门保存照片。

本实现不修改玩家相机的 Transform、FOV 或 NearClipPlane，输出直接使用 clip-space 近裁剪位置。Renderer 使用默认图层；较大的 Mesh bounds 保证相机能提交该 Quad，再由 shader 判断区域。

官方参考：

- [VRC Camera Settings](https://creators.vrchat.com/worlds/udon/vrc-graphics/vrc-camera-settings)
- [VRChat Shader Globals](https://creators.vrchat.com/worlds/udon/vrc-graphics/vrchat-shader-globals)
- [Unity Layers in VRChat](https://creators.vrchat.com/worlds/layers)

## 验证状态

已在 PlayMode 通过原包真实 `SetPhotoIndex` 拍摄、512 RT 桥接、256 VAE 编码、加噪和完整图生图；20 步日程、strength=0.85 执行 17 步，约 75 秒。输入照片 latent 载荷为 8192 bytes。32 个 SD 按钮的射线命中、三语切换、真实 UI→Udon 开关、初始控制权及跳跃也已检查。

以模拟 VRChat shader globals 的相机渲染验证：方形输出与生成 RT 的 RMSE 为 0，宽屏黑边、普通视角及区域外隐藏均通过。此检查不等同于原生 VRChat 相机触发和磁盘保存；后者、实际多客户端 latent 同步、ownership handoff 与晚加入仍待真实客户端验证。照片初始化本轮仅完整测试 256，384/512 encoder 已构建但未单独采样。证据见 [validation.json](../results/sd-experience/validation.json)。
