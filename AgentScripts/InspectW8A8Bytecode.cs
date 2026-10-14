using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;

public static class InspectW8A8Bytecode
{
    private const string ShaderName = "DiffusionLab/W8A8";
    private const string OutputRelativePath = "Reports/DiffusionLab/W8A8/accumulator.dxbc";

    public static int Export()
    {
        Shader shader = Shader.Find(ShaderName);
        if (shader == null)
            throw new InvalidOperationException("Shader was not found: " + ShaderName);

        ShaderData shaderData = ShaderUtil.GetShaderData(shader);
        if (shaderData == null || shaderData.SubshaderCount < 1)
            throw new InvalidOperationException("Shader has no compiled subshader data: " + ShaderName);

        ShaderData.Subshader subshader = shaderData.GetSubshader(0);
        if (subshader == null || subshader.PassCount < 1)
            throw new InvalidOperationException("Shader subshader 0 has no passes: " + ShaderName);

        ShaderData.Pass pass = subshader.GetPass(0);
        if (pass == null || !pass.HasShaderStage(ShaderType.Fragment))
            throw new InvalidOperationException("Shader pass 0 has no fragment stage: " + ShaderName);

        // In this Unity 2022.3 Editor, the D3D shader compiler platform targets D3D11.
        // The final bool requests bytecode prepared for external tools such as fxc /dumpbin.
        ShaderData.VariantCompileInfo variant = pass.CompileVariant(
            ShaderType.Fragment,
            Array.Empty<string>(),
            ShaderCompilerPlatform.D3D,
            BuildTarget.StandaloneWindows64,
            true);

        if (!variant.Success || variant.ShaderData == null || variant.ShaderData.Length == 0)
        {
            int count = variant.Messages == null ? 0 : variant.Messages.Length;
            throw new InvalidOperationException(
                "D3D11 fragment compilation failed for pass 0; compiler diagnostics: " + count);
        }

        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        string outputPath = Path.GetFullPath(Path.Combine(projectRoot, OutputRelativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
        File.WriteAllBytes(outputPath, variant.ShaderData);

        string magic = Encoding.ASCII.GetString(variant.ShaderData, 0, Math.Min(4, variant.ShaderData.Length));
        if (magic != "DXBC")
        {
            int prefixLength = Math.Min(16, variant.ShaderData.Length);
            string prefix = BitConverter.ToString(variant.ShaderData, 0, prefixLength);
            string message = "Exported bytes do not start with the DXBC container magic; " +
                             "the result may contain a Unity wrapper. Header=" + magic +
                             ", prefix=" + prefix + ", path=" + outputPath;
            Debug.LogError(message);
            throw new InvalidDataException(message);
        }

        Debug.Log("Exported D3D11 fragment DXBC for " + ShaderName + " pass 0: " +
                  variant.ShaderData.Length + " bytes, path=" + outputPath);
        return variant.ShaderData.Length;
    }
}
