using AkarangaBank.Tests.UnitTests.Helpers;
using AkarangaBank.Web.Controllers;
using AkarangaBank.Web.Data;
using AkarangaBank.Web.Models;
using AkarangaBank.Web.Services;
using AkarangaBank.Web.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace AkarangaBank.Tests.UnitTests.Services;

public class Chapter9WorkflowTests
{
    [Fact]
    public void ModeleEf_NeContientAucuneCleEtrangereShadow()
    {
        using var context = TestDbContextFactory.CreateInMemoryContext();
        var clesFantomes = context.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetForeignKeys()
                .SelectMany(foreignKey => foreignKey.Properties
                    .Where(property => property.IsShadowProperty())
                    .Select(property => $"{entity.DisplayName()}.{property.Name}")))
            .ToList();

        Assert.Empty(clesFantomes);
    }

    [Fact]
    public async Task VirementInterAgence_Valide_EnregistreQuatreEcrituresEtCompenseLesDeuxAgences()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var sourceAgence = await TestDbContextFactory.CreerAgenceAsync(context, "AKR-BJM");
        var destinationAgence = await TestDbContextFactory.CreerAgenceAsync(context, "AKR-GIT");
        var sourceClient = await TestDbContextFactory.CreerClientAsync(context, sourceAgence.Id, "CLI-001");
        var destinationClient = await TestDbContextFactory.CreerClientAsync(context, destinationAgence.Id, "CLI-002");
        var user = await TestDbContextFactory.CreerUtilisateurAsync(context, sourceAgence.Id);
        var source = await TestDbContextFactory.CreerCompteAsync(context, sourceClient.Id, sourceAgence.Id, "BJM-0001-0001", 100_000m);
        var destination = await TestDbContextFactory.CreerCompteAsync(context, destinationClient.Id, destinationAgence.Id, "GIT-0001-0001", 50_000m);
        var declaration = new DeclarationService(context, new HttpContextAccessor());
        var service = new VirementInterAgenceService(context, new BasicConformiteService(), new EmptyDetectionService(), declaration);

        var transfert = await service.InitierAsync(new VirementInterAgenceViewModel
        {
            CompteSourceId = source.Id,
            CompteDestinationId = destination.Id,
            Montant = 30_000m,
            Motif = "Test de compensation"
        }, user.Id);

        Assert.Equal("EnAttente", transfert.Statut);
        Assert.Single(await service.GetEnAttenteAsync(null), item => item.Id == transfert.Id);
        Assert.Equal(100_000m, (await context.Comptes.FindAsync(source.Id))!.Solde);
        Assert.Equal(50_000m, (await context.Comptes.FindAsync(destination.Id))!.Solde);
        Assert.Empty(await context.Ecritures.ToListAsync());
        Assert.True(await service.ValiderAsync(transfert.Id, user.Id));

        var operation = await context.Operations.SingleAsync();
        var ecritures = await context.Ecritures.Where(e => e.OperationId == operation.Id).ToListAsync();
        Assert.Equal(4, ecritures.Count);
        Assert.Equal(60_000m, ecritures.Where(e => e.Sens == "Debit").Sum(e => e.Montant));
        Assert.Equal(60_000m, ecritures.Where(e => e.Sens == "Credit").Sum(e => e.Montant));
        Assert.Equal(70_000m, (await context.Comptes.FindAsync(source.Id))!.Solde);
        Assert.Equal(80_000m, (await context.Comptes.FindAsync(destination.Id))!.Solde);

        var compensationService = new CompensationService(context);
        var compensationDestination = await compensationService.CalculerCompensationAsync(destinationAgence.Id, DateTime.UtcNow);
        Assert.Equal(30_000m, compensationDestination.TotalDebits);
        Assert.Equal(0m, compensationDestination.TotalCredits);
        Assert.True(await compensationService.VerifierEquilibreCompensationsAsync(DateTime.UtcNow));
    }

    [Fact]
    public async Task FileInterAgence_AdminVoitLesDemandesDeToutesLesAgences()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var agenceSource = await TestDbContextFactory.CreerAgenceAsync(context, "AKR-BJM");
        var agenceDestination = await TestDbContextFactory.CreerAgenceAsync(context, "AKR-GIT");
        var clientSource = await TestDbContextFactory.CreerClientAsync(context, agenceSource.Id, "CLI-001");
        var clientDestination = await TestDbContextFactory.CreerClientAsync(context, agenceDestination.Id, "CLI-002");
        var user = await TestDbContextFactory.CreerUtilisateurAsync(context, agenceSource.Id);
        var source = await TestDbContextFactory.CreerCompteAsync(context, clientSource.Id, agenceSource.Id, "BJM-0001-0001", 100_000m);
        var destination = await TestDbContextFactory.CreerCompteAsync(context, clientDestination.Id, agenceDestination.Id, "GIT-0001-0001", 50_000m);
        var service = new VirementInterAgenceService(
            context,
            new BasicConformiteService(),
            new EmptyDetectionService(),
            new DeclarationService(context, new HttpContextAccessor()));
        var transfert = await service.InitierAsync(new VirementInterAgenceViewModel
        {
            CompteSourceId = source.Id,
            CompteDestinationId = destination.Id,
            Montant = 30_000m
        }, user.Id);
        var controller = new VirementsInterAgencesController(service, null!, context)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.Role, "admin")], "test"))
                }
            }
        };

        var result = Assert.IsType<ViewResult>(await controller.Index());
        var model = Assert.IsAssignableFrom<IEnumerable<VirementInterAgence>>(result.Model);
        Assert.Contains(model, item => item.Id == transfert.Id);
    }

    [Fact]
    public async Task DepotDeSixMillions_DeclencheLaVigilanceMoyenne()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var agence = await TestDbContextFactory.CreerAgenceAsync(context, "AKR-BJM");
        var client = await TestDbContextFactory.CreerClientAsync(context, agence.Id, "CLI-001");
        var compte = await TestDbContextFactory.CreerCompteAsync(context, client.Id, agence.Id, "BJM-0001-0001", 10_000_000m);
        var service = new ConformiteService(context);

        var resultat = await service.AnalyserTransactionAsync(compte.Id, 6_000_000m, "Depot");

        Assert.True(resultat.NecessiteVigilanceRenforcee);
        Assert.Equal(NiveauAlerte.Moyen, resultat.Niveau);
        Assert.False(resultat.EstBloquee);
    }

    [Fact]
    public async Task DeposerSixMillions_PersisteUneAlerteDepassementSeuilMoyenne()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var agence = await TestDbContextFactory.CreerAgenceAsync(context, "AKR-BJM");
        var client = await TestDbContextFactory.CreerClientAsync(context, agence.Id, "CLI-001");
        var user = await TestDbContextFactory.CreerUtilisateurAsync(context, agence.Id);
        var compte = await TestDbContextFactory.CreerCompteAsync(context, client.Id, agence.Id, "BJM-0001-0001", 10_000_000m);
        var seuils = new FixedThresholdService();
        var conformite = new ConformiteService(context, seuils);
        var declarations = new DeclarationService(context, new HttpContextAccessor());
        var comptabilite = new ComptabiliteService(
            context,
            conformite,
            new SurveillanceService(context, conformite),
            declarations,
            new DetectionAvanceeService(context, seuils));

        var operation = await comptabilite.DeposerAsync(
            new DepotViewModel { CompteId = compte.Id, Montant = 6_000_000m }, user.Id);

        var alerte = await context.AlertesAML.SingleAsync(a => a.OperationId == operation.Id);
        Assert.Equal("DepassementSeuil", alerte.TypeAlerte);
        Assert.Equal("Moyen", alerte.Niveau);
    }

    [Fact]
    public async Task TroisDepotsJusteSousLeSeuil_CreentUneAlerteStructuring()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var agence = await TestDbContextFactory.CreerAgenceAsync(context, "AKR-BJM");
        var client = await TestDbContextFactory.CreerClientAsync(context, agence.Id, "CLI-001");
        var user = await TestDbContextFactory.CreerUtilisateurAsync(context, agence.Id);
        var compte = await TestDbContextFactory.CreerCompteAsync(context, client.Id, agence.Id, "BJM-0001-0001");
        var declarations = new DeclarationService(context, new HttpContextAccessor());
        var seuils = new FixedThresholdService();
        var detection = new DetectionAvanceeService(context, seuils);
        var comptabilite = new ComptabiliteService(context, new BasicConformiteService(), new EmptySurveillanceService(), declarations, detection);

        for (var i = 0; i < 3; i++)
            await comptabilite.DeposerAsync(new DepotViewModel { CompteId = compte.Id, Montant = 4_500_000m }, user.Id);

        Assert.Contains(await context.AlertesAML.ToListAsync(), a => a.TypeAlerte == "Structuring" && a.Niveau == "Eleve");
    }

    [Fact]
    public async Task DepotsSousLeSeuilSurPlusieursComptes_DeclenchentLeSmurfing()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var agence = await TestDbContextFactory.CreerAgenceAsync(context, "AKR-BJM");
        var client = await TestDbContextFactory.CreerClientAsync(context, agence.Id, "CLI-001");
        var user = await TestDbContextFactory.CreerUtilisateurAsync(context, agence.Id);
        var compte1 = await TestDbContextFactory.CreerCompteAsync(context, client.Id, agence.Id, "BJM-0001-0001");
        var compte2 = await TestDbContextFactory.CreerCompteAsync(context, client.Id, agence.Id, "BJM-0001-0002");
        var declarations = new DeclarationService(context, new HttpContextAccessor());
        var detection = new DetectionAvanceeService(context, new FixedThresholdService());
        var comptabilite = new ComptabiliteService(context, new BasicConformiteService(), new EmptySurveillanceService(), declarations, detection);

        await comptabilite.DeposerAsync(new DepotViewModel { CompteId = compte1.Id, Montant = 3_000_000m }, user.Id);
        await comptabilite.DeposerAsync(new DepotViewModel { CompteId = compte2.Id, Montant = 3_000_000m }, user.Id);

        Assert.Contains(await context.AlertesAML.ToListAsync(), a => a.TypeAlerte == "Smurfing" && a.Niveau == "Critique");
    }

    [Fact]
    public async Task DixOperationsEnUneHeure_CreentUneAlerteDeVelocite()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var agence = await TestDbContextFactory.CreerAgenceAsync(context, "AKR-BJM");
        var client = await TestDbContextFactory.CreerClientAsync(context, agence.Id, "CLI-001");
        var user = await TestDbContextFactory.CreerUtilisateurAsync(context, agence.Id);
        var compte = await TestDbContextFactory.CreerCompteAsync(context, client.Id, agence.Id, "BJM-0001-0001");
        var declarations = new DeclarationService(context, new HttpContextAccessor());
        var detection = new DetectionAvanceeService(context, new FixedThresholdService());
        var comptabilite = new ComptabiliteService(context, new BasicConformiteService(), new EmptySurveillanceService(), declarations, detection);

        for (var i = 0; i < 10; i++)
            await comptabilite.DeposerAsync(new DepotViewModel { CompteId = compte.Id, Montant = 1_000m }, user.Id);

        Assert.Contains(await context.AlertesAML.ToListAsync(), a => a.TypeAlerte == "VelociteAnormale");
    }

    [Fact]
    public async Task GenererPuisTransmettreUneDos_ChangeLeStatutEtConserveLeContenu()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var agence = await TestDbContextFactory.CreerAgenceAsync(context, "AKR-BJM");
        var client = await TestDbContextFactory.CreerClientAsync(context, agence.Id, "CLI-001");
        var user = await TestDbContextFactory.CreerUtilisateurAsync(context, agence.Id);
        var alerte = new AlerteAML
        {
            ClientId = client.Id,
            TypeAlerte = "DepassementSeuil",
            Niveau = "Moyen",
            Description = "Transaction au-dessus du seuil AML."
        };
        context.AlertesAML.Add(alerte);
        await context.SaveChangesAsync();
        var service = new CNRFService(context);

        var dos = await service.GenererDOSAsync(alerte.Id, user.Id);
        Assert.Contains(client.NumeroClient, dos.Contenu);
        Assert.True(await service.TransmettreAsync(dos.Id));
        Assert.Equal("Transmise", (await context.DeclarationsSoupcon.SingleAsync()).Statut);
    }

    [Fact]
    public async Task ModifierSeuilVigilance_InscritUneEntreeAudit()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var agence = await TestDbContextFactory.CreerAgenceAsync(context, "AKR-BJM");
        var user = await TestDbContextFactory.CreerUtilisateurAsync(context, agence.Id);
        var seuil = await context.SeuilsAML.SingleAsync(s => s.Code == "VIGILANCE");
        var service = new SeuilAMLService(context);

        Assert.True(await service.MettreAJourAsync(seuil.Id, 3_000_000m, user.Id));
        Assert.Equal(3_000_000m, (await context.SeuilsAML.FindAsync(seuil.Id))!.Montant);
        Assert.Contains(await context.AuditLogs.ToListAsync(), a => a.Action == "ModificationSeuilAML");

        var client = await TestDbContextFactory.CreerClientAsync(context, agence.Id, "CLI-001");
        var compte = await TestDbContextFactory.CreerCompteAsync(context, client.Id, agence.Id, "BJM-0001-0001", 10_000_000m);
        var analyse = await new ConformiteService(context, service).AnalyserTransactionAsync(compte.Id, 4_000_000m, "Depot");
        Assert.True(analyse.NecessiteVigilanceRenforcee);
    }

    private sealed class BasicConformiteService : IConformiteService
    {
        public Task<ResultatConformite> AnalyserTransactionAsync(int compteId, decimal montant, string typeOperation) =>
            Task.FromResult(new ResultatConformite { CompteId = compteId, Montant = montant, DateAnalyse = DateTime.UtcNow });
        public Task<bool> DetecterFractionnementAsync(int compteId, decimal montant, DateTime date) => Task.FromResult(false);
        public Task<NiveauRisqueClient> EvaluerRisqueClientAsync(int clientId) => Task.FromResult(NiveauRisqueClient.Faible);
        public Task<decimal> GetSeuilAMLAsync() => Task.FromResult(5_000_000m);
    }

    private sealed class EmptySurveillanceService : ISurveillanceService
    {
        public Task<ResultatSurveillance> AnalyserTransactionAsync(int compteId, decimal montant, string typeOperation, DateTime date) =>
            Task.FromResult(new ResultatSurveillance { CompteId = compteId, DateAnalyse = date });
        public Task<NiveauRisqueClient> EvaluerRisqueClientAsync(int clientId) => Task.FromResult(NiveauRisqueClient.Faible);
    }

    private sealed class EmptyDetectionService : IDetectionAvanceeService
    {
        private static Task<ResultatSurveillance> Vide(DateTime date) => Task.FromResult(new ResultatSurveillance { DateAnalyse = date });
        public Task<ResultatSurveillance> DetecterStructuringAsync(int clientId, DateTime date) => Vide(date);
        public Task<ResultatSurveillance> DetecterSmurfingAsync(int clientId, DateTime date) => Vide(date);
        public Task<ResultatSurveillance> DetecterLayeringAsync(int clientId, DateTime date) => Vide(date);
        public Task<ResultatSurveillance> DetecterVelociteAnormaleAsync(int clientId, DateTime date) => Vide(date);
    }

    private sealed class FixedThresholdService : ISeuilAMLService
    {
        public Task<SeuilAML?> GetSeuilAsync(string code, string typeOperation) => Task.FromResult<SeuilAML?>(null);
        public Task<IEnumerable<SeuilAML>> GetAllAsync() => Task.FromResult<IEnumerable<SeuilAML>>([]);
        public Task<bool> MettreAJourAsync(int seuilId, decimal nouveauMontant, int utilisateurId) => Task.FromResult(false);
        public Task<decimal> GetSeuilVigilanceAsync() => Task.FromResult(5_000_000m);
        public Task<decimal> GetSeuilDeclarationAsync(string typeOperation = "Toutes") => Task.FromResult(10_000_000m);
        public Task<decimal> GetSeuilBlocageAsync() => Task.FromResult(50_000_000m);
    }
}
