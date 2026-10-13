# Unity 2022 compatibility

Upstream: official Unity Registry com.unity.pipeline 0.8.0-exp.1.
Project: Unity 2022.3.22f1, VRChat SDK 3.10.5.
Installation approach: https://github.com/rocwood/unity-cli-2022-mod

Local changes:
- package.json: Unity minimum 2022.3; Mono.Cecil dependency 1.11.4 matches this project's existing installation.
- AssetCommands.cs: alias PhysicsMaterial to PhysicMaterial before Unity 6.
- MaterialCommands.cs: read m_CustomRenderQueue via SerializedObject before Unity 6, preserving the -1 inheritance value.
- Five bundled Roslyn plugin import descriptors: translate Unity 6 PluginImporter v3 platform maps to Unity 2022 v2 lists before first import, retaining upstream GUIDs and platform/define constraints.

DLLs are unmodified and retain the upstream CHECKSUMS hashes. GUIDs are unchanged.
Do not replace this embedded package with an unadapted registry upgrade.
The importer conversion is an installation bootstrap step; subsequent asset/importer changes must follow UNITY-GUIDE.md and use Unity/Pipeline APIs.

- PipelineAnalytics.cs: retain upstream Unity 6 analytics; disable optional usage reporting on Unity 2022 because IAnalytic/AnalyticInfo are unavailable.
- EditorConsoleGroundTruth.cs: sample console counts through Unity 2022 LogEntries.GetCountsByType reflection; retain update polling and keep the Unity 6 console event only on Unity 6.
