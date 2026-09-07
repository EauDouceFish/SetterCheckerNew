using Microsoft.CodeAnalysis.CSharp;

namespace SetterChecker.Core
{
    /// <summary>
    /// 按固定顺序运行已经完成的分析模块。
    /// </summary>
    public sealed class SetterChecker
    {
        // 建立材料和函数目录，并从 khengine 可报告函数开始读取行为。
        /// <summary>
        /// 运行当前已经完成的分析步骤。
        /// </summary>
        public async Task<(
            MaterialSet Material,
            MethodCatalogResult Catalog,
            BehaviorReadResult Behaviors)> AnalyzeAsync(
            MaterialRequest request,
            CancellationToken cancellationToken = default)
        {
            MaterialSet material = await new MaterialLoader().LoadAsync(
                request,
                cancellationToken).ConfigureAwait(false);
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(
                material,
                request.Jobs,
                cancellationToken).ConfigureAwait(false);
            BehaviorReadResult behaviors = await new BehaviorReader().ReadAsync(
                material,
                catalog,
                catalog.Methods.Where(method => method.IsReportable).ToArray(),
                request.Jobs,
                cancellationToken).ConfigureAwait(false);

            return (material, catalog, behaviors);
        }
    }

    /// <summary>
    /// 表示分析不能继续时的明确错误。
    /// </summary>
    public sealed class AnalysisException : Exception
    {
        // 保存能够直接定位问题的错误信息。
        /// <summary>
        /// 建立带错误说明的分析异常。
        /// </summary>
        public AnalysisException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// 表示一次材料读取请求。
    /// </summary>
    public sealed record MaterialRequest(string AssemblyDefinitionPath, int Jobs);

    /// <summary>
    /// 表示一个源码程序集及其真实编译内容。
    /// </summary>
    public sealed record SourceAssemblyMaterial(
        string Name,
        bool IsReportAssembly,
        CSharpCompilation Compilation,
        IReadOnlyList<string> SourcePaths,
        IReadOnlyList<string> ReportSourcePaths);

    /// <summary>
    /// 表示一个编译引用及其真实函数内容所在文件。
    /// </summary>
    public sealed record ExternalAssemblyMaterial(
        string ReferencePath,
        IReadOnlyList<string> ImplementationPaths);

    /// <summary>
    /// 表示后续分析所需的全部材料。
    /// </summary>
    public sealed record MaterialSet(
        string ProjectRoot,
        string ReportRoot,
        IReadOnlyList<SourceAssemblyMaterial> SourceAssemblies,
        IReadOnlyList<ExternalAssemblyMaterial> ExternalAssemblies,
        IReadOnlyList<string> AssemblyLookupPaths,
        IReadOnlyList<string> AnalyzerPaths,
        TimeSpan Elapsed);
}
