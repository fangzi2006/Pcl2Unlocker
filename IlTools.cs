using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Pcl2Unlocker;

/// <summary>IL 层面的查找、替换与打印工具。</summary>
public static class IlTools
{
    // ---------- 类型 / 成员查找 ----------

    public static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition mod)
    {
        foreach (var t in mod.Types)
        {
            yield return t;
            foreach (var nt in AllTypes(t)) yield return nt;
        }
    }

    private static IEnumerable<TypeDefinition> AllTypes(TypeDefinition t)
    {
        foreach (var nt in t.NestedTypes)
        {
            yield return nt;
            foreach (var nnt in AllTypes(nt)) yield return nnt;
        }
    }

    public static TypeDefinition? FindType(ModuleDefinition mod, string fullName)
        => AllTypes(mod).FirstOrDefault(t => t.FullName == fullName);

    public static MethodDefinition? FindMethod(TypeDefinition type, string name, int? paramCount = null)
        => type.Methods.FirstOrDefault(m => m.Name == name && (paramCount == null || m.Parameters.Count == paramCount));

    public static FieldDefinition? FindField(TypeDefinition type, string name)
        => type.Fields.FirstOrDefault(f => f.Name == name);

    public static List<Instruction> Instrs(MethodBody body) => body.Instructions.ToList();

    public static Instruction? Find(this MethodBody body, Func<Instruction, bool> pred)
        => body.Instructions.FirstOrDefault(pred);

    public static Instruction? Next(this Instruction ins, Func<Instruction, bool> pred, int maxSteps = 60)
    {
        var cur = ins.Next;
        for (int i = 0; i < maxSteps && cur != null; i++)
        {
            if (pred(cur)) return cur;
            cur = cur.Next;
        }
        return null;
    }

    public static string? Str(this Instruction ins) => ins.Operand as string;

    /// <summary>取得 ldloc/stloc 指令操作的局部变量（兼容无操作数的 ldloc.0~3 / stloc.0~3 短形式）。</summary>
    public static VariableDefinition GetVariable(MethodBody body, Instruction ins)
    {
        if (ins.Operand is VariableDefinition v) return v;
        int idx = ins.OpCode.Code switch
        {
            Code.Ldloc_0 or Code.Stloc_0 => 0,
            Code.Ldloc_1 or Code.Stloc_1 => 1,
            Code.Ldloc_2 or Code.Stloc_2 => 2,
            Code.Ldloc_3 or Code.Stloc_3 => 3,
            _ => -1
        };
        if (idx < 0 || idx >= body.Variables.Count)
            throw new InvalidOperationException("无法从指令获取局部变量: " + ins.OpCode.Name);
        return body.Variables[idx];
    }

    /// <summary>判断是否为任意形式的 stloc（含无操作数的 stloc.0~3）。</summary>
    public static bool IsAnyStloc(OpCode op) =>
        op == OpCodes.Stloc || op == OpCodes.Stloc_S || op == OpCodes.Stloc_0 ||
        op == OpCodes.Stloc_1 || op == OpCodes.Stloc_2 || op == OpCodes.Stloc_3;

    public static string MethodName(object? operand) => operand switch
    {
        MethodReference m => m.Name,
        _ => "?"
    };

    public static string TypeName(object? operand) => operand switch
    {
        TypeReference t => t.Name,
        MemberReference m => m.DeclaringType?.Name ?? "?",
        _ => "?"
    };

    // ---------- 指令区间替换（含分支与异常处理器重映射） ----------

    /// <summary>
    /// 用 replacement 替换 [start..end]（含两端）之间的全部指令。
    /// 外部跳转到被删区间的分支、以及异常处理器边界，都会被重映射到 replacement[0]。
    /// </summary>
    public static void ReplaceRange(MethodBody body, Instruction start, Instruction end, IReadOnlyList<Instruction> replacement)
    {
        if (replacement.Count == 0) throw new ArgumentException("replacement 不能为空");

        var il = body.GetILProcessor();
        var toRemove = new List<Instruction>();
        var cur = start;
        while (true)
        {
            toRemove.Add(cur);
            if (cur == end) break;
            cur = cur.Next ?? throw new InvalidOperationException("end 不在 start 之后");
        }
        var removed = new HashSet<Instruction>(toRemove);

        // 重映射其余指令的分支目标
        foreach (var ins in body.Instructions)
        {
            if (removed.Contains(ins)) continue;
            if (ins.Operand is Instruction t && removed.Contains(t)) ins.Operand = replacement[0];
            else if (ins.Operand is Instruction[] ts)
            {
                for (int i = 0; i < ts.Length; i++)
                    if (removed.Contains(ts[i])) ts[i] = replacement[0];
            }
        }

        // 重映射异常处理器边界
        foreach (var eh in body.ExceptionHandlers)
        {
            if (eh.TryStart != null && removed.Contains(eh.TryStart)) eh.TryStart = replacement[0];
            if (eh.TryEnd != null && removed.Contains(eh.TryEnd)) eh.TryEnd = replacement[0];
            if (eh.HandlerStart != null && removed.Contains(eh.HandlerStart)) eh.HandlerStart = replacement[0];
            if (eh.HandlerEnd != null && removed.Contains(eh.HandlerEnd)) eh.HandlerEnd = replacement[0];
            if (eh.FilterStart != null && removed.Contains(eh.FilterStart)) eh.FilterStart = replacement[0];
        }

        foreach (var r in replacement) il.InsertBefore(start, r);
        foreach (var r in toRemove) il.Remove(r);
    }

    /// <summary>创建一串占位 Nop 作为跳转标签，随后可把 Operand 指向它们。</summary>
    public static Instruction Label(ILProcessor il) => il.Create(OpCodes.Nop);

    // ---------- 反汇编打印 ----------

    public static string Disassemble(MethodDefinition m)
    {
        if (!m.HasBody) return "  (无方法体)";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"  {m.DeclaringType.FullName}.{m.Name}:");
        foreach (var ins in m.Body.Instructions)
            sb.AppendLine("    " + Format(ins));
        foreach (var eh in m.Body.ExceptionHandlers)
            sb.AppendLine($"    .try[{eh.TryStart?.Offset:x4}..{eh.TryEnd?.Offset:x4}) handler[{eh.HandlerStart?.Offset:x4}..{eh.HandlerEnd?.Offset:x4}) {eh.HandlerType} {eh.CatchType?.Name}");
        return sb.ToString();
    }

    public static string Format(Instruction ins)
    {
        string op = ins.Operand switch
        {
            null => "",
            string s => "\"" + s + "\"",
            Instruction t => "IL_" + t.Offset.ToString("x4"),
            Instruction[] ts => "(" + string.Join(", ", ts.Select(t => "IL_" + t.Offset.ToString("x4"))) + ")",
            MethodReference mr => mr.FullName,
            FieldReference fr => fr.FullName,
            TypeReference tr => tr.FullName,
            VariableDefinition v => "V_" + v.Index,
            ParameterDefinition p => p.Name,
            _ => ins.Operand.ToString() ?? ""
        };
        return $"IL_{ins.Offset:x4}: {ins.OpCode.Name,-14} {op}";
    }

    // ---------- 复用现有 IL 中的操作数（避免跨程序集解析） ----------

    /// <summary>在全模块中查找第一个调用指定名称方法的指令，返回其 (MethodReference, 所在指令)。</summary>
    public static (MethodReference? method, Instruction at) FindCallTo(ModuleDefinition mod, string methodName, Func<MethodReference, bool>? extra = null)
    {
        foreach (var t in AllTypes(mod))
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var ins in m.Body.Instructions)
                {
                    if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) &&
                        ins.Operand is MethodReference mr && mr.Name == methodName && (extra == null || extra(mr)))
                        return (mr, ins);
                }
            }
        return (null, null!);
    }

    /// <summary>查找第一个 newobj 到指定构造函数的指令。</summary>
    public static (MethodReference? ctor, Instruction at) FindNewobjOf(ModuleDefinition mod, string typeName, int paramCount)
    {
        foreach (var t in AllTypes(mod))
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var ins in m.Body.Instructions)
                {
                    if (ins.OpCode == OpCodes.Newobj && ins.Operand is MethodReference mr &&
                        mr.Name == ".ctor" && mr.Parameters.Count == paramCount &&
                        (mr.DeclaringType.Name == typeName || mr.DeclaringType.FullName == typeName))
                        return (mr, ins);
                }
            }
        return (null, null!);
    }

    /// <summary>查找第一个 newobj 到指定嵌套类型构造函数的指令。</summary>
    public static MethodReference? FindNewobjOfNested(ModuleDefinition mod, string outerType, string nestedType, int paramCount)
    {
        foreach (var t in AllTypes(mod))
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var ins in m.Body.Instructions)
                {
                    if (ins.OpCode == OpCodes.Newobj && ins.Operand is MethodReference mr &&
                        mr.Name == ".ctor" && mr.Parameters.Count == paramCount &&
                        mr.DeclaringType.Name == nestedType &&
                        mr.DeclaringType.DeclaringType?.Name == outerType)
                        return mr;
                }
            }
        return null;
    }

    /// <summary>
    /// 从一次幸存的 Dialogs.SelectFile 调用中提取参数构造所需的全部操作数
    /// （OneOf.op_Implicit、Nullable ctor、ValueTuple ctor、newarr/stelem 类型、FirstOrDefault）。
    /// </summary>
    public sealed class SelectFileOperands
    {
        public MethodReference SelectFile = null!;
        public MethodReference FirstOrDefault = null!;
        public MethodReference OneOfImplicit = null!;
        public MethodReference NullableCtor = null!;
        public MethodReference ValueTupleCtor = null!;
        public TypeReference TupleArrayType = null!;   // ValueTuple<string[], string>[]
        public TypeReference StringArrayType = null!;  // string[]
        public TypeReference StelemType = null!;       // ValueTuple<string[], string>
    }

    public static SelectFileOperands? ExtractSelectFileOperands(ModuleDefinition mod)
    {
        foreach (var t in AllTypes(mod))
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                var instrs = m.Body.Instructions.ToList();
                for (int i = 0; i < instrs.Count; i++)
                {
                    if (instrs[i].OpCode != OpCodes.Call || instrs[i].Operand is not MethodReference mr) continue;
                    if (mr.Name != "SelectFile" || mr.Parameters.Count != 4) continue;

                    var result = new SelectFileOperands { SelectFile = mr };

                    // 向后找 FirstOrDefault
                    var after = instrs[i].Next;
                    if (after == null || after.OpCode != OpCodes.Call || after.Operand is not MethodReference fd || fd.Name != "FirstOrDefault")
                        continue;
                    result.FirstOrDefault = fd;

                    // 向前收集第 4 个参数的构造序列，遇到参数边界即停止
                    for (int j = i - 1, steps = 0; j >= 0 && steps < 40; j--, steps++)
                    {
                        var ins = instrs[j];
                        if (IsArgBoundary(ins)) break;

                        if (ins.OpCode == OpCodes.Call && ins.Operand is MethodReference c && c.Name == "op_Implicit")
                        {
                            if (result.OneOfImplicit == null) result.OneOfImplicit = c;
                        }
                        else if (ins.OpCode == OpCodes.Newobj && ins.Operand is MethodReference ctor)
                        {
                            string tn = ctor.DeclaringType.Name;
                            if (tn.StartsWith("Nullable") && result.NullableCtor == null) result.NullableCtor = ctor;
                            else if (tn.StartsWith("ValueTuple") && result.ValueTupleCtor == null) result.ValueTupleCtor = ctor;
                        }
                        else if (ins.OpCode == OpCodes.Newarr && ins.Operand is TypeReference arr)
                        {
                            string en = arr.Name;
                            if (en == "String" && result.StringArrayType == null) result.StringArrayType = arr;
                            else if (en.StartsWith("ValueTuple") && result.TupleArrayType == null) result.TupleArrayType = arr;
                        }
                        else if (ins.OpCode == OpCodes.Stelem_Any && ins.Operand is TypeReference st && result.StelemType == null)
                        {
                            result.StelemType = st;
                        }
                    }

                    if (result.OneOfImplicit != null && result.NullableCtor != null && result.ValueTupleCtor != null &&
                        result.TupleArrayType != null && result.StringArrayType != null && result.StelemType != null)
                        return result;
                }
            }
        return null;
    }

    // 参数构造序列的边界：遇到这些指令说明已经越界到其他参数或更早的代码（op_Implicit 调用除外，它属于 OneOf 参数构造）
    static bool IsArgBoundary(Instruction ins)
    {
        if (ins.OpCode == OpCodes.Call && ins.Operand is MethodReference mr && mr.Name == "op_Implicit") return false;
        return ins.OpCode.Code switch
        {
            Code.Call or Code.Callvirt or Code.Ldnull or Code.Stloc or Code.Stloc_S or Code.Stloc_0 or
            Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3 or Code.Br or Code.Br_S or Code.Brtrue or Code.Brtrue_S or
            Code.Brfalse or Code.Brfalse_S or Code.Ret or Code.Throw or Code.Leave or Code.Leave_S or
            Code.Endfinally => true,
            _ => false,
        };
    }
}
