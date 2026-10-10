using System.Reflection;
using System.Security.Claims;
using System.Text;
using AkarangaBank.Tests.UnitTests.Helpers;
using AkarangaBank.Web.Controllers;
using AkarangaBank.Web.Data;
using AkarangaBank.Web.Models;
using AkarangaBank.Web.Services;
using AkarangaBank.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AkarangaBank.Tests.UnitTests.Services;

public class ChapterEManualOperationTests
{
    [Fact]
    public async Task Depot_CrediteLeCompteEtEnregistreUnePartieDoubleEquilibree()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var (_, _, utilisateur, compte) = await CreerCompteAsync(context, "BJM-0001-0001");
        var comptabilite = CreerComptabilite(context);

        var operation = await comptabilite.DeposerAsync(
            new DepotViewModel { CompteId = compte.Id, Montant = 100_000m }, utilisateur.Id);

        var ecritures = await context.Ecritures.Where(e => e.OperationId == operation.Id).ToListAsync();
        Assert.Single(ecritures, e => e.CompteId == compte.Id && e.Sens == "Credit" && e.Montant == 100_000m);
        Assert.Equal(2, ecritures.Count);
        Assert.Equal(ecritures.Where(e => e.Sens == "Debit").Sum(e => e.Montant),
            ecritures.Where(e => e.Sens == "Credit").Sum(e => e.Montant));
        Assert.Equal(100_000m, (await context.Comptes.FindAsync(compte.Id))!.Solde);
    }

    [Fact]
    public async Task Retrait_CreeLeDebitEtLesFraisDUnPourcentAvecContrepartie()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var (_, _, utilisateur, compte) = await CreerCompteAsync(context, "BJM-0001-0001", 100_000m);
        var comptabilite = CreerComptabilite(context);

        var retrait = await comptabilite.RetirerAsync(
            new RetraitViewModel { CompteId = compte.Id, Montant = 50_000m }, utilisateur.Id);

        var ecrituresRetrait = await context.Ecritures.Where(e => e.OperationId == retrait.Id).ToListAsync();
        Assert.Equal(2, ecrituresRetrait.Count);
        Assert.Single(ecrituresRetrait, e => e.CompteId == compte.Id && e.Sens == "Debit" && e.Montant == 50_000m);

        var frais = await context.Operations.SingleAsync(o => o.CompteId == compte.Id && o.TypeOperation == "Frais");
        Assert.Equal(500m, frais.Montant);
        var ecrituresFrais = await context.Ecritures.Where(e => e.OperationId == frais.Id).ToListAsync();
        Assert.Equal(2, ecrituresFrais.Count);
        Assert.Equal(49_500m, (await context.Comptes.FindAsync(compte.Id))!.Solde);
        Assert.True(await comptabilite.VerifierEquilibreGlobalAsync());
    }

    [Fact]
    public async Task PetitRetrait_AppliqueLeMinimumDeCentBif()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var (_, _, utilisateur, compte) = await CreerCompteAsync(context, "BJM-0001-0001", 10_000m);
        var comptabilite = CreerComptabilite(context);

        await comptabilite.RetirerAsync(new RetraitViewModel { CompteId = compte.Id, Montant = 5_000m }, utilisateur.Id);

        var frais = await context.Operations.SingleAsync(o => o.CompteId == compte.Id && o.TypeOperation == "Frais");
        Assert.Equal(100m, frais.Montant);
        Assert.Equal(4_900m, (await context.Comptes.FindAsync(compte.Id))!.Solde);
    }

    [Fact]
    public async Task RetraitAuDessusDuSolde_RetourneLeMessageAttenduSansEcriture()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var (_, _, utilisateur, compte) = await CreerCompteAsync(context, "BJM-0001-0001", 50_000m);
        var comptabilite = CreerComptabilite(context);

        var erreur = await Assert.ThrowsAsync<InvalidOperationException>(() => comptabilite.RetirerAsync(
            new RetraitViewModel { CompteId = compte.Id, Montant = 60_000m }, utilisateur.Id));

        Assert.Equal("Solde insuffisant.", erreur.Message);
        Assert.Empty(await context.Operations.ToListAsync());
        Assert.Equal(50_000m, (await context.Comptes.FindAsync(compte.Id))!.Solde);
    }

    [Fact]
    public async Task Virement_EnregistreUnDebitEtUnCreditSymetriques()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var (agence, _, utilisateur, source) = await CreerCompteAsync(context, "BJM-0001-0001", 80_000m);
        var clientDestination = await TestDbContextFactory.CreerClientAsync(context, agence.Id, "CLI-002");
        var destination = await TestDbContextFactory.CreerCompteAsync(context, clientDestination.Id, agence.Id, "BJM-0001-0002", 10_000m);
        var comptabilite = CreerComptabilite(context);

        var operation = await comptabilite.VirerAsync(new VirementViewModel
        {
            CompteSourceId = source.Id,
            CompteDestinationId = destination.Id,
            Montant = 30_000m
        }, utilisateur.Id);

        var ecritures = await context.Ecritures.Where(e => e.OperationId == operation.Id).ToListAsync();
        Assert.Equal(2, ecritures.Count);
        Assert.Single(ecritures, e => e.CompteId == source.Id && e.Sens == "Debit" && e.Montant == 30_000m);
        Assert.Single(ecritures, e => e.CompteId == destination.Id && e.Sens == "Credit" && e.Montant == 30_000m);
        Assert.Equal(30_000m, ecritures.Where(e => e.Sens == "Debit").Sum(e => e.Montant));
        Assert.Equal(30_000m, ecritures.Where(e => e.Sens == "Credit").Sum(e => e.Montant));
        Assert.Equal(50_000m, (await context.Comptes.FindAsync(source.Id))!.Solde);
        Assert.Equal(40_000m, (await context.Comptes.FindAsync(destination.Id))!.Solde);
    }

    [Fact]
    public async Task VirementVersLeMemeCompte_EstRefuseAvecLeMessageAttendu()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var (_, _, utilisateur, compte) = await CreerCompteAsync(context, "BJM-0001-0001", 80_000m);
        var comptabilite = CreerComptabilite(context);

        var erreur = await Assert.ThrowsAsync<InvalidOperationException>(() => comptabilite.VirerAsync(new VirementViewModel
        {
            CompteSourceId = compte.Id,
            CompteDestinationId = compte.Id,
            Montant = 30_000m
        }, utilisateur.Id));

        Assert.Equal("Les comptes source et destination doivent être différents.", erreur.Message);
        Assert.Empty(await context.Operations.ToListAsync());
    }

    [Fact]
    public async Task DepotDeUnMillion_NeCreeAucuneAlerteAml()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var (_, _, utilisateur, compte) = await CreerCompteAsync(context, "BJM-0001-0001");
        var comptabilite = CreerComptabilite(context);

        await comptabilite.DeposerAsync(new DepotViewModel { CompteId = compte.Id, Montant = 1_000_000m }, utilisateur.Id);

        Assert.Empty(await context.AlertesAML.ToListAsync());
    }

    [Fact]
    public async Task DepotDeSixMillions_CreeUneAlerteMoyenneEtSonAudit()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var (_, _, utilisateur, compte) = await CreerCompteAsync(context, "BJM-0001-0001");
        var comptabilite = CreerComptabilite(context);

        var operation = await comptabilite.DeposerAsync(
            new DepotViewModel { CompteId = compte.Id, Montant = 6_000_000m }, utilisateur.Id);

        var alerte = await context.AlertesAML.SingleAsync(a => a.OperationId == operation.Id);
        Assert.Equal("DepassementSeuil", alerte.TypeAlerte);
        Assert.Equal("Moyen", alerte.Niveau);
        Assert.Equal("EnAttente", operation.Statut);
        Assert.Empty(await context.Ecritures.Where(e => e.OperationId == operation.Id).ToListAsync());
        Assert.Single(await comptabilite.GetOperationsAmlEnAttenteAsync(), item => item.Id == operation.Id);
        Assert.Equal(0m, (await context.Comptes.FindAsync(compte.Id))!.Solde);
        Assert.Contains(await context.AuditLogs.ToListAsync(), audit =>
            audit.Action == "Creation" && audit.TableConcernee == "AlertesAML" && audit.EnregistrementId == alerte.Id);

        var affichageAlertes = await new DeclarationService(context, new HttpContextAccessor())
            .GetAlertesAsync(null, "Moyen");
        Assert.Contains(affichageAlertes, item => item.Id == alerte.Id && item.TypeAlerte == "DepassementSeuil");

        Assert.True(await comptabilite.ValiderOperationAmlAsync(operation.Id, utilisateur.Id));
        Assert.Equal("Valide", (await context.Operations.FindAsync(operation.Id))!.Statut);
        Assert.Equal(6_000_000m, (await context.Comptes.FindAsync(compte.Id))!.Solde);
        Assert.Equal(2, await context.Ecritures.CountAsync(e => e.OperationId == operation.Id));
        Assert.Contains(await context.AuditLogs.ToListAsync(), audit =>
            audit.Action == "ValidationOperationAML" && audit.EnregistrementId == operation.Id);
        Assert.False(await comptabilite.ValiderOperationAmlAsync(operation.Id, utilisateur.Id));
    }

    [Fact]
    public async Task RetraitAuDessusDuSeuil_NEstDebiteQuApresApprobationAdmin()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var (_, _, utilisateur, compte) = await CreerCompteAsync(context, "BJM-0001-0001", 10_000_000m);
        var comptabilite = CreerComptabilite(context);

        var operation = await comptabilite.RetirerAsync(
            new RetraitViewModel { CompteId = compte.Id, Montant = 6_000_000m }, utilisateur.Id);

        Assert.Equal("EnAttente", operation.Statut);
        Assert.Equal(10_000_000m, (await context.Comptes.FindAsync(compte.Id))!.Solde);
        Assert.Empty(await context.Ecritures.ToListAsync());
        Assert.DoesNotContain(await context.Operations.ToListAsync(), o => o.TypeOperation == "Frais");

        Assert.True(await comptabilite.ValiderOperationAmlAsync(operation.Id, utilisateur.Id));

        Assert.Equal(3_940_000m, (await context.Comptes.FindAsync(compte.Id))!.Solde);
        Assert.Equal(2, await context.Ecritures.CountAsync(e => e.OperationId == operation.Id));
        var operationFrais = await context.Operations.SingleAsync(o => o.TypeOperation == "Frais");
        Assert.Equal(60_000m, operationFrais.Montant);
        Assert.Equal(2, await context.Ecritures.CountAsync(e => e.OperationId == operationFrais.Id));
    }

    [Fact]
    public async Task RejeterOperationAml_NEnregistreAucuneEcritureEtConserveLeSolde()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var (_, _, utilisateur, compte) = await CreerCompteAsync(context, "BJM-0001-0001", 10_000_000m);
        var comptabilite = CreerComptabilite(context);
        var operation = await comptabilite.DeposerAsync(
            new DepotViewModel { CompteId = compte.Id, Montant = 6_000_000m }, utilisateur.Id);

        Assert.True(await comptabilite.RejeterOperationAmlAsync(operation.Id, utilisateur.Id, "Origine des fonds non justifiée"));

        Assert.Equal("Rejete", (await context.Operations.FindAsync(operation.Id))!.Statut);
        Assert.Equal(10_000_000m, (await context.Comptes.FindAsync(compte.Id))!.Solde);
        Assert.Empty(await context.Ecritures.ToListAsync());
        Assert.False(await comptabilite.ValiderOperationAmlAsync(operation.Id, utilisateur.Id));
    }

    [Fact]
    public async Task TroisDepotsDeDeuxMillionsDansLaJournee_CreentUneAlerteFractionnement()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var (_, _, utilisateur, compte) = await CreerCompteAsync(context, "BJM-0001-0001");
        var comptabilite = CreerComptabilite(context);

        for (var i = 0; i < 3; i++)
            await comptabilite.DeposerAsync(new DepotViewModel { CompteId = compte.Id, Montant = 2_000_000m }, utilisateur.Id);

        Assert.Contains(await context.AlertesAML.ToListAsync(), alerte =>
            alerte.TypeAlerte == "Fractionnement" && alerte.Niveau == "Eleve");
        Assert.Contains(await new DeclarationService(context, new HttpContextAccessor()).GetAlertesAsync(null, "Eleve"),
            alerte => alerte.TypeAlerte == "Fractionnement");
    }

    [Fact]
    public async Task JournalDeComptabilite_SignaleLesOperationsEquilibrees()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var (agence, _, utilisateur, source) = await CreerCompteAsync(context, "BJM-0001-0001", 100_000m);
        var clientDestination = await TestDbContextFactory.CreerClientAsync(context, agence.Id, "CLI-002");
        var destination = await TestDbContextFactory.CreerCompteAsync(context, clientDestination.Id, agence.Id, "BJM-0001-0002");
        var comptabilite = CreerComptabilite(context);
        await comptabilite.DeposerAsync(new DepotViewModel { CompteId = source.Id, Montant = 100_000m }, utilisateur.Id);
        await comptabilite.VirerAsync(new VirementViewModel
        {
            CompteSourceId = source.Id,
            CompteDestinationId = destination.Id,
            Montant = 30_000m
        }, utilisateur.Id);

        Assert.True(await comptabilite.VerifierEquilibreGlobalAsync());
    }

    [Fact]
    public void Depot_ExigeLaPolitiqueDeRoleOperationsWrite()
    {
        var post = typeof(OperationsController).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(method => method.Name == nameof(OperationsController.Depot)
                && method.GetParameters().Any(parameter => parameter.ParameterType == typeof(DepotViewModel)));

        Assert.Contains(post.GetCustomAttributes<AuthorizeAttribute>(), attribute => attribute.Policy == BankAuthorizationPolicies.OperationsWrite);
    }

    [Fact]
    public async Task RoleClient_EstRefuseParLaPolitiqueReelleDeDepot()
    {
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddAuthorization(options => options.AddOperationsWritePolicy())
            .BuildServiceProvider();
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var client = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "42"), new Claim(ClaimTypes.Role, "client")], "test"));
        var guichetier = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "43"), new Claim(ClaimTypes.Role, "guichetier")], "test"));

        Assert.False((await authorization.AuthorizeAsync(client, resource: null, BankAuthorizationPolicies.OperationsWrite)).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(guichetier, resource: null, BankAuthorizationPolicies.OperationsWrite)).Succeeded);
    }

    [Fact]
    public async Task GestionnairePeutConsulterLesAlertes_EtClientEnEstRefuse()
    {
        var authorize = typeof(ConformiteController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.Equal(BankAuthorizationPolicies.GestionnaireOrAdmin, authorize?.Policy);

        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddAuthorization(options => options.AddGestionnaireOrAdminPolicy())
            .BuildServiceProvider();
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var gestionnaire = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "44"), new Claim(ClaimTypes.Role, "gestionnaire")], "test"));
        var client = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "45"), new Claim(ClaimTypes.Role, "client")], "test"));

        Assert.True((await authorization.AuthorizeAsync(gestionnaire, resource: null, BankAuthorizationPolicies.GestionnaireOrAdmin)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(client, resource: null, BankAuthorizationPolicies.GestionnaireOrAdmin)).Succeeded);
    }

    [Fact]
    public async Task ExportCsvDesAlertes_ContientLesAlertesFiltrees()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var agence = await TestDbContextFactory.CreerAgenceAsync(context, "AKR-BJM");
        var client = await TestDbContextFactory.CreerClientAsync(context, agence.Id, "CLI-001");
        context.AlertesAML.Add(new AlerteAML
        {
            ClientId = client.Id,
            TypeAlerte = "DepassementSeuil",
            Niveau = "Moyen",
            Description = "Alerte de test"
        });
        await context.SaveChangesAsync();
        var controller = new ConformiteController(new DeclarationService(context, new HttpContextAccessor()), null!, null!);

        var result = Assert.IsType<FileContentResult>(await controller.ExporterCsv(null, "Moyen"));

        Assert.StartsWith("text/csv", result.ContentType);
        Assert.Contains("DepassementSeuil", Encoding.UTF8.GetString(result.FileContents));
    }

    [Fact]
    public async Task RapportQuotidien_ProduitUnCsvPourLeJourDemande()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var agence = await TestDbContextFactory.CreerAgenceAsync(context, "AKR-BJM");
        var client = await TestDbContextFactory.CreerClientAsync(context, agence.Id, "CLI-001");
        context.AlertesAML.Add(new AlerteAML
        {
            ClientId = client.Id,
            TypeAlerte = "DepassementSeuil",
            Niveau = "Moyen",
            Description = "Alerte du rapport quotidien",
            DateAlerte = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var temporaryDirectory = Directory.CreateTempSubdirectory("AkarangaAmlReportTests-");
        try
        {
            await using var provider = new ServiceCollection()
                .AddSingleton(context)
                .BuildServiceProvider();
            var environment = new TestWebHostEnvironment(temporaryDirectory.FullName);
            var service = new RapportAmlQuotidienService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                environment,
                NullLogger<RapportAmlQuotidienService>.Instance);
            var jourLocal = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(2));

            var path = await service.GenererRapportPourDateAsync(jourLocal);

            Assert.True(File.Exists(path));
            Assert.Contains("DepassementSeuil", await File.ReadAllTextAsync(path, Encoding.UTF8));
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ApplicationUser_UtiliseBienLaCleEntiereIdentityUserInt()
    {
        Assert.True(typeof(Microsoft.AspNetCore.Identity.IdentityUser<int>).IsAssignableFrom(typeof(ApplicationUser)));
    }

    private static ComptabiliteService CreerComptabilite(AkarangaBankContext context)
    {
        var seuils = new SeuilAMLService(context);
        var conformite = new ConformiteService(context, seuils);
        var declarations = new DeclarationService(context, new HttpContextAccessor());
        return new ComptabiliteService(
            context,
            conformite,
            new SurveillanceService(context, conformite),
            declarations,
            new DetectionAvanceeService(context, seuils));
    }

    private static async Task<(Agence Agence, Client Client, ApplicationUser Utilisateur, Compte Compte)> CreerCompteAsync(
        AkarangaBankContext context, string numero, decimal solde = 0m)
    {
        var agence = await TestDbContextFactory.CreerAgenceAsync(context, "AKR-BJM");
        var client = await TestDbContextFactory.CreerClientAsync(context, agence.Id, "CLI-001");
        var utilisateur = await TestDbContextFactory.CreerUtilisateurAsync(context, agence.Id);
        var compte = await TestDbContextFactory.CreerCompteAsync(context, client.Id, agence.Id, numero, solde);
        return (agence, client, utilisateur, compte);
    }

    private sealed class TestWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "AkarangaBank.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
