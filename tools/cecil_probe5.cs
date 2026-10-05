// 列出 ChaControl 里与饰品相关的方法签名（找有没有设置颜色的接口）
using System;
using Mono.Cecil;

class Probe5
{
    static void Main(string[] args)
    {
        string asmPath = args.Length > 0 ? args[0] : @"E:\game\KKS\CharaStudio_Data\Managed\Assembly-CSharp.dll";
        ModuleDefinition mod = ModuleDefinition.ReadModule(asmPath, new ReaderParameters { ReadSymbols = false });
        TypeDefinition cc = null;
        foreach (TypeDefinition t in mod.GetTypes())
            if (t.FullName == "ChaControl") { cc = t; break; }
        if (cc == null) { Console.WriteLine("未找到 ChaControl"); return; }

        Console.WriteLine("=== ChaControl 中与 Accessory 相关的方法 ===");
        foreach (MethodDefinition m in cc.Methods)
        {
            if (m.Name.IndexOf("ccessor", StringComparison.Ordinal) < 0) continue;
            var ps = new System.Collections.Generic.List<string>();
            foreach (ParameterDefinition p in m.Parameters) ps.Add(p.ParameterType.Name + " " + p.Name);
            Console.WriteLine("  " + (m.IsPublic ? "public " : "nonpublic ") + m.ReturnType.Name + " " +
                m.Name + "(" + string.Join(", ", ps.ToArray()) + ")");
        }
        Console.WriteLine();
        Console.WriteLine("=== ChaControl 中与 Color 相关的方法 ===");
        foreach (MethodDefinition m in cc.Methods)
        {
            if (m.Name.IndexOf("olor", StringComparison.Ordinal) < 0) continue;
            var ps = new System.Collections.Generic.List<string>();
            foreach (ParameterDefinition p in m.Parameters) ps.Add(p.ParameterType.Name + " " + p.Name);
            Console.WriteLine("  " + (m.IsPublic ? "public " : "nonpublic ") + m.ReturnType.Name + " " +
                m.Name + "(" + string.Join(", ", ps.ToArray()) + ")");
        }
    }
}
