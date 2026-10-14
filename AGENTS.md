# 项目协作说明

- 始终使用简体中文回复用户，除非用户要求其他语言。
- 临时文件统一放在 `~/tmp/vrc-diffusion/`；Windows 主机临时文件放在用户目录下的 `tmp\vrc-diffusion\`。
- Unity 工程位于 Windows 主机 `192.168.101.5` 的 `D:\Projects\VRChatProjects2\vrc-diffusion`，本地镜像位于 `/home/chiro/projects/vrc-projects/vrc-diffusion`。
- 进行 Unity 开发时，必须阅读并遵循 [UNITY-GUIDE.md](UNITY-GUIDE.md)。
- 操作 Unity 时使用 [.agents/skills/unity-pipeline/SKILL.md](.agents/skills/unity-pipeline/SKILL.md)。
- 使用 `python tools/unity_remote.py <Pipeline 命令及参数>` 通过 SSH、PowerShell 调用 Unity；脚本会显式指定目标工程。
- Pipeline 是 Unity 2022 本地适配包。更新前检查远端 `Packages/com.unity.pipeline/Documentation~/unity2022-compatibility.md`，保留兼容补丁。
- 当前只配置本地与 Windows 工程的双端 Git 同步。GitHub 仓库和公开推送策略尚未确定；不要假设仓库名、远端 URL 或主分支。
- 快照前停止 Play Mode、等待编译完成并保存场景，再运行 `python tools/snapshot_unity_project.py`。脚本会检查本地冲突并保留 `.meta`，不要绕过检查覆盖未解决的本地编辑。
- 双端同步前先检查两边 Git 状态；本地改动需要先由项目负责人提交。运行 `python tools/sync_windows_git.py` 会把本地分支的 Git bundle 传到 Windows，并在内容哈希检查通过后对齐 Windows 的 HEAD 和索引；它不会初始化仓库、提交或推送。
- Windows 工程必须已是本地分支的祖先，且其 Unity 文件必须与本地提交匹配。脚本只更新 Git 元数据，不改写 Unity 资产；遇到未提交或分叉提交时先人工排查。
- 勿用 checkout/reset 覆盖正在编辑的场景或未提交修改。不要纳入凭据、缓存和客户端本地数据。

## 本轮开发约束

- 不自动启动真实 VRChat 客户端或执行 BuildAndTest，不终止用户的游戏进程。优先在 Unity PlayMode 验证交互、GPU推理和布局。
- 用户更新资源窗口（北京时间 2026-10-14）：09:00 前训练/批量采样只使用 ssh a3-21 / ssh a3-22 上经审计空闲的 NPU；09:00 后禁止再使用 NPU，改用已获授权的 gamepc RTX 5080。NPU 长任务须设 09:00 前硬停止 watchdog。
- 简单并行任务和耗时监控委派用户指定的 gpt-6-luna[max]；复杂代码和调试由主代理处理。
- 模型质量须使用多类别、多seed和地图实际噪声进行成批验证；数值一致性与训练loss不能代替生成质量。
- 明确区分模型原生推理尺寸、有效图像裁剪尺寸与输出放大尺寸。

- 加入原理教学、真实数据与RT驱动的动态内部网络结构、去噪公式和CFG算法演示；不能用装饰动画冒充计算状态。
