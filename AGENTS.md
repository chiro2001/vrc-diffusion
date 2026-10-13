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
