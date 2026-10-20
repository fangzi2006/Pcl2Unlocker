using Mono.Cecil;

namespace Pcl2Unlocker;

/// <summary>
/// 内存桩程序集解析器。
/// Cecil 写回程序集时，会对“带默认常量的参数/字段”做 CheckedResolve 以确定常量底层类型；
/// 本发行版有 4 个可选参数的默认值来自外部枚举（PCLCS.ModLoaders、MeloongCore.LogLevel），
/// 其常量在元数据中以 I4 存储。这里提供仅含对应枚举桩（Int32 底层）的内存程序集，
/// 使写回结果与原始字节完全一致，而无需分发依赖 DLL。
/// </summary>
public sealed class StubAssemblyResolver : IAssemblyResolver
{
    readonly Dictionary<string, AssemblyDefinition> stubs = new(StringComparer.OrdinalIgnoreCase);
    readonly DefaultAssemblyResolver fallback = new();
    readonly AssemblyDefinition mscorlibStub;

    public StubAssemblyResolver()
    {
        stubs["PCLCS"] = CreateEnumStub("PCLCS", "PCLCS", "ModLoaders");
        stubs["MeloongCore"] = CreateEnumStub("MeloongCore", "MeloongCore", "LogLevel");
        // 包含 mscorlib 的 ThreadPriority（LoaderTask/RunInNewThread 的可选参数默认值）。
        // 注意：桩程序集不能用 "mscorlib" 命名，否则 Cecil 会把它当作自身的 corlib 造成自引用失败，
        // 因此用中性名称，仅在解析 "mscorlib" 失败（单文件发布等场景）时由 Resolve 返回。
        mscorlibStub = CreateEnumStub("Pcl2Unlocker.CorlibStub", "System.Threading", "ThreadPriority");
    }

    static AssemblyDefinition CreateEnumStub(string asmName, string ns, string enumName)
    {
        var name = new AssemblyNameDefinition(asmName, new Version(1, 0, 0, 0));
        var asm = Mono.Cecil.AssemblyDefinition.CreateAssembly(name, asmName + ".dll", ModuleKind.Dll);
        var module = asm.MainModule;
        var enumType = new TypeDefinition(
            ns, enumName,
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.AnsiClass | TypeAttributes.AutoLayout,
            module.ImportReference(typeof(Enum)));
        enumType.Fields.Add(new FieldDefinition(
            "value__",
            FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName,
            module.TypeSystem.Int32));
        module.Types.Add(enumType);
        return asm;
    }

    public AssemblyDefinition? Resolve(AssemblyNameReference name)
    {
        if (stubs.TryGetValue(name.Name, out var a)) return a;
        // 其余程序集交回默认解析器处理
        try { return fallback.Resolve(name); }
        catch
        {
            // 单文件发布时运行时程序集无磁盘路径，默认解析器必然失败；对已知枚举返回桩
            return name.Name is "mscorlib" or "System.Private.CoreLib" ? mscorlibStub : null;
        }
    }

    public AssemblyDefinition? Resolve(AssemblyNameReference name, ReaderParameters parameters)
        => Resolve(name);

    public void Dispose()
    {
        fallback.Dispose();
        foreach (var s in stubs.Values) s.Dispose();
        stubs.Clear();
    }
}
