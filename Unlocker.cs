using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Pcl2Unlocker;

/// <summary>PCL2 下载限制（RestrictMcDownload）解除器的核心逻辑。</summary>
public static class Unlocker
{
    public const string MagicMessage = "为遵守相关法律法规，PCL 暂时不再提供 MC 下载功能，非常抱歉 qwq……";

    public sealed class SiteOutcome
    {
        public required string Id { get; init; }
        public required string Title { get; init; }
        public required string Location { get; init; }
        public required string SourceRef { get; init; }
        public string CompiledState = "";   // 编译后的实际形态
        public string Action = "";          // 本工具采取的动作
        public SiteStatus Status { get; set; } = SiteStatus.Pending;
        public string? SkipReason { get; set; }
        public List<string> Details { get; } = new();
        public string? BeforeIl { get; set; }
        public string? AfterIl { get; set; }
    }

    public enum SiteStatus { Pending, Restored, AlreadyRestored, NoActionNeeded, Skipped, Failed }

    /// <summary>分析并（可选）修补全部站点。sites 为 null 时处理全部站点。</summary>
    public static List<SiteOutcome> Run(ModuleDefinition mod, bool applyChanges, HashSet<string>? sites = null)
    {
        bool Want(string id) => sites == null || sites.Contains(id);
        var results = new List<SiteOutcome>();
        if (Want("A")) results.Add(SiteA_ModpackInstallEntry(mod, applyChanges));
        if (Want("B")) results.Add(SiteB_ModpackInstallCore(mod, applyChanges));
        if (Want("C")) results.Add(SiteC_DownloadLoader(mod, applyChanges));
        if (Want("D")) results.Add(SiteD_CardVisibility(mod, applyChanges));
        if (Want("E")) results.Add(SiteE_InstallButton(mod, applyChanges));
        results.Add(ScanOtherMagicReferences(mod));
        return results;
    }

    // ---------------------------------------------------------------
    // 站点 A：ModModpack.ModpackInstall()（无参）——"安装整合包"入口
    // 源码 ModModpack.vb:11：If RestrictMcDownload Then Hint(...) : Return
    // 编译后：整个文件选择 + 安装逻辑被判定为不可达而消除，仅剩 Hint + ret
    // 恢复：合成 Dialogs.SelectFile 对话框 + IsNullOrEmpty 检查 + RunInThread(闭包 Lambda)
    // ---------------------------------------------------------------
    private static SiteOutcome SiteA_ModpackInstallEntry(ModuleDefinition mod, bool applyChanges)
    {
        var o = new SiteOutcome
        {
            Id = "A",
            Title = "整合包安装入口（选择文件对话框）",
            Location = "PCL.ModModpack.ModpackInstall()",
            SourceRef = "Modules/Minecraft/ModModpack.vb:11",
        };

        var type = IlTools.FindType(mod, "PCL.ModModpack");
        var m = type == null ? null : IlTools.FindMethod(type, "ModpackInstall", 0);
        if (m == null)
        {
            o.Status = SiteStatus.Failed;
            o.SkipReason = "未找到方法 PCL.ModModpack.ModpackInstall()";
            return o;
        }

        var magic = m.Body.Find(i => i.OpCode == OpCodes.Ldstr && i.Str() == MagicMessage);
        if (magic == null)
        {
            if (m.Body.Find(i => i.OpCode == OpCodes.Ldstr && i.Str() == "选择整合包压缩文件") != null)
            {
                o.Status = SiteStatus.AlreadyRestored;
                o.CompiledState = "已为恢复后的形态（包含文件对话框调用）";
                o.Action = "无需处理";
            }
            else
            {
                o.Status = SiteStatus.Failed;
                o.CompiledState = "未找到限制判断的锚点，形态与预期不符";
                o.Action = "跳过";
            }
            return o;
        }

        o.CompiledState = "If True Then Hint(...) : Return 之后的所有代码（SelectFile 对话框、RunInThread）被编译器作为不可达代码消除";

        if (!applyChanges) { o.Status = SiteStatus.Skipped; o.Action = "dry-run，未修改"; return o; }

        try
        {
            var il = m.Body.GetILProcessor();

            // 1. 找到方法开头的闭包对象（newobj _Closure$__0-0; stloc.0）与其局部变量
            var newobjClosure = m.Body.Find(i => i.OpCode == OpCodes.Newobj);
            if (newobjClosure == null) throw new InvalidOperationException("未找到闭包创建指令");
            var stlocClosure = newobjClosure.Next;
            if (stlocClosure == null || stlocClosure.OpCode != OpCodes.Stloc && stlocClosure.OpCode != OpCodes.Stloc_0 &&
                stlocClosure.OpCode != OpCodes.Stloc_1 && stlocClosure.OpCode != OpCodes.Stloc_2 && stlocClosure.OpCode != OpCodes.Stloc_3 &&
                stlocClosure.OpCode != OpCodes.Stloc_S)
                throw new InvalidOperationException("未找到闭包存储指令");
            var closureVar = IlTools.GetVariable(m.Body, stlocClosure);
            var closureType = closureVar.VariableType.Resolve();
            if (closureType == null) throw new InvalidOperationException("无法解析闭包类型");
            var fileField = IlTools.FindField(closureType, "$VB$Local_File");
            var lambda = IlTools.FindMethod(closureType, "_Lambda$__0", 0);
            if (fileField == null || lambda == null) throw new InvalidOperationException("闭包缺少 $VB$Local_File 字段或 _Lambda$__0 方法");

            // 2. 从幸存调用点提取 Dialogs.SelectFile 的全部参数操作数
            var sf = IlTools.ExtractSelectFileOperands(mod);
            if (sf == null) throw new InvalidOperationException("未能在程序集中找到 Dialogs.SelectFile 的现存调用以复用其参数构造");

            // 3. 复用 String.IsNullOrEmpty 与 Action..ctor 的引用
            var (isNullEmpty, _) = IlTools.FindCallTo(mod, "IsNullOrEmpty",
                mr => mr.DeclaringType.Name == "String" && mr.Parameters.Count == 1);
            var (actionCtor, _) = IlTools.FindNewobjOf(mod, "Action", 2);
            if (isNullEmpty == null || actionCtor == null) throw new InvalidOperationException("未找到 IsNullOrEmpty / Action 构造函数的现存引用");
            var runInThread = IlTools.FindType(mod, "PCL.ModBase")?.Methods
                .FirstOrDefault(x => x.Name == "RunInThread" && x.Parameters.Count == 1);
            if (runInThread == null) throw new InvalidOperationException("未找到 PCL.ModBase.RunInThread");

            // 4. 合成恢复后的方法体
            var lastRet = m.Body.Instructions.Last();
            var retLabel = IlTools.Label(il);
            var seq = new List<Instruction>
            {
                // 闭包对象先入栈，垫在 SelectFile 的参数下面，供 stfld 使用
                il.Create(OpCodes.Ldloc, closureVar),
                il.Create(OpCodes.Ldstr, "选择整合包压缩文件"),
                il.Create(OpCodes.Ldc_I4_0),
                il.Create(OpCodes.Ldnull),
                il.Create(OpCodes.Ldc_I4_1),
                il.Create(OpCodes.Newarr, sf.TupleArrayType),
                il.Create(OpCodes.Dup),
                il.Create(OpCodes.Ldc_I4_0),
                il.Create(OpCodes.Ldc_I4_3),
                il.Create(OpCodes.Newarr, sf.StringArrayType),
                il.Create(OpCodes.Dup),
                il.Create(OpCodes.Ldc_I4_0),
                il.Create(OpCodes.Ldstr, "rar"),
                il.Create(OpCodes.Stelem_Ref),
                il.Create(OpCodes.Dup),
                il.Create(OpCodes.Ldc_I4_1),
                il.Create(OpCodes.Ldstr, "zip"),
                il.Create(OpCodes.Stelem_Ref),
                il.Create(OpCodes.Dup),
                il.Create(OpCodes.Ldc_I4_2),
                il.Create(OpCodes.Ldstr, "mrpack"),
                il.Create(OpCodes.Stelem_Ref),
                il.Create(OpCodes.Ldstr, "整合包文件"),
                il.Create(OpCodes.Newobj, sf.ValueTupleCtor),
                il.Create(OpCodes.Stelem_Any, sf.StelemType),
                il.Create(OpCodes.Call, sf.OneOfImplicit),
                il.Create(OpCodes.Newobj, sf.NullableCtor),
                il.Create(OpCodes.Call, sf.SelectFile),
                il.Create(OpCodes.Call, sf.FirstOrDefault),
                il.Create(OpCodes.Stfld, fileField),
                il.Create(OpCodes.Ldloc, closureVar),
                il.Create(OpCodes.Ldfld, fileField),
                il.Create(OpCodes.Call, isNullEmpty),
                il.Create(OpCodes.Brtrue, retLabel),
                il.Create(OpCodes.Ldloc, closureVar),
                il.Create(OpCodes.Ldftn, lambda),
                il.Create(OpCodes.Newobj, actionCtor),
                il.Create(OpCodes.Call, runInThread),
                retLabel,
                il.Create(OpCodes.Ret),
            };

            IlTools.ReplaceRange(m.Body, magic, lastRet, seq);
            o.Status = SiteStatus.Restored;
            o.Action = "已合成 SelectFile 对话框 + RunInThread 安装逻辑（复用程序集中现存引用）";
            o.Details.Add($"闭包类型 {closureType.FullName}，字段 $VB$Local_File 与 _Lambda$__0 均在位");
            o.Details.Add("Dialogs.SelectFile 参数构造复用了程序集中幸存调用点的引用");
        }
        catch (Exception ex)
        {
            o.Status = SiteStatus.Failed;
            o.Action = "跳过";
            o.SkipReason = ex.Message;
        }
        return o;
    }

    // ---------------------------------------------------------------
    // 站点 B：ModModpack.ModpackInstall(File, InstanceName, Logo)——整合包安装核心
    // 源码 ModModpack.vb:88：If RestrictMcDownload Then Throw ...
    // 编译后：无条件 Throw，且后续 Select Case 分发（InstallPack* 七个方法）被整体消除
    // 恢复：用 switch 指令按 PackType 重新分发到现存的 InstallPack* 方法
    // ---------------------------------------------------------------
    private static SiteOutcome SiteB_ModpackInstallCore(ModuleDefinition mod, bool applyChanges)
    {
        var o = new SiteOutcome
        {
            Id = "B",
            Title = "整合包安装核心（按类型分发到 InstallPack*）",
            Location = "PCL.ModModpack.ModpackInstall(String, String, String)",
            SourceRef = "Modules/Minecraft/ModModpack.vb:88",
        };

        var type = IlTools.FindType(mod, "PCL.ModModpack");
        var m = type == null ? null : IlTools.FindMethod(type, "ModpackInstall", 3);
        if (m == null)
        {
            o.Status = SiteStatus.Failed;
            o.SkipReason = "未找到方法 ModpackInstall(String, String, String)";
            return o;
        }

        var magic = m.Body.Find(i => i.OpCode == OpCodes.Ldstr && i.Str() == MagicMessage);
        if (magic == null)
        {
            if (m.Body.Find(i => i.OpCode == OpCodes.Switch) != null)
            {
                o.Status = SiteStatus.AlreadyRestored;
                o.CompiledState = "已为恢复后的形态（包含 switch 分发）";
                o.Action = "无需处理";
            }
            else
            {
                o.Status = SiteStatus.Failed;
                o.CompiledState = "未找到限制判断的锚点，形态与预期不符";
                o.Action = "跳过";
            }
            return o;
        }

        o.CompiledState = "If True Then Throw 变为无条件 Throw；后面的 Select Case 分发（InstallPackCurseForge/HMCL/MMC/MCBBS/Modrinth/LauncherPack/Compress）被整体消除";

        if (!applyChanges) { o.Status = SiteStatus.Skipped; o.Action = "dry-run，未修改"; return o; }

        try
        {
            var il = m.Body.GetILProcessor();

            // 验证锚点形状：ldstr magic; newobj Exception(String); throw
            var newobjEx = magic.Next(i => i.OpCode == OpCodes.Newobj);
            var thr = newobjEx?.Next(i => i.OpCode == OpCodes.Throw);
            if (newobjEx == null || thr == null) throw new InvalidOperationException("Throw 锚点形状不符");

            // PackType 局部变量：所有 leave/leave.s 指向 magic 的指令，其前一条 stloc 的候选；
            // 其中 ArchiveBaseFolder（String）只出现一次，PackType（Int32）出现多次，取最高频的 Int32 候选
            var candidates = new Dictionary<VariableDefinition, int>();
            foreach (var ins in m.Body.Instructions)
            {
                if ((ins.OpCode == OpCodes.Leave || ins.OpCode == OpCodes.Leave_S) && ins.Operand is Instruction t && t == magic)
                {
                    var prev = ins.Previous;
                    if (prev != null && IlTools.IsAnyStloc(prev.OpCode))
                    {
                        var v = IlTools.GetVariable(m.Body, prev);
                        candidates[v] = candidates.GetValueOrDefault(v) + 1;
                    }
                }
            }
            var packVar = candidates
                .Where(kv => kv.Key.VariableType.Name == "Int32")
                .OrderByDescending(kv => kv.Value)
                .Select(kv => (VariableDefinition?)kv.Key)
                .FirstOrDefault();
            if (packVar == null) throw new InvalidOperationException("未能定位 PackType 局部变量");

            // Archive 局部变量（ZipArchive 类型）；ArchiveBaseFolder（"overrides/" 之后的 stloc，注意可能是无操作数短形式）
            var archiveVar = m.Body.Variables.FirstOrDefault(v => v.VariableType.Name.Contains("ZipArchive"));
            if (archiveVar == null) throw new InvalidOperationException("未能定位 Archive 局部变量");
            var overridesStr = m.Body.Find(i => i.OpCode == OpCodes.Ldstr && i.Str() == "overrides/");
            var baseFolderStore = overridesStr?.Next(i => IlTools.IsAnyStloc(i.OpCode));
            if (baseFolderStore == null) throw new InvalidOperationException("未能定位 ArchiveBaseFolder 局部变量");
            var baseFolderVar = IlTools.GetVariable(m.Body, baseFolderStore);
            if (baseFolderVar == packVar) throw new InvalidOperationException("ArchiveBaseFolder 与 PackType 解析到了同一局部变量，形态异常");

            // 参数按名称定位
            var pFile = m.Parameters.FirstOrDefault(p => p.Name == "File") ?? throw new InvalidOperationException("缺少 File 参数");
            var pInstance = m.Parameters.FirstOrDefault(p => p.Name == "InstanceName") ?? throw new InvalidOperationException("缺少 InstanceName 参数");
            var pLogo = m.Parameters.FirstOrDefault(p => p.Name == "Logo") ?? throw new InvalidOperationException("缺少 Logo 参数");

            // 解析七个安装方法（按参数个数校验）
            MethodDefinition Need(string name, int argc) =>
                IlTools.FindMethod(type!, name, argc) ?? throw new InvalidOperationException($"未找到 InstallPack* 方法 {name} ({argc} 参数)");
            var mCurseForge = Need("InstallPackCurseForge", 5);
            var mHMCL = Need("InstallPackHMCL", 3);
            var mMMC = Need("InstallPackMMC", 3);
            var mMCBBS = Need("InstallPackMCBBS", 4);
            var mModrinth = Need("InstallPackModrinth", 5);
            var mLauncherPack = Need("InstallPackLauncherPack", 3);
            var mCompress = Need("InstallPackCompress", 2);

            // 构造 switch 分发
            var caseLabels = new Instruction[10]; // 0..9
            for (int i = 0; i < 10; i++) caseLabels[i] = IlTools.Label(il);
            var defaultLabel = IlTools.Label(il);
            var switchInstr = il.Create(OpCodes.Switch, caseLabels);

            void EmitArgs(MethodDefinition target, List<Instruction> list)
            {
                // 依据目标方法的参数个数，按 (File, Archive, ArchiveBaseFolder, InstanceName, Logo) 顺序装载
                int n = target.Parameters.Count;
                list.Add(il.Create(OpCodes.Ldarg, pFile));
                if (n >= 2) list.Add(il.Create(OpCodes.Ldloc, archiveVar));
                if (n >= 3) list.Add(il.Create(OpCodes.Ldloc, baseFolderVar));
                if (n >= 4) list.Add(il.Create(OpCodes.Ldarg, pInstance));
                if (n == 5) list.Add(il.Create(OpCodes.Ldarg, pLogo));
                list.Add(il.Create(OpCodes.Call, target));
                list.Add(il.Create(OpCodes.Ret));
            }

            var seq = new List<Instruction>
            {
                il.Create(OpCodes.Ldloc, packVar),
                switchInstr,
            };
            // switch 之后为default（同时兜底 5..8 与负数 PackType=-1）
            seq.Add(defaultLabel);
            EmitArgs(mCompress, seq);
            seq.Add(caseLabels[0]); EmitArgs(mCurseForge, seq);
            seq.Add(caseLabels[1]); EmitArgs(mHMCL, seq);
            seq.Add(caseLabels[2]); EmitArgs(mMMC, seq);
            seq.Add(caseLabels[3]); EmitArgs(mMCBBS, seq);
            seq.Add(caseLabels[4]); EmitArgs(mModrinth, seq);
            seq.Add(caseLabels[9]); EmitArgs(mLauncherPack, seq);
            // 5..8 也指向 default
            caseLabels[5] = defaultLabel; caseLabels[6] = defaultLabel;
            caseLabels[7] = defaultLabel; caseLabels[8] = defaultLabel;

            IlTools.ReplaceRange(m.Body, magic, thr, seq);
            o.Status = SiteStatus.Restored;
            o.Action = "已恢复 switch 分发：0=CurseForge 1=HMCL 2=MMC 3=MCBBS 4=Modrinth 9=带启动器压缩包，其余=普通压缩包";
            o.Details.Add($"PackType={packVar.VariableType.Name}、Archive={archiveVar.VariableType.Name}、ArchiveBaseFolder={baseFolderVar.VariableType.Name} 局部变量均已定位");
            o.Details.Add("七个 InstallPack* 方法均在该发行版中存活，仅重新接线");
        }
        catch (Exception ex)
        {
            o.Status = SiteStatus.Failed;
            o.Action = "跳过";
            o.SkipReason = ex.Message;
        }
        return o;
    }

    // ---------------------------------------------------------------
    // 站点 C：PageDownloadInstall.LoaderInit——Minecraft 版本列表加载器（关键站点）
    // 源码 PageDownloadInstall.xaml.vb:17：If RestrictMcDownload Then 用"关闭下载"LoaderTask Else 用 DlClientListLoader
    // 编译后：仅保留 Then 分支，Else 分支（真正的 DlClientListLoader）被消除
    // 恢复：把 new LoaderTask("关闭下载", ...) 替换为 ldsfld DlClientListLoader
    // ---------------------------------------------------------------
    private static SiteOutcome SiteC_DownloadLoader(ModuleDefinition mod, bool applyChanges)
    {
        var o = new SiteOutcome
        {
            Id = "C",
            Title = "Minecraft 版本下载列表加载器（下载功能核心）",
            Location = "PCL.PageDownloadInstall.LoaderInit",
            SourceRef = "Pages/PageDownload/PageDownloadInstall.xaml.vb:17",
        };

        var type = IlTools.FindType(mod, "PCL.PageDownloadInstall");
        var m = type == null ? null : IlTools.FindMethod(type, "LoaderInit", 0);
        if (m == null)
        {
            o.Status = SiteStatus.Failed;
            o.SkipReason = "未找到方法 PCL.PageDownloadInstall.LoaderInit";
            return o;
        }

        var ldstr = m.Body.Find(i => i.OpCode == OpCodes.Ldstr && i.Str() == "关闭下载");
        if (ldstr == null)
        {
            var dlType = IlTools.FindType(mod, "PCL.ModDownload");
            var dlField = dlType == null ? null : IlTools.FindField(dlType, "DlClientListLoader");
            if (dlField != null && m.Body.Find(i => i.OpCode == OpCodes.Ldsfld && i.Operand is FieldReference fr && fr.Name == "DlClientListLoader") != null)
            {
                o.Status = SiteStatus.AlreadyRestored;
                o.CompiledState = "已为恢复后的形态（使用 DlClientListLoader）";
                o.Action = "无需处理";
            }
            else
            {
                o.Status = SiteStatus.Failed;
                o.CompiledState = "未找到\"关闭下载\"锚点，形态与预期不符";
                o.Action = "跳过";
            }
            return o;
        }

        o.CompiledState = "If/Else 仅编译了 Then 分支（新建\"关闭下载\"LoaderTask，运行即抛错）；Else 分支的 DlClientListLoader 被消除";

        if (!applyChanges) { o.Status = SiteStatus.Skipped; o.Action = "dry-run，未修改"; return o; }

        try
        {
            var il = m.Body.GetILProcessor();

            var newobjTask = ldstr.Next(i => i.OpCode == OpCodes.Newobj &&
                i.Operand is MethodReference mr && mr.DeclaringType.Name.StartsWith("LoaderTask"));
            if (newobjTask == null) throw new InvalidOperationException("未找到 LoaderTask 构造函数");

            var dlType = IlTools.FindType(mod, "PCL.ModDownload") ?? throw new InvalidOperationException("未找到 PCL.ModDownload");
            var dlField = IlTools.FindField(dlType, "DlClientListLoader") ?? throw new InvalidOperationException("未找到 DlClientListLoader 字段");

            IlTools.ReplaceRange(m.Body, ldstr, newobjTask, new[] { il.Create(OpCodes.Ldsfld, dlField) });
            o.Status = SiteStatus.Restored;
            o.Action = "已将\"关闭下载\"LoaderTask 替换为 DlClientListLoader（版本列表恢复联网获取）";
            o.Details.Add($"字段 {dlField.FullName}");
        }
        catch (Exception ex)
        {
            o.Status = SiteStatus.Failed;
            o.Action = "跳过";
            o.SkipReason = ex.Message;
        }
        return o;
    }

    // ---------------------------------------------------------------
    // 站点 D：PageDownloadModify.LoadInstanceFinished——"修改 Minecraft 版本"卡片可见性
    // 源码 PageDownloadModify.vb:315：CardMinecraft.Visibility = If(RestrictMcDownload, Collapsed, Visible)
    // 编译后：三元运算符被折叠为 Visibility.Collapsed
    // 恢复：ldc.i4.2 (Collapsed) → ldc.i4.0 (Visible)
    // ---------------------------------------------------------------
    private static SiteOutcome SiteD_CardVisibility(ModuleDefinition mod, bool applyChanges)
    {
        var o = new SiteOutcome
        {
            Id = "D",
            Title = "修改页\"Minecraft\"卡片可见性",
            Location = "PCL.PageDownloadModify.LoadInstanceFinished",
            SourceRef = "Pages/PageDownload/PageDownloadModify.vb:315",
        };

        var type = IlTools.FindType(mod, "PCL.PageDownloadModify");
        var m = type == null ? null : IlTools.FindMethod(type, "LoadInstanceFinished", 1);
        if (m == null)
        {
            o.Status = SiteStatus.Failed;
            o.SkipReason = "未找到方法 PCL.PageDownloadModify.LoadInstanceFinished";
            return o;
        }

        // 锚点：callvirt get_CardMinecraft(); ldc.i4.x; callvirt set_Visibility()
        Instruction? targetLdc = null;
        foreach (var ins in m.Body.Instructions)
        {
            if (ins.OpCode != OpCodes.Callvirt || IlTools.MethodName(ins.Operand) != "get_CardMinecraft") continue;
            var ldc = ins.Next;
            var set = ldc?.Next;
            if (ldc != null && set != null && IsLdcI4(ldc.OpCode) &&
                set.OpCode == OpCodes.Callvirt && IlTools.MethodName(set.Operand) == "set_Visibility")
            {
                targetLdc = ldc;
                break;
            }
        }

        if (targetLdc == null)
        {
            o.Status = SiteStatus.Failed;
            o.CompiledState = "未找到 CardMinecraft.Visibility 赋值锚点";
            o.Action = "跳过";
            return o;
        }

        int current = ReadI4(targetLdc);
        if (current == 0)
        {
            o.Status = SiteStatus.AlreadyRestored;
            o.CompiledState = "已为恢复后的形态（Visibility.Visible）";
            o.Action = "无需处理";
            return o;
        }

        o.CompiledState = $"三元运算符被折叠为 Visibility.Collapsed（ldc.i4.{current}）";

        if (!applyChanges) { o.Status = SiteStatus.Skipped; o.Action = "dry-run，未修改"; return o; }

        WriteI4(targetLdc, 0);
        o.Status = SiteStatus.Restored;
        o.Action = "ldc.i4." + current + "（Collapsed）→ ldc.i4.0（Visible）";
        return o;
    }

    // ---------------------------------------------------------------
    // 站点 E：PageDownloadResourceDetail.InitializeStackResourceVersions——整合包详情页"安装"按钮
    // 源码 PageDownloadCompDetail.xaml.vb:275：If Not RestrictMcDownload AndAlso TargetResourceType = ModPack Then ToListItem(Install, Save) Else ToListItem(Save)
    // 编译后：Not True AndAlso ... 恒为 False，仅保留 Else（仅保存按钮）
    // 恢复：重新合成条件分支，ModPack 类型时显示安装按钮
    // ---------------------------------------------------------------
    private static SiteOutcome SiteE_InstallButton(ModuleDefinition mod, bool applyChanges)
    {
        var o = new SiteOutcome
        {
            Id = "E",
            Title = "整合包详情页\"安装\"按钮",
            Location = "PCL.PageDownloadResourceDetail.InitializeStackResourceVersions",
            SourceRef = "Pages/PageDownload/Resource/PageDownloadCompDetail.xaml.vb:275",
        };

        var type = IlTools.FindType(mod, "PCL.PageDownloadResourceDetail");
        var m = type == null ? null : IlTools.FindMethod(type, "InitializeStackResourceVersions", 1);
        if (m == null)
        {
            o.Status = SiteStatus.Failed;
            o.SkipReason = "未找到方法 PCL.PageDownloadResourceDetail.InitializeStackResourceVersions";
            return o;
        }

        // 锚点：ldloc Version; ldarg.0; ldftn Save_Click; newobj ClickEventHandler; ldnull; ldloc IsBadDisplay; callvirt ToListItem
        // 先检查是否已恢复：恢复后方法中会存在 Install_Click 的 ldftn（而 Save_Click 在 else 分支中仍然存在）
        if (m.Body.Find(i => i.OpCode == OpCodes.Ldftn && IlTools.MethodName(i.Operand) == "Install_Click") != null)
        {
            o.Status = SiteStatus.AlreadyRestored;
            o.CompiledState = "已为恢复后的形态（包含 Install_Click 分支）";
            o.Action = "无需处理";
            return o;
        }
        var ldftnSave = m.Body.Find(i => i.OpCode == OpCodes.Ldftn && IlTools.MethodName(i.Operand) == "Save_Click");
        if (ldftnSave == null)
        {
            o.Status = SiteStatus.Failed;
            o.CompiledState = "未找到 Save_Click 锚点";
            o.Action = "跳过";
            return o;
        }

        var ldargThis = ldftnSave.Previous;
        var ldlocVer = ldargThis?.Previous;
        var newobjDel = ldftnSave.Next;
        var ldnull = newobjDel?.Next;
        var ldlocBad = ldnull?.Next;
        var callToList = ldlocBad?.Next;
        if (ldargThis == null || ldlocVer == null || ldlocVer.OpCode != OpCodes.Ldloc && ldlocVer.OpCode != OpCodes.Ldloc_S && ldlocVer.OpCode != OpCodes.Ldloc_0
            || newobjDel == null || newobjDel.OpCode != OpCodes.Newobj || ldnull == null || ldnull.OpCode != OpCodes.Ldnull
            || ldlocBad == null || callToList == null || callToList.OpCode != OpCodes.Callvirt
            || IlTools.MethodName(callToList.Operand) != "ToListItem")
        {
            o.Status = SiteStatus.Failed;
            o.CompiledState = "ToListItem 调用形状与预期不符";
            o.Action = "跳过";
            return o;
        }

        o.CompiledState = "Not True AndAlso ... 恒为 False，仅编译了 Else 分支（只有保存按钮，无安装按钮）";

        if (!applyChanges) { o.Status = SiteStatus.Skipped; o.Action = "dry-run，未修改"; return o; }

        try
        {
            var il = m.Body.GetILProcessor();
            var verVar = IlTools.GetVariable(m.Body, ldlocVer);
            var badVar = IlTools.GetVariable(m.Body, ldlocBad);
            var delCtor = (MethodReference)newobjDel.Operand;
            var saveRef = (MethodReference)ldftnSave.Operand;
            var toListRef = (MethodReference)callToList.Operand;
            var install = IlTools.FindMethod(type!, "Install_Click", 2) ?? throw new InvalidOperationException("未找到 Install_Click 方法");
            var typeField = IlTools.FindField(type!, "TargetResourceType") ?? throw new InvalidOperationException("未找到 TargetResourceType 字段");
            // ToListItem 的第二个参数是 MyIconButton.ClickEventHandler，与第一个参数的委托类型不同，必须分别构造
            var saveBtnCtor = IlTools.FindNewobjOfNested(mod, "MyIconButton", "ClickEventHandler", 2)
                ?? throw new InvalidOperationException("未找到 MyIconButton.ClickEventHandler 构造函数的现存引用");

            // 复用方法中已有的 TargetResourceType 比较常量（ResourceTypes.ModPack）
            int modPackValue = 2;
            var existingCmp = m.Body.Instructions.FirstOrDefault(i =>
                i.OpCode == OpCodes.Ldfld && i.Operand is FieldReference fr && fr.Name == "TargetResourceType");
            var cmpLdc = existingCmp?.Next;
            if (cmpLdc != null && IsLdcI4(cmpLdc.OpCode)) modPackValue = ReadI4(cmpLdc);

            var elseLabel = IlTools.Label(il);
            var endLabel = IlTools.Label(il);
            var seq = new List<Instruction>
            {
                il.Create(OpCodes.Ldarg_0),
                il.Create(OpCodes.Ldfld, typeField),
                il.Create(OpCodes.Ldc_I4, modPackValue),
                il.Create(OpCodes.Bne_Un, elseLabel),
                // Then：ToListItem(Install_Click, Save_Click, IsBadDisplay)
                il.Create(OpCodes.Ldloc, verVar),
                il.Create(OpCodes.Ldarg_0),
                il.Create(OpCodes.Ldftn, install),
                il.Create(OpCodes.Newobj, delCtor),
                il.Create(OpCodes.Ldarg_0),
                il.Create(OpCodes.Ldftn, saveRef),
                il.Create(OpCodes.Newobj, saveBtnCtor),
                il.Create(OpCodes.Ldloc, badVar),
                il.Create(OpCodes.Callvirt, toListRef),
                il.Create(OpCodes.Br, endLabel),
                // Else：ToListItem(Save_Click, Nothing, IsBadDisplay)
                elseLabel,
                il.Create(OpCodes.Ldloc, verVar),
                il.Create(OpCodes.Ldarg_0),
                il.Create(OpCodes.Ldftn, saveRef),
                il.Create(OpCodes.Newobj, delCtor),
                il.Create(OpCodes.Ldnull),
                il.Create(OpCodes.Ldloc, badVar),
                il.Create(OpCodes.Callvirt, toListRef),
                endLabel,
            };

            IlTools.ReplaceRange(m.Body, ldlocVer, callToList, seq);
            o.Status = SiteStatus.Restored;
            o.Action = "已恢复条件分支：TargetResourceType = ResourceTypes.ModPack 时显示安装按钮";
            o.Details.Add($"ResourceTypes.ModPack = {modPackValue}（复用方法内现存比较）");
        }
        catch (Exception ex)
        {
            o.Status = SiteStatus.Failed;
            o.Action = "跳过";
            o.SkipReason = ex.Message;
        }
        return o;
    }

    // ---------------------------------------------------------------
    // 站点 F 及其他：全模块扫描魔法字符串引用，分类说明
    // ---------------------------------------------------------------
    private static SiteOutcome ScanOtherMagicReferences(ModuleDefinition mod)
    {
        var o = new SiteOutcome
        {
            Id = "F",
            Title = "其余引用扫描（FormMain 拖拽安装提示、更新日志等）",
            Location = "全模块扫描 ldstr 魔法字符串",
            SourceRef = "FormMain.xaml.vb:1079 等",
        };

        var hits = new List<string>();
        foreach (var t in IlTools.AllTypes(mod))
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var ins in m.Body.Instructions)
                {
                    if (ins.OpCode != OpCodes.Ldstr || ins.Str() is not string s || !s.Contains("不再提供 MC 下载")) continue;
                    string note;
                    if (t.Name == "ModModpack" && m.Name == "ModpackInstall")
                        note = "站点 A/B（见上）";
                    else if (t.Name == "_Closure$__" && m.Name == "_Lambda$__3-0")
                        note = "\"关闭下载\"LoaderTask 的 Lambda；随站点 C 恢复而自然失效";
                    else if (t.FullName.StartsWith("PCL.FormMain"))
                        note = "If RestrictMcDownload AndAlso ex.Message = ... 已被编译器折叠为直接的消息比较，补丁后该异常不再产生，行为正确，无需处理";
                    else
                        note = "仅为界面展示文本（更新日志功能列表），不影响功能";
                    hits.Add($"{t.FullName}.{m.Name} @IL_{ins.Offset:x4} — {note}");
                }
            }

        o.CompiledState = string.Join("\n", hits);
        o.Action = "分析性扫描，未做修改";
        o.Status = SiteStatus.NoActionNeeded;
        return o;
    }

    // ---------- ldc.i4 读写辅助 ----------

    public static bool IsLdcI4(OpCode op) =>
        op == OpCodes.Ldc_I4_M1 || op == OpCodes.Ldc_I4_0 || op == OpCodes.Ldc_I4_1 || op == OpCodes.Ldc_I4_2 ||
        op == OpCodes.Ldc_I4_3 || op == OpCodes.Ldc_I4_4 || op == OpCodes.Ldc_I4_5 || op == OpCodes.Ldc_I4_6 ||
        op == OpCodes.Ldc_I4_7 || op == OpCodes.Ldc_I4_8 || op == OpCodes.Ldc_I4_S || op == OpCodes.Ldc_I4;

    public static int ReadI4(Instruction ins) => ins.OpCode.Code switch
    {
        Code.Ldc_I4_M1 => -1,
        Code.Ldc_I4_0 => 0,
        Code.Ldc_I4_1 => 1,
        Code.Ldc_I4_2 => 2,
        Code.Ldc_I4_3 => 3,
        Code.Ldc_I4_4 => 4,
        Code.Ldc_I4_5 => 5,
        Code.Ldc_I4_6 => 6,
        Code.Ldc_I4_7 => 7,
        Code.Ldc_I4_8 => 8,
        Code.Ldc_I4_S => (sbyte)ins.Operand!,
        Code.Ldc_I4 => (int)ins.Operand!,
        _ => throw new InvalidOperationException("不是 ldc.i4 指令: " + ins.OpCode.Name),
    };

    public static void WriteI4(Instruction ins, int value)
    {
        switch (value)
        {
            case -1: ins.OpCode = OpCodes.Ldc_I4_M1; ins.Operand = null; break;
            case 0: ins.OpCode = OpCodes.Ldc_I4_0; ins.Operand = null; break;
            case 1: ins.OpCode = OpCodes.Ldc_I4_1; ins.Operand = null; break;
            case 2: ins.OpCode = OpCodes.Ldc_I4_2; ins.Operand = null; break;
            case 3: ins.OpCode = OpCodes.Ldc_I4_3; ins.Operand = null; break;
            case 4: ins.OpCode = OpCodes.Ldc_I4_4; ins.Operand = null; break;
            case 5: ins.OpCode = OpCodes.Ldc_I4_5; ins.Operand = null; break;
            case 6: ins.OpCode = OpCodes.Ldc_I4_6; ins.Operand = null; break;
            case 7: ins.OpCode = OpCodes.Ldc_I4_7; ins.Operand = null; break;
            case 8: ins.OpCode = OpCodes.Ldc_I4_8; ins.Operand = null; break;
            default: ins.OpCode = OpCodes.Ldc_I4; ins.Operand = value; break;
        }
    }
}
