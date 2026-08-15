using RazorLight;

namespace Identity.Application.Templates;

public class EmailTemplateRenderer
{
    private readonly RazorLightEngine _engine;

    public EmailTemplateRenderer()
        : this(Path.Combine(Directory.GetCurrentDirectory(), "Templates"))
    {
    }

    /// <summary>Renders from a named folder rather than from the working directory.</summary>
    public EmailTemplateRenderer(string templatesRoot)
    {
        _engine = new RazorLightEngineBuilder()
            .UseFileSystemProject(templatesRoot)
            .UseMemoryCachingProvider()
            .Build();
    }

    public async Task<string> RenderAsync<T>(string templateName, T model)
    {
        return await _engine.CompileRenderAsync(templateName, model);
    }
}