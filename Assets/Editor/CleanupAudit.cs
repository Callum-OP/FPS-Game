using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class CleanupAudit
{
    public static void Run()
    {
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        var paths = new[]
        {
            "Assets/Scenes/Demo.unity",
            "Assets/Prefabs/Player.prefab",
            "Assets/Prefabs/Enemy.prefab",
            "Assets/Prefabs/EnemyMelee.prefab",
            "Assets/Prefabs/ClubEnemy_0.prefab"
        };

        var output = new StringBuilder();
        foreach (var path in paths)
        {
            output.AppendLine("## " + path);
            foreach (var dependency in AssetDatabase.GetDependencies(path, true).OrderBy(value => value))
                output.AppendLine(dependency);
            output.AppendLine();
        }

        output.AppendLine("## Y Bot sub-assets");
        foreach (var asset in AssetDatabase.LoadAllAssetsAtPath("Assets/Characters/Y Bot.fbx"))
            output.AppendLine(asset.GetType().Name + ": " + asset.name);

        File.WriteAllText("cleanup-audit.txt", output.ToString());
        Debug.Log("Cleanup audit written to cleanup-audit.txt");
    }
}