using System;
using System.Collections.Generic;
using Mono.Cecil;
class Probe12 {
    static void Main(string[] a) {
        var mod = ModuleDefinition.ReadModule(a[0], new ReaderParameters { ReadSymbols = false });
        Console.WriteLine("######## " + System.IO.Path.GetFileName(a[0]));
        foreach (var t in mod.GetTypes()) {
            if (!t.IsPublic && !t.IsNestedPublic) continue;
            bool hasStatic = false;
            foreach (var m in t.Methods) if (m.IsPublic && m.IsStatic && !m.IsConstructor) { hasStatic = true; break; }
            if (!hasStatic) continue;
            Console.WriteLine("  [type] " + t.FullName + (t.IsAbstract && t.IsSealed ? " (static)" : ""));
            int n = 0;
            foreach (var m in t.Methods) {
                if (!m.IsPublic || !m.IsStatic || m.IsConstructor || n >= 14) continue;
                var ps = new List<string>();
                foreach (var p in m.Parameters) ps.Add(Short(p.ParameterType) + " " + p.Name);
                Console.WriteLine("     " + Short(m.ReturnType) + " " + m.Name + "(" + string.Join(", ", ps.ToArray()) + ")");
                n++;
            }
        }
    }
    static string Short(TypeReference t) { if (t.IsArray) return Short(t.GetElementType()) + "[]"; return t.Name; }
}
