using AkarangaBank.Web.Data;
using AkarangaBank.Web.Models;
using AkarangaBank.Web.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace AkarangaBank.Web.Services;

public class ComptabiliteService : IComptabiliteService
{
    private const decimal TauxFraisRetrait = 0.01m;
    private const decimal FraisRetraitMinimum = 100m;

    private readonly AkarangaBankContext _context;
    private readonly IConformiteService _conformite;
    private readonly ISurveillanceService _surveillance;
    private readonly IDeclarationService _declaration;
    private readonly IDetectionAvanceeService? _detection;

    public ComptabiliteService(
        AkarangaBankContext context,
        IConformiteService conformite,
        ISurveillanceService surveillance,
        IDeclarationService declaration,
        IDetectionAvanceeService? detection = null)
    {
        _context = context;
        _conformite = conformite;
        _surveillance = surveillance;
        _declaration = declaration;
        _detection = detection;
    }

    public async Task<Operation> DeposerAsync(DepotViewModel vm, int utilisateurId)
    {
        var compte = await GetCompteActifAsync(vm.CompteId);
        var (analyse, fractionnement, surveillance) = await AnalyserAsync(vm.CompteId, vm.Montant, "Depot");
        if (analyse.EstBloquee)
            throw new InvalidOperationException(analyse.Motif ?? "Opération bloquée par le contrôle de conformité.");

        await using var transaction = await _context.Database.BeginTransactionAsync();
        var transactionCommitted = false;
        try
        {
            var operation = CreerOperation(compte, utilisateurId, "Depot", vm.Montant, vm.Commentaire);
            _context.Operations.Add(operation);
            await _context.SaveChangesAsync();

            _context.Ecritures.AddRange(new Ecriture
            {
                OperationId = operation.Id,
                CompteId = compte.Id,
                CodeCompteGeneral = $"CLIENT:{compte.NumeroCompte}",
                Sens = "Credit",
                Montant = vm.Montant,
                Devise = compte.Devise,
                DateEcriture = DateTime.UtcNow,
                Libelle = $"Dépôt sur compte {compte.NumeroCompte}"
            }, new Ecriture
            {
                OperationId = operation.Id,
                CompteId = null,
                CodeCompteGeneral = $"CAISSE:{compte.AgenceId}",
                Sens = "Debit",
                Montant = vm.Montant,
                Devise = compte.Devise,
                DateEcriture = DateTime.UtcNow,
                Libelle = $"Contrepartie de caisse du dépôt {operation.Reference}"
            });

            compte.Solde += vm.Montant;
            compte.DateModification = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
            transactionCommitted = true;

            await CreerAlertesSiNecessaireAsync(compte.ClientId, operation.Id, analyse, fractionnement, surveillance);
            await CreerAlertesAvanceesAsync(compte.ClientId, operation.Id);
            return operation;
        }
        catch
        {
            if (!transactionCommitted)
                await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<Operation> RetirerAsync(RetraitViewModel vm, int utilisateurId)
    {
        var compte = await GetCompteActifAsync(vm.CompteId);
        var frais = CalculerFraisRetrait(vm.Montant);
        if (compte.Solde < vm.Montant + frais)
            throw new InvalidOperationException("Solde insuffisant.");

        var (analyse, fractionnement, surveillance) = await AnalyserAsync(vm.CompteId, vm.Montant, "Retrait");
        if (analyse.EstBloquee)
            throw new InvalidOperationException(analyse.Motif ?? "Opération bloquée par le contrôle de conformité.");

        await using var transaction = await _context.Database.BeginTransactionAsync();
        var transactionCommitted = false;
        try
        {
            var operation = CreerOperation(compte, utilisateurId, "Retrait", vm.Montant, vm.Commentaire);
            var operationFrais = CreerOperation(compte, utilisateurId, "Frais", frais, "Frais de retrait (1 %, minimum 100 BIF)");
            _context.Operations.AddRange(operation, operationFrais);
            await _context.SaveChangesAsync();

            _context.Ecritures.AddRange(new Ecriture
            {
                OperationId = operation.Id,
                CompteId = compte.Id,
                CodeCompteGeneral = $"CLIENT:{compte.NumeroCompte}",
                Sens = "Debit",
                Montant = vm.Montant,
                Devise = compte.Devise,
                DateEcriture = DateTime.UtcNow,
                Libelle = $"Retrait du compte {compte.NumeroCompte}"
            }, new Ecriture
            {
                OperationId = operationFrais.Id,
                CompteId = compte.Id,
                CodeCompteGeneral = $"CLIENT:{compte.NumeroCompte}",
                Sens = "Debit",
                Montant = frais,
                Devise = compte.Devise,
                DateEcriture = DateTime.UtcNow,
                Libelle = $"Frais de retrait du compte {compte.NumeroCompte}"
            }, new Ecriture
            {
                OperationId = operation.Id,
                CompteId = null,
                CodeCompteGeneral = $"CAISSE:{compte.AgenceId}",
                Sens = "Credit",
                Montant = vm.Montant,
                Devise = compte.Devise,
                DateEcriture = DateTime.UtcNow,
                Libelle = $"Contrepartie de caisse du retrait {operation.Reference}"
            }, new Ecriture
            {
                OperationId = operationFrais.Id,
                CompteId = null,
                CodeCompteGeneral = $"PRODUITS_FRAIS:{compte.AgenceId}",
                Sens = "Credit",
                Montant = frais,
                Devise = compte.Devise,
                DateEcriture = DateTime.UtcNow,
                Libelle = $"Contrepartie de produit des frais {operationFrais.Reference}"
            });

            compte.Solde -= vm.Montant + frais;
            compte.DateModification = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
            transactionCommitted = true;

            await CreerAlertesSiNecessaireAsync(compte.ClientId, operation.Id, analyse, fractionnement, surveillance);
            await CreerAlertesAvanceesAsync(compte.ClientId, operation.Id);
            return operation;
        }
        catch
        {
            if (!transactionCommitted)
                await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<Operation> VirerAsync(VirementViewModel vm, int utilisateurId)
    {
        if (vm.CompteSourceId == vm.CompteDestinationId)
            throw new InvalidOperationException("Les comptes source et destination doivent être différents.");

        var source = await GetCompteActifAsync(vm.CompteSourceId);
        var destination = await GetCompteActifAsync(vm.CompteDestinationId);
        if (source.Devise != destination.Devise)
            throw new InvalidOperationException("Les devises des deux comptes doivent être identiques.");
        if (source.Solde < vm.Montant)
            throw new InvalidOperationException("Solde insuffisant sur le compte source.");

        var (analyse, fractionnement, surveillance) = await AnalyserAsync(vm.CompteSourceId, vm.Montant, "Virement");
        if (analyse.EstBloquee)
            throw new InvalidOperationException(analyse.Motif ?? "Opération bloquée par le contrôle de conformité.");

        await using var transaction = await _context.Database.BeginTransactionAsync();
        var transactionCommitted = false;
        try
        {
            var virement = new Virement
            {
                Reference = GenererReference("VIR"),
                CompteSourceId = source.Id,
                CompteDestinationId = destination.Id,
                UtilisateurId = utilisateurId,
                Montant = vm.Montant,
                Devise = source.Devise,
                Statut = "Valide",
                Motif = vm.Motif?.Trim(),
                ControleAML = analyse.NecessiteVigilanceRenforcee || fractionnement,
                DateVirement = DateTime.UtcNow,
                DateValidation = DateTime.UtcNow
            };
            _context.Virements.Add(virement);
            await _context.SaveChangesAsync();

            var operation = CreerOperation(source, utilisateurId, "Virement", vm.Montant, vm.Motif);
            operation.Reference = virement.Reference;
            operation.VirementId = virement.Id;
            _context.Operations.Add(operation);
            await _context.SaveChangesAsync();

            var ecritures = new[]
            {
                new Ecriture
                {
                    OperationId = operation.Id, CompteId = source.Id, CodeCompteGeneral = $"CLIENT:{source.NumeroCompte}", Sens = "Debit",
                    Montant = vm.Montant, Devise = source.Devise, DateEcriture = DateTime.UtcNow,
                    Libelle = $"Virement sortant vers {destination.NumeroCompte}"
                },
                new Ecriture
                {
                    OperationId = operation.Id, CompteId = destination.Id, CodeCompteGeneral = $"CLIENT:{destination.NumeroCompte}", Sens = "Credit",
                    Montant = vm.Montant, Devise = destination.Devise, DateEcriture = DateTime.UtcNow,
                    Libelle = $"Virement entrant depuis {source.NumeroCompte}"
                }
            };

            VerifierEquilibre(ecritures);
            _context.Ecritures.AddRange(ecritures);
            source.Solde -= vm.Montant;
            destination.Solde += vm.Montant;
            source.DateModification = destination.DateModification = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
            transactionCommitted = true;
            await CreerAlertesSiNecessaireAsync(source.ClientId, operation.Id, analyse, fractionnement, surveillance);
            await CreerAlertesAvanceesAsync(source.ClientId, operation.Id);
            return operation;
        }
        catch
        {
            if (!transactionCommitted)
                await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<JournalViewModel> GetJournalAsync(
        DateTime? du, DateTime? au, string? typeOperation, int page, int taille)
    {
        page = Math.Max(page, 1);
        taille = Math.Clamp(taille, 1, 100);
        var query = _context.Operations.AsNoTracking()
            .Include(o => o.Compte)
            .Include(o => o.Ecritures)
            .Include(o => o.Utilisateur)
            .AsQueryable();

        if (du.HasValue)
            query = query.Where(o => o.DateOperation >= du.Value.Date);
        if (au.HasValue)
            query = query.Where(o => o.DateOperation < au.Value.Date.AddDays(1));
        if (!string.IsNullOrWhiteSpace(typeOperation))
            query = query.Where(o => o.TypeOperation == typeOperation);

        var total = await query.CountAsync();
        var operations = await query.OrderByDescending(o => o.DateOperation)
            .Skip((page - 1) * taille)
            .Take(taille)
            .ToListAsync();

        return new JournalViewModel
        {
            Operations = operations,
            Du = du,
            Au = au,
            TypeOperation = typeOperation,
            Page = page,
            TaillePage = taille,
            Total = total
        };
    }

    public async Task<ReleveViewModel?> GetReleveAsync(int compteId)
    {
        var compte = await _context.Comptes.AsNoTracking()
            .Include(c => c.Client)
            .FirstOrDefaultAsync(c => c.Id == compteId);
        if (compte is null)
            return null;

        var ecritures = await _context.Ecritures.AsNoTracking()
            .Where(e => e.CompteId == compteId)
            .OrderBy(e => e.DateEcriture)
            .ToListAsync();
        var credits = ecritures.Where(e => e.Sens == "Credit").Sum(e => e.Montant);
        var debits = ecritures.Where(e => e.Sens == "Debit").Sum(e => e.Montant);

        return new ReleveViewModel
        {
            Compte = compte,
            SoldeInitial = compte.Solde - credits + debits,
            SoldeFinal = compte.Solde,
            Ecritures = ecritures
        };
    }

    public async Task<decimal> GetSoldeAsync(int compteId)
    {
        return await _context.Comptes.AsNoTracking()
            .Where(c => c.Id == compteId)
            .Select(c => (decimal?)c.Solde)
            .FirstOrDefaultAsync()
            ?? throw new InvalidOperationException("Compte introuvable.");
    }

    public async Task<bool> VerifierEquilibreGlobalAsync()
    {
        var operations = await _context.Operations.AsNoTracking()
            .Select(o => new
            {
                o.TypeOperation,
                o.Montant,
                NombreEcritures = o.Ecritures.Count,
                Debits = o.Ecritures.Where(e => e.Sens == "Debit").Sum(e => (decimal?)e.Montant) ?? 0m,
                Credits = o.Ecritures.Where(e => e.Sens == "Credit").Sum(e => (decimal?)e.Montant) ?? 0m
            })
            .ToListAsync();

        return operations.All(operation => operation.TypeOperation switch
        {
            "Depot" => operation.NombreEcritures == 2 && operation.Debits == operation.Montant && operation.Credits == operation.Montant,
            "Retrait" or "Frais" => operation.NombreEcritures == 2 && operation.Debits == operation.Montant && operation.Credits == operation.Montant,
            "Virement" => operation.NombreEcritures == 2 && operation.Debits == operation.Montant && operation.Credits == operation.Montant,
            _ => operation.NombreEcritures > 0 && operation.Debits == operation.Credits
        });
    }

    private async Task<Compte> GetCompteActifAsync(int compteId)
    {
        var compte = await _context.Comptes.FirstOrDefaultAsync(c => c.Id == compteId)
            ?? throw new InvalidOperationException("Compte introuvable.");
        if (compte.Statut != "Actif")
            throw new InvalidOperationException($"Compte {compte.Statut}. Opération impossible.");
        return compte;
    }

    private async Task<(ResultatConformite Analyse, bool Fractionnement, ResultatSurveillance Surveillance)> AnalyserAsync(
        int compteId, decimal montant, string typeOperation)
    {
        var date = DateTime.UtcNow;
        var analyse = await _conformite.AnalyserTransactionAsync(compteId, montant, typeOperation);
        var fractionnement = await _conformite.DetecterFractionnementAsync(compteId, montant, date);
        var surveillance = await _surveillance.AnalyserTransactionAsync(compteId, montant, typeOperation, date);
        return (analyse, fractionnement, surveillance);
    }

    private static Operation CreerOperation(Compte compte, int utilisateurId, string type, decimal montant, string? commentaire)
    {
        var now = DateTime.UtcNow;
        return new Operation
        {
            Reference = GenererReference("AKR"),
            TypeOperation = type,
            CompteId = compte.Id,
            UtilisateurId = utilisateurId,
            AgenceId = compte.AgenceId,
            Montant = montant,
            Devise = compte.Devise,
            Statut = "Valide",
            DateOperation = now,
            DateValidation = now,
            Commentaire = commentaire?.Trim()
        };
    }

    private async Task CreerAlertesSiNecessaireAsync(
        int clientId, int operationId, ResultatConformite analyse, bool fractionnement, ResultatSurveillance surveillance)
    {
        if (analyse.NecessiteVigilanceRenforcee || analyse.NecessiteDeclaration)
        {
            await _declaration.CreerAlerteAsync(
                clientId, operationId, "DepassementSeuil", analyse.Niveau.ToString(),
                analyse.Motif ?? "Dépassement du seuil AML.");
        }

        if (fractionnement)
        {
            await _declaration.CreerAlerteAsync(
                clientId, operationId, "Fractionnement", "Eleve",
                "Suspicion de fractionnement : plusieurs opérations sous le seuil sur une courte période.");
        }

        foreach (var alerte in surveillance.Alertes.Where(a =>
            a.Regle != "MontantEleve"
            && !(analyse.NecessiteVigilanceRenforcee && a.Regle == "MontantInhabituel")))
        {
            await _declaration.CreerAlerteAsync(
                clientId, operationId, alerte.Regle, alerte.Niveau.ToString(), alerte.Description);
        }
    }

    private async Task CreerAlertesAvanceesAsync(int clientId, int operationId)
    {
        if (_detection is null)
            return;

        var maintenant = DateTime.UtcNow;
        var resultats = new[]
        {
            await _detection.DetecterStructuringAsync(clientId, maintenant),
            await _detection.DetecterSmurfingAsync(clientId, maintenant),
            await _detection.DetecterLayeringAsync(clientId, maintenant),
            await _detection.DetecterVelociteAnormaleAsync(clientId, maintenant)
        };

        foreach (var alerte in resultats.SelectMany(r => r.Alertes))
        {
            var existe = await _context.AlertesAML.AnyAsync(a =>
                a.ClientId == clientId && a.TypeAlerte == alerte.Regle
                && a.Statut != "Traitee" && a.Statut != "Classee");
            if (!existe)
                await _declaration.CreerAlerteAsync(clientId, operationId, alerte.Regle, alerte.Niveau.ToString(), alerte.Description);
        }
    }

    private static string GenererReference(string prefix)
    {
        var date = DateTime.UtcNow.ToString("yyyyMMdd");
        var random = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        return $"{prefix}-{date}-{random}";
    }

    private static decimal CalculerFraisRetrait(decimal montant)
    {
        var fraisPourcentage = decimal.Round(montant * TauxFraisRetrait, 2, MidpointRounding.AwayFromZero);
        return Math.Max(FraisRetraitMinimum, fraisPourcentage);
    }

    private static void VerifierEquilibre(IEnumerable<Ecriture> ecritures)
    {
        var debit = ecritures.Where(e => e.Sens == "Debit").Sum(e => e.Montant);
        var credit = ecritures.Where(e => e.Sens == "Credit").Sum(e => e.Montant);
        if (debit != credit)
            throw new InvalidOperationException($"Écriture déséquilibrée : débit={debit:N2}, crédit={credit:N2}.");
    }
}
