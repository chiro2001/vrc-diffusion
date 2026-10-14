# 多人控制与状态同步设计

本文描述当前 Controller 的 schema 2 状态和客户端本地推理边界。PlayMode 已覆盖本地状态重放、陈旧快照和控制面板行为；这些检查不等同于真实 VRChat 多客户端所有权交接验证。

## 同步边界

实例中只有一个控制者可以修改共享生成设置和发布进度。同步的是模型与输出选择、类别、seed、步数、phase、控制者和完成步数等小型状态。每个玩家的 RenderTexture、U-Net 特征、采样噪声和 DDIM 中间状态仍留在本地 GPU；收到进度后，各客户端依据相同设置从 seed 重建并推进自己的计算。

共享进度是 `completedSteps` 水位，本地计算位置是 `localStep` 和 `localLayer`。快端可以领先于已发布水位，慢端按序追到控制者发布的位置。状态标签分别显示 shared 与 local 进度。相同 seed 提供相同的生成条件，但 GPU 浮点运算不保证不同设备逐像素相等。

语言覆盖、教学页索引、算子图高亮、像素探针坐标/通道和 probe 读回值都只在本地生效，不写入共享状态。模型权重和模型目录也是每个客户端本地预置；同步版本号用于判断本地目录能否解释网络状态。

## Schema 2 与本地模型目录门

Controller 使用 Manual Udon 同步，`schemaVersion` 当前为 `2`。本地模型目录有两个条目：`modelIndex=0` 是 Legacy 32，`modelIndex=1` 是 TeaPearce 28；Builder 默认选择 Tea。`outputIndex=0` 表示当前模型原生尺寸，`1/2/3` 分别表示 32、64、128 的最近邻输出。

各模型独立版本为 Legacy `1021740377`、Tea `692248240`。Builder 将两者合成目录版本：

```text
registryVersion = (legacyVersion × 31 + teaVersion) & 0x7fffffff
                = 153945207
```

`networkModelVersion` 携带共享目录版本；每个客户端用本地 `modelVersion` 与它比较。`ApplyState()` 和运行循环在 `schemaVersion != 2` 或 `networkModelVersion != modelVersion` 时停止应用状态，避免客户端按不同模型目录解释同一组索引。

## 配置代际与输出切换

类别、seed、步数或模型改变都会调用配置重置：`epoch` 加一，步数归零并退出当前运行状态。模型切换还会把步数设为新模型的默认值，因此在 Legacy 32 与 Tea 28 间切换会开始新 epoch，并分别重置为 32 步或 64 步。新 epoch 到达后，客户端清空自己的 layer/history RT、重建噪声并重新绑定该模型。

输出尺寸切换只更新并发布 `outputIndex`，不增加 epoch，也不重新采样。`ApplyState()` 会在同一 epoch 下重新调用 `UpdateOutput()`，因此当前图像可在运行或暂停状态下立即切换显示尺寸；每个客户端仍只对自己的 RT 做最近邻输出。

## 权威字段与进度发布

共享 Controller 字段如下。请求队列使用独立的 request 对象同步，不属于图像纹理状态。

| 字段 | 含义 |
| --- | --- |
| `schemaVersion` | 同步协议版本，当前为 2。 |
| `networkModelVersion` | 共享的本地模型目录版本，当前为 153945207。 |
| `modelIndex` / `outputIndex` | 当前模型与输出尺寸选择。 |
| `epoch` / `revision` | 配置代际，以及每次 `Publish()` 递增的快照序号。 |
| `authorityId` / `grantedId` | 当前控制者和已授权接管的玩家 ID；无控制者为 `-1`。 |
| `digit` / `seed` / `totalSteps` | 共享生成条件。类别范围为 0–9。 |
| `completedSteps` / `phase` | 控制者发布的完整 DDIM 步数和运行阶段：`0=Idle`、`1=Running`、`2=Paused`、`3=Complete`。 |
| `fixtureRun` | 数值 fixture 运行标记；普通生成中为 false。 |

运行时，控制者每 4 个完整步或距上次发布超过约 250 ms 时发布水位；暂停、恢复、重置和配置更改会立即发布。追赶中的客户端只在本地执行 shader，不会接收控制者的 RT 快照。

seed 是同步整数，写入 shader 前拆成低/高 16 位，再还原为 32 位整数参与像素哈希。这样大 seed 不会因为转成单精度 float 而丢失低位。输入框接受 1–9 位十进制数字。

## 暂停、DDIM 单步与 shader 单步

- **Generate** 开始新 epoch，清零共享进度并设为 Running。
- **Pause / Resume** 在暂停时发布控制者最近完成的整步；继续时沿用当前 epoch 和步数。
- **Step Denoise** 要求控制者处于 Paused 且本地已追平共享水位，然后安排到下一完整 DDIM 步。
- **Step Operator** 每次只安排一个独立 shader pass。Tea 的 27 个 CLASS 算子、27 个 NULL 算子，以及 CFG epsilon 合成、`x₀` 计算和 `x_next` 更新都是各自的 pass；逐 pass 操作不会把未完成的采样步提前计入 `completedSteps`。
- **Reset / 配置更改** 开始新 epoch 并清空旧进度。模型切换使用对应模型的 fixture 步数默认值：Legacy 为 32，Tea 为 64。

Controller 的 fixture 启动也从当前模型读取 `defaultSteps`，不会再把 Tea 固定为 32 步。fixture 使用各模型各自的固定输入与期望最终纹理，仅用于 shader 数值对照，不代表普通生成，也不传送纹理。

## 单控制者与 16 个请求槽

场景通过 `DiffusionControlRequest` 对象提供 16 个请求槽。非控制者申请时取得一个槽的 ownership，写入 `requesterId` 和请求时间；控制者从仍在线的请求中选择最早者。没有控制者时，玩家可以直接取得 Controller ownership。

移交时，当前控制者先暂停到自己的完整 `localStep`，发布 `completedSteps` 和 `grantedId`；`OnPostSerialization` 确认序列化成功后再转移 Controller ownership。接收者在 ownership 回调中更新 `authorityId`，清除授予状态并发布。若原控制者离开，运行状态会暂停在最后发布的共享水位；尚未发布的本地 RT 步数不会迁移给新控制者。

新玩家加入时，当前对象 owner 会重发权威状态。新客户端依 `modelIndex` 绑定本地目录，发现 epoch 后重建噪声和本地 RT，再追到 `completedSteps`。这些路径只描述当前源码实现；实际 VRChat 多端的 serialization 延迟、所有权移交和退出恢复尚未验证。

## 状态重放与验证边界

接收状态时，Controller 会拒绝越界的模型/输出索引、无效步数和非法 phase；低于已应用 `revision` 的快照会恢复上一个缓存状态。[运行报告](../results/runtime.json)标记通过，记录了 32/28 模型切换、暂停噪声、step/operator、同 epoch 输出切换、陈旧/非法快照、教学页和 GPU 像素探针等 PlayMode 覆盖，教学公式最大误差 `2.98023224e-8`。这些是本地状态路径检查，不能证明 VRChat 网络中的 ownership handoff 已正常工作。

RTX 5080 / D3D11 的[两模型数值报告](../results/models-numeric.json)标记通过，逐项记录模型算子和采样轨迹：Legacy 32 步最终 `maxAbs=4.45246696472168e-5`，Tea 64 步最终 `maxAbs=4.202127456665039e-6`。另外，[实际 shader hash 噪声批次](../results/gpu-seed-batch.json)抽查 10 组类别/seed，报告标记通过，64 步最大误差 `2.3066997528076172e-5`；[单机 Udon 轨迹报告](../results/play-trajectory.json)记录 class 3、seed 1234 的 Tea 64 步、65 帧实际生成。这些数字和单机运行不代表真实多人网络测试通过。

实际多客户端的所有权交接、16 槽请求排队、序列化延迟、晚加入、控制者离开后的恢复，以及不同客户端追赶仍未验证；需要在真实 VRChat 多客户端实例中检查。在这些检查完成前，不把 ownership handoff、排队、late join 或 owner 迁移描述为已通过。
