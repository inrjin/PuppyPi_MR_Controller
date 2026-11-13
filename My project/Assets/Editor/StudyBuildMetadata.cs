using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

// Generated into Resources at build time; no host paths or device addresses are embedded.
public static class StudyBuildMetadata
{
    [Serializable] private sealed class Metadata
    {
        public string source_revision;
        public string source_fingerprint;
        public string source_tree_status = "fingerprinted_working_tree";
        public string build_utc;
    }

    public static void Write()
    {
        string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string repository = Path.GetFullPath(Path.Combine(project, ".."));
        var inputs = new List<string>(Directory.GetFiles(Application.dataPath, "*.cs", SearchOption.AllDirectories));
        inputs.Add(Path.Combine(project, "Packages/manifest.json"));
        inputs.Add(Path.Combine(project, "Packages/packages-lock.json"));
        inputs.Add(Path.Combine(project, "ProjectSettings/ProjectVersion.txt"));
        string fingerprint;
        using (var hash = SHA256.Create())
        {
            foreach (string file in inputs.Where(File.Exists).OrderBy(path => path, StringComparer.Ordinal))
            {
                byte[] name = Encoding.UTF8.GetBytes(file.Substring(project.Length).Replace('\\', '/') + "\n");
                hash.TransformBlock(name, 0, name.Length, null, 0);
                byte[] bytes = File.ReadAllBytes(file);
                hash.TransformBlock(bytes, 0, bytes.Length, null, 0);
            }
            hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            fingerprint = BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
        }
        var data = new Metadata { source_revision = ReadRevision(repository), source_fingerprint = fingerprint,
            build_utc = DateTime.UtcNow.ToString("O") };
        string resource = Path.Combine(Application.dataPath, "Resources/PuppyPiStudyBuild.json");
        Directory.CreateDirectory(Path.GetDirectoryName(resource));
        File.WriteAllText(resource, JsonUtility.ToJson(data, true) + "\n");
        AssetDatabase.ImportAsset("Assets/Resources/PuppyPiStudyBuild.json", ImportAssetOptions.ForceSynchronousImport);
    }

    private static string ReadRevision(string repository)
    {
        try
        {
            string git = Path.Combine(repository, ".git");
            if (File.Exists(git))
            {
                string pointer = File.ReadAllText(git).Trim();
                if (!pointer.StartsWith("gitdir: ", StringComparison.Ordinal)) return "unavailable";
                git = Path.GetFullPath(Path.Combine(repository, pointer.Substring(8)));
            }
            string head = File.ReadAllText(Path.Combine(git, "HEAD")).Trim();
            if (!head.StartsWith("ref: ", StringComparison.Ordinal)) return head;
            string reference = head.Substring(5);
            string path = Path.Combine(git, reference);
            if (File.Exists(path)) return File.ReadAllText(path).Trim();
            string packed = Path.Combine(git, "packed-refs");
            if (File.Exists(packed))
                foreach (string line in File.ReadLines(packed))
                    if (line.EndsWith(" " + reference, StringComparison.Ordinal)) return line.Split(' ')[0];
        }
        catch (IOException) { }
        return "unavailable";
    }
}
