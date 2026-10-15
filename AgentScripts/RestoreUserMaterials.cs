using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEditor;
// Restore audited pre-test properties through Unity APIs, without rewriting serialized files.
public static class RestoreUserMaterials {
    public static object Apply(string jsonPath){
        if(EditorApplication.isPlaying)throw new Exception("EditMode required");int count=0;
        foreach(var entry in JArray.Parse(File.ReadAllText(jsonPath))){
            string path=(string)entry["path"];
            if(path!="Assets/DiffusionLab/Materials/CurrentImage.mat"&&path!="Assets/DiffusionLab/Materials/Sampler.mat")throw new Exception("Unexpected material path");
            var material=AssetDatabase.LoadAssetAtPath<Material>(path);
            foreach(var property in ((JObject)entry["textures"]).Properties()){
                string guid=(string)property.Value;material.SetTexture(property.Name,string.IsNullOrEmpty(guid)?null:AssetDatabase.LoadAssetAtPath<Texture>(AssetDatabase.GUIDToAssetPath(guid)));
                material.SetTextureScale(property.Name,Vector2.one);material.SetTextureOffset(property.Name,Vector2.zero);
            }
            foreach(var property in ((JObject)entry["floats"]).Properties())material.SetFloat(property.Name,(float)property.Value);
            EditorUtility.SetDirty(material);count++;
        }
        AssetDatabase.SaveAssets();return new{restoredMaterials=count};
    }
}
