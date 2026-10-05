using System;
using Mono.Cecil;
class Probe14 {
    static void Main(string[] a) {
        var mod = ModuleDefinition.ReadModule(a[0], new ReaderParameters { ReadSymbols = false });
        string filter = a.Length > 1 ? a[1] : "";
        foreach (var t in mod.GetTypes()) {
            if (filter != "" && t.FullName.IndexOf(filter, StringComparison.Ordinal) < 0) continue;
            Console.WriteLine(t.FullName + "   : base=" + (t.BaseType != null ? t.BaseType.Name : "-")
                + (t.IsPublic || t.IsNestedPublic ? " [public]" : ""));
        }
    }
}
