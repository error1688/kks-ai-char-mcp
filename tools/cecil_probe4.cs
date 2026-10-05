// 读枚举成员的真实常量值（BodyShapeIdx / FaceShapeIdx），并打印数组字段的信息
using System;
using System.Collections.Generic;
using Mono.Cecil;

class Probe4
{
    static void Main(string[] args)
    {
        string asmPath = args.Length > 0 ? args[0] : @"E:\game\KKS\CharaStudio_Data\Managed\Assembly-CSharp.dll";
        ModuleDefinition mod = ModuleDefinition.ReadModule(asmPath, new ReaderParameters { ReadSymbols = false });
        foreach (string tn in new string[] { "ChaFileDefine/BodyShapeIdx", "ChaFileDefine/FaceShapeIdx" })
        {
            TypeDefinition t = null;
            foreach (TypeDefinition x in mod.GetTypes()) if (x.FullName == tn) { t = x; break; }
            if (t == null) { Console.WriteLine(tn + " 未找到"); continue; }
            Console.WriteLine("### " + tn + "  IsEnum=" + t.IsEnum + "  BaseType=" + (t.BaseType != null ? t.BaseType.FullName : "?"));
            foreach (FieldDefinition f in t.Fields)
            {
                if (f.Name == "value__") continue;
                Console.WriteLine("   " + f.Name.PadRight(24) + " = " + (f.HasConstant ? f.Constant.ToString() : "(无常量)"));
            }
            Console.WriteLine();
        }
        // 数组字段长度线索：ChaFileBody / ChaFileFace 的 shapeValue 字段类型
        foreach (string tn in new string[] { "ChaFileBody", "ChaFileFace" })
        {
            TypeDefinition t = null;
            foreach (TypeDefinition x in mod.GetTypes()) if (x.FullName == tn) { t = x; break; }
            if (t == null) continue;
            Console.WriteLine("### " + tn + " 的字段:");
            foreach (FieldDefinition f in t.Fields)
                Console.WriteLine("   " + f.FieldType.FullName.PadRight(28) + " " + f.Name);
            Console.WriteLine();
        }
    }
}
