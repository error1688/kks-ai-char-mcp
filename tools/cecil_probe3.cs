// 从 ChaFileDefine/BodyShapeIdx 与 FaceShapeIdx 的静态构造函数 IL 里提取"名字 = 索引"
using System;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

class Probe3
{
    static Dictionary<string, int> Extract(TypeDefinition t)
    {
        var map = new Dictionary<string, int>();
        // 先按声明顺序记录
        var order = new List<string>();
        foreach (FieldDefinition f in t.Fields) order.Add(f.Name);
        // 再从 cctor 里取 (Ldc_I4, Stsfld) 对
        foreach (MethodDefinition m in t.Methods)
        {
            if (m.Name != ".cctor" || !m.HasBody) continue;
            var ins = m.Body.Instructions;
            for (int i = 0; i + 1 < ins.Count; i++)
            {
                if (ins[i].OpCode != OpCodes.Ldc_I4) continue;
                if (ins[i + 1].OpCode != OpCodes.Stsfld) continue;
                var fd = ins[i + 1].Operand as FieldReference;
                if (fd == null) continue;
                if (!map.ContainsKey(fd.Name)) map[fd.Name] = (int)ins[i].Operand;
            }
        }
        // 没有 cctor 信息就退化成"声明顺序即索引"
        if (map.Count == 0)
            for (int i = 0; i < order.Count; i++) map[order[i]] = i;
        return map;
    }

    static void Main(string[] args)
    {
        string asmPath = args.Length > 0 ? args[0] : @"E:\game\KKS\CharaStudio_Data\Managed\Assembly-CSharp.dll";
        ModuleDefinition mod = ModuleDefinition.ReadModule(asmPath, new ReaderParameters { ReadSymbols = false });
        foreach (string tn in new string[] { "ChaFileDefine/BodyShapeIdx", "ChaFileDefine/FaceShapeIdx",
                                            "ChaFileDefine/HairKind", "ChaFileDefine/ClothesKind" })
        {
            TypeDefinition t = null;
            foreach (TypeDefinition x in mod.GetTypes()) if (x.FullName == tn) { t = x; break; }
            if (t == null) { Console.WriteLine("### " + tn + " 未找到"); continue; }
            var map = Extract(t);
            var pairs = new List<KeyValuePair<string, int>>(map);
            pairs.Sort(delegate (KeyValuePair<string, int> a, KeyValuePair<string, int> b) { return a.Value.CompareTo(b.Value); });
            Console.WriteLine("### " + tn + "  共 " + pairs.Count + " 个");
            Console.WriteLine("  arraysize_field_hint: " + t.FullName);
            for (int i = 0; i < pairs.Count; i++)
            {
                Console.Write((pairs[i].Value + ":" + pairs[i].Key).PadRight(28));
                if ((i + 1) % 3 == 0) Console.WriteLine();
            }
            Console.WriteLine();
            Console.WriteLine();
        }
    }
}
