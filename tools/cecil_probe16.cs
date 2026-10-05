using System;
using Mono.Cecil;
class Probe16 {
    static void Main(string[] a) {
        var mod = ModuleDefinition.ReadModule(a[0], new ReaderParameters { ReadSymbols = false });
        foreach (var t in mod.GetTypes()) {
            if (!t.IsEnum) continue;
            if (a.Length > 1 && t.Name.IndexOf(a[1], StringComparison.OrdinalIgnoreCase) < 0) continue;
            Console.WriteLine("=== " + t.FullName + " ===");
            foreach (var f in t.Fields) {
                if (!f.HasConstant) continue;
                Console.WriteLine("   " + f.Name + " = " + f.Constant);
            }
        }
    }
}
