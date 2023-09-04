using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PaymentApi.Models;
using System.Data;
using System.Linq.Expressions;
using Windows.Devices.PointOfService;
using Windows.UI.ViewManagement.Core;

namespace PaymentApi.Controllers;

[Route("api/[controller]")]
[ApiController]
public class PayController : ControllerBase
{
    readonly IConfiguration _configuration;
    readonly SutContext _sutContext;
    readonly ILogger<PayController> _logger;

    private Random _random { get; set; } = new();

    public PayController(IConfiguration configuration, SutContext sutContext, ILogger<PayController> logger) 
    {
        _configuration = configuration;
        _sutContext = sutContext;
        _logger = logger;
    }

    [HttpGet]
    public ActionResult<string> Get()
    {
        var cs = _configuration.GetConnectionString("nixnas");
        var section = _configuration.GetSection("ConnectionStrings");
        var aClaim = _sutContext.Claims.First();
        return aClaim.ClaimNumber?.ToString() ?? string.Empty;
    }

    public ReserveBalance? GetReserveBalance(int claimId)
    {
        var reserveBalance = _sutContext.ClaimTransactions
            .Where(ct => ct.ClaimID == claimId && ct.ClaimTransType == "R")
            .GroupBy(ct => ct.ClaimID)
            .Select(gb => new ReserveBalance()
            {
                MaxReserveDate = gb.Max(ct => ct.CreatedDate),
                MaxClaimTransactionId = gb.Max(ct => ct.ClaimTransactionID),
                Balance = gb.Sum(ct => ct.Amount)
            })
            .SingleOrDefault();
        return reserveBalance;
    }

    
    public IDbContextTransaction? AcquireLock(WorkerPayload payload, int lockTry = 0, int tries = 0)
    {
        payload.LockChecks.Add(DateTime.Now);

        IDbContextTransaction? trx = _sutContext.Database.CurrentTransaction;
        if (trx != null)
        {
            _logger.LogInformation($"Worker {payload.WorkerId,3} ClaimId {payload.ClaimId,3} Attempt {lockTry,3}/{tries} is already in a transaction. {trx.TransactionId}");
            trx.Rollback();
            trx.Dispose();
            return null;
        }

        try
        {
            trx = _sutContext.Database.BeginTransaction(payload.IsolationLevelMapped);
            if (payload.IsolationLevelMapped != IsolationLevel.Snapshot)
            {
                return trx;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Worker {payload.WorkerId,3} ClaimId {payload.ClaimId,3} Attempt {lockTry,3}/{tries} could not start a transaction. The exception was {ex.Message}");
            return null;
        }

        
        // !! Critical to clear the change tracker before the query !!
        _sutContext.ChangeTracker.Clear();

        var claim = _sutContext.Claims
                        .Where(c => c.ClaimID == payload.ClaimId && c.IsLocked == false)
                        .SingleOrDefault();
        
        if (claim == null)
        {
            _logger.LogInformation($"Worker {payload.WorkerId,3} ClaimId {payload.ClaimId,3} Attempt {lockTry,3}/{tries} is not available.");
            trx.Rollback();
            trx.Dispose();
            return null;
        }

        _logger.LogInformation($"Worker {payload.WorkerId,3} ClaimId {payload.ClaimId,3} Attempt {lockTry,3}/{tries} is unlocked. Attempt to acquire.");
        
        try
        {
            claim.IsLocked = true;
            int changes = _sutContext.SaveChanges();
            payload.LockAttempts.Add(DateTime.Now);
            return trx;
        }
        catch (InvalidOperationException ex)
        {
            if (ex.InnerException is SqlException sqlEx)
            {
                _logger.LogError($"Worker {payload.WorkerId,3} ClaimId {payload.ClaimId,3} Attempt {lockTry,3}/{tries} {ex.GetType().Name} {ex.Message}");
                payload.SqlErrorWrite1 = sqlEx.Number;
            }

            _logger.LogError($"Worker {payload.WorkerId,3} ClaimId {payload.ClaimId,3} Attempt {lockTry,3}/{tries} was unlocked but was later locked by another process before worker could secure it. {ex.GetType().Name} {ex.InnerException?.GetType().Name}  {ex.Message}");

            trx.Rollback();
            trx.Dispose();
            return null;
        }
        catch (DbUpdateException ex)
        {
            //trx.Rollback();
            var innerException = ex.InnerException as SqlException;
            payload.SqlErrorWrite1 = innerException?.Number ?? -1;
            _logger.LogInformation($"Transaction state: {trx.TransactionId}");
            _logger.LogError($"Worker {payload.WorkerId,3} ClaimId {payload.ClaimId,3} Attempt {lockTry,3}/{tries} was unlocked but was later locked by another process before worker could secure it. {ex.GetType().Name} {ex.Message}");
            if (innerException != null)
                _logger.LogError($"{innerException.Message}");
            trx.Rollback();
            trx.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            //trx.Rollback();
            _logger.LogError($"Worker {payload.WorkerId,3} ClaimId {payload.ClaimId,3} Attempt {lockTry,3}/{tries} could NOT acquire lock. {ex.GetType().Name} {ex.Message}");
            trx.Rollback();
            trx.Dispose();
            return null;
        }

        throw new Exception("How did you get here?");


    }
    [HttpPost] 
    public ActionResult<WorkerPayload> Post([FromBody] WorkerPayload payload)
    {
        payload.SqlStartDate = DateTime.Now;

        IDbContextTransaction? trx = null;

        int tries = 10;
        var countdown = tries;
        int rowsAffected = 0;

        Task.Delay(_random.Next(1, 10)).Wait();

        while (true)
        {
            int lockTry = tries - countdown + 1;

            _logger.LogInformation($"Worker {payload.WorkerId} Attempt {lockTry}/{tries} ClaimID {payload.ClaimId} lock ATTEMPTED");

            trx = AcquireLock(payload, lockTry, tries);

            // If trx is not null, then we have the lock. Break out of this loop.
            if (trx != null)
            {
                _logger.LogInformation($"Worker {payload.WorkerId} Attempt {lockTry}/{tries} ClaimID {payload.ClaimId} lock ACQUIRED");
                payload.LockAcquiredDate = DateTime.Now;
                break;
            }

            countdown--;

            if (countdown <= 0)
            {
                _logger.LogWarning($"Worker {payload.WorkerId} Attempt {lockTry}/{tries} ClaimId {payload.ClaimId} is ABANDONED");
                payload.AbandonedDate = DateTime.Now;
                return payload;
            }

            var retryDelay = 10 + _random.Next(0, 25 * (tries - countdown + 1));
            _logger.LogInformation($"Worker {payload.WorkerId} Attempt {lockTry}/{tries} ClaimId {payload.ClaimId} is UNAVAILABLE. Retrying in {retryDelay}ms");
            Task.Delay(retryDelay).Wait();
        }




        // get starting reserve in first batch
        try {

            var reserveBalance = GetReserveBalance(payload.ClaimId);

            if (reserveBalance != null)
            {
                payload.BeginningReserve = reserveBalance.Balance;
                payload.BeginningMaxReserveClaimTransactionID = reserveBalance.MaxClaimTransactionId;
                payload.BeginningMaxReserveDate = reserveBalance.MaxReserveDate;
            }
            else
            {
                payload.BeginningReserve = 0;
            }
        }
        catch (SqlException sqlEx)
        {
            payload.SqlErrorRead1 = sqlEx.Number;
            if (payload.SqlErrorRead2 == 1205)
                _logger.LogInformation($"Worker {payload.WorkerId} Claim {payload.ClaimId} DEADLOCKED.");
            trx.Rollback();
            return payload;
        }

        // make payment and reserve change in second batch
        if (payload.SqlErrorRead1 == 0 && payload.Amount <= (payload.BeginningReserve ?? 0))
        {
            ClaimTransactions reserveChange = WritePayment(payload, payload.BeginningReserve ?? 0);

            // get ending reserve in third batch
            if (payload.SqlErrorWrite1 == 0)
            {
                try
                {
                    var reserveBalance = GetReserveBalance(payload.ClaimId);
                    if (reserveBalance != null)
                    {
                        payload.EndingReserve = reserveBalance.Balance;
                        payload.EndingMaxReserveClaimTransactionID = reserveBalance.MaxClaimTransactionId;
                        payload.EndingMaxReserveDate = reserveBalance.MaxReserveDate;
                    }
                    else
                    {
                        payload.BeginningReserve = 0;
                    }
                }
                catch (SqlException sqlEx)
                {
                    payload.SqlErrorRead2 = sqlEx.Number;
                    if (payload.SqlErrorRead2 == 1205)
                        _logger.LogInformation($"Worker {payload.WorkerId} Claim {payload.ClaimId} DEADLOCKED.");
                    trx.Rollback();
                    return payload;
                }
            }



        }
        else
        {
               payload.InsufficientReserve = true;
               payload.EndingReserve = payload.BeginningReserve;
        }

        if (payload.SqlErrorRead1 == 0 && payload.SqlErrorWrite1 == 0 && payload.SqlErrorRead2 == 0)
        {
            // only reset the token on Claims if it's Snapshot

            if (payload.IsolationLevelMapped == IsolationLevel.Snapshot)
            {
                var claim = _sutContext.Claims
                   .Where(c => c.ClaimID == payload.ClaimId)
                   .SingleOrDefault();

                if (claim != null)
                    claim.IsLocked = false;
            }

            
            try
            {
                _sutContext.SaveChanges();
                trx.Commit();


                _logger.LogInformation($"Worker {payload.WorkerId} Claim {payload.ClaimId} successfully COMMITTED.");
                payload.LockReleasedDate = DateTime.Now;
            }
            catch (InvalidOperationException iopex)
            {
                if (iopex.InnerException is DbUpdateException dubex)
                {
                    if (dubex.InnerException is SqlException sqlEx)
                    {
                        payload.SqlErrorWrite1 = sqlEx.Number;
                    }
                }

                trx.Rollback();
                if (payload.SqlErrorWrite1 == 1205)
                    _logger.LogInformation($"Worker {payload.WorkerId} Claim {payload.ClaimId} DEADLOCKED.");

                return payload;
            }
            catch (SqlException sqlEx)
            {
                payload.SqlErrorWrite1 = sqlEx.Number;
                if (payload.SqlErrorWrite1 == 1205)
                    _logger.LogInformation($"Worker {payload.WorkerId} Claim {payload.ClaimId} DEADLOCKED.");
                trx.Rollback();
                return payload;
            }
        }
        
        payload.SqlEndDate = DateTime.Now;
        return payload;
    }

    private ClaimTransactions WritePayment(WorkerPayload payload, decimal startingBalance)
    {
        var reserveChange = new ClaimTransactions()
        {
            Amount = -payload.Amount,
            ClaimID = payload.ClaimId,
            ClaimTransType = "R",
            ReserveBalance = startingBalance - payload.Amount
        };
        var payment = new ClaimTransactions()
        {
            Amount = payload.Amount,
            ClaimID = payload.ClaimId,
            ClaimTransType = "P",
            ReserveBalance = startingBalance - payload.Amount

        };

        _sutContext.ClaimTransactions.Add(reserveChange);
        _sutContext.ClaimTransactions.Add(payment);

        try
        {
            _sutContext.SaveChanges();
            payload.WriteCompletedDate = DateTime.Now;
            _logger.LogInformation($"Worker {payload.WorkerId} Claim {payload.ClaimId} successfully wrote payment and reserve change.");
        }
        catch (InvalidOperationException iopex)
        {
            if (iopex.InnerException is DbUpdateException dubex)
            {
                if (dubex.InnerException is SqlException sqlEx)
                {
                    payload.SqlErrorWrite1 = sqlEx.Number;
                    if (sqlEx.Number == 1205)
                        _logger.LogInformation($"Worker {payload.WorkerId} Claim {payload.ClaimId} DEADLOCKED.");
                }
            }

            _sutContext.Database.CurrentTransaction?.Rollback();

            throw;
        }
        catch (SqlException sqlEx)
        {
            payload.SqlErrorWrite1 = sqlEx.Number;
            if (payload.SqlErrorWrite1 == 1205)
                _logger.LogInformation($"Worker {payload.WorkerId} Claim {payload.ClaimId} DEADLOCKED.");
            _sutContext.Database.CurrentTransaction?.Rollback();
            throw;
        }

        return reserveChange;
    }
}

public class ReserveBalance
{
    public decimal Balance { get; set; }
    public int? MaxClaimTransactionId { get; set; }
    public DateTime MaxReserveDate { get; set; }
}
public class WorkerPayload
{
    public int ClaimNumber { get; set; }
    public int ClaimId { get; set; }
    public DateTime PayloadCreateDate { get; set; }
    public string Command { get; set; }
    public int PreDelayMs { get; set; }
    public int PostDelayMs { get; set; }
    public int MidDelayMs { get; set; }
    public decimal Amount { get; set; }
    public decimal? BeginningReserve { get; set; }
    public int? BeginningMaxReserveClaimTransactionID { get; set; }
    public DateTime? BeginningMaxReserveDate { get; set; }
    public decimal? EndingReserve { get; set; }
    public int? EndingMaxReserveClaimTransactionID { get; set; }
    public DateTime? EndingMaxReserveDate { get; set; }
    public DateTime? WriteCompletedDate { get; set; }
    public int SqlErrorRead1 { get; set; }
    public int SqlErrorWrite1 { get; set; }
    public int SqlErrorRead2 { get; set; }

    public DateTime SqlStartDate { get; set; }
    public DateTime SqlEndDate { get; set; }
    public int SqlDuration => (int)SqlEndDate.Subtract(SqlStartDate).TotalMilliseconds;
    public string IsolationLevel { get; set; }
    public bool InsufficientReserve { get; set; } = false;

    public IsolationLevel IsolationLevelMapped => Enum.Parse<IsolationLevel>(IsolationLevel);

    public int WorkerId { get; set; } = 0;

    public List<DateTime> LockChecks { get; set; } = new List<DateTime>();
    public List<DateTime> LockAttempts { get; set; } = new List<DateTime>();

    public  int LockAttemptsCount => LockAttempts.Count;
    public int LockChecksCount => LockChecks.Count;

    public DateTime? AbandonedDate { get; set; }
    public DateTime? LockAcquiredDate { get; set; }
    public DateTime? LockReleasedDate { get; set; }
    public long? LockDuration => LockReleasedDate != null && LockAcquiredDate != null ? 
        (long)LockReleasedDate.Value.Subtract(LockAcquiredDate.Value).TotalMilliseconds :
        null;


}