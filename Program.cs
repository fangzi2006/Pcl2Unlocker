using System.Security.Cryptography;
using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Pcl2Unlocker;

public static class Program
{
    const string ToolVersion = "1.0";

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine($"Pcl2Unlocker {ToolVersion} — PCL2 发行版 MC 下载限制自动解除工具");
        Console.WriteLine();

        string? input = null, output = null, report = null;
        bool dryRun = false, noDecompile = false;
        HashSet<string>? sites = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help": Usage(); return 0;
                case "--dry-run": dryRun = true; break;
                case "--no-decompile": noDecompile = true; break;
                case "-o" or "--output":
                    if (i + 1 < args.Length) output = args[++i];
                    else { Console.Error.WriteLine("-o 缺少参数"); return 2; }
                    break;
                case "--report":
                    if (i + 1 < args.Length) report = args[++i];
                    else { Console.Error.WriteLine("--report 缺少参数"); return 2; }
                    break;
                case "--sites":
                    if (i + 1 < args.Length)
                    {
                        sites = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(s => s.ToUpperInvariant()).ToHashSet();
                    }
                    else { Console.Error.WriteLine("--sites 缺少参数"); return 2; }
                    break;
                default:
                    if (args[i].StartsWith("--output=")) output = args[i]["--output=".Length..];
                    else if (args[i].StartsWith("--report=")) report = args[i]["--report=".Length..];
                    else if (input == null) input = args[i];
                    else { Console.Error.WriteLine("无法识别的参数: " + args[i]); Usage(); return 2; }
                    break;
            }
        }

        // 无参数（例如直接双击运行）：自动模式，解除本工具所在目录下的 PCL2 发行版
        if (input == null)
        {
            var auto = FindPcl2InToolDirectory();
            if (auto == null)
            {
                Console.WriteLine("未在工具所在目录找到 PCL2 发行版 exe。");
                Console.WriteLine();
                Console.WriteLine("请将 Pcl2Unlocker.exe 放到 PCL2 发行版所在的文件夹后重新双击运行，");
                Console.WriteLine("或使用命令行参数指定路径，例如:");
                Console.WriteLine("    Pcl2Unlocker.exe \"Plain Craft Launcher 2.exe\"");
                PauseIfInteractive();
                return 2;
            }
            Console.WriteLine($"自动模式: 在本工具所在目录找到 {Path.GetFileName(auto)}");
            Console.WriteLine();
            return RunCore(auto, output, report, dryRun, noDecompile, sites, pauseAtEnd: true);
        }

        if (!File.Exists(input))
        {
            Console.Error.WriteLine("输入文件不存在: " + input);
            return 2;
        }

        return RunCore(Path.GetFullPath(input), output, report, dryRun, noDecompile, sites, pauseAtEnd: false);
    }

    /// <summary>在工具所在目录查找 PCL2 发行版 exe（按 PCL.ModModpack.ModpackInstall 特征识别，跳过工具自身与已解锁副本）。</summary>
    static string? FindPcl2InToolDirectory()
    {
        string dir;
        try
        {
            dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        }
        catch
        {
            dir = AppContext.BaseDirectory;
        }
        string selfPath;
        try { selfPath = Path.GetFullPath(Environment.ProcessPath ?? ""); }
        catch { selfPath = ""; }

        var candidates = Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly)
            .Where(f => !f.EndsWith(".unlocked.exe", StringComparison.OrdinalIgnoreCase))
            .Where(f => string.IsNullOrEmpty(selfPath) || !string.Equals(Path.GetFullPath(f), selfPath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Contains("Plain Craft Launcher", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var f in candidates)
        {
            try
            {
                using var mod = ModuleDefinition.ReadModule(f, new ReaderParameters { ReadSymbols = false });
                var t = IlTools.FindType(mod, "PCL.ModModpack");
                if (t != null && IlTools.FindMethod(t, "ModpackInstall", 3) != null)
                    return f;
            }
            catch
            {
                // 不是 .NET 程序集或读取失败，跳过
            }
        }
        return null;
    }

    /// <summary>交互式运行时（如双击）结束后暂停，避免窗口一闪而过；输出被重定向时不暂停。</summary>
    static void PauseIfInteractive()
    {
        try
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected) return;
            Console.WriteLine();
            Console.WriteLine("按任意键退出…");
            Console.ReadKey(intercept: true);
        }
        catch
        {
            // 无控制台时忽略
        }
    }

    static int RunCore(string fullInput, string? output, string? report, bool dryRun,
        bool noDecompile, HashSet<string>? sites, bool pauseAtEnd)
    {
        output ??= Path.Combine(Path.GetDirectoryName(fullInput)!,
            Path.GetFileNameWithoutExtension(fullInput) + ".unlocked" + Path.GetExtension(fullInput));
        string fullOutput = Path.GetFullPath(output);
        report ??= fullOutput + ".report.md";
        string fullReport = Path.GetFullPath(report);

        Console.WriteLine($"输入: {fullInput}");
        Console.WriteLine($"输出: {fullOutput}");
        if (dryRun) Console.WriteLine("模式: dry-run（只分析，不写文件）");
        Console.WriteLine();

        ModuleDefinition mod;
        using var readResolver = new StubAssemblyResolver();
        try
        {
            mod = ModuleDefinition.ReadModule(fullInput,
                new ReaderParameters { ReadSymbols = false, AssemblyResolver = readResolver });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("无法读取程序集（可能不是 .NET PE 文件）: " + ex.Message);
            if (pauseAtEnd) PauseIfInteractive();
            return 2;
        }

        using (mod)
        {
            var asmName = mod.Assembly.Name.Name;
            var asmVer = mod.Assembly.Name.Version?.ToString() ?? "?";
            Console.WriteLine($"程序集: {asmName} {asmVer}");
            Console.WriteLine();

            // 反编译前的片段（从原始文件）
            Dictionary<string, string> beforeSnippets = noDecompile
                ? new()
                : DecompileMethods(fullInput);

            Console.WriteLine("=== 分析 RestrictMcDownload 引用站点 ===");
            var outcomes = Unlocker.Run(mod, !dryRun, sites);
            foreach (var o in outcomes)
            {
                Console.WriteLine($"  [{o.Id}] {o.Title}");
                Console.WriteLine($"      位置: {o.Location}");
                Console.WriteLine($"      状态: {StatusText(o.Status)} — {o.Action}");
                if (o.SkipReason != null) Console.WriteLine($"      原因: {o.SkipReason}");
            }

            Console.WriteLine();
            if (dryRun)
            {
                Console.WriteLine("dry-run 完成，未写入任何文件。");
                if (pauseAtEnd) PauseIfInteractive();
                return outcomes.Any(o => o.Status == Unlocker.SiteStatus.Failed) ? 1 : 0;
            }

            try
            {
                // Cecil 写回时会解析带默认常量的参数类型；读取时已装配内存桩解析器提供缺失的外部枚举定义，
                // 保证常量字节与原始文件一致
                try
                {
                    mod.Write(fullOutput);
                }
                catch (ResolutionException)
                {
                    // 兜底：仍有无法解析的外部枚举默认常量时，移除这些默认值
                    // （VB 可选参数默认值在调用点已内联，移除后不影响本程序集既有行为）
                    int stripped = StripUnresolvableDefaults(mod);
                    Console.WriteLine($"提示: 移除了 {stripped} 个无法解析的外部枚举默认常量（调用点已内联，无副作用）");
                    mod.Write(fullOutput);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("写入输出文件失败: " + ex.Message);
                Console.Error.WriteLine(ex.ToString());
                if (pauseAtEnd) PauseIfInteractive();
                return 2;
            }
            Console.WriteLine($"已写入: {fullOutput}");

            // 重新加载输出文件做验证，并从中采集恢复后的 IL 反汇编（写盘后偏移量才有效）
            var (verify, ilDumps) = Verify(fullOutput);
            foreach (var o in outcomes.Where(x => x.Status is Unlocker.SiteStatus.Restored or Unlocker.SiteStatus.AlreadyRestored))
                if (ilDumps.TryGetValue(o.Location, out var dump)) o.BeforeIl = dump;
            Console.WriteLine();
            Console.WriteLine("=== 验证 ===");
            foreach (var v in verify) Console.WriteLine("  " + v);
            bool allOk = verify.All(v => v.StartsWith("[OK]") || v.StartsWith("[信息]"));

            // 反编译后的片段
            Dictionary<string, string> afterSnippets = noDecompile
                ? new()
                : DecompileMethods(fullOutput);

            var reportText = BuildReport(fullInput, fullOutput, asmName, asmVer, outcomes, verify);
            File.WriteAllText(fullReport, reportText, new UTF8Encoding(false));
            Console.WriteLine();
            Console.WriteLine($"报告: {fullReport}");

            int restored = outcomes.Count(o => o.Status == Unlocker.SiteStatus.Restored);
            int already = outcomes.Count(o => o.Status == Unlocker.SiteStatus.AlreadyRestored);
            int failed = outcomes.Count(o => o.Status == Unlocker.SiteStatus.Failed);
            Console.WriteLine();
            Console.WriteLine($"完成: 恢复 {restored} 处，已恢复 {already} 处，失败 {failed} 处。");
            if (failed > 0)
                Console.WriteLine("注意: 有站点未能恢复，详见报告。原始文件未被修改。");
            if (pauseAtEnd) PauseIfInteractive();
            if (failed > 0) return 1;
            if (!allOk) return 1;
            return 0;
        }
    }

    static string StatusText(Unlocker.SiteStatus s) => s switch
    {
        Unlocker.SiteStatus.Restored => "已恢复",
        Unlocker.SiteStatus.AlreadyRestored => "此前已恢复",
        Unlocker.SiteStatus.NoActionNeeded => "无需处理",
        Unlocker.SiteStatus.Skipped => "已跳过",
        Unlocker.SiteStatus.Failed => "失败",
        _ => "待处理",
    };

    static void Usage()
    {
        Console.WriteLine("""
            用法: Pcl2Unlocker [PCL2 发行版 exe 路径] [选项]

            自动反编译 PCL2 发行版，定位所有引用 RestrictMcDownload 常量的代码位置，
            取反相关判断逻辑，输出解除 MC 下载限制后的可执行文件。

            不带参数运行时（例如双击）进入自动模式：自动解除本工具所在目录下的 PCL2 发行版。

            选项:
              -o, --output <路径>   输出文件（默认: <输入目录>/<原名>.unlocked.exe）
                  --report <路径>   分析报告（默认: <输出文件>.report.md）
                  --sites <列表>    只处理指定站点，如 A,B,C（默认全部）
                  --dry-run         只分析并输出报告结论，不写文件
                  --no-decompile    跳过 C# 反编译片段（仅 IL 分析，更快）
              -h, --help            显示帮助

            示例:
              Pcl2Unlocker                                  # 自动模式（双击）
              Pcl2Unlocker "Plain Craft Launcher 2.exe"     # 指定路径
              Pcl2Unlocker PCL.exe -o PCL-patched.exe --dry-run
              Pcl2Unlocker PCL.exe --sites C                # 只恢复下载列表加载器
            """);
    }

    // ---------- 反编译片段 ----------

    static readonly string[] SnippetTypes =
    {
        "PCL.ModModpack",
        "PCL.PageDownloadInstall",
        "PCL.PageDownloadModify",
        "PCL.PageDownloadResourceDetail",
    };

    static Dictionary<string, string> DecompileMethods(string asmPath)
    {
        var result = new Dictionary<string, string>();
        try
        {
            var settings = new DecompilerSettings(LanguageVersion.Latest)
            {
                ThrowOnAssemblyResolveErrors = false,
                RemoveDeadCode = false,
            };
            var decompiler = new CSharpDecompiler(asmPath, settings);
            foreach (var typeName in SnippetTypes)
            {
                try
                {
                    var tree = decompiler.DecompileType(new FullTypeName(typeName));
                    result[typeName] = tree.ToString();
                }
                catch (Exception ex)
                {
                    result[typeName] = "(反编译失败: " + ex.Message + ")";
                }
            }
        }
        catch (Exception ex)
        {
            foreach (var typeName in SnippetTypes)
                result[typeName] = "(反编译器初始化失败: " + ex.Message + ")";
        }
        return result;
    }

    static string? CaptureMethodIl(ModuleDefinition mod, string location)
    {
        // location 形如 "PCL.ModModpack.ModpackInstall()" / "...ModpackInstall(String, String, String)" / "...LoaderInit"
        try
        {
            var typeName = location[..location.LastIndexOf('.')];
            var rest = location[(location.LastIndexOf('.') + 1)..];
            var type = IlTools.FindType(mod, typeName);
            if (type == null) return null;
            MethodDefinition? m = rest switch
            {
                "ModpackInstall()" => IlTools.FindMethod(type, "ModpackInstall", 0),
                "ModpackInstall(String, String, String)" => IlTools.FindMethod(type, "ModpackInstall", 3),
                "LoaderInit" => IlTools.FindMethod(type, "LoaderInit", 0),
                "LoadInstanceFinished" => IlTools.FindMethod(type, "LoadInstanceFinished", 1),
                "InitializeStackResourceVersions" => IlTools.FindMethod(type, "InitializeStackResourceVersions", 1),
                _ => null,
            };
            return m == null ? null : IlTools.Disassemble(m);
        }
        catch { return null; }
    }

    // ---------- 验证 ----------

    static (List<string> results, Dictionary<string, string> ilDumps) Verify(string path)
    {
        var results = new List<string>();
        var ilDumps = new Dictionary<string, string>();
        using var verifyResolver = new StubAssemblyResolver();
        using var mod = ModuleDefinition.ReadModule(path,
            new ReaderParameters { ReadSymbols = false, AssemblyResolver = verifyResolver });

        // 采集各站点方法的 IL 反汇编（写盘后偏移量有效）
        void Dump(string location)
        {
            var il = CaptureMethodIl(mod, location);
            if (il != null) ilDumps[location] = il;
        }
        Dump("PCL.ModModpack.ModpackInstall()");
        Dump("PCL.ModModpack.ModpackInstall(String, String, String)");
        Dump("PCL.PageDownloadInstall.LoaderInit");
        Dump("PCL.PageDownloadModify.LoadInstanceFinished");
        Dump("PCL.PageDownloadResourceDetail.InitializeStackResourceVersions");

        // A
        var a = IlTools.FindType(mod, "PCL.ModModpack") is { } mt ? IlTools.FindMethod(mt, "ModpackInstall", 0) : null;
        if (a == null) results.Add("[失败] 站点 A: 方法丢失");
        else if (a.Body.Find(i => i.OpCode == OpCodes.Ldstr && i.Str() == "选择整合包压缩文件") != null &&
                 a.Body.Find(i => i.OpCode == OpCodes.Ldstr && i.Str() == Unlocker.MagicMessage) == null)
            results.Add("[OK] 站点 A: ModpackInstall() 已包含文件对话框逻辑，无限制提示");
        else results.Add("[失败] 站点 A: 恢复形态不符");

        // B
        var b = IlTools.FindType(mod, "PCL.ModModpack") is { } mt2 ? IlTools.FindMethod(mt2, "ModpackInstall", 3) : null;
        if (b == null) results.Add("[失败] 站点 B: 方法丢失");
        else
        {
            bool hasSwitch = b.Body.Find(i => i.OpCode == OpCodes.Switch) != null;
            int installCalls = b.Body.Instructions.Count(i => i.OpCode == OpCodes.Call &&
                IlTools.MethodName(i.Operand) is string n && n.StartsWith("InstallPack"));
            bool noMagic = b.Body.Find(i => i.OpCode == OpCodes.Ldstr && i.Str() == Unlocker.MagicMessage) == null;
            // 参数形状校验：每个 InstallPack* 调用前的参数装载必须类型正确
            var argProblems = new List<string>();
            foreach (var ins in b.Body.Instructions)
            {
                if (ins.OpCode != OpCodes.Call || ins.Operand is not MethodReference mr || !mr.Name.StartsWith("InstallPack")) continue;
                int argc = mr.Parameters.Count;
                var pushes = new List<Instruction>();
                var cur = ins.Previous;
                for (int k = 0; k < argc && cur != null; k++) { pushes.Add(cur); cur = cur.Previous; }
                if (pushes.Count < argc) { argProblems.Add($"{mr.Name}: 参数指令不足"); continue; }
                pushes.Reverse(); // 与参数顺序一致
                // 参数 0 = File（ldarg），参数 1 = Archive（ldloc，ZipArchive 类型）
                if (pushes[0].OpCode != OpCodes.Ldarg && pushes[0].OpCode != OpCodes.Ldarg_S && pushes[0].OpCode != OpCodes.Ldarg_0)
                    argProblems.Add($"{mr.Name}: 参数 0 不是 ldarg");
                if (argc >= 2)
                {
                    var v1 = pushes[1].Operand as VariableDefinition;
                    if (pushes[1].OpCode != OpCodes.Ldloc && pushes[1].OpCode != OpCodes.Ldloc_S && pushes[1].OpCode != OpCodes.Ldloc_0 ||
                        v1 == null || !v1.VariableType.Name.Contains("ZipArchive"))
                        argProblems.Add($"{mr.Name}: 参数 1 不是 ZipArchive 局部变量");
                }
                // 参数 2 = ArchiveBaseFolder（ldloc，String 类型）——曾因 stloc.2 短形式解析错误而误用 PackType
                if (argc >= 3)
                {
                    var v2 = pushes[2].Operand as VariableDefinition;
                    if (pushes[2].OpCode != OpCodes.Ldloc && pushes[2].OpCode != OpCodes.Ldloc_S && pushes[2].OpCode != OpCodes.Ldloc_0 ||
                        v2 == null || v2.VariableType.Name != "String")
                        argProblems.Add($"{mr.Name}: 参数 2 不是 String 局部变量");
                }
            }
            if (hasSwitch && installCalls == 7 && noMagic && argProblems.Count == 0)
                results.Add("[OK] 站点 B: switch 分发恢复，7 个 InstallPack* 调用参数类型全部正确");
            else
                results.Add($"[失败] 站点 B: switch={hasSwitch}, InstallPack 调用数={installCalls}, 无魔法字符串={noMagic}, 参数问题: {string.Join("; ", argProblems)}");
        }

        // C
        var c = IlTools.FindType(mod, "PCL.PageDownloadInstall") is { } pt ? IlTools.FindMethod(pt, "LoaderInit", 0) : null;
        if (c == null) results.Add("[失败] 站点 C: 方法丢失");
        else if (c.Body.Find(i => i.OpCode == OpCodes.Ldsfld && i.Operand is FieldReference fr && fr.Name == "DlClientListLoader") != null &&
                 c.Body.Find(i => i.OpCode == OpCodes.Ldstr && i.Str() == "关闭下载") == null)
            results.Add("[OK] 站点 C: LoaderInit 已使用 DlClientListLoader");
        else results.Add("[失败] 站点 C: 恢复形态不符");

        // D
        var d = IlTools.FindType(mod, "PCL.PageDownloadModify") is { } pd ? IlTools.FindMethod(pd, "LoadInstanceFinished", 1) : null;
        if (d == null) results.Add("[失败] 站点 D: 方法丢失");
        else
        {
            bool ok = false;
            foreach (var ins in d.Body.Instructions)
            {
                if (ins.OpCode != OpCodes.Callvirt || IlTools.MethodName(ins.Operand) != "get_CardMinecraft") continue;
                var ldc = ins.Next; var set = ldc?.Next;
                if (ldc != null && set != null && Unlocker.IsLdcI4(ldc.OpCode) &&
                    set.OpCode == OpCodes.Callvirt && IlTools.MethodName(set.Operand) == "set_Visibility" &&
                    Unlocker.ReadI4(ldc) == 0) ok = true;
            }
            results.Add(ok ? "[OK] 站点 D: CardMinecraft.Visibility = Visible" : "[失败] 站点 D: 恢复形态不符");
        }

        // E
        var e = IlTools.FindType(mod, "PCL.PageDownloadResourceDetail") is { } pe ? IlTools.FindMethod(pe, "InitializeStackResourceVersions", 1) : null;
        if (e == null) results.Add("[失败] 站点 E: 方法丢失");
        else if (e.Body.Find(i => i.OpCode == OpCodes.Ldftn && IlTools.MethodName(i.Operand) == "Install_Click") != null)
            results.Add("[OK] 站点 E: 已包含 Install_Click 分支");
        else results.Add("[失败] 站点 E: 恢复形态不符");

        // 程序集整体健全性
        try
        {
            int res = mod.Resources.Count;
            var ep = mod.EntryPoint;
            results.Add($"[信息] 程序集可正常解析，嵌入资源 {res} 个，入口点 {(ep == null ? "无" : ep.Name)}");
        }
        catch (Exception ex)
        {
            results.Add("[失败] 程序集解析异常: " + ex.Message);
        }
        return (results, ilDumps);
    }

    // ---------- 报告 ----------

    /// <summary>移除所有默认常量类型无法解析的可选参数默认值（兜底路径）。</summary>
    static int StripUnresolvableDefaults(ModuleDefinition mod)
    {
        int count = 0;
        foreach (var t in IlTools.AllTypes(mod))
            foreach (var m in t.Methods)
            {
                if (!m.HasParameters) continue;
                foreach (var p in m.Parameters)
                {
                    if (!p.HasDefault || p.Constant == null) continue;
                    bool resolvable;
                    try { resolvable = p.ParameterType.Resolve() != null; }
                    catch { resolvable = false; }
                    if (!resolvable)
                    {
                        p.Constant = null;
                        p.HasDefault = false;
                        count++;
                    }
                }
            }
        return count;
    }

    static string Sha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    static string BuildReport(string input, string output, string asmName, string asmVer,
        List<Unlocker.SiteOutcome> outcomes, List<string> verify)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# PCL2 下载限制解除报告");
        sb.AppendLine();
        sb.AppendLine($"- 工具: Pcl2Unlocker {ToolVersion}");
        sb.AppendLine($"- 输入: `{input}`（{new FileInfo(input).Length:N0} 字节，SHA256 `{Sha256(input)}`）");
        sb.AppendLine($"- 输出: `{output}`（{new FileInfo(output).Length:N0} 字节，SHA256 `{Sha256(output)}`）");
        sb.AppendLine($"- 程序集: {asmName} {asmVer}");
        sb.AppendLine($"- 生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine("## 原理说明");
        sb.AppendLine();
        sb.AppendLine("`ModSecret.RestrictMcDownload` 是 VB `Const`，编译时会被**内联**到每个引用点。");
        sb.AppendLine("该发行版以优化模式构建，`If <Const> Then ...` 被常量折叠：");
        sb.AppendLine();
        sb.AppendLine("- 判断为真的分支被保留，判断为假的分支（即解除限制后本应执行的代码）被作为**不可达代码整体消除**；");
        sb.AppendLine("- 三元运算符 `If(RestrictMcDownload, Collapsed, Visible)` 被折叠为单一常量；");
        sb.AppendLine("- `RestrictMcDownload AndAlso X` 被折叠为 `X`。");
        sb.AppendLine();
        sb.AppendLine("因此无法简单翻转某个开关——本工具的做法是：反编译定位每个站点在编译后的实际形态，");
        sb.AppendLine("把被消除的逻辑用 IL 重写补回（复用程序集中仍然存活的现有引用），并对幸存的分支/常量做取反。");
        sb.AppendLine();
        sb.AppendLine("## 站点总览");
        sb.AppendLine();
        sb.AppendLine("| # | 站点 | 源码位置 | 编译后形态 | 处理结果 |");
        sb.AppendLine("|---|------|----------|-----------|----------|");
        foreach (var o in outcomes)
        {
            string state = o.CompiledState.Length > 60 ? o.CompiledState[..60] + "…" : o.CompiledState;
            state = state.Replace("\n", " ").Replace("|", "\\|");
            sb.AppendLine($"| {o.Id} | {o.Title} | {o.SourceRef} | {state} | {StatusText(o.Status)} |");
        }
        sb.AppendLine();
        sb.AppendLine("## 各站点详情");
        foreach (var o in outcomes)
        {
            sb.AppendLine();
            sb.AppendLine($"### 站点 {o.Id}：{o.Title}");
            sb.AppendLine();
            sb.AppendLine($"- 编译后位置: `{o.Location}`");
            sb.AppendLine($"- 源码位置: `{o.SourceRef}`");
            sb.AppendLine($"- 编译后形态: {o.CompiledState}");
            sb.AppendLine($"- 处理动作: {o.Action}");
            if (o.SkipReason != null) sb.AppendLine($"- 跳过原因: {o.SkipReason}");
            foreach (var d in o.Details) sb.AppendLine($"- {d}");
            if (o.BeforeIl != null)
            {
                sb.AppendLine();
                sb.AppendLine("<details><summary>恢复后的 IL 反汇编</summary>");
                sb.AppendLine();
                sb.AppendLine("```");
                sb.AppendLine(o.BeforeIl.TrimEnd());
                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine("</details>");
            }
        }
        sb.AppendLine();
        sb.AppendLine("## 验证结果");
        sb.AppendLine();
        foreach (var v in verify) sb.AppendLine("- " + v);
        sb.AppendLine();
        sb.AppendLine("## 局限与说明");
        sb.AppendLine();
        sb.AppendLine("- 本工具只修改主 exe 的 IL，不触碰其引用的 MeloongCore / PCLCS 等外部程序集；");
        sb.AppendLine("- 被消除的代码是依据本仓库同步的源码语义重放的，若发行版与源码偏差较大，工具会跳过对应站点并在报告中说明；");
        sb.AppendLine("- 更新日志中的\"删除：为遵守相关法律法规…\"仅为展示文本，不影响功能，未做修改；");
        sb.AppendLine("- 建议在完整发行版目录（含全部依赖 DLL）中替换原 exe 后使用；原始文件不会被修改，输出为新文件。");
        sb.AppendLine();
        return sb.ToString();
    }
}
