using System;
using System.IO;

namespace PS4PKGTool.Startup
{
    internal enum StartupRouteKind
    {
        NormalApplication,
        MiniPkgViewer,
        InvalidArguments
    }

    internal sealed class StartupRoute
    {
        private StartupRoute(StartupRouteKind kind, string packagePath, string errorMessage)
        {
            Kind = kind;
            PackagePath = packagePath;
            ErrorMessage = errorMessage;
        }

        public StartupRouteKind Kind { get; }
        public string PackagePath { get; }
        public string ErrorMessage { get; }

        public static StartupRoute NormalApplication() =>
            new StartupRoute(StartupRouteKind.NormalApplication, null, null);

        public static StartupRoute MiniPkgViewer(string packagePath) =>
            new StartupRoute(StartupRouteKind.MiniPkgViewer, packagePath, null);

        public static StartupRoute Invalid(string message) =>
            new StartupRoute(StartupRouteKind.InvalidArguments, null, message);
    }

    internal static class StartupArgumentRouter
    {
        public static StartupRoute Route(string[] arguments)
        {
            if (arguments == null || arguments.Length == 0)
                return StartupRoute.NormalApplication();

            if (arguments.Length != 1)
            {
                return StartupRoute.Invalid(
                    "PS4 PKG Tool 只接受无参数，或一个 .pkg 文件路径。");
            }

            string suppliedPath = arguments[0];
            if (string.IsNullOrWhiteSpace(suppliedPath))
                return StartupRoute.Invalid("传入的安装包路径为空。");

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(suppliedPath);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return StartupRoute.Invalid("传入的安装包路径无效。");
            }

            if (!string.Equals(Path.GetExtension(fullPath), ".pkg", StringComparison.OrdinalIgnoreCase))
                return StartupRoute.Invalid("传入的文件必须是 .pkg 扩展名。");

            if (!File.Exists(fullPath))
                return StartupRoute.Invalid("传入的安装包文件不存在：\n\n" + fullPath);

            try
            {
                if (new FileInfo(fullPath).Length == 0)
                    return StartupRoute.Invalid("传入的安装包文件为空：\n\n" + fullPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return StartupRoute.Invalid("无法访问传入的安装包文件：\n\n" + fullPath);
            }

            return StartupRoute.MiniPkgViewer(fullPath);
        }
    }
}
