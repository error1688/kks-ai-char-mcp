// 只读探针：枚举 ChaFile* 数据模型里"游戏编辑器能改、本插件没暴露"的成员面。
// 用法: csc Probe6.cs /r:Mono.Cecil.dll && Probe6.exe [Assembly-CSharp.dll 路径]
using System;
using Mono.Cecil;

class Probe6
{
    static void Main(string[] args)
    {
        string asmPath = args.Length > 0 ? args[0] : @"E:\game\KKS\CharaStudio_Data\Managed\Assembly-CSharp.dll";
        ModuleDefinition mod = ModuleDefinition.ReadModule(asmPath, new ReaderParameters { ReadSymbols = false });

        string[] wanted = new string[] {
            "ChaFileFace+PupilInfo",
            "ChaFileClothes+PartsInfo",
            "ChaFileClothes+ColorInfo",
            "ChaFileClothes+EmblemInfo",
            "ChaFileAccessory+PartsInfo",
            "ChaFileParameter",
            "ChaFileStatus",
            "ChaFileHair+PartsInfo",
        };

        foreach (string name in wanted)
        {
            TypeDefinition t = Find(mod, name);
            Console.WriteLine("=== " + name + " ===");
            if (t == null) { Console.WriteLine("  (未找到)"); continue; }
            foreach (FieldDefinition f in t.Fields)
            {
                if (f.IsStatic || f.Name.StartsWith("<")) continue;
                Console.WriteLine("  field  " + ShortType(f.FieldType) + " " + f.Name);
            }
            foreach (PropertyDefinition p in t.Properties)
                Console.WriteLine("  prop   " + ShortType(p.PropertyType) + " " + p.Name);
            Console.WriteLine();
        }
    }

    static TypeDefinition Find(ModuleDefinition mod, string fullName)
    {
        foreach (TypeDefinition t in mod.GetTypes())
            if (t.FullName == fullName) return t;
        return null;
    }

    static string ShortType(TypeReference ty)
    {
        if (ty.IsArray) return ShortType(ty.GetElementType()) + "[]";
        string n = ty.Name;
        if (n == "Color" || n == "Vector2" || n == "Vector3" || n == "Vector4") return n;
        return n;
    }
}
