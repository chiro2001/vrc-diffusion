# VRChat 世界空间 UGUI 与三语字体参考

只读审计来源：`/home/chiro/projects/vrc-cpu-map`。本文提炼出可复用的 Canvas、按钮、输入框和本地化构造，并记录当前面板几何的 z 间距，便于决定是否沿用。

## 世界空间射线 UI

`BuildCircuitLab.cs` 的通用面板由 `MovablePanel`、`PanelCanvas`、`CreateCanvas` 和 `UiButton` 组成（约第 1188、1200、2437–2477 行）。可复用关系如下：

- 面板是世界空间物体，背板 `Surface` 是 Cube，中心在面板本地 z=-0.012，厚 0.04；因此朝 -Z 的外表面在 z=-0.032。前缘 `Frame` 中心 z=+0.035、厚 0.09，其朝 -Z 面在 z=-0.010。
- Canvas 用 `RectTransform + Canvas + GraphicRaycaster + VRCUiShape + BoxCollider`。设 `RenderMode.WorldSpace`，layer 为交互层 17，localScale 为 `.01`，RectTransform 的宽高传入“米 × 100”的 UI 单位；Canvas 碰撞盒尺寸同样是宽高 ×100、深度 2，缩放后深度为 2 cm。
- 面板 Canvas 当前设在面板本地 `z=-.105`。Canvas 中心的图形平面因此比 Surface 的可见面 `z=-.032` 朝 -Z 多出 7.3 cm；BoxCollider 前缘约为 `z=-.115`，后缘 `z=-.095`，离 Surface 面仍有 6.3 cm。它是代码里的实际值，不是贴面的通用值。若目标屏幕要贴着 Surface，应以实际可见面 z 计算，仅留几毫米渲染间隙；不要直接复制 `-.105`。保持按钮、标签与 Canvas 同 z，靠 UGUI 的层级顺序排重叠，不要给每个子项另加 z 偏移。
- 每个按钮是 `RectTransform + CanvasRenderer + Image + Button`。Image 作为 `targetGraphic` 且 `raycastTarget=true`；Label 是子级 `Text`，`raycastTarget=false`。关闭键盘导航（`Navigation.Mode.None`），用 `UnityEventTools.AddStringPersistentListener` 将点击连接到 UdonSharp backing behaviour 的 `SendCustomEvent`。按钮 Image 和文本都放在交互层。
- 射线命中 Canvas 依赖 `GraphicRaycaster`、`VRCUiShape`、`BoxCollider` 与正确的交互层；单有普通 UGUI `Button` 不足以构成世界空间交互面。示例没有为每个控件单独建 Canvas，所有控件共享面板 Canvas。

可移动面板中 Canvas 尺寸与面板物理宽高一致，例如 `ProgramPanel` 使用 7.2 × 6.8 m；在 `.01` 缩放下，RectTransform 尺寸是 720 × 680。根面板的抓取碰撞盒放在手柄上，避免覆盖整块 UI。另有独立小按钮 Canvas 的 helper `Button`，它同样使用 `.01` 缩放和 Canvas 射线组件。

## InputField

程序草稿输入框使用传统 `UnityEngine.UI.InputField`，没有用 `TMP_InputField`（`BuildCircuitLab.cs` 约第 1245–1263 行）。最小结构是背景 `Image`、`InputField`、子级 `TextViewport`（加 `RectMask2D`）、Viewport 下的内容 `Text` 和 `Placeholder Text`。`InputField.textComponent`、`placeholder`、`targetGraphic` 都明确赋值。内容和占位文本 `raycastTarget=false`，让背景接住射线；Viewport 内边距为 8×7 UI 单位。

该字段设置为多行换行、Standard 内容、8192 字符上限、白色 caret、半透明蓝绿选区、可交互，键盘导航关闭。传统 `Text` 保留 UGUI caret 和编辑行为。运行时按语言更换 Placeholder 的 Noto 字体；实际输入内容保持 IBM Plex Mono，便于输入机器码。新增可编辑字段时应区分这两类目标，别把内容文本当普通翻译标签转换成 TMP。

## 中日英文案和 TMP 显示

本地化脚本在主场景构建后运行。`BuildCircuitLabLocalization.Apply` 扫描场景里的 UGUI `Text`，跳过 InputField 的内容和占位组件；每个可本地化文本保留原 `Text` 作为 Udon 数据源，再生成三个铺满原 RectTransform 的 `TextMeshProUGUI` 子物体：`LocalizedTMP_EN`、`LocalizedTMP_ZH`、`LocalizedTMP_JA`。源 Text 隐藏；三个 TMP 子项的 `raycastTarget=false`，将原来的对齐、粗斜体、自动字号、换行、溢出和颜色转过去。运行时只激活当前语言的子项，并把源 Text 变化复制到三个展示项。代码/机器字、实时值、终端输出等按名字或层级设置为保留原文，不做短语替换。

`CircuitLabLocalization` 是 `NoVariableSync` 的 UdonSharp behaviour：AUTO 使用 `VRCPlayerApi.GetCurrentLanguage()` 并响应 `OnLanguageChanged`；手动英语/简中/日语覆盖只保存在本地玩家。语言选项按钮通过持久化 UGUI 点击事件调用 `SetAuto`、`SetChinese` 等 Udon 自定义事件。文案来自 `CircuitLabLocalization.tsv`，短语替换用于保留计数、名称和实时值周围的动态内容。

locale 选择器示例位于 `AboutPanel/LocaleCanvas`：localScale `.0092`，RectTransform 300×138，BoxCollider 同尺寸、深 2，组件为 `Canvas + GraphicRaycaster + VRCUiShape + BoxCollider`。它嵌入 AboutPanel 右下角，按钮 Image 可射线，按钮 Label Text 不可射线。按钮回调绑定到本地化 behaviour。它说明选择器自身可以是小型独立 Canvas；若嵌入另一块实体面板，也应按那块面板的 Surface 前面计算 z，而不是假设 Canvas 默认位置已贴面。

## 字体、材质和导入

字体源位于 `Assets/CircuitLab/Localization/FontSources`，源字体的 Unity `TrueTypeFontImporter` 使用 `includeFontData: 1`、`fontSize: 16`，未填写 Unity 层 fallback 或 customCharacters。字符集是在生成 TMP 资产时显式收集、校验和烘焙的，不依赖运行时动态补字。具体映射和资产生成逻辑在 `BuildCircuitLabLocalization.cs` 的 `EnsureStaticFont`、`SetFallback` 附近（约第 592–657 行）：

| 资产用途 | 主字体/图集 | 回退顺序 |
| --- | --- | --- |
| 英文 UI | Noto Sans JP；2048² | Japanese → Simplified Chinese → Noto Sans Math → IBM superscripts |
| 简中 UI | Noto Sans SC；2048² | Noto Sans Math → IBM superscripts |
| 日文 UI | Noto Sans JP；2048² | Simplified Chinese → Noto Sans Math → IBM superscripts |
| 三语代码 | IBM Plex Mono ASCII；1024²，每语言单独一个资产 | 对应语言 UI 面 → 简中 → Math → superscripts（英文先回退 English UI） |
| 数学符号、上标数字 | Noto Sans Math 与 IBM Plex Mono；各 512² | 作为上述 UI/code 面的末端回退 |

创建参数是采样大小 40、padding 6、`GlyphRenderMode.SDFAA`、单图集；先临时以 Dynamic 模式 `TryAddCharacters` 烘焙收集到的字符，再切成 Static。构建器检查主字体及允许的 fallback 是否覆盖所有字符，发现未覆盖 glyph 就失败。英语和日语主面都用 Noto Sans JP；简中回退只补缺字，保留主面已有的日/英字形。字形集包括场景文本、TSV 翻译、序列化的控制器字符串、程序名/描述和 `ExtraGlyphs.txt`。

TMP 资产的 `TMP_FontAsset.material` 与 atlas 纹理保存在同一 `.asset` 下；材质使用 `TextMeshPro/Mobile/Distance Field`，`_MainTex` 指向对应 SDF atlas。构建器调用 `AddFontSubAssets` 把 material/atlas 子资产纳入文件，因此复用时应保留 TMP font asset 自带的 material 和 atlas 关联，别换成普通 uGUI 字体材质。单图集配置 `m_IsMultiAtlasTexturesEnabled: 0`。若目标工程找不到 `TextMeshPro/Distance Field` shader，脚本会从 TMP Essential Resources 导入资源包；TMP Essential Resources 的缺失应先修复，避免 TMP 组件变粉或字体不显示。

静态展示字体是 TMP SDF；InputField 仍用 `UnityEngine.UI.Text` 并通过 `Text.font` 指向原始 TTF。语言常规字体用 Noto Sans JP/SC，编辑内容用 IBM Plex Mono。`TMP` 的 fallback table 和 legacy uGUI `Font` 的切换是两条路径，改语言时需要分别维护。

## 参考位置

- `AgentScripts/BuildCircuitLab.cs`：`MovablePanel`、`PanelCanvas`、`PanelInputField`、`CreateCanvas`、`UiButton`、`UiText`。
- `Assets/CircuitLab/Scripts/CircuitLabLocalization.cs`：语言选择、UGUI 输入字体、TMP 展示切换与短语替换。
- `AgentScripts/BuildCircuitLabLocalization.cs`：`Apply`、字体生成/回退、`EnsureLocalePanel`、`EnsureTextDisplay`。
- `Assets/CircuitLab/Localization/README.md`、`CircuitLabLocalization.tsv`、`ExtraGlyphs.txt` 和 `FontSources/*-OFL.txt`：资源说明、文案、补充 glyph 与字体许可证。
