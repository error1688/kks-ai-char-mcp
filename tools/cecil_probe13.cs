using System;
using Mono.Cecil;
class Probe13 {
    static void Main(string[] a) {
        var mod = ModuleDefinition.ReadModule(a[0], new ReaderParameters { ReadSymbols = false });
        string baseName = a.Length > 1 ? a[1] : "BoneModifier";
        foreach (var t in mod.GetTypes()) {
            if (t.BaseType == null) continue;
            bool derived = t.BaseType.Name == baseName || t.BaseType.FullName == baseName
                || (t.BaseType.FullName != null && t.BaseType.FullName.EndsWith("." + baseName));
            if (!derived) continue;
            Console.WriteLine("=== " + t.FullName + " : " + t.BaseType.Name + " ===");
            foreach (var f in t.Fields) { if (f.IsStatic || f.Name.StartsWith("<")) continue; Console.WriteLine("   f " + f.FieldType.Name + " " + f.Name); }
            foreach (var p in t.Properties) Console.WriteLine("   p " + p.PropertyType.Name + " " + p.Name);
            foreach (var m in t.Methods) {
                if (!m.IsPublic || m.IsConstructor) continue;
                var ps = new System.Collections.Generic.List<string>();
                foreach (var q in m.Parameters) ps.Add(q.ParameterType.Name + " " + q.Name);
                Console.WriteLine("   m " + m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", ps.ToArray()) + ")");
            }
        }
    }
}
