# NPU 环境核验（2026-10-14）

本记录用于 Tea 预训练模型的 NPU 采样任务。训练和批量评估未在本次核验中启动。

## 设备编号

`a3-21` 的 `npu-smi info` 将两个物理芯片组成一个 NPU 编号：NPU 0 对应 physical ID 0/1，NPU 1 对应 2/3，NPU 2 对应 4/5，NPU 3 对应 6/7。任务目标 NPU 2 因而是 `/dev/davinci4` 和 `/dev/davinci5`。预检时 NPU 2 没有运行进程；NPU 4–7 有 VLLM 进程。

在已运行的 `dsv41-ced-p0-7-20261013-v2` 容器中，用进程级环境变量 `ASCEND_RT_VISIBLE_DEVICES=2` 和 `ASCEND_VISIBLE_DEVICES=2` 运行了一个 2×2 张量乘法。PyTorch 报告一个可见设备，计算结果正确；同时 `npu-smi` 显示测试进程 PID 3624656 位于 NPU 1、Chip 0，即 physical ID 2。测试退出后，该进程已从设备进程表消失。

因此，`ASCEND_RT_VISIBLE_DEVICES=2` 选择 physical ID 2（NPU 1 的一个芯片），不是目标 NPU 2。physical ID 4/5 对应的可见设备参数尚未验证，本次没有改用其他数值再次探测。

## 容器状态与限制

- `vrc-diffusion-diagnostics` 在开始核验前已是 `Exited (137)`，没有同名运行任务。
- 新建的最小隔离容器只映射 `/dev/davinci4`、`/dev/davinci5` 和 Ascend 管理设备；它的 ACL 初始化报告 `Resource_Busy(EL0020): Device 4 is occupied by multiple containers`。只读检查发现多个运行容器也静态映射了 davinci4/5。该容器随后停止；没有停止或改动既有容器，也没有启用设备共享。
- 已有 `dsv41-ced-p0-7-20261013-v2` 容器含 Python 3.12、torch 2.10.0+cpu 和 torch_npu 2.10.0.post4，且映射 davinci0–7。最小张量运行成功，但暴露了编号偏差；没有在其中启动 Tea 评估。
- checkpoint 位于远端 `~/tmp/vrc-diffusion/diagnostics/pretrained/Conditional_Diffusion_MNIST/model_39.pth`。评估和导出由 CPU 路径继续处理。

后续若要在 NPU 2 运行任务，应先在空闲状态下核实 physical ID 4/5 的环境变量映射，并确认设备没有多容器占用；不能根据单个可见设备数量推断物理映射。
