using System.Reflection;
using AkarangaBank.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace AkarangaBank.Tests.UnitTests.Services;

public class Chapter12AuthorizationTests
{
    [Fact]
    public void PagesPretsEtCartesExigentUneConnexion()
    {
        Assert.NotNull(typeof(PretsController).GetCustomAttribute<AuthorizeAttribute>());
        Assert.NotNull(typeof(CartesController).GetCustomAttribute<AuthorizeAttribute>());
    }

    [Fact]
    public void OperationsPretsEtCartesOntLesPolitiquesDeRoleAttendues()
    {
        Assert.Equal("GuichetierOnly", PolicyFor(typeof(PretsController), nameof(PretsController.Create)));
        Assert.Equal("GestionnaireOrAdmin", PolicyFor(typeof(PretsController), nameof(PretsController.Valider)));
        Assert.Equal("AdminOnly", PolicyFor(typeof(PretsController), nameof(PretsController.Debloquer)));
        Assert.Equal("AdminOnly", PolicyFor(typeof(CartesController), nameof(CartesController.Create)));
        Assert.Equal("AdminOnly", PolicyFor(typeof(CartesController), nameof(CartesController.Bloquer)));
        Assert.Equal("AdminOnly", PolicyFor(typeof(CartesController), nameof(CartesController.Renouveler)));
    }

    private static string? PolicyFor(Type controller, string action) => controller
        .GetMethods(BindingFlags.Instance | BindingFlags.Public)
        .Where(method => method.Name == action)
        .SelectMany(method => method.GetCustomAttributes<AuthorizeAttribute>())
        .Select(attribute => attribute.Policy)
        .FirstOrDefault(policy => policy is not null);
}
