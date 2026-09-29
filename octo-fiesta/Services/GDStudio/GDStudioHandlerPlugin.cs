using System.Reflection;
using System.Runtime.Loader;

namespace octo_fiesta.Services.GDStudio;

/// <summary>
/// Optional extension point: if an assembly exists at the configured path (default /config/gdstudio/gdstudio-proxy.dll),
/// every public <see cref="DelegatingHandler"/> in it is added to the GDStudio HTTP client. Handlers are created
/// through DI, so they can ask for services such as IConfiguration or ILoggerFactory.
/// </summary>
public static class GDStudioHandlerPlugin
{
    public const string DefaultPath = "/config/gdstudio/gdstudio-proxy.dll";

    public static IHttpClientBuilder AddOptionalHandlers(this IHttpClientBuilder builder, string? path)
    {
        path = string.IsNullOrWhiteSpace(path) ? DefaultPath : path;
        if (!File.Exists(path)) return builder;

        try
        {
            var context = new PluginContext(Path.GetFullPath(path));
            var types = context.LoadFromAssemblyPath(context.PluginPath).GetExportedTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(DelegatingHandler).IsAssignableFrom(t));
            foreach (var type in types)
                builder.AddHttpMessageHandler(sp => (DelegatingHandler)ActivatorUtilities.CreateInstance(sp, type));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"GDStudio: could not load {path}: {ex.Message}");
        }
        return builder;
    }

    // Dependencies come from next to the plugin, except assemblies the host already has (shared type identity).
    private sealed class PluginContext(string pluginPath) : AssemblyLoadContext(isCollectible: false)
    {
        public string PluginPath { get; } = pluginPath;

        protected override Assembly? Load(AssemblyName name)
        {
            if (Default.Assemblies.Any(a => a.GetName().Name == name.Name)) return null;
            var file = Path.Combine(Path.GetDirectoryName(PluginPath)!, name.Name + ".dll");
            return File.Exists(file) ? LoadFromAssemblyPath(file) : null;
        }
    }
}
