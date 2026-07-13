using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Boxy.Web.Services;

/// <summary>
/// Renders a Razor partial view to an HTML string, so a controller can return the very markup a page would
/// have rendered server-side. Lets the client fetch a finished row instead of rebuilding it in JavaScript -
/// the row lives in one partial, not duplicated across Razor and JS.
/// </summary>
public sealed class PartialRenderer(ICompositeViewEngine viewEngine, ITempDataProvider tempDataProvider)
{
    public async Task<string> RenderAsync(ControllerContext controllerContext, string viewName, object model)
    {
        var view = viewEngine.GetView(null, viewName, isMainPage: false);
        if (!view.Success)
        {
            view = viewEngine.FindView(controllerContext, viewName, isMainPage: false);
        }

        if (!view.Success || view.View is null)
        {
            throw new InvalidOperationException($"Partial view '{viewName}' was not found.");
        }

        await using var writer = new StringWriter();
        var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        {
            Model = model
        };
        var tempData = new TempDataDictionary(controllerContext.HttpContext, tempDataProvider);
        var viewContext = new ViewContext(controllerContext, view.View, viewData, tempData, writer, new HtmlHelperOptions());
        await view.View.RenderAsync(viewContext);
        return writer.ToString();
    }
}
