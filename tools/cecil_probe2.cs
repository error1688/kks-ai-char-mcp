// 找滑条"名字→索引"的真表：ChaFileDefine 及其嵌套类型里的 const int 字段
using System;
using System.Collections.Generic;
using Mono.Cecil;

class Probe2
{
    static void DumpConsts(TypeDefinition t, string label)
    {
        var list = new List<string>();
        foreach (FieldDefinition f in t.Fields)
        {
            if (!f.HasConstant) continue;
            if (f.FieldType.FullName != "System.Int32") continue;
            list.Add(f.Name + "=" + f.Constant);
        }
        Console.WriteLine("### " + label + " (" + t.FullName + ")  const int 共 " + list.Count + " 个");
        for (int i = 0; i < list.Count; i++)
        {
            Console.Write(list[i].PadRight(26));
            if ((i + 1) % 4 == 0) Console.WriteLine();
        }
        if (list.Count % 4 != 0) Console.WriteLine();
        Console.WriteLine();
    }

    static void Main(string[] args)
    {
        string asmPath = args.Length > 0 ? args[0] : @"E:\game\KKS\CharaStudio_Data\Managed\Assembly-CSharp.dll";
        ModuleDefinition mod = ModuleDefinition.ReadModule(asmPath, new ReaderParameters { ReadSymbols = false });
        TypeDefinition cfd = null;
        foreach (TypeDefinition t in mod.GetTypes())
            if (t.FullName == "ChaFileDefine") { cfd = t; break; }
        if (cfd == null) { Console.WriteLine("没找到 ChaFileDefine"); return; }

        int own = 0;
        foreach (FieldDefinition f in cfd.Fields)
            if (f.HasConstant && f.FieldType.FullName == "System.Int32") own++;
        Console.WriteLine("ChaFileDefine 自身 const int 个数 = " + own);
        Console.WriteLine();

        DumpConsts(cfd, "ChaFileDefine 自身");
        foreach (TypeDefinition n in cfd.NestedTypes) DumpConsts(n, "嵌套类型");

        foreach (FieldDefinition f in cfd.Fields)
            if (f.FieldType.FullName == "System.String[]")
                Console.WriteLine("### string[] 字段 " + f.Name + (f.IsStatic ? " (static)" : ""));
    }
}
