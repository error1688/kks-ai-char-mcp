// 只读探针：枚举本机所有 BepInEx 插件的元数据（BepInPlugin GUID/名称/版本）。
using System;
using Mono.Cecil;

class Probe9
{
    static void Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : @"E:\game\KKS\BepInEx\plugins";
        ScanDir(dir);
    }

    static void ScanDir(string dir)
    {
        foreach (string f in System.IO.Directory.GetFiles(dir, "*.dll"))
        {
            try
            {
                ModuleDefinition mod = ModuleDefinition.ReadModule(f, new ReaderParameters { ReadSymbols = false });
                string title = null, ver = null, guid = null, name = null;
                foreach (CustomAttribute a in mod.Assembly.CustomAttributes)
                {
                    string tn = a.AttributeType.Name;
                    if (tn == "AssemblyTitleAttribute") title = (string)a.ConstructorArguments[0].Value;
                    else if (tn == "AssemblyVersionAttribute") ver = (string)a.ConstructorArguments[0].Value;
                }
                foreach (TypeDefinition t in mod.Types)
                {
                    foreach (CustomAttribute a in t.CustomAttributes)
                    {
                        if (a.AttributeType.Name != "BepInPlugin") continue;
                        guid = (string)a.ConstructorArguments[0].Value;
                        name = (string)a.ConstructorArguments[1].Value;
                        ver = (string)a.ConstructorArguments[2].Value;
                    }
                    if (name != null) break;
                }
                Console.WriteLine(System.IO.Path.GetFileName(f) + " | " + (name ?? title ?? "?") + " | " + guid + " | " + ver);
                mod.Dispose();
            }
            catch (Exception) { }
        }
        foreach (string sub in System.IO.Directory.GetDirectories(dir))
            ScanDir(sub);
    }
}
