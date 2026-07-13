using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Boxy.Web.Services;

/// <summary>
/// Renders a Razor view under <c>Views/Emails</c> to an HTML string, so email markup lives in .cshtml
/// templates (with a shared layout) instead of inline in C#. Works outside the request pipeline, so
/// background services (notifications, retention) can render too.
/// </summary>
public class EmailRenderer(IRazorViewEngine viewEngine, ITempDataProvider tempDataProvider, IServiceScopeFactory scopeFactory)
{
    public async Task<string> RenderAsync<TModel>(string viewName, TModel model)
    {
        var path = $"~/Views/Emails/{viewName}.cshtml";
        var view = viewEngine.GetView(null, path, true);
        if (!view.Success)
        {
            throw new InvalidOperationException($"Email template not found: {path}");
        }

        // Rendering needs scoped view services (e.g. IViewBufferScope), so run inside a fresh scope -
        // this also works from singleton background services.
        using var scope = scopeFactory.CreateScope();
        var httpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());

        await using var writer = new StringWriter();
        var viewData = new ViewDataDictionary<TModel>(new EmptyModelMetadataProvider(), new ModelStateDictionary()) { Model = model };
        var tempData = new TempDataDictionary(httpContext, tempDataProvider);
        var viewContext = new ViewContext(actionContext, view.View, viewData, tempData, writer, new HtmlHelperOptions());

        await view.View.RenderAsync(viewContext);
        return writer.ToString();
    }
}
