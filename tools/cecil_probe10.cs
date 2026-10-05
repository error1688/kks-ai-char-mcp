// 只读探针：扫描指定插件 DLL 的公开 API 面（类名含 API / KKAPI 命名空间的公开静态方法）。
using System;
using Mono.Cecil;

class Probe10
{
    static void Main(string[] args)
    {
        foreach (string asmPath in args)
        {
            Console.WriteLine("######## " + System.IO.Path.GetFileName(asmPath));
            try
            {
                ModuleDefinition mod = ModuleDefinition.ReadModule(asmPath, new ReaderParameters { ReadSymbols = false });
                foreach (TypeDefinition t in mod.GetTypes())
                {
                    if (!t.IsPublic && !t.IsNestedPublic) continue;
                    bool apiLike = t.Name.IndexOf("API", StringComparison.OrdinalIgnoreCase) >= 0
                        || t.Name.IndexOf("Controller", StringComparison.OrdinalIgnoreCase) >= 0
                        || (t.Namespace != null && t.Namespace.IndexOf("API", StringComparison.Ordinal) >= 0);
                    if (!apiLike) continue;
                    Console.WriteLine("  [type] " + t.FullName);
                    int shown = 0;
                    foreach (MethodDefinition m in t.Methods)
                    {
                        if (!m.IsPublic || m.IsConstructor || shown >= 12) continue;
                        var ps = new System.Collections.Generic.List<string>();
                        foreach (ParameterDefinition p in m.Parameters) ps.Add(Short(p.ParameterType) + " " + p.Name);
                        Console.WriteLine("      " + (m.IsStatic ? "static " : "") + Short(m.ReturnType) + " " + m.Name + "(" + string.Join(", ", ps.ToArray()) + ")");
                        shown++;
                    }
                }
                mod.Dispose();
            }
            catch (Exception e) { Console.WriteLine("  读取失败: " + e.Message); }
        }
    }

    static string Short(TypeReference ty)
    {
        if (ty.IsArray) return Short(ty.GetElementType()) + "[]";
        return ty.Name;
    }
}
