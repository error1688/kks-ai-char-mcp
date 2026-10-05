using System;
using Mono.Cecil;
class Probe15 {
    static void Main(string[] a) {
        var mod = ModuleDefinition.ReadModule(a[0], new ReaderParameters { ReadSymbols = false });
        string want = a[1];
        foreach (var t in mod.GetTypes()) {
            if (t.FullName != want) continue;
            Console.WriteLine("=== " + t.FullName + " ===");
            foreach (var p in t.Properties) {
                string acc = (p.GetMethod != null && p.GetMethod.IsPublic ? "get;" : "") + (p.SetMethod != null && p.SetMethod.IsPublic ? "set;" : "");
                Console.WriteLine("  p " + (p.GetMethod!=null&&p.GetMethod.IsStatic?"STATIC ":"") + p.PropertyType.Name + " " + p.Name + "  [" + acc + "]");
            }
            foreach (var f in t.Fields) { if (f.IsStatic || f.Name.StartsWith("<")) continue; Console.WriteLine("  f " + (f.IsPublic?"public ":"priv ") + f.FieldType.Name + " " + f.Name); }
            foreach (var m in t.Methods) {
                if (!m.IsPublic) continue;
                var ps = new System.Collections.Generic.List<string>();
                foreach (var q in m.Parameters) ps.Add(q.ParameterType.Name + " " + q.Name);
                Console.WriteLine("  m " + (m.IsStatic?"static ":"") + m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", ps.ToArray()) + ")");
            }
        }
    }
}
