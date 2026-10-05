using System;
using Mono.Cecil;
class Probe11 {
    static void Main(string[] a) {
        var mod = ModuleDefinition.ReadModule(a[0], new ReaderParameters { ReadSymbols = false });
        string[] want = {"ChaFileClothes","ChaFileCoordinate","ChaFileAccessory","ChaFileStatus"};
        foreach (var t in mod.GetTypes()) {
            bool hit = false;
            foreach (string w in want) if (t.FullName == w) hit = true;
            if (!hit) continue;
            Console.WriteLine("=== " + t.FullName + " ===");
            foreach (var f in t.Fields) {
                if (f.IsStatic || f.Name.StartsWith("<")) continue;
                Console.WriteLine("  f " + f.FieldType.Name + " " + f.Name);
            }
            foreach (var p in t.Properties) Console.WriteLine("  p " + p.PropertyType.Name + " " + p.Name);
            Console.WriteLine();
        }
    }
}
