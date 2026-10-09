using AkarangaBank.Web.Data;
using AkarangaBank.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AkarangaBank.Tests.UnitTests.Helpers;

public static class TestDbContextFactory
{
    public static AkarangaBankContext CreateInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<AkarangaBankContext>()
            .UseInMemoryDatabase($"AkarangaTests_{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var context = new AkarangaBankContext(options);
        context.Database.EnsureCreated();
        return context;
    }

    public static async Task<Agence> CreerAgenceAsync(AkarangaBankContext context, string code)
    {
        var agence = new Agence { Code = code, Nom = $"Agence {code}", Ville = "Bujumbura" };
        context.Agences.Add(agence);
        await context.SaveChangesAsync();
        return agence;
    }

    public static async Task<Client> CreerClientAsync(AkarangaBankContext context, int agenceId, string numero)
    {
        var client = new Client
        {
            NumeroClient = numero,
            Nom = "Nshimirimana",
            Prenom = "Emmanuel",
            DateNaissance = new DateTime(1985, 3, 12),
            NumeroCNI = $"CNI-{Guid.NewGuid():N}"[..20],
            AgenceId = agenceId,
            Statut = "Actif"
        };
        context.Clients.Add(client);
        await context.SaveChangesAsync();
        return client;
    }

    public static async Task<ApplicationUser> CreerUtilisateurAsync(AkarangaBankContext context, int agenceId)
    {
        var user = new ApplicationUser
        {
            UserName = $"test-{Guid.NewGuid():N}@akaranga.bi",
            Email = $"test-{Guid.NewGuid():N}@akaranga.bi",
            NomComplet = "Utilisateur test",
            Role = "admin",
            AgenceId = agenceId,
            EmailConfirmed = true
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    public static async Task<Compte> CreerCompteAsync(
        AkarangaBankContext context, int clientId, int agenceId, string numero, decimal solde = 0m)
    {
        var type = await context.TypesCompte.FirstOrDefaultAsync(t => t.Code == "COURANT");
        if (type is null)
        {
            type = new TypeCompte { Code = "COURANT", Libelle = "Compte courant" };
            context.TypesCompte.Add(type);
            await context.SaveChangesAsync();
        }

        var compte = new Compte
        {
            NumeroCompte = numero,
            ClientId = clientId,
            TypeCompteId = type.Id,
            AgenceId = agenceId,
            Solde = solde,
            Devise = "BIF",
            Statut = "Actif",
            DateOuverture = DateTime.UtcNow
        };
        context.Comptes.Add(compte);
        await context.SaveChangesAsync();
        return compte;
    }
}
