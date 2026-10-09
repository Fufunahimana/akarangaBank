using AkarangaBank.Tests.UnitTests.Helpers;
using AkarangaBank.Web.Data;
using AkarangaBank.Web.Models;
using AkarangaBank.Web.Services;
using AkarangaBank.Web.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AkarangaBank.Tests.UnitTests.Services;

public class Chapter12LoanCardTests
{
    [Theory]
    [InlineData(1_200_000, 0, 12, 100_000)]
    [InlineData(1_000_000, 12, 12, 88_848.79)]
    [InlineData(100_000, 0, 10, 10_000)]
    [InlineData(500_000, 12, 12, 44_424.39)]
    [InlineData(2_400_000, 18, 24, 119_817.84)]
    public void CalculerMensualite_ArronditAuCentime(decimal principal, decimal taux, int duree, decimal attendu)
    {
        var service = new PretService(TestDbContextFactory.CreateInMemoryContext());
        Assert.Equal(attendu, service.CalculerMensualite(principal, taux, duree));
    }

    [Fact]
    public void GenererEcheancier_AmortitToutLeCapitalEtTermineAZero()
    {
        using var context = TestDbContextFactory.CreateInMemoryContext();
        var service = new PretService(context);
        var pret = new Pret
        {
            MontantPrincipal = 1_000_000m,
            TauxInteret = 12m,
            DureeMois = 12,
            Mensualite = service.CalculerMensualite(1_000_000m, 12m, 12),
            DateDebut = new DateTime(2026, 1, 15)
        };

        var echeances = service.GenererEcheancier(pret);

        Assert.Equal(12, echeances.Count);
        Assert.Equal(1_000_000m, echeances.Sum(e => e.Capital));
        Assert.Equal(0m, echeances[^1].SoldeRestant);
        Assert.Equal(new DateTime(2026, 2, 15), echeances[0].DateEcheance);
    }

    [Fact]
    public async Task Debloquer_CrediteLeCompteEtCreeUneOperationEquilibree()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var (agence, client, user, compte) = await CreerContexteCompteAsync(context, 100_000m);
        var service = new PretService(context);
        var pret = await service.CreerDemandeAsync(new PretDemandeViewModel
        {
            ClientId = client.Id, CompteDebitId = compte.Id, TypePretId = 1,
            MontantPrincipal = 500_000m, DureeMois = 12
        });
        await service.ValiderAsync(pret.Id, 12m, user.Id);

        var operation = await service.DebloquerAsync(pret.Id, user.Id);

        Assert.Equal(600_000m, (await context.Comptes.FindAsync(compte.Id))!.Solde);
        Assert.Equal("EnCours", (await context.Prets.FindAsync(pret.Id))!.Statut);
        Assert.Equal(12, await context.EcheancesPret.CountAsync(e => e.PretId == pret.Id));
        var ecritures = await context.Ecritures.Where(e => e.OperationId == operation.Id).ToListAsync();
        Assert.Contains(ecritures, e => e.Sens == "Credit" && e.CompteId == compte.Id && e.Montant == 500_000m);
        Assert.Equal(ecritures.Where(e => e.Sens == "Debit").Sum(e => e.Montant), ecritures.Where(e => e.Sens == "Credit").Sum(e => e.Montant));
    }

    [Fact]
    public async Task Rembourser_EcheanceEnRetard_DebiteMontantEtPenaliteEtEquilibreLesEcritures()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var (_, client, user, compte) = await CreerContexteCompteAsync(context, 200_000m);
        var pret = await CreerPretEnCoursAsync(context, client.Id, compte, 100_000m, 1);
        var now = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
        var echeance = new EcheancePret
        {
            PretId = pret.Id, NumeroEcheance = 1, Capital = 100_000m, Interet = 1_000m,
            MontantTotal = 101_000m, SoldeRestant = 0m, DateEcheance = now.Date.AddDays(-31), Statut = "EnRetard"
        };
        context.EcheancesPret.Add(echeance);
        await context.SaveChangesAsync();
        var service = new PretService(context);

        var operation = await service.RembourserAsync(echeance.Id, user.Id, now);

        Assert.Equal(1_010m, (await context.EcheancesPret.FindAsync(echeance.Id))!.Penalite);
        Assert.Equal(97_990m, (await context.Comptes.FindAsync(compte.Id))!.Solde);
        Assert.Equal(102_010m, operation.Montant);
        var entries = await context.Ecritures.Where(e => e.OperationId == operation.Id).ToListAsync();
        Assert.Equal(entries.Where(e => e.Sens == "Debit").Sum(e => e.Montant), entries.Where(e => e.Sens == "Credit").Sum(e => e.Montant));
        Assert.Equal("Solde", (await context.Prets.FindAsync(pret.Id))!.Statut);
    }

    [Fact]
    public async Task MettreAJourRetards_MarqueEcheanceEtPretImpayApresTroisRetards()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var (_, client, _, compte) = await CreerContexteCompteAsync(context, 1_000_000m);
        var pret = await CreerPretEnCoursAsync(context, client.Id, compte, 300_000m, 3);
        var hier = DateTime.UtcNow.Date.AddDays(-1);
        for (var n = 1; n <= 3; n++)
            context.EcheancesPret.Add(new EcheancePret
            {
                PretId = pret.Id, NumeroEcheance = n, Capital = 100_000m, Interet = 1_000m,
                MontantTotal = 101_000m, SoldeRestant = (3 - n) * 100_000m,
                DateEcheance = hier.AddMonths(-n + 1), Statut = "EnAttente"
            });
        await context.SaveChangesAsync();

        var nombre = await new PretService(context).MettreAJourRetardsAsync(DateTime.UtcNow.Date);

        Assert.Equal(3, nombre);
        Assert.All(await context.EcheancesPret.ToListAsync(), e => Assert.Equal("EnRetard", e.Statut));
        Assert.Equal("Impaye", (await context.Prets.FindAsync(pret.Id))!.Statut);
        Assert.All(await context.EcheancesPret.ToListAsync(), e => Assert.True(e.Penalite >= 500m));
    }

    [Fact]
    public async Task EmettreCarte_VisaNumeroDeSeizeChiffresEtExpirationTroisAns()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var (_, _, _, compte) = await CreerContexteCompteAsync(context, 500_000m);
        var carte = await new CarteService(context).EmettreAsync(NouvelleCarte(compte.Id));

        Assert.Equal(16, carte.NumeroCarte.Length);
        Assert.StartsWith("4", carte.NumeroCarte);
        Assert.Equal("**** **** **** " + carte.NumeroCarte[^4..], carte.NumeroCarteMasque);
        Assert.Equal("Active", carte.Statut);
        Assert.InRange(carte.DateExpiration.Date, DateTime.UtcNow.Date.AddYears(3), DateTime.UtcNow.Date.AddYears(3).AddDays(1));
    }

    [Fact]
    public async Task CarteBloquee_TransactionEnregistreeCommeRefuseeSansOperation()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var (_, _, user, compte) = await CreerContexteCompteAsync(context, 500_000m);
        var service = new CarteService(context);
        var carte = await service.EmettreAsync(NouvelleCarte(compte.Id));
        await service.BloquerAsync(carte.Id, "Perte");

        var resultat = await service.AutoriserTransactionAsync(carte.Id, 10_000m, "Paiement", user.Id);

        Assert.Equal("Refusee", resultat.Statut);
        Assert.Contains("Perdue", resultat.MotifRefus);
        Assert.Empty(await context.Operations.ToListAsync());
        Assert.Equal(500_000m, (await context.Comptes.FindAsync(compte.Id))!.Solde);
    }

    [Fact]
    public async Task PlafondJournalierDepasse_TransactionRefusee()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var (_, _, user, compte) = await CreerContexteCompteAsync(context, 500_000m);
        var service = new CarteService(context);
        var carte = await service.EmettreAsync(NouvelleCarte(compte.Id, plafondPaiement: 50_000m));

        var resultat = await service.AutoriserTransactionAsync(carte.Id, 50_001m, "Paiement", user.Id);

        Assert.Equal("Refusee", resultat.Statut);
        Assert.Contains("Plafond", resultat.MotifRefus);
        Assert.Empty(await context.Operations.ToListAsync());
    }

    [Fact]
    public async Task SoldeInsuffisant_TransactionCarteRefuseeSansDebiterLeCompte()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var (_, _, user, compte) = await CreerContexteCompteAsync(context, 20_000m);
        var carte = await new CarteService(context).EmettreAsync(NouvelleCarte(compte.Id));

        var resultat = await new CarteService(context).AutoriserTransactionAsync(carte.Id, 25_000m, "Paiement", user.Id);

        Assert.Equal("Refusee", resultat.Statut);
        Assert.Equal("Solde insuffisant.", resultat.MotifRefus);
        Assert.Equal(20_000m, (await context.Comptes.FindAsync(compte.Id))!.Solde);
        Assert.Empty(await context.Operations.ToListAsync());
    }

    [Theory]
    [InlineData(-1, "Paiement")]
    [InlineData(100, "Inconnu")]
    public async Task ParametresTransactionInvalides_SontTracesCommeRefuses(decimal montant, string type)
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var (_, _, user, compte) = await CreerContexteCompteAsync(context, 100_000m);
        var carte = await new CarteService(context).EmettreAsync(NouvelleCarte(compte.Id));

        var resultat = await new CarteService(context).AutoriserTransactionAsync(carte.Id, montant, type, user.Id);

        Assert.Equal("Refusee", resultat.Statut);
        Assert.False(string.IsNullOrEmpty(resultat.MotifRefus));
        Assert.Equal(0, await context.Operations.CountAsync());
        Assert.Equal(montant, resultat.Montant);
    }

    [Fact]
    public async Task PlafondCumuleSurLaJournee_EstimeToutesLesTransactionsAutorisees()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var (_, _, user, compte) = await CreerContexteCompteAsync(context, 500_000m);
        var carte = await new CarteService(context).EmettreAsync(NouvelleCarte(compte.Id, plafondPaiement: 50_000m));
        var service = new CarteService(context);
        var now = new DateTime(2026, 10, 8, 10, 0, 0, DateTimeKind.Utc);

        var premier = await service.AutoriserTransactionAsync(carte.Id, 30_000m, "Paiement", user.Id, now);
        var second = await service.AutoriserTransactionAsync(carte.Id, 25_000m, "Paiement", user.Id, now.AddHours(1));

        Assert.Equal("Autorisee", premier.Statut);
        Assert.Equal("Refusee", second.Statut);
        Assert.Contains("Plafond", second.MotifRefus);
        Assert.Equal(470_000m, (await context.Comptes.FindAsync(compte.Id))!.Solde);
    }

    [Theory]
    [InlineData("Retrait", 20_001, 100_000, 20_000, 100_000)]
    [InlineData("PaiementEnLigne", 10_001, 100_000, 100_000, 10_000)]
    public async Task PlafondsRetraitEtEnLigne_SontAppliquesSelonLeType(
        string type, decimal montant, decimal plafondPaiement, decimal plafondRetrait, decimal plafondEnLigne)
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var (_, _, user, compte) = await CreerContexteCompteAsync(context, 500_000m);
        var carte = await new CarteService(context).EmettreAsync(
            NouvelleCarte(compte.Id, plafondPaiement, plafondRetrait, plafondEnLigne));

        var resultat = await new CarteService(context).AutoriserTransactionAsync(carte.Id, montant, type, user.Id);

        Assert.Equal("Refusee", resultat.Statut);
        Assert.Contains("Plafond", resultat.MotifRefus);
    }

    [Fact]
    public async Task TransactionCarteAuSeuilDeBlocageAML_EstRefuseeEtTracee()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var (_, _, user, compte) = await CreerContexteCompteAsync(context, 100_000_000m);
        var service = new CarteService(context, new ConformiteService(context), new DeclarationService(context, new HttpContextAccessor()));
        var carte = await service.EmettreAsync(
            NouvelleCarte(compte.Id, 100_000_000m, 100_000_000m, 100_000_000m));

        var resultat = await service.AutoriserTransactionAsync(carte.Id, 50_000_000m, "Paiement", user.Id);

        Assert.Equal("Refusee", resultat.Statut);
        Assert.Contains("seuil de blocage", resultat.MotifRefus);
        Assert.Empty(await context.Operations.ToListAsync());
        Assert.Single(await context.AlertesAML.ToListAsync());
    }

    [Fact]
    public async Task TransactionCarteValide_DebiteCompteEtAjouteEcritureDebitEtContrepartie()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var (_, _, user, compte) = await CreerContexteCompteAsync(context, 500_000m);
        var carte = await new CarteService(context).EmettreAsync(NouvelleCarte(compte.Id));

        var resultat = await new CarteService(context).AutoriserTransactionAsync(carte.Id, 50_000m, "Paiement", user.Id);

        Assert.Equal("Autorisee", resultat.Statut);
        Assert.Equal(450_000m, (await context.Comptes.FindAsync(compte.Id))!.Solde);
        var operation = await context.Operations.SingleAsync();
        Assert.Equal("CartePaiement", operation.TypeOperation);
        var entries = await context.Ecritures.Where(e => e.OperationId == operation.Id).ToListAsync();
        Assert.Contains(entries, e => e.Sens == "Debit" && e.CompteId == compte.Id && e.Montant == 50_000m);
        Assert.Equal(entries.Where(e => e.Sens == "Debit").Sum(e => e.Montant), entries.Where(e => e.Sens == "Credit").Sum(e => e.Montant));
    }

    [Fact]
    public async Task RenouvelerCarte_ExpireAncienneEtRecopieLesPlafonds()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var (_, _, _, compte) = await CreerContexteCompteAsync(context, 500_000m);
        var service = new CarteService(context);
        var ancienne = await service.EmettreAsync(NouvelleCarte(compte.Id, 90_000m, 80_000m, 70_000m));

        var nouvelle = await service.RenouvelerAsync(ancienne.Id);

        Assert.Equal("Expiree", (await context.Cartes.FindAsync(ancienne.Id))!.Statut);
        Assert.Equal("Active", nouvelle.Statut);
        Assert.Equal(ancienne.PlafondRetraitJournalier, nouvelle.PlafondRetraitJournalier);
        Assert.Equal(ancienne.PlafondPaiementJournalier, nouvelle.PlafondPaiementJournalier);
        Assert.Equal(ancienne.PlafondPaiementEnLigneJournalier, nouvelle.PlafondPaiementEnLigneJournalier);
        Assert.NotEqual(ancienne.NumeroCarte, nouvelle.NumeroCarte);
    }

    private static CarteEmissionViewModel NouvelleCarte(int compteId, decimal plafondPaiement = 100_000m,
        decimal plafondRetrait = 100_000m, decimal plafondEnLigne = 100_000m) => new()
    {
        CompteId = compteId,
        TypeCarte = "Visa",
        PlafondRetraitJournalier = plafondRetrait,
        PlafondPaiementJournalier = plafondPaiement,
        PlafondPaiementEnLigneJournalier = plafondEnLigne
    };

    private static async Task<(Agence Agence, Client Client, ApplicationUser User, Compte Compte)> CreerContexteCompteAsync(
        AkarangaBankContext context, decimal solde)
    {
        var agence = await TestDbContextFactory.CreerAgenceAsync(context, "AKR-" + Guid.NewGuid().ToString("N")[..3].ToUpperInvariant());
        var client = await TestDbContextFactory.CreerClientAsync(context, agence.Id, "CLI-" + Guid.NewGuid().ToString("N")[..8]);
        var user = await TestDbContextFactory.CreerUtilisateurAsync(context, agence.Id);
        var compte = await TestDbContextFactory.CreerCompteAsync(context, client.Id, agence.Id, "BJM-" + Guid.NewGuid().ToString("N")[..8], solde);
        return (agence, client, user, compte);
    }

    private static async Task<Pret> CreerPretEnCoursAsync(AkarangaBankContext context, int clientId, Compte compte,
        decimal principal, int duree)
    {
        var pret = new Pret
        {
            Reference = "PRET-" + Guid.NewGuid().ToString("N")[..8],
            ClientId = clientId,
            TypePretId = 1,
            CompteDebitId = compte.Id,
            MontantPrincipal = principal,
            TauxInteret = 12m,
            DureeMois = duree,
            Mensualite = new PretService(context).CalculerMensualite(principal, 12m, duree),
            SoldeRestant = principal,
            Statut = "EnCours",
            DateDebut = DateTime.UtcNow
        };
        context.Prets.Add(pret);
        await context.SaveChangesAsync();
        return pret;
    }
}
