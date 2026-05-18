using HookVault.Controllers;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace HookVault.Infrastructure;

/// <summary>
/// Removes <see cref="TestController"/> from the controller discovery pipeline.
/// Applied only when <c>ASPNETCORE_ENVIRONMENT != "Testing"</c>.
/// </summary>
public sealed class ExcludeTestControllersConvention : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        for (var i = application.Controllers.Count - 1; i >= 0; i--)
        {
            if (application.Controllers[i].ControllerType.AsType() == typeof(TestController))
            {
                application.Controllers.RemoveAt(i);
            }
        }
    }
}
