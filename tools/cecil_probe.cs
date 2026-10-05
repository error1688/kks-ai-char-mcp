// 用 Mono.Cecil 找"滑条名表"到底在哪：谁引用了 "ChinLowY" / "ThighUpW" 这两个字符串，
// 以及有没有静态 string[] 的初始化内容包含它们。
using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;

class Probe
{
    static void Main(string[] args)
    {
        string asmPath = args.Length > 0 ? args[0] : @"E:\game\KKS\CharaStudio_Data\Managed\Assembly-CSharp.dll";
        string[] needles = new string[] { "ChinLowY", "ThighUpW", "BustSharp", "AnkleW" };
        var mod = ModuleDefinition.ReadModule(asmPath, new ReaderParameters { ReadSymbols = false });

        // 1) 哪些方法的 IL 里有 Ldstr 这些字符串
        var hits = new Dictionary<string, List<string>>();
        foreach (TypeDefinition t in mod.GetTypes())
        {
            foreach (MethodDefinition m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (Instruction ins in m.Body.Instructions)
                {
                    if (ins.OpCode != OpCodes.Ldstr) continue;
                    string s = ins.Operand as string;
                    if (s == null) continue;
                    foreach (string n in needles)
                    {
                        if (s == n)
                        {
                            string key = t.FullName + "::" + m.Name;
                            if (!hits.ContainsKey(n)) hits[n] = new List<string>();
                            if (!hits[n].Contains(key)) hits[n].Add(key);
                        }
                    }
                }
            }
        }
        Console.WriteLine("=== 引用滑条名字符串的方法 ===");
        foreach (var kv in hits)
        {
            Console.WriteLine("  \"" + kv.Key + "\" 出现在 " + kv.Value.Count + " 处:");
            for (int i = 0; i < Math.Min(6, kv.Value.Count); i++) Console.WriteLine("      " + kv.Value[i]);
        }

        // 2) 静态字段里有没有 string[]（含 readonly），并尝试打印数组元素个数
        Console.WriteLine();
        Console.WriteLine("=== 静态 string[] / List<string> 字段（前 40 个）===");
        int shown = 0;
        foreach (TypeDefinition t in mod.GetTypes())
        {
            foreach (FieldDefinition f in t.Fields)
            {
                if (!f.IsStatic) continue;
                string tn = f.FieldType.FullName;
                if (tn != "System.String[]" && !tn.Contains("List`1<System.String>")) continue;
                // 数组初始值藏在 RVA 里：只有 string[] 有 InitialValue
                string extra = "";
                if (f.InitialValue != null && f.InitialValue.Length > 0) extra = " (有初始化数据 " + f.InitialValue.Length + " 字节)";
                Console.WriteLine("  " + t.FullName + "::" + f.Name + " : " + tn + extra);
                if (++shown >= 40) break;
            }
            if (shown >= 40) break;
        }
    }
}
