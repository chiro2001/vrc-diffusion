# 多人控制与状态同步设计

## 同步边界

每个实例有一个状态写入者：当前拥有 `DiffusionController` 且 `authorityId` 指向自己的玩家。所有玩家看到同一套 digit、seed、步数和 phase；非控制者要先申请、等控制者授予后才能编辑。VRChat 同步的是小型整数/布尔状态和请求槽，卷积 feature、`stateA/stateB`、历史 RT 等渲染目标始终留在各自客户端本地。

各客户端从同一 seed 独立生成初始噪声，并运行本地 GPU 推理。`completedSteps` 是控制者发布的进度水位；快客户端不因慢客户端停下，慢客户端按序追到已发布步数。界面分别显示 `shared` 与 `local` 步数，表示两者可能暂时不同。浮点路径跨 GPU 不保证逐像素相等。

这份文档描述当前 Udon 源码路径，不代表真实多客户端网络流程已通过验证。所有权交接、请求排队、晚加入和离开恢复仍需在实际 VRChat 多客户端实例中检查。

## 权威字段

`DiffusionController` 使用 Manual 同步。当前共享字段如下：

| 字段 | 含义 |
|---|---|
| `schemaVersion` | 同步状态结构版本。 |
| `networkModelVersion` | 同步的模型权重版本；Builder 从 `model.json` 写入。 |
| `epoch` | 一次生成或配置代际；Generate、Reset、类别/步数/seed 改变时递增。 |
| `revision` | 每次 `Publish()` 递增，标识新的共享状态发布。 |
| `authorityId` | 当前控制者的 player ID；无控制者为 `-1`。 |
| `digit` / `seed` / `totalSteps` | 共享条件；用户类别范围 0–9，默认 32 DDIM 步。 |
| `completedSteps` | 控制者最近发布的完成步数水位。 |
| `phase` | `0=Idle`、`1=Running`、`2=Paused`、`3=Complete`。 |
| `grantedId` | 已序列化并允许接管的目标玩家 ID。 |
| `fixtureRun` | 仅供 shader fixture 恢复使用，普通生成中为 false。 |

Controller 本地的 `modelVersion` 与同步的 `networkModelVersion` 必须一致；[BuildDiffusionLab](../AgentScripts/BuildDiffusionLab.cs) 从 [model.json](../Assets/DiffusionLab/Data/model.json) 读取模型 hash，校验 `weights.bytes` 后自动把相同版本号写进两者。当前权重版本为 `1021740377`。`ApplyState()` 和 `Update()` 在版本或 schema 不匹配时停止计算。

`seed` 仍以 Udon 同步整数保存。Controller 在传给 shader 时拆为 `_SeedLo = seed & 65535` 和 `_SeedHi = (seed >> 16) & 65535`；shader 重新拼回 32 位值，再与像素坐标一起哈希，避免单精度 float 丢掉大 seed 的低位。输入框接受 1–9 位十进制数字。

## 控制和运行

- **Generate**：控制者递增 `epoch`，把共享步数归零并设 `Running`。每个客户端发现新 epoch 后清空自己的 layer/history RT，从相同 seed 开始计算。
- **Pause / Resume**：暂停时控制者把自己的 `localStep` 写入 `completedSteps` 并发布 `Paused`；继续时保留 epoch 和步数，设 `Running`。慢端追到暂停水位后停止。
- **Step Denoise**：暂停且本机已追平共享水位时，控制者安排一个完整 DDIM 步；完成后按正常进度规则发布新水位。`Step Operator` 则只推进当前控制者本地的一次 shader pass，完整 DDIM 步未完成前不增加 `completedSteps`。
- **Reset / 配置变更**：递增 epoch、归零步数并发布 `Idle`，各端抛弃旧 epoch 的本地结果。更改类别、seed、步数也开始新 epoch。
- **Complete**：控制者完成 `totalSteps` 后发布最终步数与 `Complete`。

控制者在采样期间通过 `Publish()` 发布状态：完成步数每 4 步发布，或间隔超过约 250 ms 时发布；暂停、恢复、重置和配置变化会立即发布。接收端不等控制者事件去显示一张共享图片，而是由 `Update()` 按 `completedSteps` 追赶本地 RT。

## 控制权请求与恢复

场景为请求队列配置多个 `DiffusionControlRequest` 对象；Builder 当前创建 16 个槽。请求者取得一个槽的 ownership，写入 `requesterId` 和服务器时间 `requestedAt`，并序列化该槽。当前控制者只接受仍在线的请求，并选时间最早者授予。

授予时控制者先暂停到自己的完整 `localStep`，写入 `completedSteps` 和 `grantedId` 并请求序列化。只有 `OnPostSerialization` 报告成功后才转移 Controller ownership。接收者在 `OnOwnershipTransferred` 设置自己的 `authorityId`、清掉 `grantedId`、清理自己遗留的请求槽并发布新状态；这一步避免旧 request 留在队列里再次被授予。

控制者离开时，新对象 owner 检查原 `authorityId` 是否仍在线；若已离开则清除控制者并把运行状态改成 `Paused`。它保留最近一次已同步的 `completedSteps`，不能恢复离开者本机尚未发布的 RT 步数。玩家之后可重新申请控制。

新玩家加入时 owner 调用 `RequestSerialization()` 重发当前权威状态；接收端在 `OnDeserialization()` 调用 `ApplyState()`。新客户端的本地 epoch 初始未设置，因此会依共享 seed 重建噪声和本地 RT，再追到共享步数。这个恢复过程不传送任何 RenderTexture。

## Fixture 与验证边界

`fixtureRun` 是测试夹具标记。验证运行使用固定输入纹理、digit 3 和 32 步；新 epoch 恢复时各端把本地初态换成相同 fixture，再读取最终 RT 与 `reference-final.bytes` 比较。它便于检查持久状态描述能否重建已知的本地计算，不是普通生成功能，也不是传输或保存 RT 的机制。

本地 fixture 数值检查已在 RTX 5080 / Direct3D11 上通过，版本 `1021740377` 的 DDIM step 32 `maxAbs=4.45247e-5`；轨迹图片来自实际 GPU RT readback。这个单机结果不能证明多人网络正确。真实多端验证还应覆盖两个客户端的请求/授予、serialization 延迟、运行中加入、owner 离开、慢端追赶以及不同 GPU 的图像差异；在完成这些检查前，不把 ownership、late join 或 owner 迁移描述为已验证通过。
